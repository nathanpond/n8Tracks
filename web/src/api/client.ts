import { resolveAppUrl } from './baseUrl';
import { noticeMaintenance } from './maintenance';
import { ANTIFORGERY_HEADER } from './session';

/** How long one attempt of a request may take. Waiting for the user to sign in again is not counted. */
export const API_TIMEOUT_MS = 10_000;

/** The code of the API's 401 for a request without a (valid) session. */
export const NOT_AUTHENTICATED_CODE = 'not_authenticated';

/**
 * Asked when a request finds the session has ended. Resolves true once the user has signed in
 * again (the request is then sent again), false when they gave up (the 401 is returned as it is).
 */
export type SessionExpiredHandler = () => Promise<boolean>;

let sessionExpiredHandler: SessionExpiredHandler | null = null;

/** The one question in progress: every request that meets a 401 meanwhile waits on the same answer. */
let pendingReauth: Promise<boolean> | null = null;

/**
 * Registers what to do when the session ends under a request: the signed-in app shows the sign-in
 * prompt. Returns the function that removes it again. Without a handler a 401 is returned as it is.
 */
export function setSessionExpiredHandler(handler: SessionExpiredHandler): () => void {
  sessionExpiredHandler = handler;
  return () => {
    if (sessionExpiredHandler === handler) {
      sessionExpiredHandler = null;
    }
  };
}

function isUnsafe(method: string | undefined): boolean {
  return !['GET', 'HEAD', 'OPTIONS', 'TRACE'].includes((method ?? 'GET').toUpperCase());
}

function abortError(): DOMException {
  return new DOMException('The operation was aborted.', 'AbortError');
}

/** One attempt: the request with its own timeout, given up early when `signal` aborts. */
async function attempt(
  path: string,
  init: RequestInit,
  signal: AbortSignal | undefined,
): Promise<Response> {
  const controller = new AbortController();
  const abort = () => {
    controller.abort();
  };
  const timeout = setTimeout(abort, API_TIMEOUT_MS);
  signal?.addEventListener('abort', abort);
  try {
    return await fetch(resolveAppUrl(path), { ...init, signal: controller.signal });
  } finally {
    clearTimeout(timeout);
    signal?.removeEventListener('abort', abort);
  }
}

/** Whether a response is the API saying the request had no session: not sign-in's own 401. */
async function isSessionEnded(response: Response): Promise<boolean> {
  if (response.status !== 401) {
    return false;
  }
  try {
    const body: unknown = await response.clone().json();
    return (
      typeof body === 'object' &&
      body !== null &&
      'code' in body &&
      body.code === NOT_AUTHENTICATED_CODE
    );
  } catch {
    return false;
  }
}

/** Waits for the user's answer, shared by every request held meanwhile; rejects if `signal` aborts first. */
function waitForReauth(handler: SessionExpiredHandler, signal: AbortSignal | undefined) {
  pendingReauth ??= handler().finally(() => {
    pendingReauth = null;
  });
  const answer = pendingReauth;
  if (!signal) {
    return answer;
  }

  return new Promise<boolean>((resolve, reject) => {
    if (signal.aborted) {
      reject(abortError());
      return;
    }
    const onAbort = () => {
      reject(abortError());
    };
    signal.addEventListener('abort', onAbort, { once: true });
    answer.then(resolve, reject).finally(() => {
      signal.removeEventListener('abort', onAbort);
    });
  });
}

/**
 * The shared fetch helper for the signed-in app's API calls. `path` is relative to the app's base
 * (`api/v1/...`). JSON is asked for, and an unsafe method carries the anti-forgery header. Each
 * attempt gives up after {@link API_TIMEOUT_MS}, or when `init.signal` aborts.
 *
 * The one 401 interceptor: when the API answers 401 `not_authenticated` (the session has ended),
 * the request is held, the registered handler shows the sign-in prompt, and once the user has
 * signed in again the request is sent again and its answer returned, so the page that made it
 * carries on as it was. Requests that meet a 401 while the prompt is open wait for the same
 * sign-in. Any other 401 (sign-in's own `invalid_credentials`) is returned as it is.
 *
 * A 503 `maintenance` is returned as it is, and also reported, so the app shows the maintenance page.
 */
export async function apiFetch(path: string, init: RequestInit = {}): Promise<Response> {
  const { signal, headers, ...rest } = init;
  const merged = new Headers(headers);
  if (!merged.has('Accept')) {
    merged.set('Accept', 'application/json');
  }
  if (isUnsafe(rest.method)) {
    merged.set(ANTIFORGERY_HEADER, '1');
  }
  const request: RequestInit = { ...rest, headers: merged };

  const response = await attempt(path, request, signal ?? undefined);
  await noticeMaintenance(response);
  const handler = sessionExpiredHandler;
  if (!handler || !(await isSessionEnded(response))) {
    return response;
  }

  if (!(await waitForReauth(handler, signal ?? undefined))) {
    return response;
  }
  return attempt(path, request, signal ?? undefined);
}
