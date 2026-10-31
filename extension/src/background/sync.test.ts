import { describe, expect, it, vi } from 'vitest';
import type { ImageFetch } from '../adapter/imageReader.ts';
import type { ExportPart, SyncProgress, SyncRequest } from '../messages.ts';
import { fakeBrowser, jsonResponse } from '../testing/fakeBrowser.ts';
import type { Fetch } from './apiClient.ts';
import { Connection } from './connection.ts';
import { CoverImages, IMAGES_KEY } from './images.ts';
import {
  LAST_EXPORT_KEY,
  PART_RETRIES,
  reviewAddress,
  SYNC_KEY,
  SyncCoordinator,
  type SyncBrowser,
} from './sync.ts';

const ADDRESS = 'https://n8tracks.example.com/base';
const TOKEN = 'n8t_0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefg';
const TAB = 7;
const EXPORT = '0f1e2d3c-0000-4000-8000-000000000001';

/** One call n8Tracks received: method and path after the address. */
interface Call {
  method: string;
  path: string;
  body: unknown;
}

/**
 * A paired connection over a fake browser, and a fake n8Tracks: the handshake answers with the
 * scopes given, and every other call is answered by `answer`, which the test may change.
 */
async function setup(scopes = ['suno.sync'], imageFetch?: ImageFetch) {
  const fake = fakeBrowser(['https://suno.com/*', 'https://n8tracks.example.com/*']);
  const calls: Call[] = [];
  let answer = (call: Call): Response => {
    if (call.path === 'api/v1/suno/exports') {
      return jsonResponse(201, { id: EXPORT, state: 'receiving' });
    }
    return jsonResponse(200, { id: EXPORT, state: 'ready' });
  };
  const fetchImpl = vi.fn<Fetch>((input, init) => {
    const path = input.slice(`${ADDRESS}/`.length);
    if (path === 'api/v1/extension/handshake') {
      return Promise.resolve(
        jsonResponse(200, {
          applicationVersion: '0.1.0',
          credentialName: 'Chrome',
          scopes,
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
    return Promise.resolve(answer(call));
  });
  const connection = new Connection({
    browser: fake.browser,
    versions: { extension: '0.1.0', adapter: '2' },
    fetch: fetchImpl,
  });
  expect((await connection.connect(`${ADDRESS}/`, TOKEN)).ok).toBe(true);

  let time = 42;
  const session = new Map<string, unknown>();
  const local = new Map<string, unknown>();
  const store = (map: Map<string, unknown>) => ({
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
  });
  const openTabs: { id?: number; url?: string }[] = [{ id: TAB, url: 'https://suno.com/me/trash' }];
  const tabs = {
    get: vi.fn((tabId: number) => Promise.resolve({ id: tabId, windowId: 3 })),
    query: vi.fn<SyncBrowser['tabs']['query']>(() => Promise.resolve(openTabs)),
    update: vi.fn<SyncBrowser['tabs']['update']>(() => Promise.resolve({})),
    create: vi.fn<SyncBrowser['tabs']['create']>(() => Promise.resolve({})),
  };
  const sync = new SyncCoordinator({
    connection,
    browser: { session: store(session), local: store(local), tabs },
    ...(imageFetch === undefined
      ? {}
      : {
          images: new CoverImages({
            connection,
            storage: store(local),
            fetch: imageFetch,
            sleep: () => Promise.resolve(),
          }),
        }),
    now: () => time,
  });
  const send = (request: SyncRequest, tabId = TAB) => sync.handle(request, tabId);
  return {
    sync,
    send,
    calls,
    session,
    local,
    tabs,
    openTabs,
    at: (value: number) => {
      time = value;
    },
    answer: (next: (call: Call) => Response) => {
      answer = next;
    },
  };
}

const PART: ExportPart = { partNumber: 1, clips: [{ id: 'c1' }], trashedClips: [], playlists: [] };

async function started(scopes?: string[]) {
  const context = await setup(scopes);
  expect(await context.send({ type: 'sync-begin', scope: { kind: 'library' } })).toMatchObject({
    ok: true,
  });
  return context;
}

async function created(scopes?: string[]) {
  const context = await started(scopes);
  expect(await context.send({ type: 'sync-create', header: { format: 'x' } })).toEqual({
    ok: true,
    exportId: EXPORT,
  });
  return context;
}

describe('starting a sync', () => {
  it('starts only when connected with suno.sync, holding the legs in session storage', async () => {
    const context = await setup();

    const answer = await context.send({ type: 'sync-begin', scope: { kind: 'library' } });

    expect(answer).toEqual({
      ok: true,
      session: {
        tabId: TAB,
        scope: { kind: 'library' },
        legs: [
          { list: 'workspaces' },
          { list: 'voices' },
          { list: 'playlists' },
          { list: 'library' },
          { list: 'trash' },
        ],
        leg: 0,
        attempt: 0,
        partNumber: 0,
        counts: { clips: 0, trashed: 0, workspaces: 0, playlists: 0, voices: 0 },
        workspaces: [],
        exportId: null,
        updatedAt: 42,
      },
    });
    expect(context.session.get(SYNC_KEY)).toMatchObject({ tabId: TAB, leg: 0 });
    // Nothing reaches n8Tracks's exports until the first part is ready.
    expect(context.calls).toEqual([]);
  });

  it('refuses without suno.sync', async () => {
    const context = await setup(['suno.generate']);

    expect(await context.send({ type: 'sync-begin', scope: { kind: 'library' } })).toEqual({
      ok: false,
      message: 'This credential lacks suno.sync.',
    });
    expect(context.session.size).toBe(0);
  });

  it('discards the export of a sync still receiving before starting another', async () => {
    const context = await created();

    await context.send({ type: 'sync-begin', scope: { kind: 'library' } }, 9);

    expect(context.calls.at(-1)).toEqual({
      method: 'POST',
      path: `api/v1/suno/exports/${EXPORT}/discard`,
      body: undefined,
    });
    expect(context.session.get(SYNC_KEY)).toMatchObject({ tabId: 9, exportId: null });
  });

  it('warns before replacing an export of this extension that is waiting for review', async () => {
    const context = await setup();
    expect(await context.send({ type: 'sync-preview' })).toEqual({ replacesReady: false });

    context.local.set(LAST_EXPORT_KEY, EXPORT);
    expect(await context.send({ type: 'sync-preview' })).toEqual({ replacesReady: true });

    context.answer(() => jsonResponse(200, { id: EXPORT, state: 'committed' }));
    expect(await context.send({ type: 'sync-preview' })).toEqual({ replacesReady: false });
  });
});

describe('carrying a sync between page loads', () => {
  it("carries a workspace or playlist sync's Trash choice in its scope and legs (#420)", async () => {
    const context = await setup();

    await context.send({
      type: 'sync-begin',
      scope: { kind: 'workspaces', ids: ['w1'], readTrash: false },
    });
    expect(await context.send({ type: 'sync-resume' })).toMatchObject({
      session: {
        scope: { kind: 'workspaces', ids: ['w1'], readTrash: false },
        // Each chosen workspace's own feed, not the whole library (#422).
        legs: [
          { list: 'workspaces' },
          { list: 'voices' },
          { list: 'playlists' },
          { list: 'workspace', id: 'w1' },
        ],
      },
    });

    const playlists = [{ id: 'p1', name: 'Morning' }];
    await context.send({
      type: 'sync-begin',
      scope: { kind: 'playlists', playlists, readTrash: true },
    });
    expect(await context.send({ type: 'sync-resume' })).toMatchObject({
      session: {
        scope: { kind: 'playlists', playlists, readTrash: true },
        legs: [
          { list: 'workspaces' },
          { list: 'voices' },
          { list: 'playlists' },
          { list: 'playlist', id: 'p1', name: 'Morning' },
          { list: 'trash' },
        ],
      },
    });
  });

  it('gives the sync back only to its own tab', async () => {
    const context = await started();

    expect(await context.send({ type: 'sync-resume' })).toMatchObject({
      session: { tabId: TAB },
    });
    expect(await context.send({ type: 'sync-resume' }, 8)).toEqual({ session: null });
  });

  it('saves the progress of a finished leg', async () => {
    const context = await started();
    const progress: SyncProgress = {
      leg: 1,
      attempt: 0,
      partNumber: 0,
      counts: { clips: 0, trashed: 0, workspaces: 2, playlists: 0 },
      workspaces: [{ id: 'w1' }, { id: 'w2' }],
    };

    expect(await context.send({ type: 'sync-save', progress })).toEqual({ ok: true });
    expect(await context.send({ type: 'sync-save', progress }, 8)).toMatchObject({ ok: false });

    expect(context.session.get(SYNC_KEY)).toMatchObject({ ...progress, tabId: TAB });
  });
});

describe('sending to n8Tracks', () => {
  it('creates the export once, remembering it, and forgets the workspace list it carried', async () => {
    const context = await created();

    expect(await context.send({ type: 'sync-create', header: { format: 'y' } })).toEqual({
      ok: true,
      exportId: EXPORT,
    });

    expect(context.calls).toEqual([
      { method: 'POST', path: 'api/v1/suno/exports', body: { format: 'x' } },
    ]);
    expect(context.local.get(LAST_EXPORT_KEY)).toBe(EXPORT);
    expect(context.session.get(SYNC_KEY)).toMatchObject({ exportId: EXPORT, workspaces: [] });
  });

  it('says what n8Tracks refused, by its code only', async () => {
    const context = await started();
    context.answer(() => jsonResponse(422, { code: 'invalid_export', errors: { scope: ['x'] } }));

    expect(await context.send({ type: 'sync-create', header: {} })).toEqual({
      ok: false,
      message: 'n8Tracks refused the export: 422 (invalid_export).',
    });
  });

  it('uploads a part to the export', async () => {
    const context = await created();

    expect(await context.send({ type: 'sync-part', part: PART })).toEqual({ ok: true });
    expect(context.calls.at(-1)).toEqual({
      method: 'POST',
      path: `api/v1/suno/exports/${EXPORT}/parts`,
      body: PART,
    });
  });

  it('sends a failed part again three times, then stops', async () => {
    const context = await created();
    context.answer(() => jsonResponse(503, { code: 'unavailable' }));

    expect(await context.send({ type: 'sync-part', part: PART })).toEqual({
      ok: false,
      message: 'n8Tracks refused part 1: 503 (unavailable).',
    });
    const parts = context.calls.filter((call) => call.path.endsWith('/parts'));
    expect(parts).toHaveLength(1 + PART_RETRIES);
  });

  it('succeeds when a part goes through on a retry', async () => {
    const context = await created();
    let failures = 2;
    context.answer(() =>
      failures-- > 0 ? jsonResponse(502, {}) : jsonResponse(200, { id: EXPORT }),
    );

    expect(await context.send({ type: 'sync-part', part: PART })).toEqual({ ok: true });
    expect(context.calls.filter((call) => call.path.endsWith('/parts'))).toHaveLength(3);
  });

  it('stops at once on a 401, without sending the part again', async () => {
    const context = await created();
    context.answer(() => jsonResponse(401, { code: 'invalid_token' }));

    expect(await context.send({ type: 'sync-part', part: PART })).toEqual({
      ok: false,
      message: 'The extension is not connected to n8Tracks; reconnect it in the options.',
    });
    expect(context.calls.filter((call) => call.path.endsWith('/parts'))).toHaveLength(1);
  });

  it('refuses a part, or completion, before there is an export', async () => {
    const context = await started();

    expect(await context.send({ type: 'sync-part', part: PART })).toMatchObject({ ok: false });
    expect(await context.send({ type: 'sync-complete' })).toMatchObject({ ok: false });
    expect(context.calls).toEqual([]);
  });
});

describe('finishing a sync', () => {
  it('completes the export, ends the sync, and opens the review in the n8Tracks tab of the window', async () => {
    const context = await created();
    context.openTabs.push({ id: 11, url: `${ADDRESS}/songs` });

    const answer = await context.send({ type: 'sync-complete' });

    const reviewUrl = `${ADDRESS}/suno/imports/${EXPORT}`;
    expect(answer).toEqual({ ok: true, reviewUrl });
    expect(reviewAddress(ADDRESS, EXPORT)).toBe(reviewUrl);
    expect(context.calls.at(-1)).toEqual({
      method: 'POST',
      path: `api/v1/suno/exports/${EXPORT}/complete`,
      body: undefined,
    });
    expect(context.tabs.query).toHaveBeenCalledWith({ windowId: 3 });
    expect(context.tabs.update).toHaveBeenCalledWith(11, { url: reviewUrl, active: true });
    expect(context.tabs.create).not.toHaveBeenCalled();
    expect(context.session.has(SYNC_KEY)).toBe(false);
  });

  it('opens the review in a new tab when no n8Tracks tab is open', async () => {
    const context = await created();
    context.openTabs.push({ id: 12, url: 'https://n8tracks.example.com.evil.test/' });

    await context.send({ type: 'sync-complete' });

    expect(context.tabs.update).not.toHaveBeenCalled();
    expect(context.tabs.create).toHaveBeenCalledWith({
      url: `${ADDRESS}/suno/imports/${EXPORT}`,
      active: true,
      windowId: 3,
    });
  });

  it('discards the export when n8Tracks will not complete it', async () => {
    const context = await created();
    context.answer((call) =>
      call.path.endsWith('/complete')
        ? jsonResponse(409, { code: 'import_in_progress' })
        : jsonResponse(200, {}),
    );

    expect(await context.send({ type: 'sync-complete' })).toEqual({
      ok: false,
      message: 'n8Tracks refused the finished export: 409 (import_in_progress).',
    });
    expect(context.calls.at(-1)?.path).toBe(`api/v1/suno/exports/${EXPORT}/discard`);
    expect(context.tabs.create).not.toHaveBeenCalled();
  });
});

describe('ending a sync without an export', () => {
  it('discards the export when the content script asks (cancel or failure)', async () => {
    const context = await created();

    expect(await context.send({ type: 'sync-discard' })).toEqual({ ok: true });

    expect(context.calls.at(-1)?.path).toBe(`api/v1/suno/exports/${EXPORT}/discard`);
    expect(context.session.has(SYNC_KEY)).toBe(false);
    // Complement: it was never completed.
    expect(context.calls.some((call) => call.path.endsWith('/complete'))).toBe(false);
  });

  it('discards the export when the Suno tab is closed, and not for another tab', async () => {
    const context = await created();

    await context.sync.tabRemoved(8);
    expect(context.session.has(SYNC_KEY)).toBe(true);

    await context.sync.tabRemoved(TAB);
    expect(context.calls.at(-1)?.path).toBe(`api/v1/suno/exports/${EXPORT}/discard`);
    expect(context.session.has(SYNC_KEY)).toBe(false);
  });

  it('discards the export when the Suno tab leaves suno.com, not when it moves within it', async () => {
    const context = await created();

    await context.sync.tabUpdated(TAB, 'https://suno.com/me');
    await context.sync.tabUpdated(TAB, undefined);
    expect(context.session.has(SYNC_KEY)).toBe(true);

    await context.sync.tabUpdated(TAB, 'https://example.com/');
    expect(context.calls.at(-1)?.path).toBe(`api/v1/suno/exports/${EXPORT}/discard`);
    expect(context.session.has(SYNC_KEY)).toBe(false);
  });
});

describe('cover images with a sync (#152)', () => {
  const cover = (id: string) => ({
    id,
    image_large_url: `https://cdn2.suno.ai/image_large_${id}.jpeg`,
  });

  async function withImages() {
    const reads = vi.fn<ImageFetch>(() =>
      Promise.resolve(
        new Response(new Uint8Array([1, 2, 3]), { headers: { 'Content-Type': 'image/png' } }),
      ),
    );
    const context = await setup(['suno.sync'], reads);
    expect(await context.send({ type: 'sync-begin', scope: { kind: 'library' } })).toMatchObject({
      ok: true,
    });
    await context.send({ type: 'sync-create', header: { format: 'x' } });
    return { ...context, reads };
  }

  it("notes each part's covers, and sends them once the export is complete, answering only its tab", async () => {
    const context = await withImages();
    await context.send({
      type: 'sync-part',
      part: {
        partNumber: 1,
        clips: [cover('a'), { id: 'b' }],
        trashedClips: [cover('t')],
        playlists: [],
      },
    });
    // Nothing is read or sent while the sync reads.
    expect(context.reads).not.toHaveBeenCalled();
    expect(await context.send({ type: 'sync-images' })).toMatchObject({
      images: { state: 'collecting', total: 2 },
    });

    expect(await context.send({ type: 'sync-complete' })).toMatchObject({ ok: true });

    await vi.waitFor(async () => {
      expect(await context.send({ type: 'sync-images' })).toEqual({
        images: {
          exportId: EXPORT,
          tabId: TAB,
          state: 'finished',
          total: 2,
          sent: 2,
          failed: 0,
          ignored: 0,
        },
      });
    });
    expect(
      context.calls
        .filter((call) => call.method === 'PUT')
        .map((call) => call.path)
        .toSorted(),
    ).toEqual([
      `api/v1/suno/exports/${EXPORT}/artwork/a`,
      `api/v1/suno/exports/${EXPORT}/artwork/t`,
    ]);
    expect(await context.send({ type: 'sync-images' }, TAB + 1)).toEqual({ images: null });
  });

  it('sends no image for a sync that was discarded', async () => {
    const context = await withImages();
    await context.send({
      type: 'sync-part',
      part: { partNumber: 1, clips: [cover('a')], trashedClips: [], playlists: [] },
    });

    await context.send({ type: 'sync-discard' });

    expect(context.local.has(IMAGES_KEY)).toBe(false);
    expect(context.reads).not.toHaveBeenCalled();
    expect(await context.send({ type: 'sync-images' })).toEqual({ images: null });
  });
});

describe("reporting the user's voices a sync read (#439)", () => {
  const VOICES = [
    { id: 'v1', name: 'Velvet Alto', description: '', is_trashed: false, is_owned: true },
    { id: 'v2', name: 'Gravel Baritone', description: 'Low', is_trashed: true, is_owned: false },
  ];

  it('sends them on their own, as read, and nothing for the export', async () => {
    const context = await started();
    context.answer(() => jsonResponse(200, { items: [], added: ['v1', 'v2'], renamed: [] }));

    const answer = await context.send({ type: 'sync-voices', complete: true, voices: VOICES });

    expect(answer).toEqual({ ok: true, added: 2 });
    expect(context.calls).toEqual([
      {
        method: 'PUT',
        path: 'api/v1/suno/personas/discovered',
        body: { complete: true, voices: VOICES },
      },
    ]);
    // The sync goes on as it was: no export was created for the voices.
    expect(await context.send({ type: 'sync-resume' })).toMatchObject({
      session: { exportId: null, leg: 0 },
    });
  });

  it('says why n8Tracks refused them, by its code only, and keeps the sync', async () => {
    const context = await started();
    context.answer(() => jsonResponse(403, { code: 'insufficient_scope', title: 'Velvet Alto' }));

    const answer = await context.send({ type: 'sync-voices', complete: false, voices: VOICES });

    expect(answer).toEqual({
      ok: false,
      message: 'n8Tracks refused your voices: 403 (insufficient_scope).',
    });
    expect(JSON.stringify(answer)).not.toContain('Velvet');
    expect(await context.send({ type: 'sync-resume' })).toMatchObject({ session: { tabId: TAB } });
  });

  it('takes them only from the tab running the sync', async () => {
    const context = await started();

    expect(
      await context.send({ type: 'sync-voices', complete: true, voices: VOICES }, TAB + 1),
    ).toEqual({ ok: false, message: 'This tab is not running a sync.' });
    expect(context.calls).toEqual([]);
  });
});

describe('the workspaces the sync panel offers (#448)', () => {
  /** n8Tracks' list, as `GET /api/v1/suno/workspaces` answers it (made-up names). */
  const LIST = {
    items: [
      { id: 'w-1', name: 'Harbour Lights', state: 'available', songCount: 3 },
      { id: 'w-2', name: '', state: 'available', songCount: 0 },
      { id: 'w-3', name: 'Old Sketches', state: 'unavailable', songCount: 1 },
    ],
  };

  it('asks n8Tracks for the workspaces it knows, and offers the Available ones', async () => {
    const context = await setup();
    context.answer(() => jsonResponse(200, LIST));

    expect(await context.send({ type: 'sync-workspaces-known' })).toEqual({
      ok: true,
      workspaces: [
        { id: 'w-1', name: 'Harbour Lights' },
        { id: 'w-2', name: '' },
      ],
      message: '',
    });
    expect(context.calls).toEqual([
      { method: 'GET', path: 'api/v1/suno/workspaces', body: undefined },
    ]);
  });

  it('says why n8Tracks refused the list, by its code only', async () => {
    const context = await setup();
    context.answer(() =>
      jsonResponse(403, { code: 'insufficient_scope', title: 'Harbour Lights' }),
    );

    const answer = await context.send({ type: 'sync-workspaces-known' });

    expect(answer).toEqual({
      ok: false,
      workspaces: [],
      message: 'n8Tracks refused the workspace list: 403 (insufficient_scope).',
    });
    expect(JSON.stringify(answer)).not.toContain('Harbour');
  });

  it('holds a Refresh from Suno for its tab until that tab next loads a page, once', async () => {
    const context = await setup();

    expect(await context.send({ type: 'sync-workspaces-refresh' })).toEqual({ ok: true });
    expect(context.calls).toEqual([]);

    // Another tab's page load is not it.
    expect(await context.send({ type: 'sync-resume' }, TAB + 1)).toEqual({ session: null });
    expect(await context.send({ type: 'sync-resume' })).toEqual({ session: null, refresh: true });
    // Said once: the next page load reads nothing.
    expect(await context.send({ type: 'sync-resume' })).toEqual({ session: null });
  });

  it('forgets a Refresh from Suno whose page load did not come within a minute', async () => {
    const context = await setup();
    expect(await context.send({ type: 'sync-workspaces-refresh' })).toEqual({ ok: true });

    context.at(42 + 60_001);

    expect(await context.send({ type: 'sync-resume' })).toEqual({ session: null });
  });

  it('refuses a Refresh from Suno while a sync runs, and a sync forgets one waiting', async () => {
    const running = await started();
    expect(await running.send({ type: 'sync-workspaces-refresh' })).toEqual({
      ok: false,
      message: 'A sync is running; refresh once it has finished.',
    });

    const waiting = await setup();
    expect(await waiting.send({ type: 'sync-workspaces-refresh' })).toEqual({ ok: true });
    await waiting.send({ type: 'sync-begin', scope: { kind: 'library' } });
    await waiting.send({ type: 'sync-discard' });
    expect(await waiting.send({ type: 'sync-resume' })).toEqual({ session: null });
  });

  it('refuses a Refresh from Suno without suno.sync', async () => {
    const context = await setup(['suno.generate']);

    expect(await context.send({ type: 'sync-workspaces-refresh' })).toEqual({
      ok: false,
      message: 'This credential lacks suno.sync.',
    });
    expect(await context.send({ type: 'sync-resume' })).toEqual({ session: null });
  });

  it('reports the list a Refresh read, complete or not, and offers the Available ones after it', async () => {
    const context = await setup();
    context.answer(() =>
      jsonResponse(200, {
        ...LIST,
        added: ['w-2'],
        renamed: [],
        becameUnavailable: [],
        becameAvailable: [],
      }),
    );
    const projects = [
      { id: 'w-1', name: 'Harbour Lights' },
      { id: 'w-2', name: '' },
    ];

    expect(
      await context.send({ type: 'sync-workspaces-report', complete: false, workspaces: projects }),
    ).toEqual({
      ok: true,
      workspaces: [
        { id: 'w-1', name: 'Harbour Lights' },
        { id: 'w-2', name: '' },
      ],
      message: '',
    });
    expect(context.calls).toEqual([
      {
        method: 'PUT',
        path: 'api/v1/suno/workspaces/discovered',
        body: { complete: false, workspaces: projects },
      },
    ]);
  });

  it('says why n8Tracks refused a report, by its code only', async () => {
    const context = await setup();
    context.answer(() => jsonResponse(422, { code: 'validation_failed', detail: 'Paper Moons' }));

    const answer = await context.send({
      type: 'sync-workspaces-report',
      complete: true,
      workspaces: [{ id: 'w-9', name: 'Paper Moons' }],
    });

    expect(answer).toEqual({
      ok: false,
      workspaces: [],
      message: 'n8Tracks refused the workspace list: 422 (validation_failed).',
    });
    expect(JSON.stringify(answer)).not.toContain('Paper');
  });
});

describe('the playlists the sync panel offers (#449)', () => {
  /** n8Tracks' slim list, as `GET /api/v1/suno/playlists/discovered` answers it (made-up names). */
  const LIST = {
    items: [
      { id: 'p-1', name: 'Harbour Lights', state: 'available', songCount: 24 },
      { id: 'p-2', name: 'Old Mixtape', state: 'unavailable', songCount: 3 },
    ],
  };

  it('asks n8Tracks for the playlists it knows, without their members, and offers the Available ones', async () => {
    const context = await setup();
    context.answer(() => jsonResponse(200, LIST));

    expect(await context.send({ type: 'sync-playlists-known' })).toEqual({
      ok: true,
      playlists: [{ id: 'p-1', name: 'Harbour Lights' }],
      message: '',
    });
    expect(context.calls).toEqual([
      { method: 'GET', path: 'api/v1/suno/playlists/discovered', body: undefined },
    ]);
  });

  it('holds a playlist Refresh from Suno for its tab, answered as refreshPlaylists once', async () => {
    const context = await setup();

    expect(await context.send({ type: 'sync-playlists-refresh' })).toEqual({ ok: true });
    expect(await context.send({ type: 'sync-resume' })).toEqual({
      session: null,
      refreshPlaylists: true,
    });
    expect(await context.send({ type: 'sync-resume' })).toEqual({ session: null });

    // The workspaces' refresh is still its own.
    expect(await context.send({ type: 'sync-workspaces-refresh' })).toEqual({ ok: true });
    expect(await context.send({ type: 'sync-resume' })).toEqual({ session: null, refresh: true });
  });

  it('reports a playlist list as read, and says why n8Tracks refused one by its code only', async () => {
    const context = await setup();
    context.answer(() =>
      jsonResponse(200, {
        ...LIST,
        added: ['p-1'],
        renamed: [],
        becameUnavailable: [],
        becameAvailable: [],
      }),
    );
    const playlists = [{ id: 'p-1', name: 'Harbour Lights', is_trashed: false, song_count: 24 }];

    expect(
      await context.send({ type: 'sync-playlists-report', complete: true, playlists }),
    ).toEqual({ ok: true, playlists: [{ id: 'p-1', name: 'Harbour Lights' }], message: '' });
    expect(context.calls).toEqual([
      {
        method: 'PUT',
        path: 'api/v1/suno/playlists/discovered',
        body: { complete: true, playlists },
      },
    ]);

    context.answer(() =>
      jsonResponse(403, { code: 'insufficient_scope', title: 'Harbour Lights' }),
    );
    const refused = await context.send({
      type: 'sync-playlists-report',
      complete: false,
      playlists,
    });
    expect(refused).toEqual({
      ok: false,
      playlists: [],
      message: 'n8Tracks refused your playlist list: 403 (insufficient_scope).',
    });
    expect(JSON.stringify(refused)).not.toContain('Harbour');
  });
});
