import { describe, expect, it, vi } from 'vitest';
import type { PageMessage } from '../messages.ts';
import { fakeBrowser, jsonResponse } from '../testing/fakeBrowser.ts';
import type { Fetch } from './apiClient.ts';
import { Connection } from './connection.ts';
import { GENERATION_KEY, GenerateCoordinator } from './generate.ts';
import { route } from './router.ts';

const ID = 'abcdefghijklmnopabcdefghijklmnop';
const ADDRESS = 'https://n8tracks.example.com/base';
const TOKEN = 'n8t_0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefg';
const REQUEST = '0199b1a0-7000-7000-9000-0000000000aa';
const RELAY = { id: ID, url: 'https://n8tracks.example.com/base/songs/n8-1/v/1', tab: { id: 3 } };
const CLAIM_PATH = `api/v1/suno/generation-requests/${REQUEST}/claim`;

interface Call {
  method: string;
  path: string;
  authorization: string | null;
}

/** A paired extension over a fake n8Tracks whose handshake gives `scopes` and `version`. */
async function setup(options: { scopes?: string[]; version?: string } = {}) {
  const fake = fakeBrowser(['https://suno.com/*', 'https://n8tracks.example.com/*']);
  const calls: Call[] = [];
  let claim = (): Response =>
    jsonResponse(200, { id: REQUEST, state: 'claimed', snapshot: { kind: 'song' } });
  const fetchImpl = vi.fn<Fetch>((input, init) => {
    const path = input.slice(`${ADDRESS}/`.length);
    calls.push({
      method: init.method ?? 'GET',
      path,
      authorization: new Headers(init.headers).get('Authorization'),
    });
    if (path === 'api/v1/extension/handshake') {
      return Promise.resolve(
        jsonResponse(200, {
          applicationVersion: options.version ?? '0.1.0',
          credentialName: 'Chrome',
          scopes: options.scopes ?? ['suno.generate'],
          compatible: (options.version ?? '0.1.0') === '0.1.0',
        }),
      );
    }
    return Promise.resolve(path === CLAIM_PATH ? claim() : jsonResponse(404, {}));
  });
  const connection = new Connection({
    browser: fake.browser,
    versions: { extension: '0.1.0', adapter: '3' },
    fetch: fetchImpl,
  });
  expect((await connection.connect(`${ADDRESS}/`, TOKEN)).ok).toBe(true);
  const session = fakeBrowser().browser.storage;
  const openOptions = vi.fn(() => Promise.resolve());
  const generate = new GenerateCoordinator({
    connection,
    browser: { session, openOptions },
    now: () => 1_000,
  });
  return {
    connection,
    generate,
    calls,
    session,
    openOptions,
    answerClaim: (answer: () => Response) => {
      claim = answer;
    },
  };
}

const message = (fields: { type: string } & Record<string, unknown>): PageMessage => ({
  source: 'n8tracks',
  ...fields,
});

describe('Generate on Suno in the service worker', () => {
  it('checks the connection afresh, claims the request with its own token, and keeps the claim', async () => {
    const { generate, calls, session } = await setup();
    const handshakes = calls.length;

    const reply = await generate.handle(
      message({ type: 'generate', requestId: REQUEST }),
      RELAY.url,
    );

    expect(reply).toEqual({ type: 'generate-accepted', requestId: REQUEST });
    const after = calls.slice(handshakes);
    expect(after.map((call) => `${call.method} ${call.path}`)).toEqual([
      'GET api/v1/extension/handshake',
      `POST ${CLAIM_PATH}`,
    ]);
    expect(after[1]?.authorization).toBe(`Bearer ${TOKEN}`);
    expect(await generate.current()).toEqual({ requestId: REQUEST, claimedAt: 1_000 });
    // Only the ID and the time are kept: the snapshot (lyrics, prompts) is read again when needed.
    expect(JSON.stringify(await session.get([GENERATION_KEY]))).not.toContain('snapshot');
  });

  it.each<[string, { scopes?: string[]; version?: string }, string]>([
    ['without suno.generate', { scopes: ['suno.sync'] }, 'no_scope'],
    ['on an incompatible version', { version: '0.2.0' }, 'incompatible'],
  ])('refuses %s and claims nothing', async (_name, options, error) => {
    const { generate, calls } = await setup(options);

    const reply = await generate.handle(
      message({ type: 'generate', requestId: REQUEST }),
      RELAY.url,
    );

    expect(reply).toMatchObject({ type: 'error', error });
    expect(calls.some((call) => call.path === CLAIM_PATH)).toBe(false);
    expect(await generate.current()).toBeNull();
  });

  it('refuses when disconnected, from another origin, or without a request ID', async () => {
    const { connection, generate, calls } = await setup();

    expect(
      await generate.handle(
        message({ type: 'generate', requestId: REQUEST }),
        'https://elsewhere.example/songs',
      ),
    ).toMatchObject({ error: 'wrong_origin' });
    expect(
      await generate.handle(message({ type: 'generate', requestId: 'n8-1-v1' }), RELAY.url),
    ).toMatchObject({ error: 'invalid_request' });

    await connection.disconnect();
    expect(
      await generate.handle(message({ type: 'generate', requestId: REQUEST }), RELAY.url),
    ).toMatchObject({ error: 'not_connected' });
    expect(calls.some((call) => call.path === CLAIM_PATH)).toBe(false);
  });

  it('passes on n8Tracks refusing the claim by its code only', async () => {
    const { generate, answerClaim } = await setup();
    answerClaim(() =>
      jsonResponse(409, { code: 'request_ended', title: 'secret lyrics never shown' }),
    );

    const reply = await generate.handle(
      message({ type: 'generate', requestId: REQUEST }),
      RELAY.url,
    );

    expect(reply).toEqual({
      type: 'error',
      error: 'refused',
      message: 'n8Tracks refused the claim: 409 (request_ended).',
    });
    expect(await generate.current()).toBeNull();
  });

  it('opens its options when the page asks, and leaves other page messages alone', async () => {
    const { generate, openOptions } = await setup();

    expect(await generate.handle(message({ type: 'open-options' }), RELAY.url)).toEqual({
      type: 'options-opened',
    });
    expect(openOptions).toHaveBeenCalledOnce();
    expect(await generate.handle(message({ type: 'export' }), RELAY.url)).toBeNull();
  });

  it('is reached through the router only from the relay, never from the Suno content script', async () => {
    const { connection, generate } = await setup();
    const relayed = { type: 'relay', message: message({ type: 'generate', requestId: REQUEST }) };

    expect(await route(connection, relayed, RELAY, ID, undefined, undefined, generate)).toEqual({
      type: 'generate-accepted',
      requestId: REQUEST,
    });

    const fromSuno = await route(
      connection,
      relayed,
      { id: ID, url: 'https://suno.com/create', tab: { id: 9 } },
      ID,
      undefined,
      undefined,
      generate,
    );
    expect(fromSuno).toMatchObject({ type: 'error', error: 'unknown_type' });

    // Complement: without the coordinator every page message is still unknown.
    expect(await route(connection, relayed, RELAY, ID)).toMatchObject({ error: 'unknown_type' });
  });
});
