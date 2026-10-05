import { afterEach, describe, expect, it, vi } from 'vitest';
import { jsonResponse, requestPath, stubAllFetch } from '../test/helpers';
import { apiFetch, setSessionExpiredHandler } from './client';

function problem(status: number, code: string): Response {
  return new Response(JSON.stringify({ status, code, title: 'refused' }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

let removeHandler: (() => void) | undefined;

afterEach(() => {
  removeHandler?.();
  removeHandler = undefined;
});

/** A handler the test answers by hand, counting how often it was asked. */
function manualHandler() {
  let answer: (signedIn: boolean) => void = () => undefined;
  const handler = vi.fn(
    () =>
      new Promise<boolean>((resolve) => {
        answer = resolve;
      }),
  );
  removeHandler = setSessionExpiredHandler(handler);
  return {
    handler,
    answer: (signedIn: boolean) => {
      answer(signedIn);
    },
  };
}

describe('the shared fetch helper', () => {
  it('asks for JSON, and adds the anti-forgery header to unsafe methods only', async () => {
    const mock = stubAllFetch();
    mock.mockImplementation(() => Promise.resolve(jsonResponse(200, {})));

    await apiFetch('api/v1/thing');
    await apiFetch('api/v1/thing', { method: 'PUT', body: '{}' });

    const [get, put] = mock.mock.calls.map(([, init]) => new Headers(init?.headers));
    expect(get?.get('Accept')).toBe('application/json');
    expect(get?.has('X-N8Tracks-Request')).toBe(false);
    expect(put?.get('X-N8Tracks-Request')).toBe('1');
    expect(requestPath(mock.mock.calls[0]?.[0] ?? '')).toBe('/api/v1/thing');
  });

  it('holds every request that meets a session-expiry 401 behind one prompt, and replays each after sign-in', async () => {
    let signedIn = false;
    const mock = stubAllFetch();
    mock.mockImplementation((input) =>
      Promise.resolve(
        signedIn
          ? jsonResponse(200, { path: requestPath(input) })
          : problem(401, 'not_authenticated'),
      ),
    );
    const { handler, answer } = manualHandler();

    const first = apiFetch('api/v1/first', { method: 'POST', body: '{"a":1}' });
    const second = apiFetch('api/v1/second');
    await vi.waitFor(() => {
      expect(handler).toHaveBeenCalled();
    });
    // Both requests are held by now: each read its 401 before either is answered.
    await new Promise((resolve) => setTimeout(resolve, 20));

    signedIn = true;
    answer(true);

    expect(await (await first).json()).toEqual({ path: '/api/v1/first' });
    expect(await (await second).json()).toEqual({ path: '/api/v1/second' });
    expect(handler).toHaveBeenCalledTimes(1);
    expect(mock).toHaveBeenCalledTimes(4);
    const replayed = mock.mock.calls.find(
      ([input], index) => index >= 2 && requestPath(input) === '/api/v1/first',
    );
    expect(replayed?.[1]?.body).toBe('{"a":1}');
  });

  it('returns sign-in’s own invalid_credentials as it is, without the prompt', async () => {
    stubAllFetch().mockResolvedValue(problem(401, 'invalid_credentials'));
    const { handler } = manualHandler();

    const response = await apiFetch('api/v1/session', { method: 'POST', body: '{}' });

    expect(response.status).toBe(401);
    expect(handler).not.toHaveBeenCalled();
  });

  it('returns the 401 without replaying when the user gives up', async () => {
    const mock = stubAllFetch();
    mock.mockImplementation(() => Promise.resolve(problem(401, 'not_authenticated')));
    const { handler, answer } = manualHandler();

    const pending = apiFetch('api/v1/thing');
    await vi.waitFor(() => {
      expect(handler).toHaveBeenCalled();
    });
    answer(false);

    expect((await pending).status).toBe(401);
    expect(mock).toHaveBeenCalledTimes(1);
  });

  it('returns the 401 as it is when nothing is signed in to prompt for', async () => {
    stubAllFetch().mockResolvedValue(problem(401, 'not_authenticated'));

    expect((await apiFetch('api/v1/thing')).status).toBe(401);
  });

  it('stops waiting for the prompt when the caller aborts', async () => {
    stubAllFetch().mockImplementation(() => Promise.resolve(problem(401, 'not_authenticated')));
    manualHandler();
    const controller = new AbortController();

    const pending = apiFetch('api/v1/thing', { signal: controller.signal });
    await new Promise((resolve) => setTimeout(resolve, 0));
    controller.abort();

    await expect(pending).rejects.toThrow(/aborted/);
  });
});
