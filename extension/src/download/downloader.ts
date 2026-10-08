import { audioAddressOf, type AudioAddress } from '../adapter/addresses.ts';
import type { PrepareJob, PrepareOutcome } from '../adapter/downloadSteps.ts';
import { DOWNLOAD_FOLDER, fileNameFor } from './fileName.ts';
import { FORMATS, isDownloadFormat, type DownloadFormat, type PlanEntry } from './selection.ts';

/**
 * The download queue (#216), in the service worker. It takes the Download view's plan, obtains
 * each file's address, hands it to the browser's downloads interface with the relative name
 * `n8Tracks/<name>`, follows it to the end, and reads back the name the browser saved it under.
 * Two files are worked on at a time; one that fails never stops the rest.
 *
 * - **Addresses.** WAV, MP3, and M4A are prepared on Suno's page, in the run's Suno tab
 *   (`adapter/downloadSteps.ts`), one at a time and just before they are downloaded: a signed
 *   address expires after an hour, so it is never queued. The streaming-quality M4A comes from the
 *   clip data. Either is handed on only when it is on a listed Suno audio host
 *   (`audioAddressOf`), and with nothing added: no header, no cookie, no credential.
 * - **Unlocks.** A clip not yet unlocked is unlocked ("Unlock & Download") only when the user
 *   confirmed it in the summary before Start; a run never unlocks more clips than were confirmed,
 *   and never buys download packs.
 * - **Persistence.** The queue is kept in `chrome.storage.local`, so a restarted service worker
 *   carries on. After a browser restart it waits for Resume, and files that were in flight start
 *   again from zero.
 *
 * Nothing here calls n8Tracks: downloading imports, syncs, and changes nothing in the catalog. A
 * saved file gets a record ID, and the service worker reports it (#222, `records.ts`).
 */

/** The local-storage key the queue is kept under. */
export const DOWNLOAD_QUEUE_KEY = 'downloadQueue';

/** How many files are worked on at once. */
export const DOWNLOADS_AT_ONCE = 2;

/** How often a download in progress is read, for its progress. */
export const PROGRESS_POLL_MS = 1000;

/** The browser's reasons a download stopped that an expired signed address gives (HTTP 401/403). */
const EXPIRED_ERRORS: ReadonlySet<string> = new Set(['SERVER_FORBIDDEN', 'SERVER_UNAUTHORIZED']);

/** Where one file has got to. */
export type FileState = 'queued' | 'preparing' | 'downloading' | 'saved' | 'failed' | 'cancelled';

/** One file of the queue: a clip in a format, and how it went. */
export interface DownloadFile {
  /** `<Suno ID>:<format>`: a clip and format is queued once. */
  key: string;
  sunoId: string;
  title: string;
  displayName: string;
  artist: string | null;
  format: DownloadFormat;
  unlocked: boolean;
  streamAddress: string | null;
  /** The name chosen for it (`fileName.ts`). */
  fileName: string;
  state: FileState;
  /** A queued file waiting for Resume, and why; null otherwise. */
  paused: string | null;
  downloadId: number | null;
  received: number;
  total: number | null;
  /** Why it failed, in plain words. */
  reason: string | null;
  /** The name the browser saved it under, once saved. */
  savedName: string | null;
  /** The browser or another extension did not apply the chosen name (its numbering aside). */
  renamed: boolean;
  /** An M4A saved as `.mp4`, which n8Tracks does not scan until it is renamed to `.m4a`. */
  renameToM4a: boolean;
  /** Its address was fetched afresh once after it expired. */
  fetchedAgain: boolean;
  /**
   * Once saved: the ID its download record is reported to n8Tracks under (#222), new for each
   * save, so a report sent again is recorded once and a file downloaded again is a new record.
   */
  recordId: string | null;
  /** Once saved: when, as ISO 8601 UTC. */
  savedAt: string | null;
}

/** The queue, as kept and as the panel shows it. */
export interface DownloadRun {
  files: DownloadFile[];
  /** The Suno tab that prepares WAV, MP3, and M4A; null once it was closed. */
  tabId: number | null;
  /** The clips the user confirmed unlocking, and those this run has unlocked. */
  unlocks: { confirmed: string[]; spent: string[] };
}

/** A download as the browser's downloads interface reports it. */
export interface DownloadItem {
  state: 'in_progress' | 'interrupted' | 'complete';
  /** The full path it is saved under. */
  filename: string;
  /** Why it stopped (`SERVER_FORBIDDEN`, `USER_CANCELED`, ...), or null. */
  error: string | null;
  bytesReceived: number;
  /** 0 when unknown. */
  totalBytes: number;
}

/** The parts of the browser's downloads interface the queue uses. */
export interface DownloadsInterface {
  /** Starts a download of a listed audio address under `path`; answers its ID. */
  start(address: AudioAddress, path: string): Promise<number>;
  cancel(id: number): Promise<void>;
  item(id: number): Promise<DownloadItem | null>;
}

export interface DownloaderOptions {
  downloads: DownloadsInterface;
  /** Prepares one file in the run's Suno tab (the content script's `download-prepare`). */
  prepare(tabId: number, job: PrepareJob): Promise<PrepareOutcome>;
  storage: {
    get(keys: string[]): Promise<Record<string, unknown>>;
    set(items: Record<string, unknown>): Promise<void>;
  };
  /** Told of every change, to show it in the run's tab. */
  onChange?: (run: DownloadRun) => void;
  /**
   * Whether the browser started since the queue was last read (its session storage is empty), asked
   * once; false unless set.
   */
  browserStarted?: () => Promise<boolean>;
  /** Waits; `setTimeout` unless a test stands in. */
  sleep?: (ms: number) => Promise<void>;
  /** A new record ID for a saved file (#222); `crypto.randomUUID` unless a test stands in. */
  newId?: () => string;
  /** Milliseconds since the epoch; `Date.now` unless a test stands in. */
  now?: () => number;
}

const EMPTY_RUN = (): DownloadRun => ({
  files: [],
  tabId: null,
  unlocks: { confirmed: [], spent: [] },
});

const ACTIVE: ReadonlySet<FileState> = new Set(['queued', 'preparing', 'downloading']);

/**
 * Not yet given an address: a file preparing waits its turn for the page, one at a time, and only
 * one is on the page.
 */
const WAITING: ReadonlySet<FileState> = new Set(['queued', 'preparing']);

/** Whether the format is prepared on Suno's page (not the stream). */
export function needsPage(format: DownloadFormat): format is 'wav' | 'mp3' | 'm4a' {
  return format !== 'm4a-stream';
}

/** Whether the format spends an unlock on a clip not yet unlocked. */
function spendsUnlock(format: DownloadFormat): boolean {
  return FORMATS.find((choice) => choice.format === format)?.unlock === true;
}

/** The clips of a plan that would spend an unlock: not unlocked, in WAV, MP3, or M4A. */
export function clipsToUnlock(entries: readonly PlanEntry[]): string[] {
  return [
    ...new Set(
      entries
        .filter((entry) => !entry.unlocked && spendsUnlock(entry.format))
        .map((entry) => entry.sunoId),
    ),
  ];
}

/** The last part of a path, on any system. */
function baseName(path: string): string {
  return path.split(/[\\/]/).at(-1) ?? path;
}

/** The folder a path's file is in, on any system. */
function folderName(path: string): string {
  return path.split(/[\\/]/).at(-2) ?? '';
}

/** Whether `saved` is `chosen`, or the browser's numbering of it (`… (1).wav`). */
function isChosenName(saved: string, chosen: string): boolean {
  if (saved === chosen) {
    return true;
  }
  const dot = chosen.lastIndexOf('.');
  const stem = chosen.slice(0, dot);
  const extension = chosen.slice(dot);
  return (
    saved.startsWith(`${stem} (`) &&
    saved.endsWith(`)${extension}`) &&
    /^\d+$/.test(saved.slice(stem.length + 2, saved.length - extension.length - 1))
  );
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

const FILE_STATES: readonly FileState[] = [
  'queued',
  'preparing',
  'downloading',
  'saved',
  'failed',
  'cancelled',
];

/** A stored file, checked member by member; null when it is not one. */
function storedFile(value: unknown): DownloadFile | null {
  if (
    !isRecord(value) ||
    typeof value.key !== 'string' ||
    typeof value.sunoId !== 'string' ||
    typeof value.title !== 'string' ||
    typeof value.displayName !== 'string' ||
    !(value.artist === null || typeof value.artist === 'string') ||
    !isDownloadFormat(value.format) ||
    typeof value.unlocked !== 'boolean' ||
    !(value.streamAddress === null || typeof value.streamAddress === 'string') ||
    typeof value.fileName !== 'string' ||
    !FILE_STATES.includes(value.state as FileState)
  ) {
    return null;
  }
  const text = (member: unknown) => (typeof member === 'string' ? member : null);
  return {
    key: value.key,
    sunoId: value.sunoId,
    title: value.title,
    displayName: value.displayName,
    artist: value.artist,
    format: value.format,
    unlocked: value.unlocked,
    streamAddress: value.streamAddress,
    fileName: value.fileName,
    state: value.state as FileState,
    paused: text(value.paused),
    downloadId: typeof value.downloadId === 'number' ? value.downloadId : null,
    received: typeof value.received === 'number' ? value.received : 0,
    total: typeof value.total === 'number' ? value.total : null,
    reason: text(value.reason),
    savedName: text(value.savedName),
    renamed: value.renamed === true,
    renameToM4a: value.renameToM4a === true,
    fetchedAgain: value.fetchedAgain === true,
    recordId: text(value.recordId),
    savedAt: text(value.savedAt),
  };
}

function textList(value: unknown): string[] {
  return Array.isArray(value)
    ? value.filter((item): item is string => typeof item === 'string')
    : [];
}

/** The stored queue, or an empty one. */
export function storedRun(value: unknown): DownloadRun {
  if (!isRecord(value) || !Array.isArray(value.files)) {
    return EMPTY_RUN();
  }
  const unlocks = isRecord(value.unlocks) ? value.unlocks : {};
  return {
    files: value.files.map(storedFile).filter((file): file is DownloadFile => file !== null),
    tabId: typeof value.tabId === 'number' ? value.tabId : null,
    unlocks: { confirmed: textList(unlocks.confirmed), spent: textList(unlocks.spent) },
  };
}

/** Words for the panel and the queue. */
export const DOWNLOAD_WORDS = {
  tabClosed: "The Suno tab was closed. Open Suno's Library in a tab and press Resume.",
  browserRestarted: 'The browser restarted. Press Resume to download the rest.',
  noUnlock: 'it needs a Suno download unlock that was not confirmed for this run',
  streamNotListed: "the clip's stream address is not on a listed Suno audio host",
  addressNotListed: "Suno's address for the file is not on a listed Suno audio host",
  refused: 'the browser did not start the download',
  lost: 'the browser no longer knows this download',
  cancelled: 'the download was cancelled',
} as const;

export class Downloader {
  private readonly options: DownloaderOptions;
  private readonly sleep: (ms: number) => Promise<void>;
  private run: DownloadRun = EMPTY_RUN();
  /** The files being worked on, by key. */
  private readonly working = new Set<string>();
  /** Wakes the follower of a download when the browser reports a change. */
  private readonly wakers = new Map<number, () => void>();
  /** Page preparations run one at a time: there is one Download dialog. */
  private pageTurn: Promise<unknown> = Promise.resolve();
  private loaded: Promise<void> | null = null;

  constructor(options: DownloaderOptions) {
    this.options = options;
    this.sleep =
      options.sleep ??
      ((ms) =>
        new Promise((resolve) => {
          setTimeout(resolve, ms);
        }));
  }

  /**
   * Reads the stored queue once. After a browser restart every unfinished file waits for Resume
   * and starts again from zero; after a service worker restart the queue carries on, following the
   * downloads already in the browser.
   */
  async restore(): Promise<void> {
    this.loaded ??= (async () => {
      const browserStarted = (await this.options.browserStarted?.().catch(() => false)) ?? false;
      const stored = (await this.options.storage.get([DOWNLOAD_QUEUE_KEY]))[DOWNLOAD_QUEUE_KEY];
      this.run = storedRun(stored);
      for (const file of this.run.files) {
        if (file.state === 'preparing' || (browserStarted && file.state === 'downloading')) {
          Object.assign(file, { state: 'queued', downloadId: null, received: 0, total: null });
        }
        if (browserStarted && file.state === 'queued') {
          file.paused = DOWNLOAD_WORDS.browserRestarted;
        }
      }
      await this.save();
      for (const file of this.run.files) {
        if (file.state === 'downloading' && file.downloadId !== null) {
          this.begin(file.key, () => this.follow(file.key, file.downloadId ?? -1));
        }
      }
      this.pump();
    })();
    await this.loaded;
  }

  /** The queue as it stands. */
  async current(): Promise<DownloadRun> {
    await this.restore();
    return structuredClone(this.run);
  }

  /**
   * Start, from the Download view in tab `tabId`: adds the plan's files, each clip and format
   * once. `unlocks` is the count the user confirmed; it must be the plan's clips not yet unlocked
   * in WAV, MP3, or M4A, or nothing is added. Answers why not, or null.
   */
  async add(tabId: number, entries: readonly PlanEntry[], unlocks: number): Promise<string | null> {
    await this.restore();
    const toUnlock = clipsToUnlock(entries);
    if (unlocks !== toUnlock.length) {
      return `The run needs ${String(toUnlock.length)} Suno download unlocks, but ${String(unlocks)} were confirmed. Check the summary and start again.`;
    }
    const busy = this.run.files.some((file) => ACTIVE.has(file.state));
    if (!busy) {
      this.run = EMPTY_RUN();
    }
    for (const entry of entries) {
      const key = `${entry.sunoId}:${entry.format}`;
      const existing = this.run.files.find((file) => file.key === key);
      if (existing !== undefined && ACTIVE.has(existing.state)) {
        continue;
      }
      this.run.files = this.run.files.filter((file) => file.key !== key);
      this.run.files.push({
        key,
        sunoId: entry.sunoId,
        title: entry.title,
        displayName: entry.displayName,
        artist: entry.artist,
        format: entry.format,
        unlocked: entry.unlocked,
        streamAddress: entry.streamAddress,
        fileName: fileNameFor(entry),
        state: 'queued',
        paused: null,
        downloadId: null,
        received: 0,
        total: null,
        reason: null,
        savedName: null,
        renamed: false,
        renameToM4a: false,
        fetchedAgain: false,
        recordId: null,
        savedAt: null,
      });
    }
    this.run.tabId = tabId;
    this.run.unlocks.confirmed = [...new Set([...this.run.unlocks.confirmed, ...toUnlock])];
    await this.changed();
    this.pump();
    return null;
  }

  /** Cancel: queued files stop, and files in flight are cancelled through the downloads interface. */
  async cancel(): Promise<void> {
    await this.restore();
    const inFlight: number[] = [];
    for (const file of this.run.files) {
      if (!ACTIVE.has(file.state)) {
        continue;
      }
      if (file.state === 'downloading' && file.downloadId !== null) {
        inFlight.push(file.downloadId);
      }
      Object.assign(file, { state: 'cancelled', paused: null, reason: DOWNLOAD_WORDS.cancelled });
    }
    await this.changed();
    for (const id of inFlight) {
      await this.options.downloads.cancel(id).catch(() => undefined);
      this.wakers.get(id)?.();
    }
  }

  /** Retry failed downloads: only the files that failed run again, from the start. */
  async retry(): Promise<void> {
    await this.restore();
    for (const file of this.run.files) {
      if (file.state === 'failed') {
        Object.assign(file, {
          state: 'queued',
          paused: null,
          reason: null,
          downloadId: null,
          received: 0,
          total: null,
          fetchedAgain: false,
        });
      }
    }
    await this.changed();
    this.pump();
  }

  /** Resume, from the Download view in tab `tabId`: the files waiting go on, prepared there. */
  async resume(tabId: number): Promise<void> {
    await this.restore();
    this.run.tabId = tabId;
    for (const file of this.run.files) {
      file.paused = null;
    }
    await this.changed();
    this.pump();
  }

  /** The run's Suno tab was closed: the files that need the page wait for Resume. */
  async tabClosed(tabId: number): Promise<void> {
    await this.restore();
    if (this.run.tabId !== tabId) {
      return;
    }
    this.run.tabId = null;
    for (const file of this.run.files) {
      if (file.state === 'queued' && needsPage(file.format)) {
        file.paused = DOWNLOAD_WORDS.tabClosed;
      }
    }
    await this.changed();
  }

  /** The browser reported a change to download `id`. */
  downloadChanged(id: number): void {
    this.wakers.get(id)?.();
  }

  private file(key: string): DownloadFile | undefined {
    return this.run.files.find((file) => file.key === key);
  }

  private async save(): Promise<void> {
    await this.options.storage.set({ [DOWNLOAD_QUEUE_KEY]: this.run });
  }

  private async changed(): Promise<void> {
    await this.save();
    this.options.onChange?.(structuredClone(this.run));
  }

  private async update(key: string, change: Partial<DownloadFile>): Promise<void> {
    const file = this.file(key);
    if (file !== undefined) {
      Object.assign(file, change);
      await this.changed();
    }
  }

  /** Whether the file is still the one being worked on, in `state`. */
  private still(key: string, state: FileState): boolean {
    return this.file(key)?.state === state;
  }

  private begin(key: string, work: () => Promise<void>): void {
    this.working.add(key);
    void work()
      .catch(async () => {
        if (this.file(key) !== undefined && ACTIVE.has(this.file(key)?.state ?? 'saved')) {
          await this.update(key, { state: 'failed', reason: 'the download stopped with an error' });
        }
      })
      .finally(() => {
        this.working.delete(key);
        this.pump();
      });
  }

  /** Starts queued files, in order, while fewer than two are worked on. */
  private pump(): void {
    for (const file of this.run.files) {
      if (this.working.size >= DOWNLOADS_AT_ONCE) {
        return;
      }
      if (file.state === 'queued' && file.paused === null && !this.working.has(file.key)) {
        this.begin(file.key, () => this.work(file.key));
      }
    }
  }

  private async work(key: string): Promise<void> {
    const file = this.file(key);
    if (file?.state !== 'queued') {
      return;
    }
    await this.update(key, { state: 'preparing', reason: null });
    const address = await this.addressOf(key);
    if (address === null || !this.still(key, 'preparing')) {
      return;
    }
    let id: number;
    try {
      id = await this.options.downloads.start(address, `${DOWNLOAD_FOLDER}/${file.fileName}`);
    } catch {
      await this.update(key, { state: 'failed', reason: DOWNLOAD_WORDS.refused });
      return;
    }
    if (!this.still(key, 'preparing')) {
      // Cancelled while the browser was starting it.
      await this.options.downloads.cancel(id).catch(() => undefined);
      return;
    }
    await this.update(key, { state: 'downloading', downloadId: id, received: 0, total: null });
    await this.follow(key, id);
  }

  /**
   * The file's address: the stream from the clip data, or a file prepared in the run's Suno tab.
   * Null when the file failed or waits, which is recorded on it (and on the files it affects).
   */
  private async addressOf(key: string): Promise<AudioAddress | null> {
    const file = this.file(key);
    if (file === undefined) {
      return null;
    }
    const format = file.format;
    if (!needsPage(format)) {
      const address = audioAddressOf(file.streamAddress);
      if (address === null) {
        await this.update(key, { state: 'failed', reason: DOWNLOAD_WORDS.streamNotListed });
      }
      return address;
    }
    const turn = this.pageTurn.then(() => this.prepareInTab(key, format));
    this.pageTurn = turn.catch(() => undefined);
    return turn;
  }

  /** One preparation on the page, with the unlock decided just before it. */
  private async prepareInTab(
    key: string,
    format: 'wav' | 'mp3' | 'm4a',
  ): Promise<AudioAddress | null> {
    const file = this.file(key);
    if (file?.state !== 'preparing') {
      return null;
    }
    const tabId = this.run.tabId;
    if (tabId === null) {
      await this.pausePageFiles(key, DOWNLOAD_WORDS.tabClosed);
      return null;
    }
    const { unlocks } = this.run;
    const unlocking = !file.unlocked && !unlocks.spent.includes(file.sunoId);
    if (unlocking && !unlocks.confirmed.includes(file.sunoId)) {
      await this.update(key, { state: 'failed', reason: DOWNLOAD_WORDS.noUnlock });
      return null;
    }
    let outcome: PrepareOutcome;
    try {
      outcome = await this.options.prepare(tabId, { sunoId: file.sunoId, format });
    } catch {
      outcome = {
        ok: false,
        scope: 'page',
        reason: 'the Suno tab did not answer',
        pressed: false,
      };
    }
    if (unlocking && outcome.pressed) {
      // Pressing "Unlock & Download" on a clip not yet unlocked spends its unlock, whatever came next.
      unlocks.spent = [...new Set([...unlocks.spent, file.sunoId])];
    }
    if (!this.still(key, 'preparing')) {
      await this.changed();
      return null;
    }
    if (!outcome.ok) {
      if (outcome.scope === 'page') {
        await this.pausePageFiles(key, `${outcome.reason}. Open Suno's Library and press Resume.`);
      } else if (outcome.scope === 'format') {
        await this.failFormat(key, format, outcome.reason);
      } else {
        await this.update(key, { state: 'failed', reason: outcome.reason });
      }
      return null;
    }
    const address = audioAddressOf(outcome.address);
    if (address === null) {
      await this.update(key, { state: 'failed', reason: DOWNLOAD_WORDS.addressNotListed });
    }
    return address;
  }

  /**
   * The tab cannot prepare files: this one and every one that needs the page and has not reached
   * it yet (queued, or waiting its turn) wait.
   */
  private async pausePageFiles(key: string, reason: string): Promise<void> {
    for (const file of this.run.files) {
      if (file.key === key || (WAITING.has(file.state) && needsPage(file.format))) {
        Object.assign(file, { state: 'queued', paused: reason });
      }
    }
    await this.changed();
  }

  /**
   * A step no longer matches Suno's page: this file and every file of its format that has not
   * reached the page yet (queued, or waiting its turn) stop.
   */
  private async failFormat(key: string, format: DownloadFormat, reason: string): Promise<void> {
    for (const file of this.run.files) {
      if (file.key === key || (WAITING.has(file.state) && file.format === format)) {
        Object.assign(file, { state: 'failed', paused: null, reason });
      }
    }
    await this.changed();
  }

  private waitForChange(id: number): Promise<void> {
    return new Promise((resolve) => {
      const done = () => {
        this.wakers.delete(id);
        resolve();
      };
      this.wakers.set(id, done);
      void this.sleep(PROGRESS_POLL_MS).then(done);
    });
  }

  /** Follows download `id` of a file to its end. */
  private async follow(key: string, id: number): Promise<void> {
    for (;;) {
      if (!this.still(key, 'downloading')) {
        return;
      }
      const item = await this.options.downloads.item(id).catch(() => null);
      if (!this.still(key, 'downloading')) {
        return;
      }
      if (item === null) {
        await this.update(key, { state: 'failed', reason: DOWNLOAD_WORDS.lost });
        return;
      }
      if (item.state === 'complete') {
        await this.saved(key, item);
        return;
      }
      if (item.state === 'interrupted') {
        await this.interrupted(key, item);
        return;
      }
      const file = this.file(key);
      if (
        file !== undefined &&
        (file.received !== item.bytesReceived || file.total !== (item.totalBytes || null))
      ) {
        await this.update(key, {
          received: item.bytesReceived,
          total: item.totalBytes > 0 ? item.totalBytes : null,
        });
      }
      await this.waitForChange(id);
    }
  }

  /** Saved: the name the browser chose is read back and compared with the one asked for. */
  private async saved(key: string, item: DownloadItem): Promise<void> {
    const file = this.file(key);
    if (file === undefined) {
      return;
    }
    const savedName = baseName(item.filename);
    await this.update(key, {
      state: 'saved',
      savedName,
      renamed:
        !isChosenName(savedName, file.fileName) || folderName(item.filename) !== DOWNLOAD_FOLDER,
      renameToM4a: file.format !== 'wav' && file.format !== 'mp3' && /\.mp4$/i.test(savedName),
      received: item.bytesReceived,
      recordId: this.options.newId?.() ?? crypto.randomUUID(),
      savedAt: new Date(this.options.now?.() ?? Date.now()).toISOString(),
    });
  }

  /**
   * Stopped by the browser. An expired signed address is prepared afresh once, by running the
   * page's steps again; anything else fails the file.
   */
  private async interrupted(key: string, item: DownloadItem): Promise<void> {
    const file = this.file(key);
    if (file === undefined) {
      return;
    }
    if (
      needsPage(file.format) &&
      !file.fetchedAgain &&
      item.error !== null &&
      EXPIRED_ERRORS.has(item.error)
    ) {
      Object.assign(file, { state: 'queued', fetchedAgain: true, downloadId: null, received: 0 });
      await this.changed();
      await this.work(key);
      return;
    }
    await this.update(key, {
      state: 'failed',
      reason: `the browser stopped the download (${item.error ?? 'no reason given'})`,
    });
  }
}

/**
 * The browser's own downloads interface (`chrome.downloads`), the only place the extension uses
 * it (invariant 4's guard allows it in this file alone): a listed audio address and a relative
 * name, saved without asking where, a name already taken numbered by the browser. Nothing else is
 * passed: no header, no method, no body.
 */
export function browserDownloads(
  api: typeof chrome.downloads = chrome.downloads,
): DownloadsInterface & { onChanged(listener: (id: number) => void): void } {
  return {
    start: (address, path) =>
      api.download({ url: address, filename: path, conflictAction: 'uniquify', saveAs: false }),
    cancel: (id) => api.cancel(id),
    item: async (id) => {
      const [found] = await api.search({ id });
      if (found === undefined) {
        return null;
      }
      return {
        state: found.state,
        filename: found.filename,
        error: found.error ?? null,
        bytesReceived: found.bytesReceived,
        totalBytes: found.totalBytes,
      };
    },
    onChanged: (listener) => {
      api.onChanged.addListener((delta) => {
        listener(delta.id);
      });
    },
  };
}
