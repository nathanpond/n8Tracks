import { useCallback, useEffect, useState } from 'react';
import { resolveAppUrl } from './baseUrl';

export const SESSION_TIMEOUT_MS = 10_000;

const SESSION_PATH = 'api/v1/session';
const SESSIONS_PATH = 'api/v1/sessions';

/**
 * The anti-forgery header: the API refuses a state-changing request authenticated by the session
 * cookie (and sign-in) without it. A cross-site form cannot set a custom header.
 */
export const ANTIFORGERY_HEADER = 'X-N8Tracks-Request';

/** Who is signed in, and when the session ends unless it is used again (UTC, ISO 8601). */
export interface Session {
  username: string;
  expiresAt: string;
}

/** How a sign-in attempt ended, by the API's answer. */
export type SignInResult =
  | { kind: 'signedIn'; session: Session }
  | { kind: 'invalidCredentials' }
  | { kind: 'throttled'; message: string }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'failed' };

export type SessionState =
  | { phase: 'loading' }
  | { phase: 'error' }
  | { phase: 'signedOut' }
  | { phase: 'signedIn'; session: Session };

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

export function isSession(value: unknown): value is Session {
  return (
    isRecord(value) && typeof value.username === 'string' && typeof value.expiresAt === 'string'
  );
}

function isErrorMap(value: unknown): value is Record<string, string[]> {
  return (
    isRecord(value) &&
    Object.values(value).every(
      (messages) => Array.isArray(messages) && messages.every((m) => typeof m === 'string'),
    )
  );
}

/** Runs a request that gives up after the timeout or when `signal` aborts. */
async function withTimeout<T>(
  signal: AbortSignal | undefined,
  run: (signal: AbortSignal) => Promise<T>,
): Promise<T> {
  const controller = new AbortController();
  const abort = () => {
    controller.abort();
  };
  const timeout = setTimeout(abort, SESSION_TIMEOUT_MS);
  signal?.addEventListener('abort', abort);
  try {
    return await run(controller.signal);
  } finally {
    clearTimeout(timeout);
    signal?.removeEventListener('abort', abort);
  }
}

/** Reads the current session: null when there is none (401). Anything else unexpected rejects. */
export function fetchSession(signal: AbortSignal): Promise<Session | null> {
  return withTimeout(signal, async (bounded) => {
    const response = await fetch(resolveAppUrl(SESSION_PATH), {
      headers: { Accept: 'application/json' },
      cache: 'no-store',
      signal: bounded,
    });
    if (response.status === 401) {
      return null;
    }
    if (!response.ok) {
      throw new Error(`Session request failed with status ${String(response.status)}.`);
    }

    const body: unknown = await response.json();
    if (!isSession(body)) {
      throw new Error('Session response is not a session.');
    }

    return body;
  });
}

async function problemBody(response: Response): Promise<Record<string, unknown>> {
  try {
    const body: unknown = await response.json();
    return isRecord(body) ? body : {};
  } catch {
    return {};
  }
}

/** Signs in. Never rejects: a network failure or an unknown answer is `failed`. */
export async function signIn(username: string, password: string): Promise<SignInResult> {
  try {
    return await withTimeout(undefined, async (bounded) => {
      const response = await fetch(resolveAppUrl(SESSION_PATH), {
        method: 'POST',
        headers: {
          Accept: 'application/json',
          'Content-Type': 'application/json',
          [ANTIFORGERY_HEADER]: '1',
        },
        body: JSON.stringify({ username, password }),
        signal: bounded,
      });
      if (response.status === 201) {
        const body: unknown = await response.json();
        return isSession(body) ? { kind: 'signedIn', session: body } : { kind: 'failed' };
      }

      const problem = await problemBody(response);
      if (response.status === 401 && problem.code === 'invalid_credentials') {
        return { kind: 'invalidCredentials' };
      }
      if (
        response.status === 429 &&
        problem.code === 'sign_in_throttled' &&
        typeof problem.detail === 'string'
      ) {
        return { kind: 'throttled', message: problem.detail };
      }
      if (
        response.status === 422 &&
        problem.code === 'validation_failed' &&
        isErrorMap(problem.errors)
      ) {
        return { kind: 'invalid', errors: problem.errors };
      }
      return { kind: 'failed' };
    });
  } catch {
    return { kind: 'failed' };
  }
}

/**
 * Ends the current session (`everywhere`: every session of the administrator). True when no
 * session is left in this browser: the API ended it, or there was none (401).
 */
export async function signOut(everywhere: boolean): Promise<boolean> {
  try {
    return await withTimeout(undefined, async (bounded) => {
      const response = await fetch(resolveAppUrl(everywhere ? SESSIONS_PATH : SESSION_PATH), {
        method: 'DELETE',
        headers: { Accept: 'application/json', [ANTIFORGERY_HEADER]: '1' },
        signal: bounded,
      });
      return response.status === 204 || response.status === 401;
    });
  } catch {
    return false;
  }
}

/**
 * Loads the current session once, and again on `refresh`. `signedIn` and `signedOut` record what
 * this browser just did, without asking the backend again.
 */
export function useSession(): {
  state: SessionState;
  refresh: () => void;
  signedIn: (session: Session) => void;
  signedOut: () => void;
} {
  const [state, setState] = useState<SessionState>({ phase: 'loading' });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();

    fetchSession(controller.signal).then(
      (session) => {
        if (!controller.signal.aborted) {
          setState(session ? { phase: 'signedIn', session } : { phase: 'signedOut' });
        }
      },
      () => {
        if (!controller.signal.aborted) {
          setState({ phase: 'error' });
        }
      },
    );

    return () => {
      controller.abort();
    };
  }, [attempt]);

  const refresh = useCallback(() => {
    setState((previous) => (previous.phase === 'error' ? { phase: 'loading' } : previous));
    setAttempt((current) => current + 1);
  }, []);

  const signedIn = useCallback((session: Session) => {
    setState({ phase: 'signedIn', session });
  }, []);

  const signedOut = useCallback(() => {
    setState({ phase: 'signedOut' });
  }, []);

  return { state, refresh, signedIn, signedOut };
}
