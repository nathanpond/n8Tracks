import { describe, expect, it, vi } from 'vitest';
import { fakeBrowser, jsonResponse } from '../testing/fakeBrowser.ts';
import type { Fetch } from './apiClient.ts';
import { Connection } from './connection.ts';
import {
  CLIP_LOOKUP_PATH,
  DOWNLOAD_FORMATS_KEY,
  DOWNLOAD_LOAD_KEY,
  DownloadCoordinator,
  LOAD_WAIT_MS,
  LOOKUP_BATCH,
} from './download.ts';

const ADDRESS = 'https://n8tracks.example.com';
const TOKEN = 'n8t_0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefg';
const TAB = 7;

interface Call {
  method: string;
  path: string;
  body: unknown;
}

function store() {
  const map = new Map<string, unknown>();
  return {
    map,
    get: (keys: string[]) =>
      Promise.resolve(
        Object.fromEntries(keys.filter((key) => map.has(key)).map((key) => [key, map.get(key)])),
      ),
    set: (items: Record<string, unknown>) => {
      for (const [key, value] of Object.entries(items)) {
        map.set(key, structuredClone(value));
      }
      return Promise.resolve();
    },
    remove: (keys: string[]) => {
      for (const key of keys) {
        map.delete(key);
      }
      return Promise.resolve();
    },
  };
}

function row(sunoId: string) {
  return { sunoId, generation: null, artist: null, deleted: false, downloadedFormats: [] };
}

/** A coordinator over a fake browser and a fake n8Tracks, paired unless `paired` is false. */
async function setup(
  options: { scopes?: string[]; paired?: boolean; busy?: string | null; answer?: Fetch } = {},
) {
  const fake = fakeBrowser(['https://suno.com/*', 'https://n8tracks.example.com/*']);
  const calls: Call[] = [];
  const fetchImpl = vi.fn<Fetch>((input, init) => {
    const path = input.slice(`${ADDRESS}/`.length);
    if (path === 'api/v1/extension/handshake') {
      return Promise.resolve(
        jsonResponse(200, {
          applicationVersion: '0.1.0',
          credentialName: 'Chrome',
          scopes: options.scopes ?? ['suno.sync'],
          compatible: true,
        }),
      );
    }
    const call = {
      method: init.method ?? 'GET',
      path,
      body: typeof init.body === 'string' ? (JSON.parse(init.body) as unknown) : undefined,
    };
    calls.push(call);
    if (options.answer !== undefined) {
      return options.answer(input, init);
    }
    const ids = (call.body as { sunoIds: string[] }).sunoIds;
    return Promise.resolve(jsonResponse(200, { items: ids.map(row) }));
  });
  const connection = new Connection({
    browser: fake.browser,
    versions: { extension: '0.1.0', adapter: '11' },
    fetch: fetchImpl,
  });
  if (options.paired !== false) {
    expect((await connection.connect(`${ADDRESS}/`, TOKEN)).ok).toBe(true);
  }
  const session = store();
  const local = store();
  let now = 1_000;
  const coordinator = new DownloadCoordinator({
    connection,
    browser: { session, local },
    busy: () => Promise.resolve(options.busy ?? null),
    now: () => now,
  });
  return {
    coordinator,
    calls,
    session,
    local,
    advance: (ms: number) => {
      now += ms;
    },
  };
}

describe('Load library across its page load', () => {
  it('keeps the selection for this tab, hands it over once, and to no other tab', async () => {
    const { coordinator, session } = await setup();

    expect(await coordinator.handle({ type: 'download-begin', selected: ['a', 'b'] }, TAB)).toEqual(
      { ok: true },
    );
    expect(session.map.get(DOWNLOAD_LOAD_KEY)).toMatchObject({ tabId: TAB, selected: ['a', 'b'] });

    expect(await coordinator.handle({ type: 'download-resume' }, 8)).toEqual({ load: null });
    expect(await coordinator.handle({ type: 'download-resume' }, TAB)).toEqual({
      load: { selected: ['a', 'b'] },
    });
    // Taken once: the next page load reads nothing.
    expect(await coordinator.handle({ type: 'download-resume' }, TAB)).toEqual({ load: null });
  });

  it('forgets a Load library whose page load did not come in time', async () => {
    const { coordinator, advance } = await setup();
    await coordinator.handle({ type: 'download-begin', selected: [] }, TAB);

    advance(LOAD_WAIT_MS + 1);

    expect(await coordinator.handle({ type: 'download-resume' }, TAB)).toEqual({ load: null });
  });

  it('is refused, saying why, while a sync or Generate on Suno runs', async () => {
    const { coordinator, session } = await setup({ busy: 'A sync to n8Tracks is running.' });

    expect(await coordinator.handle({ type: 'download-begin', selected: [] }, TAB)).toEqual({
      ok: false,
      message: 'A sync to n8Tracks is running.',
    });
    expect(session.map.has(DOWNLOAD_LOAD_KEY)).toBe(false);
  });

  it('works when the extension is not connected to n8Tracks', async () => {
    const { coordinator } = await setup({ paired: false });

    expect(await coordinator.handle({ type: 'download-begin', selected: [] }, TAB)).toEqual({
      ok: true,
    });
  });
});

describe('the formats last chosen', () => {
  it('are none the first time, then the ones remembered, keeping only known formats', async () => {
    const { coordinator, local } = await setup();

    expect(await coordinator.handle({ type: 'download-formats' }, TAB)).toEqual({ formats: [] });
    expect(
      await coordinator.handle(
        { type: 'download-formats', formats: ['wav', 'flac', 'm4a-stream', 'wav'] },
        TAB,
      ),
    ).toEqual({ formats: ['wav', 'm4a-stream'] });
    expect(local.map.get(DOWNLOAD_FORMATS_KEY)).toEqual(['wav', 'm4a-stream']);
    expect(await coordinator.handle({ type: 'download-formats' }, TAB)).toEqual({
      formats: ['wav', 'm4a-stream'],
    });
  });
});

describe('the clip lookup', () => {
  it('asks n8Tracks in batches of 500 distinct Suno IDs, and answers every row', async () => {
    const { coordinator, calls } = await setup();
    const ids = Array.from({ length: LOOKUP_BATCH + 20 }, (_, i) => `clip-${String(i)}`);

    const answer = await coordinator.handle(
      { type: 'download-lookup', sunoIds: [...ids, 'clip-0'] },
      TAB,
    );

    expect(calls.map((call) => [call.method, call.path])).toEqual([
      ['POST', CLIP_LOOKUP_PATH],
      ['POST', CLIP_LOOKUP_PATH],
    ]);
    expect((calls[0]?.body as { sunoIds: string[] }).sunoIds).toHaveLength(LOOKUP_BATCH);
    expect((calls[1]?.body as { sunoIds: string[] }).sunoIds).toHaveLength(20);
    expect(answer).toMatchObject({ ok: true });
    expect((answer as { rows: unknown[] }).rows).toHaveLength(ids.length);
  });

  it('keeps only the members the view reads', async () => {
    const { coordinator } = await setup({
      answer: () =>
        Promise.resolve(
          jsonResponse(200, {
            items: [
              {
                ...row('a'),
                generation: { id: 'g', shortcode: 'n8-1-v1-g1', extra: 'x' },
                artist: 'Art',
                secret: 'never',
              },
            ],
          }),
        ),
    });

    expect(await coordinator.handle({ type: 'download-lookup', sunoIds: ['a'] }, TAB)).toEqual({
      ok: true,
      rows: [
        {
          sunoId: 'a',
          generation: { id: 'g', shortcode: 'n8-1-v1-g1' },
          artist: 'Art',
          deleted: false,
          downloadedFormats: [],
        },
      ],
    });
  });

  it('is unavailable, saying why, when not connected or without suno.sync, and calls nothing', async () => {
    const notPaired = await setup({ paired: false });
    const noScope = await setup({ scopes: ['suno.generate'] });

    expect(
      await notPaired.coordinator.handle({ type: 'download-lookup', sunoIds: ['a'] }, TAB),
    ).toEqual({
      ok: false,
      unavailable: true,
      message: 'The extension is not connected to n8Tracks.',
    });
    expect(
      await noScope.coordinator.handle({ type: 'download-lookup', sunoIds: ['a'] }, TAB),
    ).toEqual({ ok: false, unavailable: true, message: 'This credential lacks suno.sync.' });
    expect(notPaired.calls).toEqual([]);
    expect(noScope.calls).toEqual([]);
  });

  it('fails, to be tried again, when n8Tracks answers badly or cannot be reached', async () => {
    const refused = await setup({ answer: () => Promise.resolve(jsonResponse(500, {})) });
    const unreachable = await setup({ answer: () => Promise.reject(new Error('offline')) });

    expect(
      await refused.coordinator.handle({ type: 'download-lookup', sunoIds: ['a'] }, TAB),
    ).toEqual({
      ok: false,
      unavailable: false,
      message: 'n8Tracks did not answer the lookup (500).',
    });
    expect(
      await unreachable.coordinator.handle({ type: 'download-lookup', sunoIds: ['a'] }, TAB),
    ).toEqual({
      ok: false,
      unavailable: false,
      message: 'Cannot reach n8Tracks. Check that it is running.',
    });
  });
});
