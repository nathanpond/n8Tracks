import { useCallback, useEffect, useRef, useState } from 'react';
import { isRetryable, type FailureReason, type FieldValue } from '../api/saves';
import type { SaveOutcome } from '../common/useRevisionedSave';

/** How long after the last change a save is sent. */
export const AUTOSAVE_DELAY_MS = 1_500;

/** The wait before the first retry of a failed save; each one after it waits twice as long… */
export const FIRST_RETRY_MS = 2_000;

/** …up to this, and from then on every retry waits this long, without limit. */
export const LONGEST_RETRY_MS = 30_000;

/** The wait before retry number `failures` (1 after the first failure): 2, 4, 8, 16, 30, 30… s. */
export function retryDelay(failures: number): number {
  return Math.min(FIRST_RETRY_MS * 2 ** Math.max(failures - 1, 0), LONGEST_RETRY_MS);
}

/** The changed fields of a record, key to value, as one save sends them. */
export type Edit = Readonly<Record<string, FieldValue>>;

/** Whether the work on screen is stored, and when it is not, why. */
export type AutosaveStatus =
  /** Everything on screen is stored. */
  | { kind: 'saved' }
  /** A change is waiting for the user to pause, or being sent. */
  | { kind: 'saving' }
  /** The last save failed in a way a retry may fix; it is retried on its own. */
  | { kind: 'retrying'; reason: string }
  /** The last save was refused in a way retrying cannot fix; nothing is sent until it is resolved. */
  | { kind: 'stopped'; reason: string; canRetry: boolean }
  /** The record changed elsewhere and the user has not chosen yet: Reload or Reapply. */
  | { kind: 'conflict' };

/** What the user is told when a save failed, by why. */
const FAILURE_MESSAGES: Record<FailureReason, string> = {
  unreachable:
    'n8Tracks cannot be reached. Your text is kept here, and saving is retried automatically.',
  server:
    'n8Tracks could not store the change. Your text is kept here, and saving is retried automatically.',
  'signed-out': 'you are signed out. Sign in again, then choose Try again.',
  frozen:
    'this Version’s lyrics and styles can no longer change. Create a new Version from it to keep editing them.',
  deleted:
    'this Version was deleted. Your text is kept here: create a new Version with it to keep it.',
  gone: 'this Version is no longer there. Copy your text somewhere safe before you leave.',
  refused: 'n8Tracks refused the change. Reload the page and try again.',
};

/** The message for a save that failed for `reason` (a failure without one is a server error). */
export function failureMessage(reason: FailureReason | undefined): string {
  return FAILURE_MESSAGES[reason ?? 'server'];
}

/** What the indicator says for `status`: "Saving…", "Saved", or "Not saved: " and the reason. */
export function autosaveText(status: AutosaveStatus): string {
  switch (status.kind) {
    case 'saved':
      return 'Saved';
    case 'saving':
      return 'Saving…';
    case 'retrying':
    case 'stopped':
      return `Not saved: ${status.reason}`;
    case 'conflict':
      return 'Not saved: this Version was changed elsewhere. Your text is kept here. Reload to take the current Version, or reapply your change over it.';
  }
}

type Hold = Exclude<AutosaveStatus, { kind: 'saved' } | { kind: 'saving' }>;

/**
 * Saves a record's edited fields automatically: {@link AUTOSAVE_DELAY_MS} after the last change,
 * every field that differs from the stored record goes in one save, through `send` (the shared
 * save helper's `saveFields`, which sends the revision and routes a conflict to its dialog). One
 * save is in flight at a time: what is typed meanwhile goes out once it returns, on the revision it
 * returned, so the user never conflicts with their own save.
 *
 * A save that fails without an answer or with a server error is retried without limit, waiting
 * {@link retryDelay}; a refusal retrying cannot fix (invalid, frozen, signed out, gone) stops
 * saving until the user changes the text again or chooses {@link retry}; a conflict the user left
 * undecided ("Keep editing") pauses saving until {@link reapply} or {@link reload}. The text on
 * screen is never touched, except by `onReloaded` when the user takes the current record.
 *
 * Call `changed` after every change of a draft. `pending` and `problem` are read when a save is
 * due, so they must read the latest drafts and record (refs), not a render's copy.
 */
export function useAutosave({
  pending,
  problem,
  send,
  onReloaded,
}: {
  /** The fields that differ from the stored record now; empty when everything is stored. */
  pending: () => Edit;
  /** Why the drafts cannot be sent as they are (over a limit), or undefined. */
  problem: () => string | undefined;
  send: (edit: Edit) => Promise<SaveOutcome>;
  /** The user took the current record over their change: put its values back in the drafts. */
  onReloaded: () => void;
}): {
  status: AutosaveStatus;
  /** Tells autosave a draft changed: the next save is due {@link AUTOSAVE_DELAY_MS} from now. */
  changed: () => void;
  /** Saves now, waiting for a save in flight first; true once everything on screen is stored. */
  flush: () => Promise<boolean>;
  /** After a conflict: saves the user's change over the current record. */
  reapply: () => void;
  /** After a conflict: takes the current record and drops the user's change. */
  reload: () => void;
  /** After a refusal or while retrying: tries again now. */
  retry: () => void;
} {
  const options = useRef({ pending, problem, send, onReloaded });
  useEffect(() => {
    options.current = { pending, problem, send, onReloaded };
  }, [pending, problem, send, onReloaded]);

  const [busy, setBusy] = useState(false);
  const [hold, setHoldState] = useState<Hold | undefined>();
  const holdRef = useRef<Hold | undefined>(undefined);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const flight = useRef<Promise<void> | null>(null);
  const failures = useRef(0);
  const mounted = useRef(true);
  const runRef = useRef<() => Promise<void>>(() => Promise.resolve());

  const setHold = useCallback((next: Hold | undefined) => {
    holdRef.current = next;
    setHoldState(next);
  }, []);

  const clearTimer = useCallback(() => {
    if (timer.current !== undefined) {
      clearTimeout(timer.current);
      timer.current = undefined;
    }
  }, []);

  const arm = useCallback(
    (delay: number) => {
      clearTimer();
      timer.current = setTimeout(() => {
        timer.current = undefined;
        void runRef.current();
      }, delay);
    },
    [clearTimer],
  );

  const isEmpty = (edit: Edit) => Object.keys(edit).length === 0;

  /** Nothing more to send now: idle unless a save is still due. */
  const settle = useCallback(() => {
    if (timer.current === undefined && flight.current === null) {
      setBusy(false);
    }
  }, []);

  /** After a save returned: what was typed meanwhile goes now, unless a pause is still running. */
  const next = useCallback(() => {
    if (isEmpty(options.current.pending())) {
      settle();
    } else if (timer.current === undefined) {
      void runRef.current();
    }
  }, [settle]);

  const handle = useCallback(
    (outcome: SaveOutcome) => {
      switch (outcome.kind) {
        case 'saved':
          failures.current = 0;
          setHold(undefined);
          next();
          return;
        case 'reloaded':
          failures.current = 0;
          setHold(undefined);
          options.current.onReloaded();
          next();
          return;
        case 'keep-editing':
          clearTimer();
          setHold({ kind: 'conflict' });
          settle();
          return;
        case 'invalid':
          clearTimer();
          setHold({
            kind: 'stopped',
            reason: `${Object.values(outcome.errors).flat().join(' ')} Change the text to save it.`,
            canRetry: false,
          });
          settle();
          return;
        case 'failed':
          if (isRetryable(outcome.reason)) {
            failures.current += 1;
            setHold({ kind: 'retrying', reason: failureMessage(outcome.reason) });
            arm(retryDelay(failures.current));
          } else {
            clearTimer();
            setHold({ kind: 'stopped', reason: failureMessage(outcome.reason), canRetry: true });
            settle();
          }
          return;
      }
    },
    [arm, clearTimer, next, setHold, settle],
  );

  const run = useCallback((): Promise<void> => {
    if (flight.current !== null) {
      // Its end sends whatever is still unsaved.
      return flight.current;
    }
    const held = holdRef.current;
    if (held?.kind === 'conflict' || held?.kind === 'stopped') {
      return Promise.resolve();
    }
    const edit = options.current.pending();
    if (isEmpty(edit)) {
      failures.current = 0;
      setHold(undefined);
      clearTimer();
      settle();
      return Promise.resolve();
    }
    const problemNow = options.current.problem();
    if (problemNow !== undefined) {
      clearTimer();
      setHold({ kind: 'stopped', reason: problemNow, canRetry: false });
      settle();
      return Promise.resolve();
    }

    // Everything typed so far is in this save; a later change arms the timer again.
    clearTimer();
    setBusy(true);
    const sending = (async () => {
      let outcome: SaveOutcome;
      try {
        outcome = await options.current.send(edit);
      } catch {
        outcome = { kind: 'failed', reason: 'server' };
      }
      flight.current = null;
      if (mounted.current) {
        handle(outcome);
      }
    })();
    flight.current = sending;
    return sending;
  }, [clearTimer, handle, setHold, settle]);

  useEffect(() => {
    runRef.current = run;
  }, [run]);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      clearTimer();
    };
  }, [clearTimer]);

  const changed = useCallback(() => {
    const held = holdRef.current;
    if (held?.kind === 'conflict') {
      return;
    }
    if (isEmpty(options.current.pending())) {
      // Back to what is stored: nothing to save, and nothing failing.
      clearTimer();
      failures.current = 0;
      setHold(undefined);
      settle();
      return;
    }
    const problemNow = options.current.problem();
    if (problemNow !== undefined) {
      clearTimer();
      setHold({ kind: 'stopped', reason: problemNow, canRetry: false });
      settle();
      return;
    }
    if (held?.kind === 'stopped') {
      setHold(undefined);
    }
    setBusy(true);
    arm(AUTOSAVE_DELAY_MS);
  }, [arm, clearTimer, setHold, settle]);

  const flush = useCallback(async (): Promise<boolean> => {
    let tried = false;
    for (;;) {
      if (flight.current !== null) {
        await flight.current;
        continue;
      }
      if (!mounted.current) {
        return false;
      }
      if (isEmpty(options.current.pending())) {
        return true;
      }
      const held = holdRef.current;
      if (tried || (held !== undefined && held.kind !== 'retrying')) {
        return false;
      }
      tried = true;
      await run();
    }
  }, [run]);

  const reapply = useCallback(() => {
    if (holdRef.current?.kind === 'conflict') {
      failures.current = 0;
      setHold(undefined);
      void run();
    }
  }, [run, setHold]);

  const reload = useCallback(() => {
    if (holdRef.current?.kind === 'conflict') {
      failures.current = 0;
      setHold(undefined);
      options.current.onReloaded();
      settle();
    }
  }, [setHold, settle]);

  const retry = useCallback(() => {
    const held = holdRef.current;
    if (held?.kind === 'stopped' || held?.kind === 'retrying') {
      setHold(undefined);
      void run();
    }
  }, [run, setHold]);

  const status: AutosaveStatus = hold ?? (busy ? { kind: 'saving' } : { kind: 'saved' });

  return { status, changed, flush, reapply, reload, retry };
}
