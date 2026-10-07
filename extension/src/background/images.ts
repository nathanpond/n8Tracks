import {
  coverOf,
  IMAGES_READABLE,
  readImage,
  type Cover,
  type ImageFetch,
} from '../adapter/imageReader.ts';
import type { ExportPart, ImageProgress } from '../messages.ts';
import { DisconnectedError, type Connection } from './connection.ts';

/**
 * Cover images for a sync (#152), in the service worker. While the Suno tab reads, each part's
 * clips name their covers; once the export is complete and n8Tracks has it ready, each cover is
 * read (`adapter/imageReader.ts`, no credentials) and staged with its record, four at a time, so
 * the user may start the review meanwhile. An image that cannot be read or is refused is counted
 * and skipped, never sent again (#134's decision), and never stops the sync: that Generation is
 * imported without artwork. An image that arrives after the import was confirmed goes to the
 * Generation the record became, which keeps an image it already has.
 *
 * What is left to send is kept in `chrome.storage.local`, so a restarted service worker carries on.
 */

/** The local-storage key of the images of the last sync. */
export const IMAGES_KEY = 'sunoImages';

/** How many images are read and sent at once. */
export const IMAGES_IN_FLIGHT = 4;

/** How often the export's state is read while n8Tracks gets it ready (or commits it). */
export const STATE_POLL_MS = 2_000;

/** How long images wait for the export to become ready before they stop. */
export const READY_WAIT_MS = 15 * 60_000;

const EXPORTS_PATH = 'api/v1/suno/exports';
const GENERATIONS_PATH = 'api/v1/generations';

/** The states in which an export may still take images later. */
const WAITING_STATES = new Set(['receiving', 'classifying', 'committing']);

/** What is stored: the progress, the covers still to send, and since when it has waited. */
interface ImageRecord extends ImageProgress {
  pending: Cover[];
  since?: number;
}

/** The part of `chrome.storage.local` the images use. */
export interface ImageStorage {
  get(keys: string[]): Promise<Record<string, unknown>>;
  set(items: Record<string, unknown>): Promise<void>;
  remove(keys: string[]): Promise<void>;
}

export interface CoverImagesOptions {
  connection: Connection;
  storage: ImageStorage;
  /** Whether Suno's images can be read without credentials (spike TS-003: yes). */
  readable?: boolean;
  /** The image read's fetch, for tests. */
  fetch?: ImageFetch;
  sleep?: (ms: number) => Promise<void>;
  now?: () => number;
}

/** What sending one image came to. */
type Outcome = 'sent' | 'failed' | 'ignored' | 'wait' | 'stop';

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

async function bodyOf(response: Response): Promise<Record<string, unknown>> {
  try {
    const body = (await response.json()) as unknown;
    return isRecord(body) ? body : {};
  } catch {
    return {};
  }
}

function isImageRecord(value: unknown): value is ImageRecord {
  return (
    isRecord(value) &&
    typeof value.exportId === 'string' &&
    typeof value.tabId === 'number' &&
    typeof value.state === 'string' &&
    Array.isArray(value.pending)
  );
}

/** The file name an image is sent under; n8Tracks reads the type from the bytes. */
function fileName(image: Blob): string {
  const subtype = /^image\/([a-z0-9.+-]+)/i.exec(image.type)?.[1] ?? 'jpeg';
  return `cover.${subtype}`;
}

/** The progress the panel shows: never the addresses. */
function progressOf(record: ImageRecord): ImageProgress {
  return {
    exportId: record.exportId,
    tabId: record.tabId,
    state: record.state,
    total: record.total,
    sent: record.sent,
    failed: record.failed,
    ignored: record.ignored,
  };
}

export class CoverImages {
  private readonly connection: Connection;
  private readonly storage: ImageStorage;
  private readonly readable: boolean;
  private readonly fetch: ImageFetch | undefined;
  private readonly sleep: (ms: number) => Promise<void>;
  private readonly now: () => number;
  /** Storage writes, one after another, so counts from parallel sends are not lost. */
  private writes: Promise<unknown> = Promise.resolve();
  private running: Promise<void> | null = null;

  constructor(options: CoverImagesOptions) {
    this.connection = options.connection;
    this.storage = options.storage;
    this.readable = options.readable ?? IMAGES_READABLE;
    this.fetch = options.fetch;
    this.sleep =
      options.sleep ??
      ((ms) =>
        new Promise((resolve) => {
          setTimeout(resolve, ms);
        }));
    this.now = options.now ?? (() => Date.now());
  }

  /** The images of the export that tab `tabId` synced, as the panel shows them. */
  async progress(tabId: number): Promise<ImageProgress | null> {
    const record = await this.read();
    return record?.tabId === tabId ? progressOf(record) : null;
  }

  /** Notes the covers of a part sent to `exportId`. A part sent again replaces nothing twice. */
  async collect(exportId: string, tabId: number, part: ExportPart): Promise<void> {
    const covers = [...part.clips, ...part.trashedClips]
      .map(coverOf)
      .filter((cover) => cover !== null);
    await this.update((record) => {
      const current = record?.exportId === exportId ? record : this.fresh(exportId, tabId, []);
      const bySunoId = new Map(current.pending.map((cover) => [cover.sunoId, cover]));
      for (const cover of covers) {
        bySunoId.set(cover.sunoId, cover);
      }
      const pending = [...bySunoId.values()];
      return { ...current, pending, total: pending.length };
    });
  }

  /** The export was discarded: its images are not sent. */
  async discard(exportId: string): Promise<void> {
    await this.update((record) => (record?.exportId === exportId ? null : record));
  }

  /**
   * The export is complete: its images are sent once n8Tracks has it ready. Resolves when they
   * all have been, or have stopped; the service worker does not wait for it.
   */
  async start(exportId: string, tabId: number): Promise<void> {
    await this.update((record) => {
      const current = record?.exportId === exportId ? record : this.fresh(exportId, tabId, []);
      if (!this.readable) {
        return { ...current, state: 'skipped', pending: [] };
      }
      return current.pending.length === 0
        ? { ...current, state: 'finished' }
        : { ...current, state: 'waiting', since: this.now() };
    });
    await this.run();
  }

  /** When the service worker starts: carries on with images a stopped service worker left. */
  async resume(): Promise<void> {
    const record = await this.read();
    if (record !== null && (record.state === 'waiting' || record.state === 'sending')) {
      await this.run();
    }
  }

  private fresh(exportId: string, tabId: number, pending: Cover[]): ImageRecord {
    return {
      exportId,
      tabId,
      state: 'collecting',
      total: pending.length,
      sent: 0,
      failed: 0,
      ignored: 0,
      since: this.now(),
      pending,
    };
  }

  private async read(): Promise<ImageRecord | null> {
    const stored = (await this.storage.get([IMAGES_KEY]))[IMAGES_KEY];
    return isImageRecord(stored) ? stored : null;
  }

  /** Changes the stored record (null removes it), after any change still being written. */
  private update(change: (record: ImageRecord | null) => ImageRecord | null): Promise<void> {
    const next = this.writes.then(async () => {
      const changed = change(await this.read());
      if (changed === null) {
        await this.storage.remove([IMAGES_KEY]);
      } else {
        await this.storage.set({ [IMAGES_KEY]: changed });
      }
    });
    this.writes = next.catch(() => undefined);
    return next;
  }

  private run(): Promise<void> {
    this.running ??= this.loop().finally(() => {
      this.running = null;
    });
    return this.running;
  }

  /** Waits for the export to take images, then sends them; again while some had to wait. */
  private async loop(): Promise<void> {
    for (;;) {
      const record = await this.read();
      if (record === null || (record.state !== 'waiting' && record.state !== 'sending')) {
        return;
      }
      if (record.pending.length === 0) {
        await this.finish(record.exportId, 'finished');
        return;
      }
      const state = await this.exportState(record.exportId);
      if (state === 'ready' || state === 'committed') {
        await this.update((current) =>
          current?.exportId === record.exportId ? { ...current, state: 'sending' } : current,
        );
        const pass = await this.sendAll(record.exportId, record.pending);
        if (pass === 'stopped') {
          return;
        }
        // Some may have had to wait (the import was being confirmed): read the state again. The
        // wait is measured from the last pass that got anything done.
        if (pass === 'progressed') {
          await this.update((current) =>
            current?.exportId === record.exportId ? { ...current, since: this.now() } : current,
          );
        } else if (this.now() - (record.since ?? this.now()) > READY_WAIT_MS) {
          await this.finish(record.exportId, 'stopped');
          return;
        }
        continue;
      }
      if (state === null || !WAITING_STATES.has(state)) {
        await this.finish(record.exportId, 'stopped');
        return;
      }
      if (this.now() - (record.since ?? this.now()) > READY_WAIT_MS) {
        await this.finish(record.exportId, 'stopped');
        return;
      }
      // Each wait writes the record again, which also keeps the service worker awake.
      await this.update((current) =>
        current?.exportId === record.exportId ? { ...current, state: 'waiting' } : current,
      );
      await this.sleep(STATE_POLL_MS);
    }
  }

  /** The images still to send are counted as not sent, and the record ends in `state`. */
  private async finish(exportId: string, state: 'finished' | 'stopped'): Promise<void> {
    await this.update((current) =>
      current?.exportId === exportId
        ? { ...current, state, failed: current.failed + current.pending.length, pending: [] }
        : current,
    );
  }

  /** The export's state, or null when n8Tracks no longer has it (or cannot be asked). */
  private async exportState(exportId: string): Promise<string | null> {
    try {
      const response = await this.connection.call(
        `${EXPORTS_PATH}/${encodeURIComponent(exportId)}`,
      );
      if (!response.ok) {
        return null;
      }
      const body = await bodyOf(response);
      return typeof body.state === 'string' ? body.state : null;
    } catch {
      return null;
    }
  }

  /**
   * Sends `covers`, four at a time: `stopped` when sending stopped for good (the record says so),
   * `progressed` when at least one cover was counted, and `stalled` when every one had to wait.
   */
  private async sendAll(
    exportId: string,
    covers: Cover[],
  ): Promise<'stopped' | 'progressed' | 'stalled'> {
    const queue = [...covers];
    // Set by the workers; an object, so the checks after they finish read what they set.
    const seen = { stopped: false, waiting: false, progressed: false };
    const worker = async () => {
      for (let cover = queue.shift(); cover !== undefined && !seen.stopped; cover = queue.shift()) {
        const outcome = await this.sendOne(exportId, cover);
        if (outcome === 'stop') {
          seen.stopped = true;
          return;
        }
        if (outcome === 'wait') {
          seen.waiting = true;
          continue;
        }
        seen.progressed = true;
        await this.update((current) => {
          if (current?.exportId !== exportId) {
            return current;
          }
          return {
            ...current,
            pending: current.pending.filter((item) => item.sunoId !== cover.sunoId),
            [outcome]: current[outcome] + 1,
          };
        });
      }
    };
    await Promise.all(Array.from({ length: IMAGES_IN_FLIGHT }, worker));
    if (seen.stopped) {
      await this.finish(exportId, 'stopped');
      return 'stopped';
    }
    if (seen.waiting) {
      await this.sleep(STATE_POLL_MS);
    }
    return seen.progressed ? 'progressed' : 'stalled';
  }

  /** Reads one cover and stages it, or gives it to its Generation when the import is confirmed. */
  private async sendOne(exportId: string, cover: Cover): Promise<Outcome> {
    const read = await readImage(cover.address, this.fetch);
    if (!read.ok) {
      return 'failed';
    }
    try {
      const staged = await this.put(
        `${EXPORTS_PATH}/${encodeURIComponent(exportId)}/artwork/${encodeURIComponent(cover.sunoId)}`,
        read.image,
      );
      if (staged.ok) {
        return 'sent';
      }
      if (staged.status === 404) {
        // No such record (a list read again dropped it), or the export has gone: the next
        // state check finds the latter.
        return 'ignored';
      }
      const body = await bodyOf(staged);
      if (staged.status !== 409 || body.code !== 'export_not_ready') {
        return 'failed';
      }
      if (body.state !== 'committed') {
        return typeof body.state === 'string' && WAITING_STATES.has(body.state) ? 'wait' : 'stop';
      }
      if (typeof body.generationId !== 'string') {
        // The record became no Generation (the user did not import it).
        return 'ignored';
      }
      const late = await this.put(
        `${GENERATIONS_PATH}/${encodeURIComponent(body.generationId)}/artwork`,
        read.image,
      );
      if (late.ok) {
        return 'sent';
      }
      // A Generation that already has an image keeps it: refused, and ignored.
      return late.status === 409 && (await bodyOf(late)).code === 'artwork_exists'
        ? 'ignored'
        : 'failed';
    } catch (error) {
      return error instanceof DisconnectedError ? 'stop' : 'failed';
    }
  }

  private put(path: string, image: Blob): Promise<Response> {
    const form = new FormData();
    form.append('file', image, fileName(image));
    return this.connection.call(path, { method: 'PUT', body: form });
  }
}
