import { poll, realClock, type Clock } from './clock.ts';
import type { Observations } from './libraryReader.ts';
import {
  CONTENT_SOURCE,
  isObservedMessage,
  type ObservedKind,
  type ObservedMessage,
  type ObserverReady,
} from './observed.ts';

/**
 * The content script's side of the page observer: it takes the observer's messages, checking that
 * they come from this page's own window and origin, queues them for the library reader, and
 * remembers the workspaces and playlists Suno has listed on this page, for the panel's choice.
 */

/** The part of `window` the feed uses, so tests can stand in for the page. */
export interface FeedWindow {
  origin: string;
  postMessage(message: unknown, targetOrigin: string): void;
  addEventListener(type: 'message', listener: (event: MessageEvent) => void): void;
  removeEventListener(type: 'message', listener: (event: MessageEvent) => void): void;
}

/** A workspace or playlist Suno has listed on this page: its ID and name. */
export interface Listed {
  id: string;
  name: string;
}

/** The most responses waiting to be read; older ones are dropped first. */
const QUEUE_LIMIT = 200;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function listed(list: unknown): Listed[] {
  return Array.isArray(list)
    ? list
        .filter(isRecord)
        .flatMap((item) =>
          typeof item.id === 'string' && item.id !== ''
            ? [{ id: item.id, name: typeof item.name === 'string' ? item.name : '' }]
            : [],
        )
    : [];
}

export class ObservationFeed implements Observations {
  private readonly view: FeedWindow;
  private readonly clock: Clock;
  private readonly queue: ObservedMessage[] = [];
  private readonly workspaces = new Map<string, string>();
  private readonly playlists = new Map<string, string>();
  private readonly listener = (event: MessageEvent) => {
    if (event.source !== (this.view as unknown) || event.origin !== this.view.origin) {
      return;
    }
    if (isObservedMessage(event.data)) {
      this.take(event.data);
    }
  };

  constructor(view: FeedWindow, clock: Clock = realClock) {
    this.view = view;
    this.clock = clock;
  }

  /** Starts listening, and asks the observer for what it saw before this script started. */
  start(): void {
    this.view.addEventListener('message', this.listener);
    const ready: ObserverReady = { source: CONTENT_SOURCE, type: 'observer-ready' };
    this.view.postMessage(ready, this.view.origin);
  }

  stop(): void {
    this.view.removeEventListener('message', this.listener);
  }

  /** The workspaces Suno has listed on this page so far, by name. */
  listedWorkspaces(): Listed[] {
    return [...this.workspaces].map(([id, name]) => ({ id, name }));
  }

  /** The user's playlists Suno has listed on this page so far, by name. */
  listedPlaylists(): Listed[] {
    return [...this.playlists].map(([id, name]) => ({ id, name }));
  }

  /** Adds a response; exported for the content script's tests and the listener. */
  take(message: ObservedMessage): void {
    if (message.kind === 'workspaces' && isRecord(message.body)) {
      for (const item of listed(message.body.projects)) {
        this.workspaces.set(item.id, item.name);
      }
    }
    if (message.kind === 'playlists' && isRecord(message.body)) {
      for (const item of listed(message.body.playlists)) {
        this.playlists.set(item.id, item.name);
      }
    }
    this.queue.push(message);
    if (this.queue.length > QUEUE_LIMIT) {
      this.queue.shift();
    }
  }

  async next(
    kind: ObservedKind,
    accept: (message: ObservedMessage) => boolean,
    timeoutMs: number,
    signal: AbortSignal,
  ): Promise<ObservedMessage | null> {
    let found: ObservedMessage | null = null;
    await poll(
      () => {
        const index = this.queue.findIndex((message) => message.kind === kind && accept(message));
        if (index >= 0) {
          found = this.queue.splice(index, 1)[0] ?? null;
        }
        return { ok: found !== null || signal.aborted };
      },
      timeoutMs,
      this.clock,
    );
    return signal.aborted ? null : found;
  }
}
