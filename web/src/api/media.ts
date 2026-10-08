import { useCallback, useEffect, useRef, useState } from 'react';
import { apiFetch } from './client';
import { body, isRecord } from './songs';

export const MEDIA_STATUS_PATH = 'api/v1/media/status';
const MEDIA_SCANS_PATH = 'api/v1/media/scans';

/** How often the Media page reads the status while no scan is queued or running. */
export const STATUS_POLL_MS = 10_000;

/** How often the page reads the status, and the scan's job, while a scan is queued or running. */
export const ACTIVE_POLL_MS = 1000;

export type MountState = 'available' | 'unavailable';

/** Why a scan failed: a known cause, or `failed` for anything else. */
export type ScanFailure = 'media_folder_unavailable' | 'interrupted' | 'failed';

export type ScanTrigger = 'manual' | 'startup' | 'scheduled' | 'recovery';

/** What a scan counted, as its job reports it. */
export interface ScanCounts {
  seen: number;
  new: number;
  changed: number;
  unchanged: number;
  missing: number;
  restored: number;
  associated: number;
  unmatched: number;
  skipped: number;
  unreadable: number;
  unreadableDirectories: number;
  availableBefore: number;
}

/** One finished scan. `trigger` and `counts` are null for a scan cut off by a restart. */
export interface ScanReport {
  jobId: string;
  trigger: ScanTrigger | null;
  outcome: 'succeeded' | 'failed';
  startedAt: string;
  finishedAt: string;
  durationSeconds: number;
  counts: ScanCounts | null;
  failure: ScanFailure | null;
}

/** The media library's state, as `GET /api/v1/media/status` answers it (#208). */
export interface MediaStatus {
  mount: { state: MountState; since: string | null; path: string };
  counts: {
    total: number;
    available: number;
    missing: number;
    associated: number;
    unmatched: number;
  };
  lastScan: ScanReport | null;
  lastSuccessfulScan: ScanReport | null;
  activeScanJobId: string | null;
  schedule: { enabled: boolean; intervalMinutes: number };
  nextScheduledScan: string | null;
  majorityMissingWarning: boolean;
}

/** A scan's job as `GET /api/v1/jobs/{id}` answers it: the fields the page reads. */
export interface ScanJob {
  id: string;
  status: 'queued' | 'running' | 'succeeded' | 'failed';
  progress: number;
  message: string | null;
  error: string | null;
}

const isNumber = (value: unknown): value is number => typeof value === 'number';
const isNullableString = (value: unknown): value is string | null =>
  value === null || typeof value === 'string';

const COUNT_KEYS: (keyof ScanCounts)[] = [
  'seen',
  'new',
  'changed',
  'unchanged',
  'missing',
  'restored',
  'associated',
  'unmatched',
  'skipped',
  'unreadable',
  'unreadableDirectories',
  'availableBefore',
];

function isScanCounts(value: unknown): value is ScanCounts {
  return isRecord(value) && COUNT_KEYS.every((key) => isNumber(value[key]));
}

function isScanReport(value: unknown): value is ScanReport {
  return (
    isRecord(value) &&
    typeof value.jobId === 'string' &&
    isNullableString(value.trigger) &&
    (value.outcome === 'succeeded' || value.outcome === 'failed') &&
    typeof value.startedAt === 'string' &&
    typeof value.finishedAt === 'string' &&
    isNumber(value.durationSeconds) &&
    (value.counts === null || isScanCounts(value.counts)) &&
    isNullableString(value.failure)
  );
}

export function isMediaStatus(value: unknown): value is MediaStatus {
  if (!isRecord(value)) {
    return false;
  }
  const { mount, counts, schedule } = value;
  return (
    isRecord(mount) &&
    (mount.state === 'available' || mount.state === 'unavailable') &&
    isNullableString(mount.since) &&
    typeof mount.path === 'string' &&
    isRecord(counts) &&
    ['total', 'available', 'missing', 'associated', 'unmatched'].every((key) =>
      isNumber(counts[key]),
    ) &&
    (value.lastScan === null || isScanReport(value.lastScan)) &&
    (value.lastSuccessfulScan === null || isScanReport(value.lastSuccessfulScan)) &&
    isNullableString(value.activeScanJobId) &&
    isRecord(schedule) &&
    typeof schedule.enabled === 'boolean' &&
    isNumber(schedule.intervalMinutes) &&
    isNullableString(value.nextScheduledScan) &&
    typeof value.majorityMissingWarning === 'boolean'
  );
}

export function isScanJob(value: unknown): value is ScanJob {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    (value.status === 'queued' ||
      value.status === 'running' ||
      value.status === 'succeeded' ||
      value.status === 'failed') &&
    isNumber(value.progress) &&
    isNullableString(value.message) &&
    (value.error === undefined || isNullableString(value.error))
  );
}

export function isActive(job: ScanJob): boolean {
  return job.status === 'queued' || job.status === 'running';
}

/**
 * Where the status is: loading, failed before it was ever read, or read (`stale` when the last
 * re-read failed). `readAt` is when it was read, in milliseconds since the epoch.
 */
export type MediaStatusState =
  | { phase: 'loading' }
  | { phase: 'error' }
  | { phase: 'ready'; data: MediaStatus; readAt: number; stale: boolean };

/**
 * The media status, read again every {@link STATUS_POLL_MS}, or every {@link ACTIVE_POLL_MS} while
 * `fast` or the status itself says a scan is queued or running, so a scheduled scan is noticed. A
 * re-read that fails keeps what was read, marked stale. `reload` reads at once.
 */
export function useMediaStatus(fast: boolean): { state: MediaStatusState; reload: () => void } {
  const [state, setState] = useState<MediaStatusState>({ phase: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const latest = useRef<{ data: MediaStatus; readAt: number } | null>(null);
  const fastRef = useRef(fast);
  useEffect(() => {
    fastRef.current = fast;
  }, [fast]);

  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const read = async () => {
      try {
        const response = await apiFetch(MEDIA_STATUS_PATH, { signal: controller.signal });
        const answer = await body(response);
        if (controller.signal.aborted) {
          return;
        }
        if (response.ok && isMediaStatus(answer)) {
          latest.current = { data: answer, readAt: Date.now() };
          setState({ phase: 'ready', ...latest.current, stale: false });
        } else {
          setState(failedRead(latest.current));
        }
      } catch {
        if (controller.signal.aborted) {
          return;
        }
        setState(failedRead(latest.current));
      }
      const active = fastRef.current || (latest.current?.data.activeScanJobId ?? null) !== null;
      timer = setTimeout(
        () => {
          void read();
        },
        active ? ACTIVE_POLL_MS : STATUS_POLL_MS,
      );
    };
    void read();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [attempt]);

  const reload = useCallback(() => {
    setAttempt((previous) => previous + 1);
  }, []);

  return { state, reload };
}

function failedRead(latest: { data: MediaStatus; readAt: number } | null): MediaStatusState {
  return latest === null ? { phase: 'error' } : { phase: 'ready', ...latest, stale: true };
}

/**
 * How asking for a scan ended: its job (`alreadyInProgress` when one was already queued or running,
 * which is answered instead of starting another), or `failed`.
 */
export type StartScanResult =
  { kind: 'started'; jobId: string; alreadyInProgress: boolean } | { kind: 'failed' };

/** Scan Library: queues a scan, or answers the one already queued or running. */
export async function startScan(): Promise<StartScanResult> {
  try {
    const response = await apiFetch(MEDIA_SCANS_PATH, { method: 'POST' });
    const answer = await body(response);
    if ((response.status === 202 || response.status === 200) && isRecord(answer)) {
      if (typeof answer.jobId === 'string') {
        return {
          kind: 'started',
          jobId: answer.jobId,
          alreadyInProgress: answer.alreadyInProgress === true,
        };
      }
    }
    return { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/**
 * Follows the scan job `jobId` (null for none): reads it every {@link ACTIVE_POLL_MS} until it has
 * succeeded or failed, then calls `onFinished` once. A read that fails is tried again at the next
 * tick. `gone` is true when the job no longer exists (an unattended scan that found nothing is
 * dropped from the jobs list when the next one finishes).
 */
export function useScanJob(
  jobId: string | null,
  onFinished: (job: ScanJob | null) => void,
): { job: ScanJob | null; gone: boolean } {
  const [seen, setSeen] = useState<{ jobId: string; job: ScanJob | null; gone: boolean } | null>(
    null,
  );
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
        if (response.status === 404) {
          setSeen({ jobId, job: null, gone: true });
          finished.current(null);
          return;
        }
        if (response.ok && isScanJob(answer)) {
          setSeen({ jobId, job: answer, gone: false });
          if (!isActive(answer)) {
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
      }, ACTIVE_POLL_MS);
    };
    void poll();

    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [jobId]);

  return seen !== null && seen.jobId === jobId
    ? { job: seen.job, gone: seen.gone }
    : { job: null, gone: false };
}
