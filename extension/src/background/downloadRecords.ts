import type { DownloadFile, DownloadRun } from '../download/downloader.ts';
import { isDownloadFormat, type DownloadFormat } from '../download/selection.ts';
import type { RecordingStatus } from '../messages.ts';
import { DisconnectedError, type Connection } from './connection.ts';

/**
 * Download records (#222), in the service worker: each file the download queue saves is reported
 * to n8Tracks once (`POST /api/v1/suno/downloads`, with the extension's `suno.sync` token), so the
 * user can see on the Generation which outputs they already have, and the Download view can skip
 * them. Reporting never holds up or fails a download.
 *
 * - **Once.** A saved file carries a record ID made when it was saved; the report is sent under it,
 *   and n8Tracks records an ID once, so a report sent again is not a second record. A failed or
 *   cancelled file is never reported.
 * - **Retries.** A report is tried three times. One that still fails is kept in
 *   `chrome.storage.local` with the n8Tracks address it was made for, and sent when the connection
 *   next works: when the service worker starts, when another file is saved, and when the panel
 *   reads the run. At most 500 are kept, the oldest dropped first. Pairing with another n8Tracks
 *   discards them.
 * - **Refused.** A report n8Tracks refuses for good (422, or 403 for a credential without
 *   `suno.sync`) is dropped and counted as not recorded.
 * - **Not connected.** A file saved while the extension holds no token is not recorded, and is
 *   counted so the panel can say so.
 *
 * A report holds the clip's Suno ID, the format, the saved file's base name, when it finished, its
 * size, and whether the run spent a Suno unlock on the clip: never an address, a path, or Suno's
 * data.
 */

/** The local-storage key of the reports. */
export const DOWNLOAD_RECORDS_KEY = 'downloadRecords';

/** The n8Tracks endpoint a report is sent to. */
export const DOWNLOADS_PATH = 'api/v1/suno/downloads';

/** How many times a report is tried before it is kept for later. */
export const REPORT_TRIES = 3;

/** The waits between tries. */
export const RETRY_WAITS_MS: readonly number[] = [1_000, 4_000];

/** The most reports kept to send later. */
export const REPORTS_KEPT = 500;

/** One file's report, as sent. */
export interface DownloadReport {
  id: string;
  sunoId: string;
  format: DownloadFormat;
  fileName: string;
  completedAt: string;
  sizeBytes: number | null;
  spentUnlock: boolean;
}

/** What is kept between service worker starts. */
interface StoredRecords {
  /** The n8Tracks address the pending reports were made for. */
  address: string | null;
  /** Reports still to send, oldest first. */
  pending: DownloadReport[];
  /** Record IDs of the run's saved files already handled (sent, kept, or counted). */
  seen: string[];
  /** Record IDs of the run's files n8Tracks refused. */
  refused: string[];
  /** Record IDs of the run's files saved while not connected. */
  unrecorded: string[];
}

/** The part of `chrome.storage.local` the records use. */
export interface RecordStorage {
  get(keys: string[]): Promise<Record<string, unknown>>;
  set(items: Record<string, unknown>): Promise<void>;
}

export interface DownloadRecorderOptions {
  connection: Pick<Connection, 'call' | 'pairedAddress'>;
  storage: RecordStorage;
  /** Waits; `setTimeout` unless a test stands in. */
  sleep?: (ms: number) => Promise<void>;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function textList(value: unknown): string[] {
  return Array.isArray(value)
    ? value.filter((item): item is string => typeof item === 'string')
    : [];
}

function isReport(value: unknown): value is DownloadReport {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.sunoId === 'string' &&
    isDownloadFormat(value.format) &&
    typeof value.fileName === 'string' &&
    typeof value.completedAt === 'string' &&
    (value.sizeBytes === null || typeof value.sizeBytes === 'number') &&
    typeof value.spentUnlock === 'boolean'
  );
}

function storedRecords(value: unknown): StoredRecords {
  if (!isRecord(value)) {
    return { address: null, pending: [], seen: [], refused: [], unrecorded: [] };
  }
  return {
    address: typeof value.address === 'string' ? value.address : null,
    pending: Array.isArray(value.pending) ? value.pending.filter(isReport) : [],
    seen: textList(value.seen),
    refused: textList(value.refused),
    unrecorded: textList(value.unrecorded),
  };
}

/** The report of a saved file, or null for a file not saved. */
export function reportOf(file: DownloadFile, run: DownloadRun): DownloadReport | null {
  if (file.state !== 'saved' || file.recordId === null || file.savedName === null) {
    return null;
  }
  return {
    id: file.recordId,
    sunoId: file.sunoId,
    format: file.format,
    fileName: file.savedName,
    completedAt: file.savedAt ?? new Date().toISOString(),
    sizeBytes: file.received > 0 ? file.received : file.total,
    spentUnlock: run.unlocks.spent.includes(file.sunoId),
  };
}

/** How sending one report went. */
type Sent = 'recorded' | 'refused' | 'later' | 'disconnected';

export class DownloadRecorder {
  private readonly connection: Pick<Connection, 'call' | 'pairedAddress'>;
  private readonly storage: RecordStorage;
  private readonly sleep: (ms: number) => Promise<void>;
  /** Storage changes, one after another. */
  private writes: Promise<unknown> = Promise.resolve();
  private sending: Promise<void> | null = null;

  constructor(options: DownloadRecorderOptions) {
    this.connection = options.connection;
    this.storage = options.storage;
    this.sleep =
      options.sleep ??
      ((ms) =>
        new Promise((resolve) => {
          setTimeout(resolve, ms);
        }));
  }

  /**
   * The queue changed: each file saved since is reported (or counted, when not connected), and
   * the reports waiting are sent. Resolves once they are noted, without waiting for n8Tracks.
   */
  async observe(run: DownloadRun): Promise<void> {
    const address = await this.connection.pairedAddress();
    // Set inside the change; an object, so the check after it reads what was set.
    const noted = { added: false };
    await this.update((records) => {
      const inRun = new Set(
        run.files.flatMap((file) => (file.recordId === null ? [] : [file.recordId])),
      );
      const keep = (ids: string[]) => ids.filter((id) => inRun.has(id));
      const next: StoredRecords = {
        ...this.forAddress(records, address),
        seen: keep(records.seen),
        refused: keep(records.refused),
        unrecorded: keep(records.unrecorded),
      };
      for (const file of run.files) {
        const report = reportOf(file, run);
        if (report === null || next.seen.includes(report.id)) {
          continue;
        }
        next.seen.push(report.id);
        if (address === null) {
          next.unrecorded.push(report.id);
          continue;
        }
        next.pending.push(report);
        noted.added = true;
      }
      next.pending = next.pending.slice(-REPORTS_KEPT);
      return next;
    });
    if (noted.added) {
      void this.flush();
    }
  }

  /**
   * Sends the reports waiting, one after another, each tried three times; stops at the first that
   * still fails, keeping it and the rest for later. Resolves when this pass is over.
   */
  flush(): Promise<void> {
    this.sending ??= this.send().finally(() => {
      this.sending = null;
    });
    return this.sending;
  }

  /** How recording stands, for the panel: the reports waiting, and the run's files refused or not recorded. */
  async status(): Promise<RecordingStatus> {
    const records = storedRecords(
      (await this.storage.get([DOWNLOAD_RECORDS_KEY]))[DOWNLOAD_RECORDS_KEY],
    );
    const address = await this.connection.pairedAddress();
    return {
      connected: address !== null,
      pending: this.forAddress(records, address).pending.length,
      refused: records.refused.length,
      unrecorded: records.unrecorded.length,
    };
  }

  /** The records, with the reports discarded when the extension is now paired with another n8Tracks. */
  private forAddress(records: StoredRecords, address: string | null): StoredRecords {
    if (address === null || records.address === address) {
      return { ...records, pending: [...records.pending] };
    }
    return { ...records, address, pending: [] };
  }

  private async read(): Promise<StoredRecords> {
    return storedRecords((await this.storage.get([DOWNLOAD_RECORDS_KEY]))[DOWNLOAD_RECORDS_KEY]);
  }

  private update(change: (records: StoredRecords) => StoredRecords): Promise<void> {
    const next = this.writes.then(async () => {
      await this.storage.set({ [DOWNLOAD_RECORDS_KEY]: change(await this.read()) });
    });
    this.writes = next.catch(() => undefined);
    return next;
  }

  private async send(): Promise<void> {
    for (;;) {
      const address = await this.connection.pairedAddress();
      if (address === null) {
        return;
      }
      const records = this.forAddress(await this.read(), address);
      const report = records.pending[0];
      if (report === undefined) {
        return;
      }
      const sent = await this.sendOne(report);
      if (sent === 'later' || sent === 'disconnected') {
        return;
      }
      await this.update((current) => {
        const next = this.forAddress(current, address);
        return {
          ...next,
          pending: next.pending.filter((item) => item.id !== report.id),
          refused:
            sent === 'refused' && next.seen.includes(report.id)
              ? [...next.refused, report.id]
              : next.refused,
        };
      });
    }
  }

  private async sendOne(report: DownloadReport): Promise<Sent> {
    for (let attempt = 1; ; attempt += 1) {
      const sent = await this.tryOnce(report);
      if (sent !== 'later' || attempt >= REPORT_TRIES) {
        return sent;
      }
      await this.sleep(RETRY_WAITS_MS[attempt - 1] ?? RETRY_WAITS_MS.at(-1) ?? 0);
    }
  }

  private async tryOnce(report: DownloadReport): Promise<Sent> {
    try {
      const response = await this.connection.call(DOWNLOADS_PATH, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(report),
      });
      if (response.ok) {
        return 'recorded';
      }
      return response.status === 403 || response.status === 422 ? 'refused' : 'later';
    } catch (error) {
      return error instanceof DisconnectedError ? 'disconnected' : 'later';
    }
  }
}
