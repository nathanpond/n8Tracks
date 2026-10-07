import { describe, expect, it, vi } from 'vitest';
import type { ImageFetch } from '../adapter/imageReader.ts';
import type { ExportPart } from '../messages.ts';
import { fakeBrowser, jsonResponse } from '../testing/fakeBrowser.ts';
import type { Fetch } from './apiClient.ts';
import { Connection } from './connection.ts';
import { CoverImages, IMAGES_IN_FLIGHT, IMAGES_KEY, READY_WAIT_MS } from './images.ts';

const ADDRESS = 'https://n8tracks.example.com/base';
const TOKEN = 'n8t_0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefg';
const TAB = 7;
const EXPORT = '0f1e2d3c-0000-4000-8000-000000000001';
const GENERATION = '0f1e2d3c-0000-4000-8000-0000000000aa';
const EXPORT_PATH = `api/v1/suno/exports/${EXPORT}`;

/** A clip with a cover on Suno's image host. */
function clip(id: string, extra: Record<string, unknown> = {}): Record<string, unknown> {
  return { id, image_large_url: `https://cdn2.suno.ai/image_large_${id}.jpeg`, ...extra };
}

function part(number: number, clips: unknown[], trashedClips: unknown[] = []): ExportPart {
  return { partNumber: number, clips, trashedClips, playlists: [] };
}

/** One call n8Tracks received. */
interface Call {
  method: string;
  path: string;
  authorization: string | null;
  file: File | null;
}

function jpeg(): Response {
  return new Response(new Uint8Array([0xff, 0xd8, 0xff, 0xe0]), {
    status: 200,
    headers: { 'Content-Type': 'image/jpeg' },
  });
}

/**
 * A paired connection over a fake n8Tracks, which answers with `answer` (the test may change it),
 * and a fake image host, which answers each image read with `image`.
 */
async function setup(options: { readable?: boolean } = {}) {
  const fake = fakeBrowser(['https://suno.com/*', 'https://n8tracks.example.com/*']);
  const calls: Call[] = [];
  let answer = (call: Call): Response =>
    call.method === 'GET'
      ? jsonResponse(200, { id: EXPORT, state: 'ready' })
      : jsonResponse(200, { sunoId: 'x' });
  const fetchImpl = vi.fn<Fetch>((input, init) => {
    const path = input.slice(`${ADDRESS}/`.length);
    if (path === 'api/v1/extension/handshake') {
      return Promise.resolve(
        jsonResponse(200, {
          applicationVersion: '0.1.0',
          credentialName: 'Chrome',
          scopes: ['suno.sync'],
          compatible: true,
        }),
      );
    }
    const body = init.body instanceof FormData ? init.body.get('file') : null;
    const call: Call = {
      method: init.method ?? 'GET',
      path,
      authorization: new Headers(init.headers).get('Authorization'),
      file: body instanceof File ? body : null,
    };
    calls.push(call);
    return Promise.resolve(answer(call));
  });
  const connection = new Connection({
    browser: fake.browser,
    versions: { extension: '0.1.0', adapter: '3' },
    fetch: fetchImpl,
  });
  expect((await connection.connect(`${ADDRESS}/`, TOKEN)).ok).toBe(true);

  let image: (address: string) => Promise<Response> = () => Promise.resolve(jpeg());
  const reads = vi.fn<ImageFetch>((address) => image(address));
  const slept: number[] = [];
  let clock = 0;
  const local = new Map<string, unknown>();
  const storage = {
    get: (keys: string[]) =>
      Promise.resolve(
        Object.fromEntries(
          keys.filter((key) => local.has(key)).map((key) => [key, local.get(key)]),
        ),
      ),
    set: (items: Record<string, unknown>) => {
      for (const [key, value] of Object.entries(items)) {
        local.set(key, structuredClone(value));
      }
      return Promise.resolve();
    },
    remove: (keys: string[]) => {
      for (const key of keys) {
        local.delete(key);
      }
      return Promise.resolve();
    },
  };
  const make = () =>
    new CoverImages({
      connection,
      storage,
      fetch: reads,
      readable: options.readable ?? true,
      sleep: (ms) => {
        slept.push(ms);
        clock += ms;
        return Promise.resolve();
      },
      now: () => clock,
    });
  return {
    images: make(),
    make,
    calls,
    reads,
    slept,
    local,
    connection,
    /** The calls that sent an image, by path. */
    puts: () => calls.filter((call) => call.method === 'PUT').map((call) => call.path),
    answer: (next: (call: Call) => Response) => {
      answer = next;
    },
    image: (next: (address: string) => Promise<Response>) => {
      image = next;
    },
  };
}

const staged = (sunoId: string) => `${EXPORT_PATH}/artwork/${sunoId}`;

describe('cover images of a sync', () => {
  it('sends one image per clip that has one, once each, to its staged record', async () => {
    const context = await setup();
    await context.images.collect(EXPORT, TAB, part(1, [clip('a'), { id: 'no-image' }, clip('b')]));
    // A clip in two lists, and a part sent again, still send one image each.
    await context.images.collect(EXPORT, TAB, part(2, [clip('b')], [clip('t')]));
    await context.images.collect(EXPORT, TAB, part(1, [clip('a'), { id: 'no-image' }, clip('b')]));

    await context.images.start(EXPORT, TAB);

    expect(context.puts().toSorted()).toEqual([staged('a'), staged('b'), staged('t')]);
    expect(context.reads.mock.calls.map(([address]) => address).toSorted()).toEqual([
      'https://cdn2.suno.ai/image_large_a.jpeg',
      'https://cdn2.suno.ai/image_large_b.jpeg',
      'https://cdn2.suno.ai/image_large_t.jpeg',
    ]);
    // n8Tracks gets the image as a file, with the extension's token; the image read gets neither.
    for (const call of context.calls.filter((item) => item.method === 'PUT')) {
      expect(call.authorization).toBe(`Bearer ${TOKEN}`);
      expect(call.file?.type).toBe('image/jpeg');
      expect(call.file?.size).toBe(4);
    }
    for (const [, init] of context.reads.mock.calls) {
      expect(init.credentials).toBe('omit');
      expect(init.headers).toBeUndefined();
    }
    expect(await context.images.progress(TAB)).toEqual({
      exportId: EXPORT,
      tabId: TAB,
      state: 'finished',
      total: 3,
      sent: 3,
      failed: 0,
      ignored: 0,
    });
    expect(await context.images.progress(TAB + 1)).toBeNull();
  });

  it('has at most four images in flight', async () => {
    const context = await setup();
    const ids = Array.from({ length: 10 }, (_, index) => `c${String(index)}`);
    await context.images.collect(
      EXPORT,
      TAB,
      part(
        1,
        ids.map((id) => clip(id)),
      ),
    );
    let inFlight = 0;
    let most = 0;
    const waiting: (() => void)[] = [];
    context.image(async () => {
      inFlight += 1;
      most = Math.max(most, inFlight);
      await new Promise<void>((resolve) => {
        waiting.push(resolve);
      });
      inFlight -= 1;
      return jpeg();
    });

    const sending = context.images.start(EXPORT, TAB);
    // Let the reads queue up, then let them go one at a time, one release per image.
    let released = 0;
    while (released < ids.length) {
      released += 1;
      await vi.waitFor(() => {
        expect(waiting.length).toBeGreaterThan(0);
      });
      expect(inFlight).toBeLessThanOrEqual(IMAGES_IN_FLIGHT);
      waiting.shift()?.();
    }
    await sending;

    expect(most).toBe(IMAGES_IN_FLIGHT);
    expect(context.puts()).toHaveLength(10);
  });

  it('counts an image that cannot be read or is refused, skips it, and never sends it again', async () => {
    const context = await setup();
    await context.images.collect(
      EXPORT,
      TAB,
      part(1, [
        clip('ok'),
        clip('blocked'),
        clip('missing'),
        clip('refused'),
        { id: 'elsewhere', image_url: 'https://example.com/image.jpeg' },
      ]),
    );
    context.image((address) => {
      if (address.includes('blocked')) {
        return Promise.reject(new TypeError('Failed to fetch'));
      }
      return Promise.resolve(
        address.includes('missing') ? new Response('', { status: 404 }) : jpeg(),
      );
    });
    context.answer((call) => {
      if (call.method === 'GET') {
        return jsonResponse(200, { id: EXPORT, state: 'ready' });
      }
      return call.path === staged('refused')
        ? jsonResponse(415, { code: 'artwork_type_not_supported' })
        : jsonResponse(200, {});
    });

    await context.images.start(EXPORT, TAB);

    expect(context.reads).toHaveBeenCalledTimes(4);
    expect(context.puts().toSorted()).toEqual([staged('ok'), staged('refused')]);
    expect(await context.images.progress(TAB)).toMatchObject({
      state: 'finished',
      total: 4,
      sent: 1,
      failed: 3,
    });
    // An image on a host that is not listed is never requested, nor counted.
    expect(
      context.reads.mock.calls.some(([address]) => new URL(address).hostname === 'example.com'),
    ).toBe(false);
  });

  it('waits for n8Tracks to get the export ready before sending', async () => {
    const context = await setup();
    await context.images.collect(EXPORT, TAB, part(1, [clip('a')]));
    const states = ['classifying', 'classifying', 'ready'];
    context.answer((call) =>
      call.method === 'GET'
        ? jsonResponse(200, { id: EXPORT, state: states.shift() ?? 'ready' })
        : jsonResponse(200, {}),
    );

    await context.images.start(EXPORT, TAB);

    expect(context.calls.map((call) => call.method)).toEqual(['GET', 'GET', 'GET', 'PUT']);
    expect(context.slept).toEqual([2_000, 2_000]);
    expect(await context.images.progress(TAB)).toMatchObject({ state: 'finished', sent: 1 });
  });

  it('stops, counting the images not sent, when the export is discarded or never gets ready', async () => {
    const discarded = await setup();
    await discarded.images.collect(EXPORT, TAB, part(1, [clip('a'), clip('b')]));
    discarded.answer(() => jsonResponse(200, { id: EXPORT, state: 'discarded' }));

    await discarded.images.start(EXPORT, TAB);

    expect(discarded.puts()).toEqual([]);
    expect(await discarded.images.progress(TAB)).toMatchObject({
      state: 'stopped',
      sent: 0,
      failed: 2,
    });

    const slow = await setup();
    await slow.images.collect(EXPORT, TAB, part(1, [clip('a')]));
    slow.answer(() => jsonResponse(200, { id: EXPORT, state: 'classifying' }));

    await slow.images.start(EXPORT, TAB);

    expect(slow.slept.reduce((sum, ms) => sum + ms, 0)).toBeGreaterThan(READY_WAIT_MS);
    expect(await slow.images.progress(TAB)).toMatchObject({ state: 'stopped', failed: 1 });
  });

  it('stops when n8Tracks forgets the token, without sending anything more', async () => {
    const context = await setup();
    await context.images.collect(EXPORT, TAB, part(1, [clip('a'), clip('b'), clip('c')]));
    context.answer((call) =>
      call.method === 'GET'
        ? jsonResponse(200, { id: EXPORT, state: 'ready' })
        : jsonResponse(401, { code: 'invalid_token' }),
    );

    await context.images.start(EXPORT, TAB);

    // The first refusal forgets the token; nothing else reaches n8Tracks.
    expect(context.puts().length).toBeLessThanOrEqual(IMAGES_IN_FLIGHT);
    expect(await context.images.progress(TAB)).toMatchObject({ state: 'stopped', sent: 0 });
    expect((await context.connection.state()).status).toBe('revoked');
  });

  it('skips images, saying so, when they cannot be read without credentials', async () => {
    const context = await setup({ readable: false });
    await context.images.collect(EXPORT, TAB, part(1, [clip('a')]));

    await context.images.start(EXPORT, TAB);

    expect(context.reads).not.toHaveBeenCalled();
    expect(context.calls).toEqual([]);
    expect(await context.images.progress(TAB)).toMatchObject({ state: 'skipped', sent: 0 });
  });

  it('forgets the images of a discarded export', async () => {
    const context = await setup();
    await context.images.collect(EXPORT, TAB, part(1, [clip('a')]));

    await context.images.discard(EXPORT);

    expect(context.local.has(IMAGES_KEY)).toBe(false);
    await context.images.start(EXPORT, TAB);
    expect(context.calls).toEqual([]);
    expect(await context.images.progress(TAB)).toMatchObject({ state: 'finished', total: 0 });
  });

  it('carries on after the service worker restarts, sending only what was left', async () => {
    const context = await setup();
    await context.images.collect(EXPORT, TAB, part(1, [clip('a'), clip('b')]));
    const stored = context.local.get(IMAGES_KEY) as Record<string, unknown>;
    // As a stopped service worker left it: one image sent, one still to send.
    context.local.set(IMAGES_KEY, {
      ...stored,
      state: 'sending',
      sent: 1,
      pending: [{ sunoId: 'b', address: 'https://cdn2.suno.ai/image_large_b.jpeg' }],
    });

    await context.make().resume();

    expect(context.puts()).toEqual([staged('b')]);
    expect(await context.images.progress(TAB)).toMatchObject({
      state: 'finished',
      total: 2,
      sent: 2,
    });
  });
  it('stops when every image keeps waiting for longer than the export may take', async () => {
    const context = await setup();
    await context.images.collect(EXPORT, TAB, part(1, [clip('a')]));
    // n8Tracks says ready, yet each upload is told the import is being confirmed.
    context.answer((call) =>
      call.method === 'GET'
        ? jsonResponse(200, { id: EXPORT, state: 'ready' })
        : jsonResponse(409, { code: 'export_not_ready', state: 'committing' }),
    );

    await context.images.start(EXPORT, TAB);

    expect(context.slept.reduce((sum, ms) => sum + ms, 0)).toBeGreaterThan(READY_WAIT_MS);
    expect(await context.images.progress(TAB)).toMatchObject({ state: 'stopped', failed: 1 });
  });
});

describe('a cover image that arrives after the import was confirmed', () => {
  async function committed(late: (call: Call) => Response, generationId: string | null) {
    const context = await setup();
    await context.images.collect(EXPORT, TAB, part(1, [clip('a')]));
    context.answer((call) => {
      if (call.method === 'GET') {
        return jsonResponse(200, { id: EXPORT, state: 'committed' });
      }
      if (call.path === staged('a')) {
        return jsonResponse(409, {
          code: 'export_not_ready',
          state: 'committed',
          ...(generationId === null ? {} : { generationId }),
        });
      }
      return late(call);
    });
    await context.images.start(EXPORT, TAB);
    return context;
  }

  it('is attached to its Generation when the Generation has no image', async () => {
    const context = await committed(() => jsonResponse(200, {}), GENERATION);

    expect(context.puts()).toEqual([staged('a'), `api/v1/generations/${GENERATION}/artwork`]);
    const late = context.calls.at(-1);
    expect(late?.authorization).toBe(`Bearer ${TOKEN}`);
    expect(late?.file?.type).toBe('image/jpeg');
    // The image was read once, for both.
    expect(context.reads).toHaveBeenCalledTimes(1);
    expect(await context.images.progress(TAB)).toMatchObject({ state: 'finished', sent: 1 });
  });

  it('is refused, and ignored, when the Generation has an image', async () => {
    const context = await committed(
      () => jsonResponse(409, { code: 'artwork_exists' }),
      GENERATION,
    );

    expect(context.puts()).toEqual([staged('a'), `api/v1/generations/${GENERATION}/artwork`]);
    expect(await context.images.progress(TAB)).toMatchObject({
      state: 'finished',
      sent: 0,
      failed: 0,
      ignored: 1,
    });
  });

  it('is ignored when the record became no Generation', async () => {
    const context = await committed(() => jsonResponse(200, {}), null);

    expect(context.puts()).toEqual([staged('a')]);
    expect(await context.images.progress(TAB)).toMatchObject({ state: 'finished', ignored: 1 });
  });

  it('waits while the import is being confirmed, then goes to the Generation', async () => {
    const context = await setup();
    await context.images.collect(EXPORT, TAB, part(1, [clip('a')]));
    let state = 'ready';
    context.answer((call) => {
      if (call.method === 'GET') {
        return jsonResponse(200, { id: EXPORT, state });
      }
      if (call.path === staged('a')) {
        // Confirmed between the state check and the upload.
        const answer = jsonResponse(409, {
          code: 'export_not_ready',
          state: state === 'ready' ? 'committing' : 'committed',
          ...(state === 'ready' ? {} : { generationId: GENERATION }),
        });
        state = 'committed';
        return answer;
      }
      return jsonResponse(200, {});
    });

    await context.images.start(EXPORT, TAB);

    expect(context.puts()).toEqual([
      staged('a'),
      staged('a'),
      `api/v1/generations/${GENERATION}/artwork`,
    ]);
    expect(await context.images.progress(TAB)).toMatchObject({ state: 'finished', sent: 1 });
  });
});
