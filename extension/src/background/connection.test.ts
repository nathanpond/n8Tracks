import { describe, expect, it, vi } from 'vitest';
import { fakeBrowser, jsonResponse } from '../testing/fakeBrowser.ts';
import type { Fetch } from './apiClient.ts';
import {
  Connection,
  DisconnectedError,
  HANDSHAKE_CACHE_MS,
  OBSERVER_ID,
  RELAY_ID,
  SUNO_SCRIPT_ID,
} from './connection.ts';

const SUNO = 'https://suno.com/*';
const ADDRESS = 'https://n8tracks.example.com/base/';
const PATTERN = 'https://n8tracks.example.com/*';
const TOKEN = 'n8t_0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefg';
const VERSIONS = { extension: '0.1.0', adapter: '1' };

function handshakeAnswer(change: Record<string, unknown> = {}) {
  return {
    applicationVersion: '0.1.3',
    credentialName: 'Chrome at home',
    scopes: ['catalog.read', 'suno.sync', 'suno.generate'],
    compatible: true,
    ...change,
  };
}

/** A connection over a fake browser and a fake n8Tracks whose answers the test sets. */
function setup(granted: string[] = [SUNO, PATTERN]) {
  const fake = fakeBrowser(granted);
  let answer: () => Promise<Response> = () => Promise.resolve(jsonResponse(200, handshakeAnswer()));
  const fetchImpl = vi.fn<Fetch>(() => answer());
  let now = 1_000_000;
  const connection = new Connection({
    browser: fake.browser,
    versions: VERSIONS,
    fetch: fetchImpl,
    now: () => now,
  });
  return {
    ...fake,
    connection,
    fetchImpl,
    answer: (next: () => Promise<Response>) => {
      answer = next;
    },
    advance: (ms: number) => {
      now += ms;
    },
  };
}

async function paired() {
  const context = setup();
  const result = await context.connection.connect(ADDRESS, TOKEN);
  expect(result.ok).toBe(true);
  return context;
}

describe('connecting', () => {
  it('checks the token, then stores only the address and the token and registers the content scripts', async () => {
    const { connection, stored, scripts, fetchImpl } = setup();

    const result = await connection.connect(ADDRESS, `  ${TOKEN} `);

    expect(result).toEqual({
      ok: true,
      state: {
        status: 'connected',
        address: 'https://n8tracks.example.com/base',
        credentialName: 'Chrome at home',
        scopes: ['catalog.read', 'suno.sync', 'suno.generate'],
        applicationVersion: '0.1.3',
        compatibility: { kind: 'compatible' },
        features: [
          {
            feature: 'sync',
            label: 'Library sync',
            scope: 'suno.sync',
            available: true,
            reason: null,
          },
          {
            feature: 'generate',
            label: 'Generate on Suno',
            scope: 'suno.generate',
            available: true,
            reason: null,
          },
        ],
      },
    });
    expect(fetchImpl).toHaveBeenCalledTimes(1);
    expect([...stored.entries()]).toEqual([
      ['pairing', { address: 'https://n8tracks.example.com/base', token: TOKEN }],
    ]);
    expect([...scripts.values()]).toEqual([
      {
        id: RELAY_ID,
        matches: [PATTERN],
        js: ['relay.js'],
        runAt: 'document_start',
        allFrames: false,
        persistAcrossSessions: true,
      },
      {
        id: SUNO_SCRIPT_ID,
        matches: [SUNO],
        js: ['suno.js'],
        runAt: 'document_idle',
        allFrames: false,
        persistAcrossSessions: true,
      },
      {
        // The page observer runs in Suno's own page, before Suno's code first calls fetch.
        id: OBSERVER_ID,
        matches: [SUNO],
        js: ['observe.js'],
        runAt: 'document_start',
        world: 'MAIN',
        allFrames: false,
        persistAcrossSessions: true,
      },
    ]);
  });

  it('stays disconnected, storing nothing and calling nothing, when the permission was declined', async () => {
    const { connection, stored, fetchImpl, scripts } = setup([]);

    const result = await connection.connect(ADDRESS, TOKEN);

    expect(result).toMatchObject({ ok: false, failure: 'permission-declined' });
    expect(fetchImpl).not.toHaveBeenCalled();
    expect(stored.size).toBe(0);
    expect(scripts.size).toBe(0);
    expect(await connection.state()).toEqual({ status: 'not-paired' });
  });

  it.each([
    [
      'unreachable',
      () => Promise.reject(new TypeError('Failed to fetch')),
      'Cannot reach n8Tracks',
    ],
    [
      'not-n8tracks',
      () => Promise.resolve(new Response('<html></html>', { status: 200 })),
      'not an n8Tracks server',
    ],
    [
      'rejected',
      () => Promise.resolve(jsonResponse(401, { code: 'invalid_token' })),
      'n8Tracks rejected the token',
    ],
    [
      'no-suno-scope',
      () => Promise.resolve(jsonResponse(200, handshakeAnswer({ scopes: ['catalog.read'] }))),
      'neither suno.sync nor suno.generate',
    ],
    [
      'failed',
      () => Promise.resolve(jsonResponse(500, { code: 'internal_error' })),
      'did not answer as expected',
    ],
  ])(
    'reports %s in plain words, saves nothing, and gives back the new origin',
    async (failure, answer, words) => {
      const context = setup();
      context.answer(answer);

      const result = await context.connection.connect(ADDRESS, TOKEN);

      expect(result).toMatchObject({ ok: false, failure });
      expect(result.ok ? '' : result.message).toContain(words);
      expect(context.stored.size).toBe(0);
      expect(context.scripts.size).toBe(0);
      expect(context.browser.permissions.remove).toHaveBeenCalledWith({ origins: [PATTERN] });
      // Complement: suno.com stays granted for the next attempt.
      expect(context.origins.has(SUNO)).toBe(true);
    },
  );

  it.each([
    ['', TOKEN, 'invalid-address'],
    ['n8tracks.example.com', TOKEN, 'invalid-address'],
    [ADDRESS, '   ', 'missing-token'],
  ])('refuses %j with %j before asking n8Tracks', async (address, token, failure) => {
    const { connection, stored, fetchImpl } = setup();

    expect(await connection.connect(address, token)).toMatchObject({ ok: false, failure });
    expect(fetchImpl).not.toHaveBeenCalled();
    expect(stored.size).toBe(0);
  });

  it('accepts a credential with only one of the Suno scopes, showing the other feature disabled', async () => {
    const context = setup();
    context.answer(() =>
      Promise.resolve(jsonResponse(200, handshakeAnswer({ scopes: ['suno.generate'] }))),
    );

    const result = await context.connection.connect(ADDRESS, TOKEN);

    expect(result.ok && result.state.features).toEqual([
      {
        feature: 'sync',
        label: 'Library sync',
        scope: 'suno.sync',
        available: false,
        reason: 'This credential lacks suno.sync',
      },
      {
        feature: 'generate',
        label: 'Generate on Suno',
        scope: 'suno.generate',
        available: true,
        reason: null,
      },
    ]);
  });

  it('replacing a pairing with another origin gives back the old origin and moves the relay', async () => {
    const context = await paired();
    context.grant(['http://192.168.1.20:8080/*']);

    const result = await context.connection.connect('http://192.168.1.20:8080', 'n8t_other');

    expect(result.ok).toBe(true);
    expect(context.browser.permissions.remove).toHaveBeenCalledWith({ origins: [PATTERN] });
    expect(context.origins.has(PATTERN)).toBe(false);
    expect(context.scripts.get(RELAY_ID)?.matches).toEqual(['http://192.168.1.20:8080/*']);
    expect(context.stored.get('pairing')).toEqual({
      address: 'http://192.168.1.20:8080',
      token: 'n8t_other',
    });
  });

  it('a failed replacement on the same origin keeps the old pairing and its permission', async () => {
    const context = await paired();
    context.answer(() => Promise.resolve(jsonResponse(401, { code: 'invalid_token' })));

    const result = await context.connection.connect(ADDRESS, 'n8t_wrong');

    expect(result).toMatchObject({ ok: false, failure: 'rejected' });
    expect(context.origins.has(PATTERN)).toBe(true);
    expect(context.stored.get('pairing')).toEqual({
      address: 'https://n8tracks.example.com/base',
      token: TOKEN,
    });
  });
});

describe('disconnecting', () => {
  it('forgets the token, unregisters the relay, and gives back the n8Tracks permission', async () => {
    const context = await paired();

    expect(await context.connection.disconnect()).toEqual({ status: 'not-paired' });

    expect(context.stored.size).toBe(0);
    expect(context.scripts.size).toBe(0);
    expect(context.browser.permissions.remove).toHaveBeenCalledWith({ origins: [PATTERN] });
    expect(context.origins.has(PATTERN)).toBe(false);
    expect(await context.connection.state()).toEqual({ status: 'not-paired' });
  });
});

describe('the state', () => {
  it('reuses the handshake for a minute, and the check before work always asks again', async () => {
    const context = await paired();
    context.fetchImpl.mockClear();

    await context.connection.state();
    expect(context.fetchImpl).not.toHaveBeenCalled();

    await context.connection.state(true);
    expect(context.fetchImpl).toHaveBeenCalledTimes(1);

    context.advance(HANDSHAKE_CACHE_MS - 1);
    await context.connection.state();
    expect(context.fetchImpl).toHaveBeenCalledTimes(1);

    context.advance(2);
    await context.connection.state();
    expect(context.fetchImpl).toHaveBeenCalledTimes(2);
  });

  it('shows Disconnected and forgets the token when the next call finds it revoked', async () => {
    const context = await paired();
    context.answer(() => Promise.resolve(jsonResponse(401, { code: 'invalid_token' })));

    const state = await context.connection.state(true);

    expect(state).toEqual({ status: 'revoked', address: 'https://n8tracks.example.com/base' });
    expect(context.stored.get('pairing')).toEqual({ address: 'https://n8tracks.example.com/base' });
    // It stays so, without asking n8Tracks again: there is no token to ask with.
    context.fetchImpl.mockClear();
    expect(await context.connection.state(true)).toEqual(state);
    expect(context.fetchImpl).not.toHaveBeenCalled();
  });

  it('keeps the token when n8Tracks cannot be reached, and shows that instead of a mismatch', async () => {
    const context = await paired();
    context.answer(() => Promise.reject(new TypeError('Failed to fetch')));

    expect(await context.connection.state(true)).toEqual({
      status: 'unreachable',
      address: 'https://n8tracks.example.com/base',
    });
    expect(context.stored.get('pairing')).toMatchObject({ token: TOKEN });
  });

  it('shows a permission removed outside the extension, naming the origins to ask for again', async () => {
    const context = await paired();
    context.origins.delete(PATTERN);

    expect(await context.connection.state()).toEqual({
      status: 'permission-removed',
      address: 'https://n8tracks.example.com/base',
      origins: [SUNO, PATTERN],
    });
  });

  it('carries the version mismatch the handshake found', async () => {
    const context = setup();
    context.answer(() =>
      Promise.resolve(
        jsonResponse(200, handshakeAnswer({ applicationVersion: '0.2.0', compatible: false })),
      ),
    );

    const result = await context.connection.connect(ADDRESS, TOKEN);

    expect(result.ok && result.state.compatibility).toEqual({
      kind: 'mismatch',
      extensionVersion: '0.1.0',
      applicationVersion: '0.2.0',
      update: 'extension',
    });
  });

  it('registers the relay again at start when it is missing', async () => {
    const context = await paired();
    context.scripts.clear();

    await context.connection.start();

    expect(context.scripts.get(RELAY_ID)?.matches).toEqual([PATTERN]);
    expect(context.scripts.get(SUNO_SCRIPT_ID)?.matches).toEqual([SUNO]);
  });

  it('registers the Suno content script again when only it is missing', async () => {
    const context = await paired();
    context.scripts.delete(SUNO_SCRIPT_ID);

    await context.connection.state(true);

    expect([...context.scripts.keys()].toSorted()).toEqual(
      [RELAY_ID, SUNO_SCRIPT_ID, OBSERVER_ID].toSorted(),
    );
  });

  it('registers the page observer again when only it is missing', async () => {
    const context = await paired();
    context.scripts.delete(OBSERVER_ID);

    await context.connection.state(true);

    expect(context.scripts.get(OBSERVER_ID)).toMatchObject({ matches: [SUNO], world: 'MAIN' });
    expect(context.scripts.size).toBe(3);
  });

  it('never carries the token', async () => {
    const context = await paired();

    expect(JSON.stringify(await context.connection.state())).not.toContain(TOKEN);
  });
});

describe('a workflow call', () => {
  it('stops with DisconnectedError and forgets the token when n8Tracks refuses it', async () => {
    const context = await paired();
    context.answer(() => Promise.resolve(jsonResponse(401, { code: 'invalid_token' })));

    await expect(context.connection.call('api/v1/suno/exports')).rejects.toBeInstanceOf(
      DisconnectedError,
    );
    expect(context.stored.get('pairing')).toEqual({ address: 'https://n8tracks.example.com/base' });

    // The next call stops before reaching n8Tracks at all.
    context.fetchImpl.mockClear();
    await expect(context.connection.call('api/v1/suno/exports')).rejects.toBeInstanceOf(
      DisconnectedError,
    );
    expect(context.fetchImpl).not.toHaveBeenCalled();
  });

  it('passes any other answer back to the workflow', async () => {
    const context = await paired();
    context.answer(() => Promise.resolve(jsonResponse(403, { code: 'insufficient_scope' })));

    const response = await context.connection.call('api/v1/suno/exports');

    expect(response.status).toBe(403);
    expect(context.stored.get('pairing')).toMatchObject({ token: TOKEN });
  });
});
