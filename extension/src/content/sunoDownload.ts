import { sunoListAddress, sunoPageOf } from '../adapter/addresses.ts';
import type { Clock } from '../adapter/clock.ts';
import {
  readLibrary,
  ReadCancelled,
  ReadStop,
  type Observations,
} from '../adapter/libraryReader.ts';
import type { ObservationFeed } from '../adapter/observations.ts';
import type { Page } from '../adapter/primitives.ts';
import type { AdapterSession } from '../adapter/registry.ts';
import { loadMore } from '../adapter/workflows/loadMore.ts';
import { clipOf, type DownloadClip } from '../download/clips.ts';
import { isDownloadFormat } from '../download/selection.ts';
import type { ClipLookupReply, ClipLookupRow, Request, ResponseFor } from '../messages.ts';
import type { DownloadView } from '../panel/DownloadView.ts';

/**
 * The Suno tab's half of the Download view (#215). Load library (or Refresh) asks the service
 * worker, which refuses while a sync or Generate on Suno runs, and opens Library › Songs again; on
 * that page load the library is read the way a sync reads it (`readLibrary`, the same reader and
 * observer), with counts as it reads and Cancel. Then n8Tracks is asked which clips it has, the
 * one call the view makes to n8Tracks. Nothing is pressed on the page: reading only scrolls the
 * list (the `load-more` workflow), and nothing is sent to Suno or imported into n8Tracks.
 */

export interface SunoDownloadOptions {
  page: Page;
  session: AdapterSession;
  observations: Observations & Pick<ObservationFeed, 'downloadUsage' | 'onDownloadUsage'>;
  send: (request: Request) => Promise<unknown>;
  view: DownloadView;
  /** Opens the panel, if closed, to show the read. */
  open: () => void;
  clock?: Clock;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

const NO_ANSWER = 'The extension did not answer. Reload the Suno page.';

export class SunoDownload {
  private readonly options: SunoDownloadOptions;
  private controller: AbortController | null = null;
  private prepared = false;

  constructor(options: SunoDownloadOptions) {
    this.options = options;
    options.observations.onDownloadUsage((usage) => {
      options.view.setUsage(usage);
    });
    options.view.setUsage(options.observations.downloadUsage());
  }

  /** Whether the library is being read in this tab. */
  get running(): boolean {
    return this.controller !== null;
  }

  /** Reads the formats chosen last time into the view, once. */
  async prepare(): Promise<void> {
    if (this.prepared) {
      return;
    }
    this.prepared = true;
    const answer = (await this.options
      .send({ type: 'download-formats' })
      .catch(() => undefined)) as ResponseFor['download-formats'] | undefined;
    const formats = isRecord(answer) && Array.isArray(answer.formats) ? answer.formats : [];
    this.options.view.setFormats(formats.filter(isDownloadFormat));
  }

  /** Remembers the formats chosen. */
  async rememberFormats(formats: readonly string[]): Promise<void> {
    await this.options
      .send({ type: 'download-formats', formats: [...formats] })
      .catch(() => undefined);
  }

  /** Load library or Refresh: the library page opens again and is read on its next load. */
  async load(): Promise<void> {
    if (this.running) {
      return;
    }
    const { view } = this.options;
    let answer: unknown;
    try {
      answer = await this.options.send({
        type: 'download-begin',
        selected: view.selection.selectedIds(),
      });
    } catch {
      answer = undefined;
    }
    if (!isRecord(answer) || answer.ok !== true) {
      const message =
        isRecord(answer) && typeof answer.message === 'string' ? answer.message : NO_ANSWER;
      view.setRead({ kind: 'refused', message });
      return;
    }
    view.restart(view.selection.selectedIds());
    view.setRead({ kind: 'reading', count: 0 });
    this.options.page.go(sunoListAddress({ page: 'library' }));
  }

  /** Cancel: nothing more is read; the clips read so far stay listed. */
  cancel(): void {
    this.controller?.abort();
  }

  /** On every page load: reads the library, if this tab's Load library is waiting for it. */
  async resume(): Promise<void> {
    const answer = (await this.options.send({ type: 'download-resume' }).catch(() => undefined)) as
      ResponseFor['download-resume'] | undefined;
    const load = isRecord(answer) && isRecord(answer.load) ? answer.load : null;
    if (load === null) {
      return;
    }
    const { view, page } = this.options;
    this.options.open();
    await this.prepare();
    view.restart(load.selected);
    if (sunoPageOf(page.address()) !== 'library') {
      view.setRead({
        kind: 'refused',
        message: 'the Suno tab left the library before it was read. Load it again.',
      });
      return;
    }
    await this.read();
  }

  /** Retry after the lookup failed. */
  async retryLookup(): Promise<void> {
    await this.lookUp();
  }

  private async read(): Promise<void> {
    const { view, session } = this.options;
    const controller = new AbortController();
    this.controller = controller;
    const count = () => view.selection.all().length;
    view.setRead({ kind: 'reading', count: 0 });
    try {
      await readLibrary(
        {
          observations: this.options.observations,
          more: async (step) => {
            const result = await session.run(
              loadMore,
              {},
              this.options.clock === undefined ? {} : { clock: this.options.clock },
            );
            if (!result.ok) {
              throw new ReadStop(step, result.failure.expected);
            }
          },
          signal: controller.signal,
        },
        (records) => {
          view.addClips(records.map(clipOf).filter((clip): clip is DownloadClip => clip !== null));
          view.setRead({ kind: 'reading', count: count() });
        },
      );
      view.setRead({ kind: 'read', count: count() });
    } catch (error) {
      const reason =
        error instanceof ReadCancelled || controller.signal.aborted
          ? 'loading was cancelled'
          : error instanceof ReadStop
            ? `step '${error.step}' expected ${error.expected}`
            : 'the read stopped with an error';
      view.setRead({ kind: 'incomplete', count: count(), reason });
    } finally {
      this.controller = null;
    }
    await this.lookUp();
  }

  /** Asks n8Tracks which of the clips listed it has; the list stays usable whatever it answers. */
  private async lookUp(): Promise<void> {
    const { view } = this.options;
    const sunoIds = view.selection.all().map((clip) => clip.sunoId);
    if (sunoIds.length === 0) {
      view.setLookup({ kind: 'none' });
      return;
    }
    view.setLookup({ kind: 'checking' });
    let answer: unknown;
    try {
      answer = await this.options.send({ type: 'download-lookup', sunoIds });
    } catch {
      answer = undefined;
    }
    if (!isRecord(answer) || typeof answer.ok !== 'boolean') {
      view.setLookup({ kind: 'failed', message: NO_ANSWER });
      return;
    }
    const reply = answer as ClipLookupReply;
    if (reply.ok) {
      view.setLookup({
        kind: 'found',
        rows: new Map(reply.rows.map((row): [string, ClipLookupRow] => [row.sunoId, row])),
      });
      return;
    }
    view.setLookup(
      reply.unavailable
        ? { kind: 'unavailable', message: reply.message }
        : { kind: 'failed', message: reply.message },
    );
  }
}
