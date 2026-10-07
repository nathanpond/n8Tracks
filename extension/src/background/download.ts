import type { Downloader } from '../download/downloader.ts';
import { isDownloadFormat } from '../download/selection.ts';
import type {
  ClipLookupReply,
  ClipLookupRow,
  DownloadAction,
  DownloadReply,
  DownloadRequest,
  ResponseFor,
} from '../messages.ts';
import { DisconnectedError, type Connection } from './connection.ts';

/**
 * The service worker's half of the Download view (#215). It keeps a Load library across the page
 * load that reads it (in session storage), refuses one while a sync or Generate on Suno runs,
 * remembers the formats last chosen (in local storage), and asks n8Tracks which clips it has as
 * Generations: `POST /api/v1/suno/clips/lookup`, the only call the view makes, with the extension's
 * `suno.sync` token. Nothing here reaches Suno, and nothing is imported. Start, Cancel, Retry, and
 * Resume go to the download queue (#216, `download/downloader.ts`).
 */

/** The parts of `chrome.storage` the Download view uses, so tests can stand in for the browser. */
export interface DownloadBrowser {
  /** `chrome.storage.session`: a Load library waiting for its page load. */
  session: {
    get(keys: string[]): Promise<Record<string, unknown>>;
    set(items: Record<string, unknown>): Promise<void>;
    remove(keys: string[]): Promise<void>;
  };
  /** `chrome.storage.local`: the formats last chosen. */
  local: {
    get(keys: string[]): Promise<Record<string, unknown>>;
    set(items: Record<string, unknown>): Promise<void>;
  };
}

/** The session-storage key of a Load library waiting for its page load. */
export const DOWNLOAD_LOAD_KEY = 'downloadLoad';
/** The local-storage key of the formats last chosen. */
export const DOWNLOAD_FORMATS_KEY = 'downloadFormats';

export const CLIP_LOOKUP_PATH = 'api/v1/suno/clips/lookup';
/** The most Suno IDs n8Tracks takes in one lookup. */
export const LOOKUP_BATCH = 500;

/** How long a Load library waits for its page load before it is forgotten (two minutes). */
export const LOAD_WAIT_MS = 120_000;

const DISCONNECTED = 'The extension is not connected to n8Tracks.';
const NO_SYNC_SCOPE = 'This credential lacks suno.sync.';
const UNREACHABLE = 'Cannot reach n8Tracks. Check that it is running.';
const NO_DOWNLOADER = 'Downloading is not available in this extension.';

export interface DownloadCoordinatorOptions {
  connection: Connection;
  browser: DownloadBrowser;
  /**
   * Why the library cannot be loaded now, or null: a sync or Generate on Suno running in the
   * extension, in any tab.
   */
  busy: () => Promise<string | null>;
  /** The download queue (#216); without it, Start is refused. */
  downloader?: Downloader;
  now?: () => number;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isRow(value: unknown): value is ClipLookupRow {
  return (
    isRecord(value) &&
    typeof value.sunoId === 'string' &&
    (value.generation === null ||
      (isRecord(value.generation) &&
        typeof value.generation.id === 'string' &&
        typeof value.generation.shortcode === 'string')) &&
    (value.artist === null || typeof value.artist === 'string') &&
    typeof value.deleted === 'boolean' &&
    Array.isArray(value.downloadedFormats) &&
    value.downloadedFormats.every((format) => typeof format === 'string')
  );
}

/** Only the members the view reads, so nothing else n8Tracks answers reaches the page's tab. */
function rowOf(row: ClipLookupRow): ClipLookupRow {
  return {
    sunoId: row.sunoId,
    generation:
      row.generation === null
        ? null
        : { id: row.generation.id, shortcode: row.generation.shortcode },
    artist: row.artist,
    deleted: row.deleted,
    downloadedFormats: [...row.downloadedFormats],
  };
}

export class DownloadCoordinator {
  private readonly connection: Connection;
  private readonly browser: DownloadBrowser;
  private readonly busy: () => Promise<string | null>;
  private readonly downloader: Downloader | null;
  private readonly now: () => number;

  constructor(options: DownloadCoordinatorOptions) {
    this.connection = options.connection;
    this.browser = options.browser;
    this.busy = options.busy;
    this.downloader = options.downloader ?? null;
    this.now = options.now ?? (() => Date.now());
  }

  /** Answers a Download message from the Suno content script in tab `tabId`. */
  async handle(
    request: DownloadRequest,
    tabId: number,
  ): Promise<ResponseFor[DownloadRequest['type']]> {
    switch (request.type) {
      case 'download-begin':
        return this.begin(request.selected, tabId);
      case 'download-resume':
        return { load: await this.resume(tabId) };
      case 'download-lookup':
        return this.lookup(request.sunoIds);
      case 'download-formats':
        return { formats: await this.formats(request.formats) };
      case 'download-start':
        return this.start(request.files, request.unlocks, tabId);
      case 'download-control':
        return this.control(request.action, tabId);
      case 'download-run':
        return { run: this.downloader === null ? null : await this.downloader.current() };
    }
  }

  /** Start: refused while a sync or Generate on Suno runs, since the page is shared. */
  private async start(
    files: Parameters<Downloader['add']>[1],
    unlocks: number,
    tabId: number,
  ): Promise<DownloadReply> {
    if (this.downloader === null) {
      return { ok: false, message: NO_DOWNLOADER };
    }
    const reason = await this.busy();
    if (reason !== null) {
      return { ok: false, message: reason };
    }
    const refused = await this.downloader.add(tabId, files, unlocks);
    return refused === null ? { ok: true } : { ok: false, message: refused };
  }

  private async control(action: DownloadAction, tabId: number): Promise<DownloadReply> {
    if (this.downloader === null) {
      return { ok: false, message: NO_DOWNLOADER };
    }
    switch (action) {
      case 'cancel':
        await this.downloader.cancel();
        break;
      case 'retry':
        await this.downloader.retry();
        break;
      case 'resume':
        await this.downloader.resume(tabId);
        break;
    }
    return { ok: true };
  }

  private async begin(selected: string[], tabId: number): Promise<DownloadReply> {
    const reason = await this.busy();
    if (reason !== null) {
      return { ok: false, message: reason };
    }
    await this.browser.session.set({
      [DOWNLOAD_LOAD_KEY]: { tabId, selected: [...selected], at: this.now() },
    });
    return { ok: true };
  }

  /** This tab's waiting Load library, taken once; one left too long, or for another tab, is not. */
  private async resume(tabId: number): Promise<{ selected: string[] } | null> {
    const stored = (await this.browser.session.get([DOWNLOAD_LOAD_KEY]))[DOWNLOAD_LOAD_KEY];
    if (
      !isRecord(stored) ||
      stored.tabId !== tabId ||
      typeof stored.at !== 'number' ||
      !Array.isArray(stored.selected)
    ) {
      return null;
    }
    await this.browser.session.remove([DOWNLOAD_LOAD_KEY]);
    if (this.now() - stored.at > LOAD_WAIT_MS) {
      return null;
    }
    return {
      selected: stored.selected.filter((id): id is string => typeof id === 'string' && id !== ''),
    };
  }

  private async formats(chosen: string[] | undefined): Promise<string[]> {
    if (chosen !== undefined) {
      const kept = [...new Set(chosen.filter(isDownloadFormat))];
      await this.browser.local.set({ [DOWNLOAD_FORMATS_KEY]: kept });
      return kept;
    }
    const stored = (await this.browser.local.get([DOWNLOAD_FORMATS_KEY]))[DOWNLOAD_FORMATS_KEY];
    return Array.isArray(stored) ? stored.filter(isDownloadFormat) : [];
  }

  private async lookup(sunoIds: string[]): Promise<ClipLookupReply> {
    const state = await this.connection.state();
    if (state.status !== 'connected') {
      return { ok: false, unavailable: true, message: DISCONNECTED };
    }
    if (!state.scopes.includes('suno.sync')) {
      return { ok: false, unavailable: true, message: NO_SYNC_SCOPE };
    }
    const ids = [...new Set(sunoIds)];
    const rows: ClipLookupRow[] = [];
    for (let start = 0; start < ids.length; start += LOOKUP_BATCH) {
      let response: Response;
      try {
        response = await this.connection.call(CLIP_LOOKUP_PATH, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ sunoIds: ids.slice(start, start + LOOKUP_BATCH) }),
        });
      } catch (error) {
        return error instanceof DisconnectedError
          ? { ok: false, unavailable: true, message: DISCONNECTED }
          : { ok: false, unavailable: false, message: UNREACHABLE };
      }
      let body: unknown;
      try {
        body = response.ok ? await response.json() : undefined;
      } catch {
        body = undefined;
      }
      const items = isRecord(body) ? body.items : undefined;
      if (!response.ok || !Array.isArray(items) || !items.every(isRow)) {
        return {
          ok: false,
          unavailable: false,
          message: `n8Tracks did not answer the lookup (${String(response.status)}).`,
        };
      }
      rows.push(...items.map(rowOf));
    }
    return { ok: true, rows };
  }
}
