import { describe, expect, it, vi } from 'vitest';
import type { PageMessage } from '../messages.ts';
import { fakeBrowser, jsonResponse } from '../testing/fakeBrowser.ts';
import type { Fetch } from './apiClient.ts';
import { CompletionWatch } from './completion.ts';
import { Connection } from './connection.ts';
import {
  GENERATION_TAB_KEY,
  GenerateCoordinator,
  SUNO_CREATE_ADDRESS,
  type GenerateTab,
} from './generate.ts';
import { route } from './router.ts';

const ID = 'abcdefghijklmnopabcdefghijklmnop';
const ADDRESS = 'https://n8tracks.example.com/base';
const TOKEN = 'n8t_0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefg';
const REQUEST = '0199b1a0-7000-7000-9000-0000000000aa';
const REQUEST_PATH = `api/v1/suno/generation-requests/${REQUEST}`;
const RELAY_TAB = 3;
const RELAY_URL = 'https://n8tracks.example.com/base/songs/n8-1/v/1';

interface Call {
  method: string;
  path: string;
  body: unknown;
}

const SNAPSHOT = {
  schemaVersion: 1,
  kind: 'song',
  mode: 'advanced',
  song: { title: 'Night Drive', shortcode: 'N8-1' },
  workspace: null,
  entries: [{ key: 'songs.advanced.lyrics', value: 'secret lyric line' }],
  sources: [
    {
      key: 'songs.advanced.audio',
      group: 'audio',
      position: 1,
      title: 'Origin',
      sunoAction: 'cover',
      target: { kind: 'generation', id: 'g-1', sunoId: 'clip-1' },
      availability: 'ok',
      continueAtSeconds: null,
    },
  ],
  fileInputs: [{ key: 'songs.advanced.audio', kind: 'audio', description: 'a demo' }],
  unsupported: [{ key: 'songs.advanced.crop', value: 1 }],
};

/**
 * A paired extension over a fake n8Tracks that holds one request, and a browser with the tabs
 * given (the n8Tracks tab is tab 3, in window 1).
 */
async function setup(tabs: GenerateTab[] = []) {
  const fake = fakeBrowser(['https://suno.com/*', 'https://n8tracks.example.com/*']);
  const calls: Call[] = [];
  const request = { active: true, message: null as string | null, snapshot: SNAPSHOT };
  let patch = (): Response => jsonResponse(200, { state: 'workspace' });
  let observedCreate = (): Response =>
    jsonResponse(200, {
      id: REQUEST,
      state: 'waiting',
      message: '2 Generations recorded on n8-1-v1.',
      observed: [{ outcome: 'attached' }],
    });
  let discovered = (): Response =>
    jsonResponse(200, {
      items: [
        { id: 'w-1', name: 'Night Drive', songCount: 0 },
        { id: 'w-2', name: 'Studio', songCount: 3 },
      ],
    });
  const fetchImpl = vi.fn<Fetch>((input, init) => {
    const path = input.slice(`${ADDRESS}/`.length);
    const method = init.method ?? 'GET';
    calls.push({
      method,
      path,
      body: typeof init.body === 'string' ? (JSON.parse(init.body) as unknown) : null,
    });
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
    if (path === `${REQUEST_PATH}/claim`) {
      return Promise.resolve(jsonResponse(200, { id: REQUEST, state: 'claimed' }));
    }
    if (path === REQUEST_PATH && method === 'GET') {
      return Promise.resolve(jsonResponse(200, { id: REQUEST, ...request }));
    }
    if (path === REQUEST_PATH && method === 'PATCH') {
      return Promise.resolve(patch());
    }
    if (path === 'api/v1/suno/workspaces/discovered') {
      return Promise.resolve(discovered());
    }
    if (path === `${REQUEST_PATH}/observed-create` && method === 'POST') {
      return Promise.resolve(observedCreate());
    }
    if (path === `${REQUEST_PATH}/clips` && method === 'POST') {
      return Promise.resolve(jsonResponse(200, { outcome: 'completed' }));
    }
    return Promise.resolve(jsonResponse(404, {}));
  });
  const connection = new Connection({
    browser: fake.browser,
    versions: { extension: '0.1.0', adapter: '4' },
    fetch: fetchImpl,
  });
  expect((await connection.connect(`${ADDRESS}/`, TOKEN)).ok).toBe(true);
  const storage = fakeBrowser();
  const browserTabs = {
    get: vi.fn((tabId: number) =>
      Promise.resolve(
        tabId === RELAY_TAB
          ? { id: RELAY_TAB, windowId: 1, index: 4, url: RELAY_URL }
          : { id: tabId, windowId: 1 },
      ),
    ),
    query: vi.fn((query: { windowId?: number }) =>
      Promise.resolve(tabs.filter((tab) => tab.windowId === query.windowId)),
    ),
    update: vi.fn(() => Promise.resolve({})),
    create: vi.fn(() => Promise.resolve({ id: 42 })),
  };
  const alarms = new Map<string, number>();
  const completion = new CompletionWatch({
    connection,
    browser: {
      session: storage.browser.storage,
      alarms: {
        create: (name: string, info: { when: number }) =>
          Promise.resolve(alarms.set(name, info.when)),
        clear: (name: string) => Promise.resolve(alarms.delete(name)),
      },
    },
  });
  const generate = new GenerateCoordinator({
    connection,
    browser: {
      session: storage.browser.storage,
      openOptions: () => Promise.resolve(),
      tabs: browserTabs,
    },
    completion,
  });
  const handOff = async () =>
    generate.handle(
      { source: 'n8tracks', type: 'generate', requestId: REQUEST } satisfies PageMessage,
      RELAY_URL,
      RELAY_TAB,
    );
  return {
    connection,
    generate,
    calls,
    request,
    tabs: browserTabs,
    stored: storage.stored,
    handOff,
    since: (count: number) => calls.slice(count).map((call) => `${call.method} ${call.path}`),
    answerPatch: (answer: () => Response) => {
      patch = answer;
    },
    answerDiscovered: (answer: () => Response) => {
      discovered = answer;
    },
    answerObserved: (answer: () => Response) => {
      observedCreate = answer;
    },
  };
}

describe('Generate on Suno: the Suno tab', () => {
  it('reuses the most recently active Suno tab in the n8Tracks tab’s window, on Create, after reporting opening', async () => {
    const { handOff, tabs, calls, stored } = await setup([
      { id: 10, windowId: 1, url: 'https://suno.com/me', lastAccessed: 100 },
      { id: 11, windowId: 1, url: 'https://suno.com/song/abc', lastAccessed: 300 },
      { id: 12, windowId: 2, url: 'https://suno.com/create', lastAccessed: 900 },
      { id: 13, windowId: 1, url: 'https://example.com/', lastAccessed: 999 },
    ]);

    expect(await handOff()).toEqual({ type: 'generate-accepted', requestId: REQUEST });

    expect(tabs.update).toHaveBeenCalledWith(11, { url: SUNO_CREATE_ADDRESS, active: true });
    expect(tabs.create).not.toHaveBeenCalled();
    expect(SUNO_CREATE_ADDRESS).toBe('https://suno.com/create');
    const opening = calls.find((call) => call.method === 'PATCH');
    expect(opening?.body).toEqual({ state: 'opening', step: 'open Suno', message: null });
    expect(stored.get(GENERATION_TAB_KEY)).toEqual({
      requestId: REQUEST,
      tabId: 11,
      loads: 0,
      chosen: null,
      created: 0,
      source: null,
    });
  });

  it('opens a new tab beside the n8Tracks tab when its window has no Suno tab', async () => {
    const { handOff, tabs, stored } = await setup([
      { id: 12, windowId: 2, url: 'https://suno.com/create', lastAccessed: 900 },
    ]);

    await handOff();

    expect(tabs.update).not.toHaveBeenCalled();
    expect(tabs.create).toHaveBeenCalledWith({
      url: SUNO_CREATE_ADDRESS,
      active: true,
      windowId: 1,
      index: 5,
    });
    expect(stored.get(GENERATION_TAB_KEY)).toMatchObject({ tabId: 42 });
  });

  it('stops the request, saying so, when no tab can be had', async () => {
    const { handOff, tabs, calls } = await setup();
    tabs.create.mockRejectedValueOnce(new Error('no window'));

    await handOff();

    expect(calls.filter((call) => call.method === 'PATCH').map((call) => call.body)).toEqual([
      { state: 'opening', step: 'open Suno', message: null },
      {
        state: 'stopped',
        step: 'open Suno',
        message: 'The extension could not open a Suno tab. Try again.',
      },
    ]);
  });

  it('gives the tab its job on each page load while the request is active, and nothing to another tab', async () => {
    const { handOff, generate, request } = await setup();
    await handOff();

    expect(await generate.handleTab({ type: 'generate-resume' }, 7)).toEqual({ job: null });
    const first = await generate.handleTab({ type: 'generate-resume' }, 42);
    expect(first).toEqual({
      job: {
        requestId: REQUEST,
        songTitle: 'Night Drive',
        workspace: null,
        loads: 1,
        // What the tab fills the form from (#146): the snapshot's values, by entry.
        form: {
          kind: 'song',
          mode: 'advanced',
          entries: { 'songs.advanced.lyrics': 'secret lyric line' },
          // Each source with its Suno ID, place, and availability (#148).
          sources: [
            {
              key: 'songs.advanced.audio',
              title: 'Origin',
              sunoAction: 'cover',
              group: 'audio',
              position: 1,
              sunoId: 'clip-1',
              availability: 'ok',
              continueAtSeconds: null,
            },
          ],
          fileInputs: [{ key: 'songs.advanced.audio', description: 'a demo' }],
          unsupported: ['songs.advanced.crop'],
        },
        created: 0,
        source: null,
      },
    });
    expect(await generate.handleTab({ type: 'generate-resume' }, 42)).toMatchObject({
      job: { loads: 2 },
    });

    // Once the request is no longer active (cancelled in n8Tracks), the tab is forgotten.
    request.active = false;
    expect(await generate.handleTab({ type: 'generate-resume' }, 42)).toEqual({ job: null });
    request.active = true;
    expect(await generate.handleTab({ type: 'generate-resume' }, 42)).toEqual({ job: null });
  });

  it('reads the request before each step and reports it; stops the tab when it has ended', async () => {
    const { handOff, generate, request, calls, since, stored } = await setup();
    await handOff();
    const before = calls.length;

    expect(
      await generate.handleTab(
        { type: 'generate-progress', state: 'workspace', step: 'check sign-in' },
        42,
      ),
    ).toEqual({ ok: true });
    expect(since(before)).toEqual([`GET ${REQUEST_PATH}`, `PATCH ${REQUEST_PATH}`]);
    expect(calls.at(-1)?.body).toEqual({
      state: 'workspace',
      step: 'check sign-in',
      message: null,
    });

    request.active = false;
    request.message = 'Cancelled in n8Tracks.';
    const after = calls.length;
    expect(
      await generate.handleTab(
        { type: 'generate-progress', state: 'workspace', step: 'select workspace' },
        42,
      ),
    ).toEqual({ ok: false, ended: true, message: 'Cancelled in n8Tracks.' });
    // Complement: nothing was reported for a request that is over.
    expect(since(after)).toEqual([`GET ${REQUEST_PATH}`]);
    expect(stored.has(GENERATION_TAB_KEY)).toBe(false);
  });

  it('refuses the steps from a tab that is not the generation’s', async () => {
    const { handOff, generate, calls } = await setup();
    await handOff();
    const before = calls.length;

    expect(
      await generate.handleTab(
        { type: 'generate-progress', state: 'workspace', step: 'check sign-in' },
        7,
      ),
    ).toEqual({ ok: false, ended: true, message: 'This tab is not generating anything.' });
    expect(calls.length).toBe(before);
  });

  it('reports Suno’s workspace list as complete and answers n8Tracks’ Song counts', async () => {
    const { handOff, generate, calls, answerDiscovered } = await setup();
    await handOff();
    const workspaces = [{ id: 'w-1', name: 'Night Drive' }, { id: 'w-2' }];

    expect(await generate.handleTab({ type: 'generate-workspaces', workspaces }, 42)).toEqual({
      ok: true,
      songCounts: { 'w-1': 0, 'w-2': 3 },
    });
    expect(calls.at(-1)).toEqual({
      method: 'PUT',
      path: 'api/v1/suno/workspaces/discovered',
      body: { complete: true, workspaces },
    });

    answerDiscovered(() => jsonResponse(403, { code: 'insufficient_scope' }));
    expect(await generate.handleTab({ type: 'generate-workspaces', workspaces }, 42)).toEqual({
      ok: false,
      ended: false,
      message: 'n8Tracks refused the workspace list: 403 (insufficient_scope).',
    });
  });

  it('records the chosen workspace, sending the report again when it fails on the way, and later loads use it', async () => {
    const { handOff, generate, calls, answerPatch } = await setup();
    await handOff();
    let failures = 2;
    answerPatch(() => {
      failures -= 1;
      return failures >= 0 ? jsonResponse(503, {}) : jsonResponse(200, { state: 'workspace' });
    });
    const chosen = { sunoId: 'w-9', name: 'Night Drive', how: 'created' } as const;

    expect(await generate.handleTab({ type: 'generate-resolve', workspace: chosen }, 42)).toEqual({
      ok: true,
    });
    const reports = calls.filter((call) => call.method === 'PATCH').slice(1);
    expect(reports).toHaveLength(3);
    expect(reports[2]?.body).toEqual({
      state: 'workspace',
      step: 'workspace chosen',
      message: null,
      resolvedWorkspace: chosen,
    });
    expect(await generate.handleTab({ type: 'generate-resume' }, 42)).toMatchObject({
      job: { workspace: { sunoId: 'w-9', name: 'Night Drive', state: 'available' } },
    });
  });

  it('keeps where loading the source has got to for the next page load, counting loads afresh (#148)', async () => {
    const { handOff, generate, stored } = await setup();
    await handOff();
    await generate.handleTab({ type: 'generate-resume' }, 42);
    await generate.handleTab({ type: 'generate-resume' }, 42);

    const opening = { phase: 'opening', sunoId: 'clip-1' } as const;
    expect(await generate.handleTab({ type: 'generate-source', source: opening }, 42)).toEqual({
      ok: true,
    });
    expect(stored.get(GENERATION_TAB_KEY)).toMatchObject({ loads: 0, source: opening });
    expect(await generate.handleTab({ type: 'generate-resume' }, 42)).toMatchObject({
      job: { loads: 1, source: opening },
    });

    // #341: the user loading the source by hand is kept the same way, across the action's loads.
    const byHand = { phase: 'byHand', sunoId: 'clip-1' } as const;
    await generate.handleTab({ type: 'generate-source', source: byHand }, 42);
    expect(await generate.handleTab({ type: 'generate-resume' }, 42)).toMatchObject({
      job: { source: byHand },
    });

    await generate.handleTab({ type: 'generate-source', source: null }, 42);
    expect(await generate.handleTab({ type: 'generate-resume' }, 42)).toMatchObject({
      job: { source: null },
    });
    // Only the generation's own tab may record it.
    expect(await generate.handleTab({ type: 'generate-source', source: opening }, 7)).toEqual({
      ok: false,
      ended: true,
      message: 'This tab is not generating anything.',
    });
  });

  it('does not send a refused choice again, and says why', async () => {
    const { handOff, generate, calls, answerPatch } = await setup();
    await handOff();
    answerPatch(() => jsonResponse(409, { code: 'workspace_already_set' }));
    const before = calls.length;

    expect(
      await generate.handleTab(
        { type: 'generate-resolve', workspace: { sunoId: 'w-2', name: 'Studio', how: 'picked' } },
        42,
      ),
    ).toEqual({
      ok: false,
      ended: false,
      message: 'n8Tracks refused the report: 409 (workspace_already_set).',
    });
    expect(calls.slice(before).filter((call) => call.method === 'PATCH')).toHaveLength(1);
  });

  it('sends the user’s Create to n8Tracks and counts it, so a later load of the tab only watches (#149)', async () => {
    const { handOff, generate, calls, stored } = await setup();
    await handOff();
    const response = { id: 'suno-request', clips: [{ id: 'clip-1' }] };
    const submitted = { mv: 'chirp-goose', metadata: { vocal_gender: 'f' } };

    const answer = await generate.handleTab({ type: 'generate-observed', response, submitted }, 42);

    expect(answer).toEqual({
      ok: true,
      recorded: { outcome: 'attached', message: '2 Generations recorded on n8-1-v1.' },
    });
    const sent = calls.filter((call) => call.path.endsWith('/observed-create'));
    expect(sent).toEqual([
      {
        method: 'POST',
        path: `${REQUEST_PATH}/observed-create`,
        body: { response, request: submitted },
      },
    ]);
    expect(stored.get(GENERATION_TAB_KEY)).toMatchObject({ created: 1 });
    expect(await generate.handleTab({ type: 'generate-resume' }, 42)).toMatchObject({
      job: { created: 1 },
    });
    // Another tab sends nothing.
    expect(
      await generate.handleTab({ type: 'generate-observed', response, submitted: null }, 7),
    ).toMatchObject({ ok: false, ended: true });
    expect(calls.filter((call) => call.path.endsWith('/observed-create'))).toHaveLength(1);
  });

  it('watches the Generations of a recorded Create for completion, past the request, in that tab only (#154)', async () => {
    const { handOff, generate, calls, answerObserved } = await setup();
    await handOff();
    answerObserved(() =>
      jsonResponse(200, {
        id: REQUEST,
        state: 'waiting',
        message: '2 Generations recorded on n8-1-v1.',
        observed: [
          {
            outcome: 'attached',
            generations: [
              { id: 'g-1', shortcode: 'n8-1-v1-g1', sunoId: 'clip-1' },
              { id: 'g-2', shortcode: 'n8-1-v1-g2', sunoId: 'clip-2' },
            ],
          },
        ],
      }),
    );
    const response = { id: 'suno-request', clips: [{ id: 'clip-1' }, { id: 'clip-2' }] };
    await generate.handleTab({ type: 'generate-observed', response, submitted: null }, 42);

    expect(await generate.handleTab({ type: 'generate-resume' }, 42)).toMatchObject({
      watching: ['clip-1', 'clip-2'],
    });
    expect(await generate.handleTab({ type: 'generate-resume' }, 7)).toEqual({ job: null });

    const finished = { id: 'clip-1', status: 'complete' };
    expect(
      await generate.handleTab({ type: 'generate-completion', clips: [finished] }, 42),
    ).toEqual({ ok: true, watching: ['clip-2'] });
    expect(calls.filter((call) => call.path.endsWith('/clips'))).toEqual([
      { method: 'POST', path: `${REQUEST_PATH}/clips`, body: { clip: finished } },
    ]);
    // Another tab's report sends nothing.
    expect(
      await generate.handleTab(
        { type: 'generate-completion', clips: [{ id: 'clip-2', status: 'complete' }] },
        7,
      ),
    ).toEqual({ ok: true, watching: [] });
    expect(calls.filter((call) => call.path.endsWith('/clips'))).toHaveLength(1);
  });

  it('sends a Create again while n8Tracks cannot answer, and says when it was not recorded', async () => {
    const { handOff, generate, calls, answerObserved, stored } = await setup();
    await handOff();
    let failures = 2;
    answerObserved(() =>
      failures-- > 0
        ? jsonResponse(503, {})
        : jsonResponse(200, { message: 'recorded', observed: [{ outcome: 'branched' }] }),
    );
    const response = { id: 'suno-request', clips: [{ id: 'clip-1' }] };

    expect(
      await generate.handleTab({ type: 'generate-observed', response, submitted: null }, 42),
    ).toEqual({ ok: true, recorded: { outcome: 'branched', message: 'recorded' } });
    expect(calls.filter((call) => call.path.endsWith('/observed-create'))).toHaveLength(3);

    // A refusal is not sent again; one that ends the request forgets the tab.
    answerObserved(() => jsonResponse(409, { code: 'request_ended' }));
    const refused = await generate.handleTab(
      { type: 'generate-observed', response, submitted: null },
      42,
    );
    expect(refused).toMatchObject({ ok: false, ended: true });
    const words = JSON.stringify(refused);
    expect(words).toContain('were not recorded');
    expect(words).toContain('request_ended');
    expect(calls.filter((call) => call.path.endsWith('/observed-create'))).toHaveLength(4);
    expect(stored.get(GENERATION_TAB_KEY) ?? null).toBeNull();
  });

  it('stops the request when its Suno tab is closed', async () => {
    const { handOff, generate, calls, stored } = await setup();
    await handOff();

    await generate.tabRemoved(7);
    expect(stored.has(GENERATION_TAB_KEY)).toBe(true);
    await generate.tabRemoved(42);

    expect(calls.at(-1)?.body).toEqual({
      state: 'stopped',
      step: null,
      message: 'The Suno tab was closed.',
    });
    expect(stored.has(GENERATION_TAB_KEY)).toBe(false);
  });

  it('takes the generate messages only from the Suno content script in a tab', async () => {
    const { handOff, generate, connection } = await setup();
    await handOff();
    const resume = { type: 'generate-resume' };
    const onSuno = { id: ID, url: 'https://suno.com/create', tab: { id: 42 } };

    expect(
      await route(connection, resume, onSuno, ID, undefined, undefined, generate),
    ).toMatchObject({ job: { requestId: REQUEST } });
    for (const sender of [
      { id: ID, url: RELAY_URL, tab: { id: RELAY_TAB } },
      { id: ID, url: `chrome-extension://${ID}/popup.html` },
      { id: ID, url: 'https://suno.com/create' },
    ]) {
      expect(
        await route(connection, resume, sender, ID, undefined, undefined, generate),
      ).toHaveProperty('refused');
    }
    // A malformed step is not a request at all.
    expect(
      await route(
        connection,
        { type: 'generate-progress', state: 'generating', step: 'x' },
        onSuno,
        ID,
        undefined,
        undefined,
        generate,
      ),
    ).toHaveProperty('refused');
    expect(
      await route(
        connection,
        { type: 'generate-resolve', workspace: { sunoId: 'w', name: 'n', how: 'renamed' } },
        onSuno,
        ID,
        undefined,
        undefined,
        generate,
      ),
    ).toHaveProperty('refused');
  });
});
