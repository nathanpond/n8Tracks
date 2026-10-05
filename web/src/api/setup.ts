import { useCallback, useEffect, useState } from 'react';
import { isScheduleSettings, type BackupLocation, type ScheduleSettings } from './backups';
import { resolveAppUrl } from './baseUrl';

export const SETUP_TIMEOUT_MS = 10_000;

const STATUS_PATH = 'api/v1/setup/status';
const SUBMIT_PATH = 'api/v1/setup';

/** Where backups would be written now, and the schedule the backup step starts from. */
export interface SetupBackups {
  destination: BackupLocation;
  sharesDiskWithData: boolean;
  defaults: ScheduleSettings;
}

/** Where setup stands. The checks are present only while setup is incomplete. */
export interface SetupStatus {
  complete: boolean;
  storage?: { writable: boolean };
  media?: { available: boolean };
  backups?: SetupBackups;
}

export interface SetupSubmission {
  username: string;
  password: string;
  passwordConfirmation: string;
  /** The backup step's choices; the API stores the defaults when they are missing. */
  backupSchedule?: ScheduleSettings;
}

/** How a submission ended, by the API's answer. */
export type SetupResult =
  | { kind: 'created' }
  | { kind: 'alreadyComplete' }
  | { kind: 'storageNotWritable' }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'failed' };

export type SetupStatusState =
  | { phase: 'loading' }
  | { phase: 'error' }
  | { phase: 'ready'; status: SetupStatus; checking: boolean };

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function hasBoolean(value: unknown, key: string): boolean {
  return isRecord(value) && typeof value[key] === 'boolean';
}

function isSetupBackups(value: unknown): value is SetupBackups {
  return (
    isRecord(value) &&
    (value.destination === 'mount' || value.destination === 'data') &&
    typeof value.sharesDiskWithData === 'boolean' &&
    isScheduleSettings(value.defaults)
  );
}

export function isSetupStatus(value: unknown): value is SetupStatus {
  return (
    isRecord(value) &&
    typeof value.complete === 'boolean' &&
    (value.storage === undefined || hasBoolean(value.storage, 'writable')) &&
    (value.media === undefined || hasBoolean(value.media, 'available')) &&
    (value.backups === undefined || isSetupBackups(value.backups))
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
  const timeout = setTimeout(abort, SETUP_TIMEOUT_MS);
  signal?.addEventListener('abort', abort);
  try {
    return await run(controller.signal);
  } finally {
    clearTimeout(timeout);
    signal?.removeEventListener('abort', abort);
  }
}

/** Reads the setup status. Any non-2xx answer, an invalid body, or no answer rejects. */
export function fetchSetupStatus(signal: AbortSignal): Promise<SetupStatus> {
  return withTimeout(signal, async (bounded) => {
    const response = await fetch(resolveAppUrl(STATUS_PATH), {
      headers: { Accept: 'application/json' },
      cache: 'no-store',
      signal: bounded,
    });
    if (!response.ok) {
      throw new Error(`Setup status request failed with status ${String(response.status)}.`);
    }

    const body: unknown = await response.json();
    if (!isSetupStatus(body)) {
      throw new Error('Setup status response is not a setup status.');
    }

    return body;
  });
}

async function problemCode(response: Response): Promise<{ code?: string; errors?: unknown }> {
  try {
    const body: unknown = await response.json();
    return isRecord(body)
      ? { code: typeof body.code === 'string' ? body.code : undefined, errors: body.errors }
      : {};
  } catch {
    return {};
  }
}

/** Submits the administrator. Never rejects: a network failure or an unknown answer is `failed`. */
export async function submitSetup(submission: SetupSubmission): Promise<SetupResult> {
  try {
    return await withTimeout(undefined, async (bounded) => {
      const response = await fetch(resolveAppUrl(SUBMIT_PATH), {
        method: 'POST',
        headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
        body: JSON.stringify(submission),
        signal: bounded,
      });
      if (response.status === 201) {
        return { kind: 'created' };
      }

      const { code, errors } = await problemCode(response);
      if (response.status === 409 && code === 'setup_already_complete') {
        return { kind: 'alreadyComplete' };
      }
      if (response.status === 409 && code === 'storage_not_writable') {
        return { kind: 'storageNotWritable' };
      }
      if (response.status === 422 && code === 'validation_failed' && isErrorMap(errors)) {
        return { kind: 'invalid', errors };
      }
      return { kind: 'failed' };
    });
  } catch {
    return { kind: 'failed' };
  }
}

/**
 * Loads the setup status once, and again on `refresh` (Retry and Re-check refetch, so every check
 * is run afresh by the backend). While a refresh runs the last status stays, marked `checking`.
 * `markComplete` records that setup was just completed, without asking the backend again.
 */
export function useSetupStatus(): {
  state: SetupStatusState;
  refresh: () => void;
  markComplete: () => void;
} {
  const [state, setState] = useState<SetupStatusState>({ phase: 'loading' });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();

    fetchSetupStatus(controller.signal).then(
      (status) => {
        if (!controller.signal.aborted) {
          setState({ phase: 'ready', status, checking: false });
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
    setState((previous) =>
      previous.phase === 'ready' ? { ...previous, checking: true } : { phase: 'loading' },
    );
    setAttempt((current) => current + 1);
  }, []);

  const markComplete = useCallback(() => {
    setState({ phase: 'ready', status: { complete: true }, checking: false });
  }, []);

  return { state, refresh, markComplete };
}
