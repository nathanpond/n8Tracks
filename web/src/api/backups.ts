import { useCallback, useEffect, useRef, useState } from 'react';
import { resolveAppUrl } from './baseUrl';
import { apiFetch } from './client';
import { writeWithRevision, type SaveResult } from './saves';

const BACKUPS_PATH = 'api/v1/backups';
const SCHEDULE_PATH = 'api/v1/settings/backup-schedule';

export const MIN_KEEP = 1;
export const MAX_KEEP = 365;

export type BackupFrequency = 'daily' | 'weekly';

/** The backup schedule's four settings. `time` is `HH:mm` in the configured time zone; weekly runs on Sundays. */
export interface ScheduleSettings {
  enabled: boolean;
  frequency: BackupFrequency;
  time: string;
  keep: number;
}

/** The schedule as `GET /api/v1/settings/backup-schedule` answers it, with its revision. */
export interface BackupScheduleRecord extends ScheduleSettings {
  revision: number;
}

/** The latest scheduled attempt. Times are UTC ISO 8601. */
export interface BackupAttempt {
  outcome: 'running' | 'succeeded' | 'failed';
  startedAt: string;
  finishedAt: string | null;
  error: string | null;
  /** When a first failure's one retry runs; null when none is pending. */
  retryAt: string | null;
}

/** The schedule as the Backups page shows it. */
export interface BackupScheduleStatus extends ScheduleSettings {
  /** The next planned time; null when scheduling is off. */
  nextAt: string | null;
  lastAttempt: BackupAttempt | null;
}

/** The defaults: on, daily at 03:00, keeping seven. */
export const DEFAULT_SCHEDULE: ScheduleSettings = {
  enabled: true,
  frequency: 'daily',
  time: '03:00',
  keep: 7,
};

const TIME_PATTERN = /^([01]\d|2[0-3]):[0-5]\d$/;

/** The client-side checks of a schedule, keyed by field, as the API words them. Empty when valid. */
export function scheduleErrors(
  settings: Omit<ScheduleSettings, 'keep'> & { keep: number | string },
): Partial<Record<keyof ScheduleSettings, string>> {
  const errors: Partial<Record<keyof ScheduleSettings, string>> = {};
  if (!TIME_PATTERN.test(settings.time)) {
    errors.time = 'Enter a time of day as HH:mm, from 00:00 to 23:59.';
  }
  const keep = settings.keep;
  if (typeof keep !== 'number' || !Number.isInteger(keep) || keep < MIN_KEEP || keep > MAX_KEEP) {
    errors.keep = `Keep from ${String(MIN_KEEP)} to ${String(MAX_KEEP)} backups.`;
  }
  return errors;
}

/** The schedule in a few words: "Daily at 03:00, keep 7", or "Off". */
export function describeSchedule(settings: ScheduleSettings): string {
  if (!settings.enabled) {
    return 'Off';
  }
  const when =
    settings.frequency === 'weekly'
      ? `Weekly on Sunday at ${settings.time}`
      : `Daily at ${settings.time}`;
  return `${when}, keep ${String(settings.keep)}`;
}

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

/**
 * The Backups page's data: where the next backup goes, the one in progress, every archive, the
 * newest valid one of any kind, and the schedule's state.
 */
export interface BackupList {
  destination: BackupLocation;
  sharesDiskWithData: boolean;
  activeJobId: string | null;
  items: Backup[];
  lastSuccessAt: string | null;
  schedule: BackupScheduleStatus;
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

export function isScheduleSettings(value: unknown): value is ScheduleSettings {
  return (
    isRecord(value) &&
    typeof value.enabled === 'boolean' &&
    (value.frequency === 'daily' || value.frequency === 'weekly') &&
    typeof value.time === 'string' &&
    typeof value.keep === 'number'
  );
}

function isScheduleRecord(value: unknown): value is BackupScheduleRecord {
  return isScheduleSettings(value) && isRecord(value) && typeof value.revision === 'number';
}

function isAttempt(value: unknown): value is BackupAttempt {
  return (
    isRecord(value) &&
    (value.outcome === 'running' || value.outcome === 'succeeded' || value.outcome === 'failed') &&
    typeof value.startedAt === 'string' &&
    isNullableString(value.finishedAt) &&
    isNullableString(value.error) &&
    isNullableString(value.retryAt)
  );
}

function isScheduleStatus(value: unknown): value is BackupScheduleStatus {
  return (
    isScheduleSettings(value) &&
    isRecord(value) &&
    isNullableString(value.nextAt) &&
    (value.lastAttempt === null || isAttempt(value.lastAttempt))
  );
}

export function isBackupList(value: unknown): value is BackupList {
  return (
    isRecord(value) &&
    isLocation(value.destination) &&
    typeof value.sharesDiskWithData === 'boolean' &&
    isNullableString(value.activeJobId) &&
    Array.isArray(value.items) &&
    value.items.every(isBackup) &&
    isNullableString(value.lastSuccessAt) &&
    isScheduleStatus(value.schedule)
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

export type BackupScheduleState =
  { phase: 'loading' } | { phase: 'error' } | { phase: 'ready'; record: BackupScheduleRecord };

/** The schedule with its revision, loaded once and again on `reload`; `replace` sets what a save answered. */
export function useBackupSchedule(): {
  state: BackupScheduleState;
  reload: () => void;
  replace: (record: BackupScheduleRecord) => void;
} {
  const [state, setState] = useState<BackupScheduleState>({ phase: 'loading' });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    const load = async () => {
      try {
        const response = await apiFetch(SCHEDULE_PATH, { signal: controller.signal });
        const answer = await body(response);
        if (controller.signal.aborted) {
          return;
        }
        setState(
          response.ok && isScheduleRecord(answer)
            ? { phase: 'ready', record: answer }
            : { phase: 'error' },
        );
      } catch {
        if (!controller.signal.aborted) {
          setState({ phase: 'error' });
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
  const replace = useCallback((record: BackupScheduleRecord) => {
    setState({ phase: 'ready', record });
  }, []);

  return { state, reload, replace };
}

/** Replaces the schedule, based on `revision`. */
export function saveBackupSchedule(
  revision: number,
  settings: ScheduleSettings,
): Promise<SaveResult<BackupScheduleRecord>> {
  return writeWithRevision('PUT', SCHEDULE_PATH, revision, { ...settings }, (answer) =>
    isScheduleRecord(answer) ? answer : undefined,
  );
}
