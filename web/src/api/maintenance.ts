import { useEffect, useState, useSyncExternalStore } from 'react';
import { resolveAppUrl } from './baseUrl';

/** The code of the API's 503 while the instance is in maintenance. */
export const MAINTENANCE_CODE = 'maintenance';

const STATUS_PATH = 'api/v1/maintenance';

/** How often the maintenance page reads the status. */
export const MAINTENANCE_POLL_MS = 1_000;

export type MaintenanceStage =
  'validating' | 'safety-backup' | 'replacing' | 'migrating' | 'finishing';

export type MaintenanceOutcome = 'succeeded' | 'failed' | 'rolled-back';

/** `GET /api/v1/maintenance`: never a path or an error text. */
export interface MaintenanceStatus {
  active: boolean;
  stage: MaintenanceStage | null;
  percent: number;
  outcome: MaintenanceOutcome | null;
}

const STAGES: readonly string[] = [
  'validating',
  'safety-backup',
  'replacing',
  'migrating',
  'finishing',
];
const OUTCOMES: readonly string[] = ['succeeded', 'failed', 'rolled-back'];

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

export function isMaintenanceStatus(value: unknown): value is MaintenanceStatus {
  return (
    isRecord(value) &&
    typeof value.active === 'boolean' &&
    (value.stage === null || (typeof value.stage === 'string' && STAGES.includes(value.stage))) &&
    typeof value.percent === 'number' &&
    (value.outcome === null ||
      (typeof value.outcome === 'string' && OUTCOMES.includes(value.outcome)))
  );
}

// Whether some answer has said the instance is in maintenance. The gate at the top of the app
// shows the maintenance page while it is set.
let reported = false;
const listeners = new Set<() => void>();

function notify() {
  for (const listener of listeners) {
    listener();
  }
}

/** Marks the instance as in maintenance: the app is replaced by the maintenance page. */
export function reportMaintenance(): void {
  if (!reported) {
    reported = true;
    notify();
  }
}

/** Clears the mark, once the maintenance page has seen maintenance end. */
export function clearMaintenance(): void {
  if (reported) {
    reported = false;
    notify();
  }
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

function snapshot(): boolean {
  return reported;
}

/** Whether the app should show the maintenance page now. */
export function useMaintenanceReported(): boolean {
  return useSyncExternalStore(subscribe, snapshot, snapshot);
}

/**
 * Whether `response` is the API's 503 `maintenance`; when it is, maintenance is reported. The body
 * is read from a clone, so the caller can still read it.
 */
export async function noticeMaintenance(response: Response): Promise<boolean> {
  if (response.status !== 503) {
    return false;
  }
  try {
    const body: unknown = await response.clone().json();
    if (isRecord(body) && body.code === MAINTENANCE_CODE) {
      reportMaintenance();
      return true;
    }
  } catch {
    // Not a problem body: some other 503.
  }
  return false;
}

/** Reads the maintenance status; rejects on any failure. */
export async function fetchMaintenance(signal: AbortSignal): Promise<MaintenanceStatus> {
  const response = await fetch(resolveAppUrl(STATUS_PATH), {
    headers: { Accept: 'application/json' },
    cache: 'no-store',
    signal,
  });
  if (!response.ok) {
    throw new Error(`Maintenance status request failed with status ${String(response.status)}.`);
  }
  const body: unknown = await response.json();
  if (!isMaintenanceStatus(body)) {
    throw new Error('Maintenance status response is not a maintenance status.');
  }
  return body;
}

/**
 * The maintenance status, read every {@link MAINTENANCE_POLL_MS} while `polling` is true. Null
 * until the first answer; a read that fails keeps the last one and is tried again at the next tick.
 */
export function useMaintenanceStatus(polling: boolean): MaintenanceStatus | null {
  const [status, setStatus] = useState<MaintenanceStatus | null>(null);

  useEffect(() => {
    if (!polling) {
      return;
    }
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      try {
        const next = await fetchMaintenance(controller.signal);
        if (controller.signal.aborted) {
          return;
        }
        setStatus(next);
      } catch {
        if (controller.signal.aborted) {
          return;
        }
      }
      timer = setTimeout(() => {
        void poll();
      }, MAINTENANCE_POLL_MS);
    };
    void poll();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [polling]);

  return status;
}
