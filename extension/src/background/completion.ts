import { isFinished } from '../adapter/finished.ts';
import { coverOf, readImage, type ImageFetch } from '../adapter/imageReader.ts';
import type { GenerateReply } from '../messages.ts';
import { DisconnectedError, type Connection } from './connection.ts';

/**
 * The service worker's half of the completion watch (#154). Once n8Tracks has recorded the user's
 * Create (#149), each Generation it made is watched for up to ten minutes: the Suno tab reports the
 * clips that a feed answer its page got shows finished, and each finished clip of a watched
 * Generation goes to n8Tracks (`POST .../generation-requests/{id}/clips`), which fills the
 * Generation in once; its cover is then read from Suno's image host and given to the Generation
 * through the Generation artwork upload (#121), which never replaces an image it already has. A clip
 * still unfinished after ten minutes is left as it is: the next sync brings it. The watch is kept in
 * session storage, so a stopped service worker carries on, and the ten minutes are a
 * `chrome.alarms` alarm (Chrome's alarms cannot fire every 15 seconds, so the tab paces its own
 * refresh prompts). Closing the tab ends its watch.
 */

/** The session-storage key of the watched clips. */
export const COMPLETION_KEY = 'completion';

/** The alarm that ends the watch of clips whose ten minutes are up. */
export const COMPLETION_ALARM = 'n8tracks-completion';

/** How long a Create's clips are watched: ten minutes. */
export const WATCH_LIMIT_MS = 10 * 60_000;

const REQUESTS_PATH = 'api/v1/suno/generation-requests';
const GENERATIONS_PATH = 'api/v1/generations';

/** One watched clip: its Generation, the request and tab that recorded it, and when its watch ends. */
export interface WatchedClip {
  sunoId: string;
  generationId: string;
  requestId: string;
  tabId: number;
  /** When its ten minutes are up, in milliseconds since the epoch. */
  until: number;
}

/** The parts of `chrome.*` the watch uses, so tests can stand in for the browser. */
export interface CompletionBrowser {
  session: {
    get(keys: string[]): Promise<Record<string, unknown>>;
    set(items: Record<string, unknown>): Promise<void>;
  };
  /** `chrome.alarms`. */
  alarms: {
    create(name: string, info: { when: number }): Promise<unknown>;
    clear(name: string): Promise<unknown>;
  };
}

export interface CompletionWatchOptions {
  connection: Connection;
  browser: CompletionBrowser;
  now?: () => number;
  /** The image request (`imageReader.ts`); tests stand in for it. */
  fetchImage?: ImageFetch;
}

/** A Generation an observed Create made, as n8Tracks answered it: its ID and its clip's Suno ID. */
export interface ObservedGeneration {
  id: string;
  sunoId: string;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isWatched(value: unknown): value is WatchedClip {
  return (
    isRecord(value) &&
    typeof value.sunoId === 'string' &&
    typeof value.generationId === 'string' &&
    typeof value.requestId === 'string' &&
    typeof value.tabId === 'number' &&
    typeof value.until === 'number'
  );
}

/** What sending one finished clip came to. */
type Sent = 'completed' | 'refused' | 'retry';

export class CompletionWatch {
  private readonly connection: Connection;
  private readonly browser: CompletionBrowser;
  private readonly now: () => number;
  private readonly fetchImage: ImageFetch | undefined;

  constructor(options: CompletionWatchOptions) {
    this.connection = options.connection;
    this.browser = options.browser;
    this.now = options.now ?? Date.now;
    this.fetchImage = options.fetchImage;
  }

  /** Starts watching the Generations `generations` of one recorded Create, for ten minutes. */
  async watch(
    requestId: string,
    tabId: number,
    generations: readonly ObservedGeneration[],
  ): Promise<void> {
    const until = this.now() + WATCH_LIMIT_MS;
    const kept = (await this.watched()).filter(
      (clip) => !generations.some((generation) => generation.sunoId === clip.sunoId),
    );
    await this.save([
      ...kept,
      ...generations.map((generation) => ({
        sunoId: generation.sunoId,
        generationId: generation.id,
        requestId,
        tabId,
        until,
      })),
    ]);
  }

  /** The Suno IDs the tab `tabId` still watches. */
  async watching(tabId: number): Promise<string[]> {
    return (await this.current()).filter((clip) => clip.tabId === tabId).map((clip) => clip.sunoId);
  }

  /**
   * The tab `tabId` saw `clips` in a feed answer: each finished one of its watched clips goes to
   * n8Tracks, and its cover to its Generation. Answers what the tab still watches.
   */
  async report(
    tabId: number,
    clips: readonly Record<string, unknown>[],
  ): Promise<GenerateReply<{ watching: string[] }>> {
    let watched = await this.current();
    for (const clip of clips.filter(isFinished)) {
      const entry = watched.find((item) => item.tabId === tabId && item.sunoId === clip.id);
      if (entry === undefined) {
        continue;
      }
      let sent: Sent;
      try {
        sent = await this.send(entry, clip);
      } catch (error) {
        if (error instanceof DisconnectedError) {
          return { ok: false, ended: true, message: 'The extension is not connected to n8Tracks.' };
        }
        sent = 'retry';
      }
      if (sent !== 'retry') {
        watched = watched.filter((item) => item !== entry);
        await this.save(watched);
      }
    }
    return {
      ok: true,
      watching: watched.filter((clip) => clip.tabId === tabId).map((clip) => clip.sunoId),
    };
  }

  /** An alarm fired: the clips whose ten minutes are up are no longer watched. */
  async alarm(name: string): Promise<void> {
    if (name === COMPLETION_ALARM) {
      await this.current();
    }
  }

  /** The tab `tabId` was closed: its clips are no longer watched (the next sync brings them). */
  async tabRemoved(tabId: number): Promise<void> {
    const watched = await this.watched();
    if (watched.some((clip) => clip.tabId === tabId)) {
      await this.save(watched.filter((clip) => clip.tabId !== tabId));
    }
  }

  /** Sends one finished clip; once its Generation is filled in, gives it the clip's cover. */
  private async send(entry: WatchedClip, clip: Record<string, unknown>): Promise<Sent> {
    const response = await this.connection.call(
      `${REQUESTS_PATH}/${encodeURIComponent(entry.requestId)}/clips`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ clip }),
      },
    );
    if (response.status >= 500 || response.status === 429) {
      return 'retry';
    }
    if (!response.ok) {
      // Already complete, not this request's, no longer there, or not a finished clip: a later
      // change is the next sync's.
      return 'refused';
    }
    await this.giveCover(entry.generationId, clip);
    return 'completed';
  }

  /** The clip's cover, read from Suno's image host, as the Generation's image; never fails. */
  private async giveCover(generationId: string, clip: Record<string, unknown>): Promise<void> {
    const cover = coverOf(clip);
    if (cover === null) {
      return;
    }
    const read = await readImage(cover.address, this.fetchImage);
    if (!read.ok) {
      return;
    }
    const form = new FormData();
    form.append('file', read.image, read.image.type === 'image/png' ? 'cover.png' : 'cover.jpg');
    try {
      // 409 artwork_exists: the Generation already has an image, which it keeps.
      await this.connection.call(
        `${GENERATIONS_PATH}/${encodeURIComponent(generationId)}/artwork`,
        { method: 'PUT', body: form },
      );
    } catch {
      // The Generation stays without an image; a sync's cover images bring it.
    }
  }

  /** The watched clips whose ten minutes are not up, with the alarm set for the next to end. */
  private async current(): Promise<WatchedClip[]> {
    const watched = await this.watched();
    const now = this.now();
    const live = watched.filter((clip) => clip.until > now);
    if (live.length !== watched.length) {
      await this.save(live);
    }
    return live;
  }

  private async watched(): Promise<WatchedClip[]> {
    const stored = (await this.browser.session.get([COMPLETION_KEY]))[COMPLETION_KEY];
    return Array.isArray(stored) ? stored.filter(isWatched) : [];
  }

  /** Stores `watched`, and sets the alarm for the first to end, or clears it when none is left. */
  private async save(watched: readonly WatchedClip[]): Promise<void> {
    await this.browser.session.set({ [COMPLETION_KEY]: watched });
    if (watched.length === 0) {
      await this.browser.alarms.clear(COMPLETION_ALARM);
      return;
    }
    await this.browser.alarms.create(COMPLETION_ALARM, {
      when: Math.min(...watched.map((clip) => clip.until)),
    });
  }
}
