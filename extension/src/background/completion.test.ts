import { describe, expect, it, vi } from 'vitest';
import type { ImageFetch } from '../adapter/imageReader.ts';
import { fakeBrowser, jsonResponse } from '../testing/fakeBrowser.ts';
import { sunoObject } from '../testing/sunoResponses.ts';
import type { Fetch } from './apiClient.ts';
import { COMPLETION_ALARM, COMPLETION_KEY, CompletionWatch, WATCH_LIMIT_MS } from './completion.ts';
import { Connection } from './connection.ts';

const ADDRESS = 'https://n8tracks.example.com/base';
const TOKEN = 'n8t_0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefg';
const TAB = 7;
const REQUEST = '0f1e2d3c-0000-4000-8000-000000000001';
const FIRST = { id: '0f1e2d3c-0000-4000-8000-0000000000a1', sunoId: 'clip-1' };
const SECOND = { id: '0f1e2d3c-0000-4000-8000-0000000000a2', sunoId: 'clip-2' };
const START = Date.parse('2026-10-07T12:00:00Z');

/** One call n8Tracks received. */
interface Call {
  method: string;
  path: string;
  body: unknown;
  file: File | null;
}

/** Suno's finished clip (TS-001, `feed-v3.completed-clip`) as `id`, with `status`. */
function finished(id: string, status = 'complete'): Record<string, unknown> {
  const feed = sunoObject('feed-v3.completed-clip.response');
  const [clip] = feed.clips as Record<string, unknown>[];
  return { ...clip, id, status };
}

/**
 * The watch over a paired connection to a fake n8Tracks (which answers `answer`), a fake image host,
 * fake session storage, a fake alarm clock (`alarms`), and a clock the test moves (`now`).
 */
async function setup() {
  const fake = fakeBrowser(['https://suno.com/*', 'https://n8tracks.example.com/*']);
  const calls: Call[] = [];
  let answer = (call: Call): Response =>
    call.path.endsWith('/clips')
      ? jsonResponse(200, { outcome: 'completed', generation: { id: FIRST.id } })
      : jsonResponse(200, {});
  const fetchImpl = vi.fn<Fetch>((input, init) => {
    const path = input.slice(`${ADDRESS}/`.length);
    if (path === 'api/v1/extension/handshake') {
      return Promise.resolve(
        jsonResponse(200, {
          applicationVersion: '0.1.0',
          credentialName: 'Chrome',
          scopes: ['suno.generate'],
          compatible: true,
        }),
      );
    }
    const form = init.body instanceof FormData ? init.body.get('file') : null;
    const call: Call = {
      method: init.method ?? 'GET',
      path,
      body: typeof init.body === 'string' ? JSON.parse(init.body) : null,
      file: form instanceof File ? form : null,
    };
    calls.push(call);
    return Promise.resolve(answer(call));
  });
  const connection = new Connection({
    browser: fake.browser,
    versions: { extension: '0.1.0', adapter: '9' },
    fetch: fetchImpl,
  });
  expect((await connection.connect(`${ADDRESS}/`, TOKEN)).ok).toBe(true);
  calls.length = 0;

  const images = vi.fn<ImageFetch>(() =>
    Promise.resolve(
      new Response(new Uint8Array([0xff, 0xd8, 0xff, 0xe0]), {
        status: 200,
        headers: { 'Content-Type': 'image/jpeg' },
      }),
    ),
  );
  const session = new Map<string, unknown>();
  const alarms = new Map<string, number>();
  const clock = { now: START };
  const make = () =>
    new CompletionWatch({
      connection,
      browser: {
        session: {
          get: (keys: string[]) =>
            Promise.resolve(
              Object.fromEntries(
                keys.filter((key) => session.has(key)).map((key) => [key, session.get(key)]),
              ),
            ),
          set: (items: Record<string, unknown>) => {
            for (const [key, value] of Object.entries(items)) {
              session.set(key, structuredClone(value));
            }
            return Promise.resolve();
          },
        },
        alarms: {
          create: (name: string, info: { when: number }) => {
            alarms.set(name, info.when);
            return Promise.resolve();
          },
          clear: (name: string) => Promise.resolve(alarms.delete(name)),
        },
      },
      now: () => clock.now,
      fetchImage: images,
    });
  return {
    watch: make(),
    /** A service worker started afresh over the same session storage. */
    restarted: make,
    calls,
    images,
    session,
    alarms,
    clock,
    answer: (next: (call: Call) => Response) => {
      answer = next;
    },
  };
}

describe('The completion watch in the service worker (#154)', () => {
  it('sends a watched clip Suno shows finished to n8Tracks, then its cover through the Generation artwork upload', async () => {
    const { watch, calls, images, alarms } = await setup();
    await watch.watch(REQUEST, TAB, [FIRST, SECOND]);
    expect(alarms.get(COMPLETION_ALARM)).toBe(START + WATCH_LIMIT_MS);

    const answer = await watch.report(TAB, [finished('clip-1'), finished('clip-2', 'streaming')]);

    expect(answer).toEqual({ ok: true, watching: ['clip-2'] });
    expect(calls.map((call) => `${call.method} ${call.path}`)).toEqual([
      `POST api/v1/suno/generation-requests/${REQUEST}/clips`,
      `PUT api/v1/generations/${FIRST.id}/artwork`,
    ]);
    // The clip goes as Suno's feed returned it.
    expect(calls[0]?.body).toEqual({ clip: finished('clip-1') });
    expect(images).toHaveBeenCalledWith(
      'https://cdn2.suno.ai/image_large_00000000-0000-4000-8000-000000000003.jpeg',
      expect.objectContaining({ credentials: 'omit' }),
    );
    expect(calls[1]?.file?.type).toBe('image/jpeg');
  });

  it('sends nothing for a clip still generating, one it does not watch, or one of another tab', async () => {
    const { watch, calls } = await setup();
    await watch.watch(REQUEST, TAB, [FIRST]);

    await watch.report(TAB, [finished('clip-1', 'submitted'), finished('clip-9')]);
    await watch.report(TAB + 1, [finished('clip-1')]);

    expect(calls).toEqual([]);
    expect(await watch.watching(TAB)).toEqual(['clip-1']);
    expect(await watch.watching(TAB + 1)).toEqual([]);
  });

  it('records a clip that ended in error too, and stops watching a clip n8Tracks refuses', async () => {
    const { watch, calls, answer } = await setup();
    await watch.watch(REQUEST, TAB, [FIRST, SECOND]);
    answer((call) =>
      call.path.endsWith('/clips') && (call.body as { clip: { id: string } }).clip.id === 'clip-2'
        ? jsonResponse(409, { code: 'already_complete' })
        : jsonResponse(200, { outcome: 'failed' }),
    );

    const failed = finished('clip-1', 'error');
    delete failed.image_large_url;
    delete failed.image_url;
    const answered = await watch.report(TAB, [failed, finished('clip-2')]);

    expect(answered).toEqual({ ok: true, watching: [] });
    // No cover for the failed clip, which has none; none for the refused one.
    expect(calls.map((call) => call.path)).toEqual([
      `api/v1/suno/generation-requests/${REQUEST}/clips`,
      `api/v1/suno/generation-requests/${REQUEST}/clips`,
    ]);
  });

  it('keeps a clip watched when n8Tracks cannot take it now, and sends it at the next sighting', async () => {
    const { watch, calls, answer } = await setup();
    await watch.watch(REQUEST, TAB, [FIRST]);
    answer(() => jsonResponse(503, {}));

    expect(await watch.report(TAB, [finished('clip-1')])).toEqual({
      ok: true,
      watching: ['clip-1'],
    });

    answer((call) =>
      call.path.endsWith('/clips')
        ? jsonResponse(200, { outcome: 'completed' })
        : jsonResponse(200, {}),
    );
    expect(await watch.report(TAB, [finished('clip-1')])).toEqual({ ok: true, watching: [] });
    expect(calls.filter((call) => call.path.endsWith('/clips'))).toHaveLength(2);
  });

  it('stops at ten minutes: the alarm ends the watch, a stopped service worker included, and a late clip is not sent', async () => {
    const { watch, restarted, calls, session, alarms, clock } = await setup();
    await watch.watch(REQUEST, TAB, [FIRST]);
    clock.now = START + 5 * 60_000;
    await watch.watch(REQUEST, TAB, [SECOND]);
    // The alarm is set for the first watch to end.
    expect(alarms.get(COMPLETION_ALARM)).toBe(START + WATCH_LIMIT_MS);

    clock.now = START + WATCH_LIMIT_MS;
    await restarted().alarm(COMPLETION_ALARM);

    expect(await watch.watching(TAB)).toEqual(['clip-2']);
    expect(alarms.get(COMPLETION_ALARM)).toBe(START + 5 * 60_000 + WATCH_LIMIT_MS);

    clock.now = START + 5 * 60_000 + WATCH_LIMIT_MS;
    await restarted().alarm(COMPLETION_ALARM);
    expect(session.get(COMPLETION_KEY)).toEqual([]);
    expect(alarms.has(COMPLETION_ALARM)).toBe(false);
    expect(await watch.report(TAB, [finished('clip-1'), finished('clip-2')])).toEqual({
      ok: true,
      watching: [],
    });
    expect(calls).toEqual([]);
  });

  it('ends the watch of a tab that was closed', async () => {
    const { watch, alarms } = await setup();
    await watch.watch(REQUEST, TAB, [FIRST]);
    await watch.watch(REQUEST, TAB + 1, [SECOND]);

    await watch.tabRemoved(TAB);

    expect(await watch.watching(TAB)).toEqual([]);
    expect(await watch.watching(TAB + 1)).toEqual(['clip-2']);
    await watch.tabRemoved(TAB + 1);
    expect(alarms.has(COMPLETION_ALARM)).toBe(false);
  });
});
