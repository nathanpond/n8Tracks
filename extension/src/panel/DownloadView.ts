import { remainingUnlocks, type DownloadUsage } from '../download/allowance.ts';
import { createdText, durationText, type DownloadClip } from '../download/clips.ts';
import {
  DownloadSelection,
  FORMATS,
  isDownloadFormat,
  type DownloadFormat,
  type ListFilter,
} from '../download/selection.ts';
import type { DownloadFile, DownloadRun } from '../download/downloader.ts';
import type { ClipLookupRow, RecordingStatus } from '../messages.ts';

/**
 * Download from Suno, in the panel on Suno (#215): the user loads their Suno library, narrows it by
 * workspace and by text in the title, selects clips one by one or all shown, chooses formats, and
 * reads what would be downloaded before anything is fetched. Listing and selecting change nothing
 * in Suno and import nothing into n8Tracks. Plain DOM in the panel's shadow root, every control
 * labelled and reachable by keyboard; the list draws only the rows in view above 200 clips.
 *
 * Start (#216) hands the plan to the service worker's download queue, once the user has confirmed
 * the Suno unlocks the run uses; the run's files then show here with their progress, and can be
 * cancelled, retried, or resumed.
 *
 * Download records (#222): the lookup says which formats of each clip were already downloaded. The
 * list can show only the clips not yet downloaded, and Skip files already downloaded (on unless
 * turned off) leaves those files out of the plan, naming them in the summary. The run says how
 * recording its files in n8Tracks stands.
 */

/** Where reading the library has got to. */
export type ReadState =
  | { kind: 'idle' }
  /** Load library was refused (a sync or Generate on Suno is running), with the reason. */
  | { kind: 'refused'; message: string }
  | { kind: 'reading'; count: number }
  | { kind: 'read'; count: number }
  /** The read stopped part-way, or was cancelled: the clips read so far stay listed. */
  | { kind: 'incomplete'; count: number; reason: string };

/** What n8Tracks said about the clips listed. */
export type LookupState =
  | { kind: 'none' }
  | { kind: 'checking' }
  | { kind: 'found'; rows: ReadonlyMap<string, ClipLookupRow> }
  /** Not connected, or the credential lacks `suno.sync`: nothing to retry. */
  | { kind: 'unavailable'; message: string }
  /** The lookup failed while connected: Retry is offered. */
  | { kind: 'failed'; message: string };

export interface DownloadViewOptions {
  /** Load library, or Refresh: the library is read again, the selection carried over. */
  load(): void;
  cancel(): void;
  /** Retry after the lookup failed. */
  retryLookup(): void;
  /** The formats chosen changed, to be remembered. */
  formatsChanged(formats: DownloadFormat[]): void;
  /** Start: the plan goes to the download queue (#216), with the unlocks the user confirmed. */
  start?(unlocks: number): void;
  /** Cancel the downloads, Retry failed downloads, or Resume (#216). */
  control?(action: 'cancel' | 'retry' | 'resume'): void;
  /** Stands in for the selection, for tests. */
  selection?: DownloadSelection;
}

/** Above this many rows shown, the list draws only those in view. */
export const VIRTUALISE_ABOVE = 200;
/** Each row's height in the list, in pixels, so the rows in view can be worked out. */
export const ROW_HEIGHT = 52;
/** How many rows are drawn around those in view when the list is virtualised. */
export const DRAWN_ROWS = 40;

/** Where the files go, said before every start. */
export const DESTINATION =
  "The files go to the n8Tracks folder in the browser's download folder. Copy them into the n8Tracks media folder yourself.";

/**
 * The browser setting the extension cannot read, said before every start: with it on, the browser
 * asks where to save every file.
 */
export const ASK_WHERE_TO_SAVE =
  'Turn off the browser\'s "Ask where to save each file before downloading" setting, or it asks for every file.';

/** What a run that unlocks clips also says (TS-004): Suno's page saves its own copy too. */
export const PAGE_COPY =
  "Suno's page may also save its own copy of a WAV, MP3, or M4A, named by its title only: that copy has no Suno ID, so n8Tracks cannot match it.";

/** The most skipped files the summary names one by one. */
export const SKIPPED_NAMED = 20;

/** What is said about the view's effect, beside Load library. */
export const NOTHING_CHANGES =
  'Loading, filtering, and selecting change nothing in Suno and import nothing into n8Tracks.';

function plural(count: number, one: string, many: string): string {
  return `${String(count)} ${count === 1 ? one : many}`;
}

function formatLabel(format: DownloadFormat): string {
  return FORMATS.find((choice) => choice.format === format)?.label ?? format;
}

/**
 * Why Start cannot be pressed, in plain words, or null when it can. A run that unlocks clips
 * starts only once the user has confirmed that count (`confirmed`, the count ticked).
 */
export function startRefusal(
  selection: DownloadSelection,
  usage: DownloadUsage | null,
  confirmed: number | null = null,
): string | null {
  if (selection.selected().length === 0) {
    return 'Select at least one clip.';
  }
  if (selection.chosenFormats().length === 0) {
    return 'Choose at least one format.';
  }
  const needed = selection.unlocksNeeded();
  if (needed > 0 && usage === null) {
    return `This run needs ${plural(needed, 'Suno download unlock', 'Suno download unlocks')}, and how many remain is not known yet.`;
  }
  if (usage !== null && needed > remainingUnlocks(usage)) {
    return `This run needs ${plural(needed, 'Suno download unlock', 'Suno download unlocks')}, but only ${String(remainingUnlocks(usage))} remain this period.`;
  }
  if (needed > 0 && confirmed !== needed) {
    return `Confirm the ${plural(needed, 'Suno download unlock', 'Suno download unlocks')} this run uses.`;
  }
  return null;
}

/** The summary's lines, before Start. */
export function summaryLines(
  selection: DownloadSelection,
  usage: DownloadUsage | null,
  filter: ListFilter,
): string[] {
  const clips = selection.selected().length;
  const formats = selection.chosenFormats();
  const files = selection.plan().length;
  const lines = [
    `Selected: ${plural(clips, 'clip', 'clips')}.`,
    formats.length === 0
      ? 'Formats: none chosen.'
      : `Formats: ${formats.map(formatLabel).join(', ')}.`,
    `Files: ${String(files)}.`,
  ];
  const hidden = selection.hiddenByFilter(filter);
  if (hidden > 0) {
    lines.push(
      `${plural(hidden, 'selected clip is', 'selected clips are')} hidden by the current filter.`,
    );
  }
  const paid = formats.some(
    (format) => FORMATS.find((choice) => choice.format === format)?.unlock === true,
  );
  if (paid) {
    const needed = selection.unlocksNeeded();
    const uses = `This run uses ${plural(needed, 'Suno download unlock', 'Suno download unlocks')} (selected clips not yet unlocked).`;
    lines.push(
      usage === null
        ? `${uses} How many remain this period is not known yet: Suno sends it when a clip's Download dialog opens. Open any clip's More options › Download on this page, then close the dialog without downloading.`
        : `${uses} ${String(remainingUnlocks(usage))} remain this period.`,
    );
  }
  const skipped = selection.skipped();
  if (skipped.length > 0) {
    const named = skipped
      .slice(0, SKIPPED_NAMED)
      .map((file) => `${file.title} (${formatLabel(file.format)})`);
    const more = skipped.length - named.length;
    lines.push(
      `Skipped, already downloaded: ${named.join(', ')}${more > 0 ? `, and ${String(more)} more` : ''}.`,
    );
  } else if (selection.skipDownloaded && !selection.downloadsKnown && clips > 0) {
    lines.push('What was already downloaded is not known, so no file is skipped.');
  }
  lines.push(DESTINATION, ASK_WHERE_TO_SAVE);
  if (formats.some((format) => format !== 'm4a-stream')) {
    lines.push(PAGE_COPY);
  }
  return lines;
}

const STATE_WORDS: Readonly<Record<DownloadFile['state'], string>> = {
  queued: 'Waiting',
  preparing: 'Being prepared on Suno',
  downloading: 'Downloading',
  saved: 'Saved',
  failed: 'Failed',
  cancelled: 'Cancelled',
};

/** How the panel says one file of the run is getting on. */
export function fileStatusText(file: DownloadFile): string {
  const format = formatLabel(file.format);
  const head = `${file.fileName} (${format})`;
  if (file.state === 'queued' && file.paused !== null) {
    return `${head}: Waiting for Resume. ${file.paused}`;
  }
  switch (file.state) {
    case 'downloading':
      return file.total !== null && file.total > 0
        ? `${head}: Downloading, ${String(Math.floor((file.received / file.total) * 100))}%`
        : `${head}: Downloading`;
    case 'failed':
      return `${head}: Failed: ${file.reason ?? 'no reason given'}.`;
    case 'saved': {
      const notes: string[] = [];
      if (file.renamed && file.savedName !== null) {
        notes.push(`saved under a different name: ${file.savedName}`);
      } else if (file.savedName !== null && file.savedName !== file.fileName) {
        notes.push(`saved as ${file.savedName}`);
      }
      if (file.renameToM4a) {
        notes.push('rename it from .mp4 to .m4a before n8Tracks scans it');
      }
      return notes.length === 0 ? `${head}: Saved` : `${head}: Saved, ${notes.join('; ')}.`;
    }
    default:
      return `${head}: ${STATE_WORDS[file.state]}`;
  }
}

/** The run's overall line: how many files are done, failed, and still to come. */
export function runSummaryText(run: DownloadRun): string {
  const count = (state: DownloadFile['state']) =>
    run.files.filter((file) => file.state === state).length;
  const total = run.files.length;
  const saved = count('saved');
  const failed = count('failed');
  const cancelled = count('cancelled');
  const waiting = run.files.filter(
    (file) => file.state === 'queued' && file.paused !== null,
  ).length;
  const going = total - saved - failed - cancelled;
  const parts = [`${String(saved)} of ${plural(total, 'file', 'files')} saved`];
  if (failed > 0) {
    parts.push(`${String(failed)} failed`);
  }
  if (cancelled > 0) {
    parts.push(`${String(cancelled)} cancelled`);
  }
  if (going > 0) {
    parts.push(waiting > 0 ? `${String(waiting)} waiting for Resume` : `${String(going)} to go`);
  }
  return `${parts.join(', ')}.`;
}

/**
 * How recording the run's files in n8Tracks stands (#222), in plain words; none when there is
 * nothing to say.
 */
export function recordingLines(status: RecordingStatus | null): string[] {
  if (status === null) {
    return [];
  }
  const lines: string[] = [];
  if (!status.connected) {
    lines.push(
      'The extension is not connected to n8Tracks: downloads still work, but they are not recorded there.',
    );
  }
  if (status.unrecorded > 0) {
    lines.push(
      `${plural(status.unrecorded, 'download was', 'downloads were')} not recorded in n8Tracks: the extension was not connected.`,
    );
  }
  if (status.pending > 0) {
    lines.push(
      `${plural(status.pending, 'download is', 'downloads are')} not yet recorded in n8Tracks; they are sent when the connection works.`,
    );
  }
  if (status.refused > 0) {
    lines.push(
      `${plural(status.refused, 'download', 'downloads')} could not be recorded: n8Tracks refused ${status.refused === 1 ? 'the report' : 'the reports'}.`,
    );
  }
  return lines;
}

/** How a row says whether n8Tracks has the clip. */
export function inN8TracksText(lookup: LookupState, sunoId: string): string {
  switch (lookup.kind) {
    case 'found': {
      const row = lookup.rows.get(sunoId);
      if (row?.generation != null) {
        return `In n8Tracks: ${row.generation.shortcode}`;
      }
      return row?.deleted === true ? 'Deleted in n8Tracks' : 'Not in n8Tracks';
    }
    case 'checking':
      return 'In n8Tracks: checking';
    case 'none':
    case 'unavailable':
    case 'failed':
      return 'In n8Tracks: unknown';
  }
}

function nameOrUnnamed(name: string): string {
  return name.trim() === '' ? '(unnamed)' : name;
}

export class DownloadView {
  readonly element: HTMLElement;
  readonly selection: DownloadSelection;
  private readonly page: Document;
  private readonly options: DownloadViewOptions;
  private read: ReadState = { kind: 'idle' };
  private lookup: LookupState = { kind: 'none' };
  private usage: DownloadUsage | null = null;
  /** The unlock count the user ticked, or null. */
  private confirmed: number | null = null;
  private run: DownloadRun | null = null;
  private records: RecordingStatus | null = null;
  private filter: ListFilter = { workspaceId: null, text: '', notDownloaded: false };
  /** The first row drawn when the list is virtualised. */
  private firstDrawn = 0;
  /** The Suno ID of the row whose checkbox has focus, to keep it when the list is drawn again. */
  private focusedRow: string | null = null;

  private readonly loadButton: HTMLButtonElement;
  private readonly cancelButton: HTMLButtonElement;
  private readonly status: HTMLElement;
  private readonly banner: HTMLElement;
  private readonly lookupLine: HTMLElement;
  private readonly retryButton: HTMLButtonElement;
  private readonly workspaceSelect: HTMLSelectElement;
  private readonly textInput: HTMLInputElement;
  private readonly notDownloadedBox: HTMLInputElement;
  private readonly notDownloadedWhy: HTMLElement;
  private readonly skipBox: HTMLInputElement;
  private readonly selectAllButton: HTMLButtonElement;
  private readonly clearButton: HTMLButtonElement;
  private readonly selectAllWhy: HTMLElement;
  private readonly listBox: HTMLElement;
  private readonly list: HTMLElement;
  private readonly empty: HTMLElement;
  private readonly formatBoxes = new Map<DownloadFormat, HTMLInputElement>();
  private readonly summary: HTMLElement;
  private readonly startButton: HTMLButtonElement;
  private readonly startWhy: HTMLElement;
  private readonly confirmLabel: HTMLLabelElement;
  private readonly confirmBox: HTMLInputElement;
  private readonly confirmText: HTMLElement;
  private readonly runSection: HTMLElement;
  private readonly runStatus: HTMLElement;
  private readonly runMessage: HTMLElement;
  private readonly recordsLine: HTMLElement;
  private readonly runList: HTMLElement;
  private readonly cancelRunButton: HTMLButtonElement;
  private readonly retryRunButton: HTMLButtonElement;
  private readonly resumeRunButton: HTMLButtonElement;
  private rows = new Map<string, { box: HTMLInputElement; known: HTMLElement }>();

  constructor(page: Document, options: DownloadViewOptions) {
    this.page = page;
    this.options = options;
    this.selection = options.selection ?? new DownloadSelection();
    this.element = this.make('section', {
      class: 'download-view',
      'aria-labelledby': 'n8-dl-title',
    });
    const heading = this.make('h3', { id: 'n8-dl-title' }, 'Download from Suno');
    const intro = this.make(
      'p',
      { class: 'detail', id: 'n8-dl-intro' },
      `Choose clips from your Suno library and the formats to download them in. ${NOTHING_CHANGES}`,
    );

    this.loadButton = this.button('Load library', () => {
      this.options.load();
    });
    this.loadButton.setAttribute('aria-describedby', 'n8-dl-intro');
    this.cancelButton = this.button('Cancel loading', () => {
      this.options.cancel();
    });
    this.cancelButton.hidden = true;
    const actions = this.make('p', { class: 'dl-actions' });
    actions.append(this.loadButton, ' ', this.cancelButton);

    this.status = this.make('p', { role: 'status', class: 'dl-status' });
    this.banner = this.make('p', { role: 'alert', class: 'warning dl-incomplete' });
    this.banner.hidden = true;
    this.lookupLine = this.make('p', { class: 'detail dl-lookup', id: 'n8-dl-lookup' });
    this.lookupLine.hidden = true;
    this.retryButton = this.button('Retry', () => {
      this.options.retryLookup();
    });
    this.retryButton.setAttribute('aria-label', 'Retry: check which clips are in n8Tracks');
    this.retryButton.hidden = true;

    // Filters.
    const filters = this.make('div', { class: 'dl-filters' });
    const workspaceLabel = this.make('label', { class: 'dl-field' }, 'Workspace ');
    this.workspaceSelect = this.make('select');
    this.workspaceSelect.addEventListener('change', () => {
      const value = this.workspaceSelect.value;
      this.filter = { ...this.filter, workspaceId: value === '' ? null : value };
      this.firstDrawn = 0;
      this.renderList();
      this.renderSummary();
    });
    workspaceLabel.append(this.workspaceSelect);
    const textLabel = this.make('label', { class: 'dl-field' }, 'Title contains ');
    this.textInput = this.make('input', { type: 'search' });
    this.textInput.addEventListener('input', () => {
      this.filter = { ...this.filter, text: this.textInput.value };
      this.firstDrawn = 0;
      this.renderList();
      this.renderSummary();
    });
    textLabel.append(this.textInput);
    const notDownloadedLabel = this.make('label', { class: 'dl-field' });
    this.notDownloadedBox = this.make('input', {
      type: 'checkbox',
      'aria-describedby': 'n8-dl-not-downloaded',
    });
    this.notDownloadedBox.addEventListener('change', () => {
      this.filter = { ...this.filter, notDownloaded: this.notDownloadedBox.checked };
      this.firstDrawn = 0;
      this.renderList();
      this.renderSummary();
    });
    notDownloadedLabel.append(this.notDownloadedBox, ' Not yet downloaded');
    this.notDownloadedWhy = this.make('span', {
      class: 'detail dl-not-downloaded-why',
      id: 'n8-dl-not-downloaded',
    });
    filters.append(workspaceLabel, textLabel, notDownloadedLabel, this.notDownloadedWhy);

    // Selection controls.
    this.selectAllWhy = this.make('span', { class: 'detail dl-select-all-why', id: 'n8-dl-all' });
    this.selectAllButton = this.button('Select all shown', () => {
      this.selection.selectAllShown(this.filter);
      this.renderList();
      this.renderSummary();
    });
    this.selectAllButton.setAttribute('aria-describedby', 'n8-dl-all');
    this.clearButton = this.button('Clear selection', () => {
      this.selection.clear();
      this.renderList();
      this.renderSummary();
    });
    const selecting = this.make('p', { class: 'dl-selecting' });
    selecting.append(this.selectAllButton, ' ', this.clearButton, ' ', this.selectAllWhy);

    // The list.
    this.listBox = this.make('div', { class: 'dl-list' });
    this.listBox.addEventListener('scroll', () => {
      this.onScroll();
    });
    this.list = this.make('ul', { 'aria-label': 'Clips' });
    this.list.addEventListener('focusin', (event) => {
      const target = event.target as HTMLElement | null;
      this.focusedRow = target?.dataset.sunoId ?? null;
    });
    this.list.addEventListener('focusout', () => {
      this.focusedRow = null;
    });
    this.empty = this.make('p', { class: 'detail dl-empty' }, 'Load your library to choose clips.');
    this.listBox.append(this.list);

    // Formats.
    const formats = this.make('fieldset', { class: 'dl-formats' });
    formats.append(this.make('legend', {}, 'Formats'));
    for (const choice of FORMATS) {
      const label = this.make('label');
      const box = this.make('input', { type: 'checkbox', value: choice.format });
      box.addEventListener('change', () => {
        this.selection.setFormat(choice.format, box.checked);
        this.options.formatsChanged(this.selection.chosenFormats());
        if (this.filter.notDownloaded === true) {
          this.renderList();
        }
        this.renderSummary();
      });
      this.formatBoxes.set(choice.format, box);
      label.append(box, ` ${choice.label}`);
      if (choice.note !== null) {
        const note = this.make('span', { class: 'detail' }, ` ${choice.note}`);
        label.append(note);
      }
      formats.append(label);
    }

    // Files already downloaded (#222).
    const skipLabel = this.make('label', { class: 'dl-skip' });
    this.skipBox = this.make('input', { type: 'checkbox' });
    this.skipBox.checked = this.selection.skipDownloaded;
    this.skipBox.addEventListener('change', () => {
      this.selection.setSkipDownloaded(this.skipBox.checked);
      this.renderSummary();
    });
    skipLabel.append(this.skipBox, ' Skip files already downloaded');

    // Summary and Start.
    this.summary = this.make('div', { class: 'dl-summary', 'aria-live': 'polite' });
    this.startWhy = this.make('p', { class: 'detail dl-start-why', id: 'n8-dl-start-why' });
    this.confirmLabel = this.make('label', { class: 'dl-confirm' });
    this.confirmBox = this.make('input', { type: 'checkbox' });
    this.confirmText = this.make('span');
    this.confirmBox.addEventListener('change', () => {
      this.confirmed = this.confirmBox.checked ? this.selection.unlocksNeeded() : null;
      this.renderSummary();
    });
    this.confirmLabel.append(this.confirmBox, ' ', this.confirmText);
    this.startButton = this.button(
      'Start download',
      () => {
        if (startRefusal(this.selection, this.usage, this.confirmed) === null) {
          this.options.start?.(this.selection.unlocksNeeded());
          this.confirmed = null;
          this.renderSummary();
        }
      },
      true,
    );
    this.startButton.setAttribute('aria-describedby', 'n8-dl-start-why');

    // The run: its files and how they are getting on (#216).
    this.runSection = this.make('section', { class: 'dl-run', 'aria-labelledby': 'n8-dl-run' });
    this.runStatus = this.make('p', { class: 'dl-run-status', role: 'status' });
    this.runMessage = this.make('p', { class: 'warning dl-run-message', role: 'alert' });
    this.runMessage.hidden = true;
    this.runList = this.make('ul', { 'aria-label': 'Files' });
    this.recordsLine = this.make('div', { class: 'detail dl-records', role: 'status' });
    this.cancelRunButton = this.button('Cancel downloads', () => {
      this.options.control?.('cancel');
    });
    this.retryRunButton = this.button('Retry failed downloads', () => {
      this.options.control?.('retry');
    });
    this.resumeRunButton = this.button('Resume', () => {
      this.options.control?.('resume');
    });
    this.resumeRunButton.setAttribute('aria-label', 'Resume downloads');
    const runActions = this.make('p', { class: 'dl-run-actions' });
    runActions.append(this.cancelRunButton, ' ', this.retryRunButton, ' ', this.resumeRunButton);
    this.runSection.append(
      this.make('h4', { id: 'n8-dl-run' }, 'Downloads'),
      this.runStatus,
      this.recordsLine,
      runActions,
      this.runList,
    );
    this.runSection.hidden = true;

    this.element.append(
      heading,
      intro,
      actions,
      this.status,
      this.banner,
      this.lookupLine,
      this.retryButton,
      filters,
      selecting,
      this.empty,
      this.listBox,
      formats,
      skipLabel,
      this.summary,
      this.confirmLabel,
      this.startButton,
      this.startWhy,
      this.runMessage,
      this.runSection,
    );
    this.renderAll();
  }

  get readState(): ReadState {
    return this.read;
  }

  get lookupState(): LookupState {
    return this.lookup;
  }

  /** Where reading has got to; the counts and the incomplete banner follow it. */
  setRead(state: ReadState): void {
    this.read = state;
    if (state.kind === 'read' || state.kind === 'incomplete') {
      this.selection.finish(state.kind === 'read');
    }
    this.renderRead();
    this.renderList();
    this.renderSummary();
  }

  /** Clips read, added as they come. */
  addClips(clips: readonly DownloadClip[]): void {
    this.selection.add(clips);
    this.renderWorkspaces();
    this.renderList();
    this.renderSummary();
  }

  /** A new read starts: the list empties, keeping the selection (or the one carried over). */
  restart(selected?: readonly string[]): void {
    this.selection.restart(selected);
    this.lookup = { kind: 'none' };
    this.renderAll();
  }

  setLookup(state: LookupState): void {
    this.lookup = state;
    this.renderDownloaded();
    this.renderLookup();
    this.renderList();
    this.renderSummary();
  }

  /** How recording downloads in n8Tracks stands (#222); null when not known. */
  setRecords(status: RecordingStatus | null): void {
    this.records = status;
    this.renderRecords();
  }

  /** The plan's download allowance as the page last read it. */
  setUsage(usage: DownloadUsage | null): void {
    this.usage = usage;
    this.renderSummary();
  }

  /** The download queue as the service worker reports it (#216); null when there is none. */
  setRun(run: DownloadRun | null): void {
    this.run = run;
    this.renderRun();
    // Files saved in this run count as downloaded too, before n8Tracks is asked again.
    if (this.lookup.kind === 'found' && run?.files.some((file) => file.state === 'saved')) {
      this.renderDownloaded();
      if (this.filter.notDownloaded === true) {
        this.renderList();
      }
      this.renderSummary();
    }
  }

  /** Why Start or a run control did not work, or null to clear it. */
  setRunMessage(message: string | null): void {
    this.runMessage.textContent = message ?? '';
    this.runMessage.hidden = message === null;
  }

  get runState(): DownloadRun | null {
    return this.run;
  }

  /** The formats remembered from last time; none the first time. */
  setFormats(formats: readonly DownloadFormat[]): void {
    this.selection.setFormats(formats);
    for (const [format, box] of this.formatBoxes) {
      box.checked = this.selection.chosenFormats().includes(format);
    }
    this.renderSummary();
  }

  private make<K extends keyof HTMLElementTagNameMap>(
    tag: K,
    attributes: Record<string, string> = {},
    text?: string,
  ): HTMLElementTagNameMap[K] {
    const element = this.page.createElement(tag);
    for (const [name, value] of Object.entries(attributes)) {
      element.setAttribute(name, value);
    }
    if (text !== undefined) {
      element.textContent = text;
    }
    return element;
  }

  private button(text: string, onPress: () => void, disabled = false): HTMLButtonElement {
    const button = this.make('button', { type: 'button' }, text);
    button.disabled = disabled;
    button.addEventListener('click', onPress);
    return button;
  }

  /**
   * What was downloaded, from the lookup and this run's saved files; unknown (nothing skipped, the
   * filter off) unless the lookup answered.
   */
  private renderDownloaded(): void {
    const lookup = this.lookup;
    if (lookup.kind !== 'found') {
      this.selection.setDownloaded(null);
    } else {
      const downloaded = new Map<string, DownloadFormat[]>();
      for (const [sunoId, row] of lookup.rows) {
        downloaded.set(sunoId, row.downloadedFormats.filter(isDownloadFormat));
      }
      for (const file of this.run?.files ?? []) {
        if (file.state === 'saved') {
          downloaded.set(file.sunoId, [...(downloaded.get(file.sunoId) ?? []), file.format]);
        }
      }
      this.selection.setDownloaded(downloaded);
    }
    const known = this.selection.downloadsKnown;
    this.notDownloadedBox.disabled = !known;
    this.notDownloadedWhy.textContent = known
      ? ''
      : 'Needs n8Tracks to say what was already downloaded.';
  }

  private renderRecords(): void {
    const lines = recordingLines(this.records);
    this.recordsLine.replaceChildren(...lines.map((line) => this.make('p', {}, line)));
    this.recordsLine.hidden = lines.length === 0;
  }

  private renderAll(): void {
    this.renderDownloaded();
    this.renderRead();
    this.renderLookup();
    this.renderWorkspaces();
    this.renderList();
    this.renderSummary();
  }

  private renderRead(): void {
    const state = this.read;
    const reading = state.kind === 'reading';
    this.loadButton.disabled = reading;
    this.loadButton.textContent =
      state.kind === 'read' || state.kind === 'incomplete' ? 'Refresh' : 'Load library';
    this.loadButton.setAttribute(
      'aria-label',
      state.kind === 'read' || state.kind === 'incomplete'
        ? 'Refresh: read the library again'
        : 'Load library',
    );
    this.cancelButton.hidden = !reading;
    switch (state.kind) {
      case 'idle':
        this.status.textContent = '';
        break;
      case 'refused':
        this.status.textContent = `The library was not loaded: ${state.message}`;
        break;
      case 'reading':
        this.status.textContent = `Reading your Suno library: ${plural(state.count, 'clip', 'clips')} so far. Keep this tab open.`;
        break;
      case 'read':
        this.status.textContent = `${plural(state.count, 'clip', 'clips')} listed from every workspace.`;
        break;
      case 'incomplete':
        this.status.textContent = `${plural(state.count, 'clip', 'clips')} listed.`;
        break;
    }
    this.banner.hidden = state.kind !== 'incomplete';
    this.banner.textContent =
      state.kind === 'incomplete'
        ? `The list is incomplete: ${state.reason}. The clips read so far can be selected one by one; Select all is off until the library is read to its end (Refresh).`
        : '';
  }

  private renderLookup(): void {
    const state = this.lookup;
    let text = '';
    switch (state.kind) {
      case 'none':
      case 'found':
        break;
      case 'checking':
        text = 'Checking which clips are already in n8Tracks.';
        break;
      case 'unavailable':
        text = `Already in n8Tracks: unavailable. ${state.message} The clips can still be downloaded, but they are not recorded in n8Tracks.`;
        break;
      case 'failed':
        text = `Already in n8Tracks: unknown. ${state.message}`;
        break;
    }
    this.lookupLine.textContent = text;
    this.lookupLine.hidden = text === '';
    this.retryButton.hidden = state.kind !== 'failed';
  }

  private renderWorkspaces(): void {
    const chosen = this.filter.workspaceId;
    const all = this.make('option', { value: '' }, 'All workspaces');
    const options = [
      all,
      ...this.selection
        .workspaces()
        .map((workspace) =>
          this.make('option', { value: workspace.id }, nameOrUnnamed(workspace.name)),
        ),
    ];
    this.workspaceSelect.replaceChildren(...options);
    const known = chosen === null || this.selection.workspaces().some((item) => item.id === chosen);
    this.workspaceSelect.value = known && chosen !== null ? chosen : '';
    if (!known) {
      this.filter = { ...this.filter, workspaceId: null };
    }
  }

  private onScroll(): void {
    if (this.selection.shown(this.filter).length <= VIRTUALISE_ABOVE) {
      return;
    }
    const first = Math.max(0, Math.floor(this.listBox.scrollTop / ROW_HEIGHT) - DRAWN_ROWS / 4);
    if (Math.abs(first - this.firstDrawn) >= DRAWN_ROWS / 4) {
      this.firstDrawn = first;
      this.renderList();
    }
  }

  private renderList(): void {
    const shown = this.selection.shown(this.filter);
    const loaded = this.selection.all().length > 0;
    this.empty.hidden = loaded && shown.length > 0;
    this.empty.textContent = !loaded
      ? this.read.kind === 'reading'
        ? 'No clips read yet.'
        : 'Load your library to choose clips.'
      : 'No clips match the filter.';
    this.listBox.hidden = shown.length === 0;

    const virtual = shown.length > VIRTUALISE_ABOVE;
    this.listBox.classList.toggle('dl-virtual', virtual);
    const first = virtual ? Math.min(this.firstDrawn, Math.max(0, shown.length - DRAWN_ROWS)) : 0;
    const drawn = virtual ? shown.slice(first, first + DRAWN_ROWS) : shown;
    const refocus = this.focusedRow;
    this.rows = new Map();
    const items: HTMLElement[] = [];
    if (virtual && first > 0) {
      items.push(this.spacer(first));
    }
    drawn.forEach((clip, index) => {
      items.push(
        this.row(clip, virtual ? { position: first + index + 1, size: shown.length } : null),
      );
    });
    if (virtual && first + drawn.length < shown.length) {
      items.push(this.spacer(shown.length - first - drawn.length));
    }
    this.list.replaceChildren(...items);
    if (refocus !== null) {
      this.rows.get(refocus)?.box.focus();
    }

    const selectable = shown.some((clip) => clip.unavailable === null);
    this.selectAllButton.disabled = !this.selection.complete || !selectable;
    this.selectAllWhy.textContent = !loaded
      ? ''
      : !this.selection.complete
        ? this.read.kind === 'reading'
          ? 'Select all waits until the whole library is read.'
          : 'Select all needs the whole library read (Refresh).'
        : '';
    this.clearButton.disabled = this.selection.selectedIds().length === 0;
  }

  private spacer(rows: number): HTMLElement {
    const spacer = this.make('li', { class: 'dl-spacer', 'aria-hidden': 'true' });
    spacer.style.height = `${String(rows * ROW_HEIGHT)}px`;
    return spacer;
  }

  private row(clip: DownloadClip, place: { position: number; size: number } | null): HTMLElement {
    const item = this.make('li', { class: 'dl-row', 'data-suno-id': clip.sunoId });
    if (place !== null) {
      item.setAttribute('aria-posinset', String(place.position));
      item.setAttribute('aria-setsize', String(place.size));
    }
    const label = this.make('label');
    const box = this.make('input', { type: 'checkbox', 'data-suno-id': clip.sunoId });
    box.checked = this.selection.isSelected(clip.sunoId);
    box.disabled = clip.unavailable !== null;
    box.addEventListener('change', () => {
      this.selection.toggle(clip.sunoId, box.checked);
      box.checked = this.selection.isSelected(clip.sunoId);
      this.clearButton.disabled = this.selection.selectedIds().length === 0;
      this.renderSummary();
    });
    const title = this.make('span', { class: 'dl-title' }, clip.title);
    label.append(box, ' ', title);

    const facts = [
      durationText(clip.durationSeconds),
      createdText(clip.createdAt),
      clip.workspace === null ? 'No workspace' : nameOrUnnamed(clip.workspace.name),
      clip.unlocked ? 'Unlocked on Suno' : 'Not unlocked',
      ...(clip.hidden ? ['Hidden in Suno'] : []),
    ].filter((fact) => fact !== '');
    const details = this.make('span', { class: 'detail dl-facts' }, facts.join(' · '));
    const known = this.make(
      'span',
      { class: 'detail dl-known' },
      ` · ${inN8TracksText(this.lookup, clip.sunoId)}`,
    );
    details.append(known);
    item.append(label, details);
    if (clip.unavailable !== null) {
      const id = `n8-dl-why-${clip.sunoId}`;
      const why = this.make('span', { class: 'dl-unavailable', id }, clip.unavailable);
      box.setAttribute('aria-describedby', id);
      item.append(why);
    }
    this.rows.set(clip.sunoId, { box, known });
    return item;
  }

  private renderSummary(): void {
    const lines = summaryLines(this.selection, this.usage, this.filter);
    this.summary.replaceChildren(...lines.map((line) => this.make('p', {}, line)));
    const needed = this.selection.unlocksNeeded();
    if (this.confirmed !== null && this.confirmed !== needed) {
      // The count changed since it was ticked: it is asked again.
      this.confirmed = null;
    }
    this.confirmLabel.hidden = needed === 0;
    this.confirmBox.checked = this.confirmed !== null;
    this.confirmText.textContent = `Use ${plural(needed, 'Suno download unlock', 'Suno download unlocks')} for this run`;
    const refusal = startRefusal(this.selection, this.usage, this.confirmed);
    this.startButton.disabled = refusal !== null;
    this.startWhy.textContent = refusal ?? '';
  }

  private renderRun(): void {
    const run = this.run;
    this.runSection.hidden = run === null || run.files.length === 0;
    this.renderRecords();
    if (run === null) {
      this.runList.replaceChildren();
      return;
    }
    this.runStatus.textContent = runSummaryText(run);
    this.runList.replaceChildren(
      ...run.files.map((file) =>
        this.make(
          'li',
          { class: `dl-file dl-file-${file.state}`, 'data-key': file.key },
          fileStatusText(file),
        ),
      ),
    );
    const active = run.files.some((file) =>
      ['queued', 'preparing', 'downloading'].includes(file.state),
    );
    this.cancelRunButton.disabled = !active;
    this.retryRunButton.disabled = !run.files.some((file) => file.state === 'failed');
    this.resumeRunButton.hidden = !run.files.some(
      (file) => file.state === 'queued' && file.paused !== null,
    );
  }
}
