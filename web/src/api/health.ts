import { useCallback, useEffect, useState } from 'react';
import { resolveAppUrl } from './baseUrl';

export const HEALTH_REFRESH_MS = 30_000;
export const HEALTH_TIMEOUT_MS = 10_000;

export interface HealthComponent {
  status: string;
  detail?: string;
  /** Migrations only: `succeeded` when this start applied migrations, `none` when it found none pending. */
  lastOutcome?: string;
  /** Migrations only: when the newest safety backup was made (UTC ISO 8601), or null when there is none. */
  lastSafetyBackupAt?: string | null;
}

export interface HealthReport {
  status: string;
  version: string;
  /** The configured time zone (the `TZ` setting). */
  timeZone?: string;
  components: Record<string, HealthComponent>;
}

export type HealthState =
  | { phase: 'loading' }
  | { phase: 'error' }
  | { phase: 'ready'; report: HealthReport; stale: boolean };

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isHealthComponent(value: unknown): value is HealthComponent {
  return (
    isRecord(value) &&
    typeof value.status === 'string' &&
    (value.detail === undefined || typeof value.detail === 'string') &&
    (value.lastOutcome === undefined || typeof value.lastOutcome === 'string') &&
    (value.lastSafetyBackupAt === undefined ||
      value.lastSafetyBackupAt === null ||
      typeof value.lastSafetyBackupAt === 'string')
  );
}

/**
 * Whether a response body is a health report. Statuses and component names are not restricted to
 * the ones known today: a newer backend may add some, and the page shows them as they are.
 */
export function isHealthReport(value: unknown): value is HealthReport {
  return (
    isRecord(value) &&
    typeof value.status === 'string' &&
    typeof value.version === 'string' &&
    (value.timeZone === undefined || typeof value.timeZone === 'string') &&
    isRecord(value.components) &&
    Object.values(value.components).every(isHealthComponent)
  );
}

/**
 * Reads the health report. A 503 with a valid body is a report (the backend is up and says it is
 * unhealthy). Any other non-2xx answer, an invalid body, a network failure, or no answer within
 * the timeout rejects.
 */
export async function fetchHealth(signal: AbortSignal): Promise<HealthReport> {
  const controller = new AbortController();
  const abort = () => {
    controller.abort();
  };
  const timeout = setTimeout(abort, HEALTH_TIMEOUT_MS);
  signal.addEventListener('abort', abort);

  try {
    const response = await fetch(resolveAppUrl('health'), {
      headers: { Accept: 'application/json' },
      cache: 'no-store',
      signal: controller.signal,
    });

    if (!response.ok && response.status !== 503) {
      throw new Error(`Health request failed with status ${String(response.status)}.`);
    }

    const body: unknown = await response.json();
    if (!isHealthReport(body)) {
      throw new Error('Health response is not a health report.');
    }

    return body;
  } finally {
    clearTimeout(timeout);
    signal.removeEventListener('abort', abort);
  }
}

/**
 * Loads the health report and refreshes it every 30 seconds while the tab is visible. A failed
 * refresh keeps the last report and marks it stale; a failed first load is the error state.
 * Becoming visible again refreshes at once.
 */
export function useHealth(): { state: HealthState; retry: () => void } {
  const [state, setState] = useState<HealthState>({ phase: 'loading' });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let inFlight = false;

    const isHidden = () => document.visibilityState === 'hidden';

    const refresh = async () => {
      if (inFlight) {
        return;
      }

      inFlight = true;
      clearTimeout(timer);
      try {
        const report = await fetchHealth(controller.signal);
        if (!controller.signal.aborted) {
          setState({ phase: 'ready', report, stale: false });
        }
      } catch {
        if (!controller.signal.aborted) {
          setState((previous) =>
            previous.phase === 'ready' ? { ...previous, stale: true } : { phase: 'error' },
          );
        }
      } finally {
        inFlight = false;
        if (!controller.signal.aborted && !isHidden()) {
          timer = setTimeout(() => void refresh(), HEALTH_REFRESH_MS);
        }
      }
    };

    const onVisibilityChange = () => {
      if (isHidden()) {
        clearTimeout(timer);
      } else {
        void refresh();
      }
    };

    document.addEventListener('visibilitychange', onVisibilityChange);
    void refresh();

    return () => {
      controller.abort();
      clearTimeout(timer);
      document.removeEventListener('visibilitychange', onVisibilityChange);
    };
  }, [attempt]);

  const retry = useCallback(() => {
    setState({ phase: 'loading' });
    setAttempt((current) => current + 1);
  }, []);

  return { state, retry };
}
