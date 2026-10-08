import { apiFetch } from './client';
import { failureOf, ifMatch, isErrorMap, type FailureReason } from './saves';
import { isRecord, useResource } from './songs';

const LOGGING_PATH = 'api/v1/settings/logging';

/** The bounds of the two limits, as the API takes them. */
export const MIN_RETENTION_DAYS = 1;
export const MAX_RETENTION_DAYS = 90;
export const MIN_MAX_MEGABYTES = 10;
export const MAX_MAX_MEGABYTES = 5120;

/** The levels the administrator can choose, quietest first. */
export const SETTABLE_LEVELS = ['error', 'warning', 'information', 'debug'] as const;
export type SettableLevel = (typeof SETTABLE_LEVELS)[number];

/**
 * The log settings. `level` is one of the four once saved; before that it is the environment's
 * (`levelSource: 'environment'`), which may also be `trace` or `critical`. `debugUntil` is when a
 * saved Debug level switches back to Information; `folderProblem` why log files are not being
 * written. Revision 0 until first saved.
 */
export interface LoggingSettings {
  revision: number;
  level: string;
  levelSource: 'environment' | 'setting';
  retentionDays: number;
  maxMegabytes: number;
  debugUntil: string | null;
  folderProblem: string | null;
}

export function isLoggingSettings(value: unknown): value is LoggingSettings {
  return (
    isRecord(value) &&
    typeof value.revision === 'number' &&
    typeof value.level === 'string' &&
    (value.levelSource === 'environment' || value.levelSource === 'setting') &&
    typeof value.retentionDays === 'number' &&
    typeof value.maxMegabytes === 'number' &&
    (value.debugUntil === null || typeof value.debugUntil === 'string') &&
    (value.folderProblem === null || typeof value.folderProblem === 'string')
  );
}

const accept = (answer: unknown) => (isLoggingSettings(answer) ? answer : undefined);

/** The log settings, for Settings → Diagnostics. */
export function useLoggingSettings() {
  return useResource(LOGGING_PATH, accept);
}

/** What the administrator saves. */
export interface LoggingChoice {
  level: SettableLevel;
  retentionDays: number;
  maxMegabytes: number;
}

/**
 * How a save ended: as any revisioned save, or `confirm` when the limits would delete log files and
 * the deletion was not yet confirmed (nothing was saved).
 */
export type LoggingSaveResult =
  | { kind: 'saved'; record: LoggingSettings }
  | { kind: 'conflict'; current: LoggingSettings }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'confirm'; files: number; bytes: number }
  | { kind: 'failed'; reason?: FailureReason };

/** Saves `choice` based on `revision` (0 before the first save), deleting log files only when `confirmDelete`. */
export async function saveLoggingSettings(
  revision: number,
  choice: LoggingChoice,
  confirmDelete: boolean,
): Promise<LoggingSaveResult> {
  try {
    const response = await apiFetch(LOGGING_PATH, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', 'If-Match': ifMatch(revision) },
      body: JSON.stringify({ ...choice, confirmDelete }),
    });
    let answer: unknown;
    try {
      answer = await response.json();
    } catch {
      answer = undefined;
    }
    if (response.ok) {
      const record = accept(answer);
      return record ? { kind: 'saved', record } : { kind: 'failed', reason: 'server' };
    }
    if (response.status === 409 && isRecord(answer)) {
      if (answer.code === 'revision_conflict') {
        const current = accept(answer.current);
        return current ? { kind: 'conflict', current } : { kind: 'failed', reason: 'server' };
      }
      if (
        answer.code === 'confirmation_required' &&
        typeof answer.files === 'number' &&
        typeof answer.bytes === 'number'
      ) {
        return { kind: 'confirm', files: answer.files, bytes: answer.bytes };
      }
    }
    if (
      response.status === 422 &&
      isRecord(answer) &&
      answer.code === 'validation_failed' &&
      isErrorMap(answer.errors)
    ) {
      return { kind: 'invalid', errors: answer.errors };
    }
    return { kind: 'failed', reason: failureOf(response.status, answer) };
  } catch {
    return { kind: 'failed', reason: 'unreachable' };
  }
}

/** A level's name as the page shows it: "Information". */
export function levelLabel(level: string): string {
  return level.length === 0 ? level : level.charAt(0).toUpperCase() + level.slice(1);
}

/** A size in bytes as words: "512 bytes", "3.2 MB". */
export function describeBytes(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes.toLocaleString('en-US')} ${bytes === 1 ? 'byte' : 'bytes'}`;
  }
  const units = ['KB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${value.toLocaleString('en-US', { maximumFractionDigits: 1 })} ${units[unit] ?? 'GB'}`;
}
