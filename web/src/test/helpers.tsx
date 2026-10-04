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

/** Replaces `fetch` with a mock; each test says what it answers. */
export function stubFetch(): FetchMock {
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

export function renderApp() {
  return render(
    <MemoryRouter>
      <App />
    </MemoryRouter>,
  );
}
