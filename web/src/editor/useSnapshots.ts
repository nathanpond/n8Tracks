import { useCallback, useEffect, useRef, useState } from 'react';
import { postSnapshot, sendSnapshotAsPageCloses, type SnapshotText } from '../api/snapshots';

/** How long the user pauses after a change before the text is snapshotted. */
export const SNAPSHOT_IDLE_MS = 30_000;

/** How many snapshots that could not be sent are held in memory; the oldest goes first. */
export const SNAPSHOT_QUEUE_LIMIT = 10;

/** How long after a failed send the held snapshots are tried again. */
export const SNAPSHOT_RETRY_MS = 15_000;

/** A Version's lyrics and styles, as the editor has them. */
export interface EditorText {
  lyrics: string;
  styles: string;
}

interface Held {
  versionId: string;
  text: SnapshotText;
}

// The snapshots not yet stored, shared by every editor on the page, so one taken as the user
// leaves a Version is still sent after its pane is gone. They live only in memory.
const held: Held[] = [];
let sending: Promise<void> | null = null;
let retryTimer: ReturnType<typeof setTimeout> | undefined;
const storedListeners = new Set<(versionId: string) => void>();

function scheduleRetry() {
  retryTimer ??= setTimeout(() => {
    retryTimer = undefined;
    void flushSnapshots();
  }, SNAPSHOT_RETRY_MS);
}

/**
 * Sends the held snapshots, oldest first, each carrying the time it was captured. One the API
 * stores (or already has) is done; one it refuses for good is dropped; when there is no answer
 * they stay held and are tried again after {@link SNAPSHOT_RETRY_MS}, or when the browser says it
 * is back online.
 */
export function flushSnapshots(): Promise<void> {
  if (sending !== null) {
    return sending;
  }
  sending = (async () => {
    try {
      for (let next = held[0]; next !== undefined; next = held[0]) {
        const result = await postSnapshot(next.versionId, next.text);
        if (result.kind === 'failed' && result.retryable) {
          scheduleRetry();
          return;
        }
        const index = held.indexOf(next);
        if (index >= 0) {
          held.splice(index, 1);
        }
        if (result.kind === 'stored') {
          const versionId = next.versionId;
          storedListeners.forEach((listener) => {
            listener(versionId);
          });
        }
      }
    } finally {
      sending = null;
    }
  })();
  return sending;
}

function hold(item: Held) {
  held.push(item);
  while (held.length > SNAPSHOT_QUEUE_LIMIT) {
    held.shift();
  }
  void flushSnapshots();
}

/** The snapshots held, oldest first (for tests). */
export function heldSnapshots(): readonly Held[] {
  return [...held];
}

/** Forgets every held snapshot and the retry (for tests: the queue outlives a rendered page). */
export function resetSnapshots(): void {
  held.length = 0;
  if (retryTimer !== undefined) {
    clearTimeout(retryTimer);
    retryTimer = undefined;
  }
}

function same(a: EditorText, b: EditorText): boolean {
  return a.lyrics === b.lyrics && a.styles === b.styles;
}

function isBlank(text: EditorText): boolean {
  return text.lyrics === '' && text.styles === '';
}

/**
 * Keeps a Version's editing history: snapshots of the lyrics and styles in the editor, taken
 * {@link SNAPSHOT_IDLE_MS} after the user's last change and when they leave the Version (the pane
 * goes, or the page closes) with changes not yet snapshotted. A snapshot is of the text on screen
 * whether or not it has been saved, so text that could not be saved still reaches history. One that
 * cannot be sent is held (up to {@link SNAPSHOT_QUEUE_LIMIT}) and sent later with its capture time.
 *
 * The text the Version had when the editor opened goes in first, before the first change is
 * snapshotted (unless it is empty): it may have come from elsewhere (a tool, another tab) and not
 * be in history yet. The API stores nothing when it equals the newest snapshot.
 *
 * `read` gives the text now, or undefined when it cannot be snapshotted (over a limit). Call
 * `changed` after every change of the lyrics or styles; `capture` snapshots at once (before the
 * conflict dialog's Reload discards the text); `rebase` says the editor now holds text that came
 * from the server (a restore, a reload) rather than from typing.
 */
export function useSnapshots({
  versionId,
  read,
  onStored,
}: {
  versionId: string;
  read: () => EditorText | undefined;
  /** A snapshot of this Version has been stored (History can be read again). */
  onStored: () => void;
}): {
  changed: () => void;
  capture: () => void;
  rebase: (text: EditorText, keepInHistory: boolean) => void;
  onPageHide: () => void;
} {
  const options = useRef({ read, onStored });
  useEffect(() => {
    options.current = { read, onStored };
  }, [read, onStored]);

  // The text history already has (or has been given), and the opening text still to be given.
  const [opening] = useState(() => read() ?? { lyrics: '', styles: '' });
  const last = useRef<EditorText>(opening);
  const baseline = useRef<{ text: EditorText; capturedAt: string } | null>({
    text: opening,
    capturedAt: new Date().toISOString(),
  });
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  const clearTimer = useCallback(() => {
    if (timer.current !== undefined) {
      clearTimeout(timer.current);
      timer.current = undefined;
    }
  }, []);

  /** The snapshots due now, oldest first: the opening text if still owed, then `text`. */
  const due = useCallback(
    (text: EditorText): Held[] => {
      const items: Held[] = [];
      const owed = baseline.current;
      if (owed !== null && !isBlank(owed.text)) {
        items.push({ versionId, text: { ...owed.text, capturedAt: owed.capturedAt } });
      }
      baseline.current = null;
      items.push({ versionId, text: { ...text, capturedAt: new Date().toISOString() } });
      last.current = text;
      return items;
    },
    [versionId],
  );

  const capture = useCallback(() => {
    clearTimer();
    const text = options.current.read();
    if (text === undefined || same(text, last.current)) {
      return;
    }
    due(text).forEach(hold);
  }, [clearTimer, due]);

  const changed = useCallback(() => {
    const text = options.current.read();
    if (text === undefined || same(text, last.current)) {
      clearTimer();
      return;
    }
    clearTimer();
    timer.current = setTimeout(() => {
      timer.current = undefined;
      capture();
    }, SNAPSHOT_IDLE_MS);
  }, [capture, clearTimer]);

  const rebase = useCallback(
    (text: EditorText, keepInHistory: boolean) => {
      clearTimer();
      last.current = text;
      baseline.current = keepInHistory ? { text, capturedAt: new Date().toISOString() } : null;
    },
    [clearTimer],
  );

  const onPageHide = useCallback(() => {
    clearTimer();
    const text = options.current.read();
    const leaving = text === undefined || same(text, last.current) ? [] : due(text);
    // Everything held goes now, as the page closes; what does not arrive is lost with the page.
    [...held.splice(0), ...leaving].forEach((item) => {
      sendSnapshotAsPageCloses(item.versionId, item.text);
    });
  }, [clearTimer, due]);

  // Stored snapshots of this Version; held ones are sent again when the connection returns.
  useEffect(() => {
    const listener = (stored: string) => {
      if (stored === versionId) {
        options.current.onStored();
      }
    };
    const online = () => {
      void flushSnapshots();
    };
    storedListeners.add(listener);
    window.addEventListener('online', online);
    return () => {
      storedListeners.delete(listener);
      window.removeEventListener('online', online);
    };
  }, [versionId]);

  // Leaving the Version (another Version, another page) snapshots what has changed.
  useEffect(
    () => () => {
      capture();
    },
    [capture],
  );

  return { changed, capture, rebase, onPageHide };
}
