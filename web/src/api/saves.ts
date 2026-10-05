import { apiFetch } from './client';

// The one way the web UI writes an editable record (project conventions, "Concurrency"): the write
// carries the revision it was based on in `If-Match`, and a stale write comes back as a conflict
// with the record as it is now. `useRevisionedSave` (common/) routes conflicts to the
// ConflictDialog; every editing screen saves through it.

/** The value of one compared field: text, or null when it is empty. */
export type FieldValue = string | null;

/** A record with a revision, as every editable record has. */
export interface Revisioned {
  revision: number;
}

/**
 * Why a write failed. `unreachable` (no answer: offline, stopped, timed out) and `server` (a server
 * error, or an answer the client could not read) may go through if the same write is sent again;
 * the rest will not: `signed-out` (the session ended and was not renewed), `frozen` (the record's
 * inputs can no longer change), `gone` (the record is no longer there), `refused` (any other
 * refusal).
 */
export type FailureReason = 'unreachable' | 'server' | 'signed-out' | 'frozen' | 'gone' | 'refused';

/** Whether a write that failed for `reason` may go through if it is sent again unchanged. */
export function isRetryable(reason: FailureReason | undefined): boolean {
  return reason === undefined || reason === 'unreachable' || reason === 'server';
}

/** How one write ended, by the API's answer. Never a rejection. */
export type SaveResult<T> =
  | { kind: 'saved'; record: T }
  | { kind: 'conflict'; current: T }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'failed'; reason?: FailureReason };

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isErrorMap(value: unknown): value is Record<string, string[]> {
  return (
    isRecord(value) &&
    Object.values(value).every(
      (messages) => Array.isArray(messages) && messages.every((m) => typeof m === 'string'),
    )
  );
}

/** `If-Match` for a revision: the revision in double quotes. */
export function ifMatch(revision: number): string {
  return `"${String(revision)}"`;
}

/** Why a write that was answered (but neither saved, nor a conflict, nor invalid) failed. */
function failureOf(status: number, answer: unknown): FailureReason {
  if (status === 401) {
    return 'signed-out';
  }
  if (status === 404) {
    return 'gone';
  }
  if (status === 409 && isRecord(answer) && answer.code === 'version_frozen') {
    return 'frozen';
  }
  return status >= 500 || status === 408 || status === 429 || status < 400 ? 'server' : 'refused';
}

/**
 * PATCHes `body` to `path` based on `revision`. 200 with a record `accept` takes is saved; 409
 * `revision_conflict` with a `current` it takes is a conflict; 422 `validation_failed` is invalid
 * with its field errors; anything else (a 404, a timeout, a body it does not take) has failed, with
 * the reason ({@link FailureReason}).
 */
export async function patchWithRevision<T>(
  path: string,
  revision: number,
  body: Record<string, unknown>,
  accept: (answer: unknown) => T | undefined,
): Promise<SaveResult<T>> {
  try {
    const response = await apiFetch(path, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json', 'If-Match': ifMatch(revision) },
      body: JSON.stringify(body),
    });
    let answer: unknown;
    try {
      answer = await response.json();
    } catch {
      answer = undefined;
    }
    if (response.ok) {
      const record = accept(answer);
      return record === undefined
        ? { kind: 'failed', reason: 'server' }
        : { kind: 'saved', record };
    }
    if (response.status === 409 && isRecord(answer) && answer.code === 'revision_conflict') {
      const current = accept(answer.current);
      return current === undefined
        ? { kind: 'failed', reason: 'server' }
        : { kind: 'conflict', current };
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

/** One compared field of a record: its key in an edit, what it is called, and how to read it. */
export interface ComparedField<T> {
  key: string;
  label: string;
  read: (record: T) => FieldValue;
}

/**
 * The fields that differ between `base` (what the client loaded) and `current` (what the server
 * has now), field by field. A field being saved is left out when the server already holds the
 * value being saved: both sides changed it to the same value, which is not a conflict.
 */
export function differingFields<T>(
  fields: readonly ComparedField<T>[],
  base: T,
  current: T,
  edit: Readonly<Record<string, FieldValue>>,
): ComparedField<T>[] {
  return fields.filter(
    (field) =>
      field.read(base) !== field.read(current) &&
      !(Object.hasOwn(edit, field.key) && field.read(current) === edit[field.key]),
  );
}

/**
 * A queue that runs tasks one after another, in the order given: each starts once the one before
 * it has finished, whether it succeeded or not.
 */
export function createSaveQueue(): <R>(task: () => Promise<R>) => Promise<R> {
  let tail: Promise<unknown> = Promise.resolve();
  return <R>(task: () => Promise<R>) => {
    const run = tail.then(task, task);
    tail = run.catch(() => undefined);
    return run;
  };
}
