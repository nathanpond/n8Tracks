import { describe, expect, it, vi } from 'vitest';
import { jsonResponse } from '../testing/fakeBrowser.ts';
import { callApi, handshake, type Fetch } from './apiClient.ts';

const TARGET = { address: 'https://n8tracks.example.com/base', token: 'n8t_secret-token' };
const VERSIONS = { extension: '0.1.0-rc.1', adapter: '1' };
const ANSWER = {
  applicationVersion: '0.1.0',
  credentialName: 'Chrome at home',
  scopes: ['suno.sync'],
  compatible: true,
};

function fetchAnswering(answer: () => Promise<Response>) {
  return vi.fn<Fetch>(() => answer());
}

describe('the handshake', () => {
  it('sends the token and the versions, and no cookies, to the address plus the path', async () => {
    const fetchImpl = fetchAnswering(() => Promise.resolve(jsonResponse(200, ANSWER)));

    expect(await handshake(TARGET, VERSIONS, fetchImpl)).toEqual({ kind: 'ok', handshake: ANSWER });

    const [url, init] = fetchImpl.mock.calls[0] ?? [];
    expect(url).toBe('https://n8tracks.example.com/base/api/v1/extension/handshake');
    const headers = new Headers(init?.headers);
    expect(headers.get('Authorization')).toBe('Bearer n8t_secret-token');
    expect(headers.get('X-N8Tracks-Extension-Version')).toBe('0.1.0-rc.1');
    expect(headers.get('X-N8Tracks-Adapter-Version')).toBe('1');
    expect(headers.has('Cookie')).toBe(false);
    expect(init?.credentials).toBe('omit');
  });

  it('is unreachable when nothing answers', async () => {
    const fetchImpl = fetchAnswering(() => Promise.reject(new TypeError('Failed to fetch')));

    expect(await handshake(TARGET, VERSIONS, fetchImpl)).toEqual({ kind: 'unreachable' });
  });

  it('is rejected on 401 invalid_token', async () => {
    const fetchImpl = fetchAnswering(() =>
      Promise.resolve(jsonResponse(401, { code: 'invalid_token' })),
    );

    expect(await handshake(TARGET, VERSIONS, fetchImpl)).toEqual({ kind: 'rejected' });
  });

  it.each([
    ['a web page', new Response('<html>Hello</html>', { status: 200 })],
    ['JSON without an application version', jsonResponse(200, { status: 'ok' })],
    ['a plain 404', new Response('Not found', { status: 404 })],
    ['a 401 from something else', new Response('Unauthorized', { status: 401 })],
  ])('is not n8Tracks for %s', async (_, response) => {
    const fetchImpl = fetchAnswering(() => Promise.resolve(response));

    expect(await handshake(TARGET, VERSIONS, fetchImpl)).toEqual({ kind: 'not-n8tracks' });
  });

  it('is a failure of n8Tracks for its own problem answer', async () => {
    const fetchImpl = fetchAnswering(() =>
      Promise.resolve(jsonResponse(503, { code: 'maintenance' })),
    );

    expect(await handshake(TARGET, VERSIONS, fetchImpl)).toEqual({ kind: 'failed', status: 503 });
  });
});

describe('callApi', () => {
  it('keeps the caller headers but always sets its own authorization', async () => {
    const fetchImpl = fetchAnswering(() => Promise.resolve(jsonResponse(200, {})));

    await callApi(
      TARGET,
      'api/v1/suno/exports',
      VERSIONS,
      { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'x' } },
      fetchImpl,
    );

    const init = fetchImpl.mock.calls[0]?.[1];
    const headers = new Headers(init?.headers);
    expect(init?.method).toBe('POST');
    expect(headers.get('Content-Type')).toBe('application/json');
    expect(headers.get('Authorization')).toBe('Bearer n8t_secret-token');
  });
});
