import { useCallback, useEffect, useRef, useState } from 'react';
import { resolveAppUrl } from './baseUrl';
import { apiFetch } from './client';

const BACKUPS_PATH = 'api/v1/backups';

/** How often a running backup's job is read. */
export const BACKUP_POLL_MS = 1_000;

const SIZE_UNITS = ['bytes', 'KB', 'MB', 'GB', 'TB'];

/** A size in bytes as people read it: 1 decimal place, binary multiples. */
export function formatSize(bytes: number): string {
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < SIZE_UNITS.length - 1) {
    value /= 1024;
    unit += 1;
  }
  const number = new Intl.NumberFormat(undefined, {
    maximumFractionDigits: unit === 0 ? 0 : 1,
  }).format(value);
  return `${number} ${SIZE_UNITS[unit] ?? 'bytes'}`;
}

export type BackupLocation = 'mount' | 'data';

/** `valid`; `newer` (made by a newer version: downloadable, not restorable); `invalid` (only deletable). */
export type BackupStatus = 'valid' | 'newer' | 'invalid';

/** One archive as the API lists it. Times are UTC ISO 8601. */
export interface Backup {
  location: BackupLocation;
  name: string;
  size: number;
  createdAt: string;
  applicationVersion: string | null;
  kind: string | null;
  status: BackupStatus;
}

/** The Backups page's data: where the next backup goes, the one in progress, and every archive. */
export interface BackupList {
  destination: BackupLocation;
  sharesDiskWithData: boolean;
  activeJobId: string | null;
  items: Backup[];
}

/** A backup job as `GET /api/v1/jobs/{id}` answers it, the fields the page reads. */
export interface BackupJob {
  id: string;
  status: 'queued' | 'running' | 'succeeded' | 'failed';
  progress: number;
  message: string | null;
  error: string | null;
  result: { location?: string; name?: string; size?: number } | null;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isNullableString(value: unknown): value is string | null {
  return value === null || typeof value === 'string';
}

function isLocation(value: unknown): value is BackupLocation {
  return value === 'mount' || value === 'data';
}

export function isBackup(value: unknown): value is Backup {
  return (
    isRecord(value) &&
    isLocation(value.location) &&
    typeof value.name === 'string' &&
    typeof value.size === 'number' &&
    typeof value.createdAt === 'string' &&
    isNullableString(value.applicationVersion) &&
    isNullableString(value.kind) &&
    (value.status === 'valid' || value.status === 'newer' || value.status === 'invalid')
  );
}

export function isBackupList(value: unknown): value is BackupList {
  return (
    isRecord(value) &&
    isLocation(value.destination) &&
    typeof value.sharesDiskWithData === 'boolean' &&
    isNullableString(value.activeJobId) &&
    Array.isArray(value.items) &&
    value.items.every(isBackup)
  );
}

function isBackupJob(value: unknown): value is BackupJob {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    (value.status === 'queued' ||
      value.status === 'running' ||
      value.status === 'succeeded' ||
      value.status === 'failed') &&
    typeof value.progress === 'number' &&
    isNullableString(value.message) &&
    isNullableString(value.error)
  );
}

async function body(response: Response): Promise<unknown> {
  try {
    return await response.json();
  } catch {
    return undefined;
  }
}

/** The path of one archive, its name escaped as one path segment. */
function archivePath(backup: Pick<Backup, 'location' | 'name'>): string {
  return `${BACKUPS_PATH}/${backup.location}/${encodeURIComponent(backup.name)}`;
}

/** The URL the browser downloads an archive from: a plain GET with the session cookie. */
export function downloadUrl(backup: Pick<Backup, 'location' | 'name'>): string {
  return resolveAppUrl(archivePath(backup)).toString();
}

export type BackupsState =
  { phase: 'loading' } | { phase: 'error' } | { phase: 'ready'; list: BackupList };

/** The list, loaded once and again on `reload`. A failed reload keeps what was shown. */
export function useBackups(): { state: BackupsState; reload: () => void } {
  const [state, setState] = useState<BackupsState>({ phase: 'loading' });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    const load = async () => {
      try {
        const response = await apiFetch(BACKUPS_PATH, { signal: controller.signal });
        const answer = await body(response);
        if (controller.signal.aborted) {
          return;
        }
        if (response.ok && isBackupList(answer)) {
          setState({ phase: 'ready', list: answer });
        } else {
          setState((previous) => (previous.phase === 'ready' ? previous : { phase: 'error' }));
        }
      } catch {
        if (!controller.signal.aborted) {
          setState((previous) => (previous.phase === 'ready' ? previous : { phase: 'error' }));
        }
      }
    };
    void load();
    return () => {
      controller.abort();
    };
  }, [attempt]);

  const reload = useCallback(() => {
    setAttempt((previous) => previous + 1);
  }, []);

  return { state, reload };
}

export type StartBackupResult =
  { kind: 'started'; jobId: string } | { kind: 'in-progress'; jobId: string } | { kind: 'failed' };

/** "Back up now": the new job, or the one already queued or running. Never a rejection. */
export async function startBackup(): Promise<StartBackupResult> {
  try {
    const response = await apiFetch(BACKUPS_PATH, { method: 'POST' });
    const answer = await body(response);
    if (response.status === 202 && isRecord(answer) && typeof answer.jobId === 'string') {
      return { kind: 'started', jobId: answer.jobId };
    }
    if (
      response.status === 409 &&
      isRecord(answer) &&
      answer.code === 'backup_in_progress' &&
      typeof answer.jobId === 'string'
    ) {
      return { kind: 'in-progress', jobId: answer.jobId };
    }
    return { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/** Deletes an archive. `gone` when it was already gone. Never a rejection. */
export async function deleteBackup(backup: Backup): Promise<'deleted' | 'gone' | 'failed'> {
  try {
    const response = await apiFetch(archivePath(backup), { method: 'DELETE' });
    if (response.status === 204) {
      return 'deleted';
    }
    return response.status === 404 ? 'gone' : 'failed';
  } catch {
    return 'failed';
  }
}

/**
 * Follows the job `jobId` (null for none): reads it every {@link BACKUP_POLL_MS} until it has
 * succeeded or failed, then calls `onFinished` once. A read that fails is tried again at the next tick.
 */
export function useBackupJob(
  jobId: string | null,
  onFinished: (job: BackupJob) => void,
): BackupJob | null {
  // Kept with the job it belongs to, so a new jobId never shows the previous job.
  const [seen, setSeen] = useState<{ jobId: string; job: BackupJob } | null>(null);
  const finished = useRef(onFinished);
  useEffect(() => {
    finished.current = onFinished;
  }, [onFinished]);

  useEffect(() => {
    if (jobId === null) {
      return;
    }

    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      try {
        const response = await apiFetch(`api/v1/jobs/${encodeURIComponent(jobId)}`, {
          signal: controller.signal,
        });
        const answer = await body(response);
        if (controller.signal.aborted) {
          return;
        }
        if (response.ok && isBackupJob(answer)) {
          setSeen({ jobId, job: answer });
          if (answer.status === 'succeeded' || answer.status === 'failed') {
            finished.current(answer);
            return;
          }
        }
      } catch {
        if (controller.signal.aborted) {
          return;
        }
      }
      timer = setTimeout(() => {
        void poll();
      }, BACKUP_POLL_MS);
    };
    void poll();

    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [jobId]);

  return seen !== null && seen.jobId === jobId ? seen.job : null;
}
