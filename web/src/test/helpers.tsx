import { render } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { vi } from 'vitest';
import { App } from '../App';
import type { HealthReport } from '../api/health';

export const healthyReport: HealthReport & { timeZone: string } = {
  status: 'healthy',
  version: '0.1.0',
  timeZone: 'UTC',
  components: {
    application: { status: 'healthy', detail: 'running' },
    database: { status: 'healthy', detail: 'reachable' },
    migrations: { status: 'healthy', detail: 'up to date' },
    media: { status: 'healthy', detail: 'available' },
  },
};

export const degradedReport = {
  ...healthyReport,
  status: 'degraded',
  components: {
    ...healthyReport.components,
    media: { status: 'degraded', detail: 'unavailable' },
  },
};

export const unhealthyReport = {
  ...healthyReport,
  status: 'unhealthy',
  components: {
    ...healthyReport.components,
    database: { status: 'unhealthy', detail: 'unreachable' },
  },
};

export function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

type FetchMock = ReturnType<typeof vi.fn<typeof fetch>>;

export const completeSetup = { complete: true };

export const incompleteSetup = {
  complete: false,
  storage: { writable: true },
  media: { available: true },
};

/** The path a request was made to, whatever form `fetch` was given it in. */
export function requestPath(input: RequestInfo | URL): string {
  const url = input instanceof Request ? input.url : input.toString();
  return new URL(url, document.baseURI).pathname;
}

export function isSetupStatusRequest(input: RequestInfo | URL): boolean {
  return requestPath(input).endsWith('/api/v1/setup/status');
}

export const signedInSession = { username: 'owner', expiresAt: '2026-11-04T09:00:00Z' };

/** Whether the request reads the current session (a GET of `api/v1/session`). */
export function isSessionRequest(input: RequestInfo | URL, init?: RequestInit): boolean {
  return (
    requestPath(input).endsWith('/api/v1/session') &&
    (init?.method ?? 'GET').toUpperCase() === 'GET'
  );
}

/**
 * Replaces `fetch` with a mock; each test says what it answers. The setup status is answered
 * "complete" and the session "signed in" outside the mock, so tests of the app's pages see neither
 * the wizard nor the sign-in page, nor those requests.
 */
export function stubFetch(): FetchMock {
  const mock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
    if (isSetupStatusRequest(input)) {
      return Promise.resolve(jsonResponse(200, completeSetup));
    }
    if (isSessionRequest(input, init)) {
      return Promise.resolve(jsonResponse(200, signedInSession));
    }
    return mock(input, init);
  });
  return mock;
}

/** Replaces `fetch` with a mock that answers every request, the setup status included. */
export function stubAllFetch(): FetchMock {
  const mock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', mock);
  return mock;
}

/** A request that never answers, but fails as the real one does when its signal aborts. */
export function neverAnswers(_input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  return new Promise((_resolve, reject) => {
    init?.signal?.addEventListener('abort', () => {
      reject(new DOMException('The operation was aborted.', 'AbortError'));
    });
  });
}

export function setVisibility(state: DocumentVisibilityState): void {
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
  document.dispatchEvent(new Event('visibilitychange'));
}

export function renderApp(path = '/') {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <App />
    </MemoryRouter>,
  );
}
