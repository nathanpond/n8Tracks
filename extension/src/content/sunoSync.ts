import {
  sunoCreateAddress,
  sunoListAddress,
  sunoPageOf,
  sunoWorkspaceAddress,
  workspaceOfAddress,
} from '../adapter/addresses.ts';
import { realClock, type Clock } from '../adapter/clock.ts';
import {
  PAGE_RETRIES,
  PLAYLISTS_STEP,
  readLeg,
  readPlaylistList,
  readWorkspaceList,
  ReadCancelled,
  ReadStop,
  stepOf,
  type ExportSink,
  type Observations,
} from '../adapter/libraryReader.ts';
import type { Page } from '../adapter/primitives.ts';
import type { AdapterSession } from '../adapter/registry.ts';
import { readPlaylistPage } from '../adapter/playlistPage.ts';
import { openPlaylistList, openWorkspaceList } from '../adapter/workflows/libraryTabs.ts';
import { switchForm } from '../adapter/workflows/fillSongs.ts';
import { loadMore } from '../adapter/workflows/loadMore.ts';
import { closeVoiceList, openVoiceList, scrollVoiceList } from '../adapter/workflows/voices.ts';
import type { StepContext, Workflow } from '../adapter/workflow.ts';
import type {
  ImageProgress,
  KnownPlaylist,
  KnownWorkspace,
  Request,
  ResponseFor,
  SyncLeg,
  SyncProgress,
  SyncReply,
  SyncScope,
  SyncSession,
} from '../messages.ts';
import type { SyncViewState, WorkspaceListState } from '../panel/SyncView.ts';

/**
 * The Suno tab's half of a sync (#134). The user starts it from the panel; each leg is read on
 * its own page, reached by address (a workspace's songs on `/create?wid=<id>`, #422), and the service worker carries the sync from one page load to
 * the next. On every page load the content script asks whether its tab is in a sync, and if so
 * reads the leg the page is for. It never starts a sync by itself. The workspace list is the one
 * leg not reached by address alone (#421): its page is Library (`/me`), and the leg presses
 * Library's Workspaces tab there, since a direct load of `/me/workspaces` asks Suno for nothing.
 * The user's voices (#439) are read on Create (`/create`): Songs in Advanced mode, then "+ Voice"
 * opens the Voice picker, which makes the page ask Suno for the voice list; the picker is closed
 * without a choice, and the voices go to n8Tracks through the service worker on their own.
 *
 * The panel's Workspaces choice (#448) offers the workspaces n8Tracks knows, asked for when the
 * choice is opened. "Refresh from Suno" reads Suno's workspace list as the sync's first leg does,
 * with the same reader and workflows (Library's Workspaces tab, then a scroll for each next page),
 * on Library's own page load, and reports it to n8Tracks; nothing else is pressed and no export is
 * made.
 *
 * Playlists (#449) follow the same pattern: the playlist list is read on Library by pressing its
 * Playlists tab, in a leg of every sync and by the Playlists choice's own "Refresh from Suno", and
 * reported on its own. A playlist's songs are read from the rows of its page (TS-007-24), with a
 * scroll for more while fewer rows are read than its "N songs"; nothing is pressed there.
 */

export interface SunoSyncOptions {
  page: Page;
  session: AdapterSession;
  observations: Observations;
  send: (request: Request) => Promise<unknown>;
  /** Shows a state in the panel's Sync to n8Tracks, opening the panel. */
  show: (state: SyncViewState) => void;
  /** Shows the finished sync's cover images (#152), changing nothing else in the panel. */
  showImages?: (images: ImageProgress | null) => void;
  versions: { extension: string; adapter: number };
  /** Shows where the Workspaces choice's list stands (#448), opening the panel. */
  showWorkspaces?: (state: WorkspaceListState) => void;
  /** Shows where the Playlists choice's list stands (#449), as for the workspaces. */
  showPlaylists?: (state: WorkspaceListState) => void;
  clock?: Clock;
  now?: () => string;
}

/** The step a Refresh from Suno names when it stops (#448). */
export const REFRESH_STEP = 'Read the workspace list';

/** How long a playlist page is given to draw its rows, after it opened or was scrolled (#449). */
export const PLAYLIST_SETTLE_MS = 1_000;

/** How often the panel asks how the cover images are going (#152). */
export const IMAGES_POLL_MS = 1_000;

/** How long the panel follows the cover images after a sync before it stops asking. */
export const IMAGES_WATCH_MS = 30 * 60_000;

/** The image states after which nothing changes. */
const IMAGES_DONE = new Set<ImageProgress['state']>(['finished', 'stopped', 'skipped']);

/** The step named when the Suno tab is no longer on the page a leg is read on. */
export const TAB_LOST = 'Suno tab lost';

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** A sync answer, or a failure in plain words when the service worker gave none. */
function reply<T extends object>(answer: unknown): SyncReply<T> {
  if (isRecord(answer) && typeof answer.ok === 'boolean') {
    return answer as SyncReply<T>;
  }
  return { ok: false, message: 'The extension did not answer. Reload the Suno page.' };
}

/**
 * The page a leg is read on. The workspace list is read on Library (`/me`), where its tab is
 * pressed (#421); a workspace's songs on the Create page opened for it (`/create?wid=`, #422).
 */
function addressOf(leg: SyncLeg): URL {
  switch (leg.list) {
    case 'workspace':
      return sunoWorkspaceAddress(leg.id);
    case 'playlist':
      return sunoListAddress({ page: 'playlist', id: leg.id });
    case 'workspaces':
    case 'playlists':
    case 'library':
      return sunoListAddress({ page: 'library' });
    case 'trash':
      return sunoListAddress({ page: 'trash' });
    case 'voices':
      return sunoCreateAddress();
  }
}

/** Whether the tab shows the page a leg is read on: for a workspace, the Create page opened for it. */
function onPageOf(leg: SyncLeg, address: URL): boolean {
  if (leg.list === 'workspace') {
    return workspaceOfAddress(address) === leg.id;
  }
  const wanted = addressOf(leg);
  return (
    sunoPageOf(address) === sunoPageOf(wanted) &&
    address.pathname.replace(/\/+$/, '') === wanted.pathname
  );
}

/** The progress part of a session: what a page load hands on. */
function progressOf(session: SyncSession): SyncProgress {
  return {
    leg: session.leg,
    attempt: session.attempt,
    partNumber: session.partNumber,
    counts: session.counts,
    workspaces: session.workspaces,
    ...(session.workspacesComplete === undefined
      ? {}
      : { workspacesComplete: session.workspacesComplete }),
    ...(session.voicesNote === undefined ? {} : { voicesNote: session.voicesNote }),
    ...(session.notes === undefined ? {} : { notes: session.notes }),
  };
}

/** How a stop reads in the panel, after "Sync stopped: ". */
function report(step: string, expected: string): string {
  return `step '${step}' expected ${expected}`;
}

export class SunoSync {
  private readonly options: SunoSyncOptions;
  private controller: AbortController | null = null;
  /** The Available workspaces n8Tracks knows (#448), as last asked or reported. */
  private known: KnownWorkspace[] = [];
  /** The Available playlists n8Tracks knows (#449), as last asked or reported. */
  private playlists: KnownPlaylist[] = [];

  constructor(options: SunoSyncOptions) {
    this.options = options;
  }

  /** Whether a leg is being read in this tab. */
  get running(): boolean {
    return this.controller !== null;
  }

  /** "Next" in the panel: the summary, warning when an export waiting for review is replaced. */
  async preview(scope: SyncScope): Promise<void> {
    const answer: unknown = await this.options
      .send({ type: 'sync-preview' })
      .catch(() => undefined);
    const replacesReady = isRecord(answer) && answer.replacesReady === true;
    this.options.show({ kind: 'summary', scope, replacesReady });
  }

  /** "Start sync": the service worker starts the sync, and the first leg's page is opened. */
  async start(scope: SyncScope): Promise<void> {
    let answer: SyncReply<{ session: SyncSession }>;
    try {
      answer = reply(await this.options.send({ type: 'sync-begin', scope }));
    } catch {
      answer = reply(undefined);
    }
    if (!answer.ok) {
      this.options.show({ kind: 'stopped', report: `step 'Start the sync': ${answer.message}` });
      return;
    }
    const first = answer.session.legs[0];
    if (first === undefined) {
      return;
    }
    this.options.show({
      kind: 'reading',
      step: stepOf(first, answer.session.legs),
      counts: answer.session.counts,
    });
    this.options.page.go(addressOf(first));
  }

  /** "Cancel sync": nothing more is read, and the export is discarded. */
  async cancel(): Promise<void> {
    if (this.controller !== null) {
      // The running leg stops at its next check and discards the export itself.
      this.controller.abort();
      return;
    }
    await this.discard();
    this.options.show({ kind: 'cancelled' });
  }

  /**
   * On every page load: reads this tab's leg, if the tab is in a sync, or Suno's workspace list, if
   * its Refresh from Suno is waiting for this load (#448).
   */
  async resume(): Promise<void> {
    const answer = (await this.options.send({ type: 'sync-resume' }).catch(() => undefined)) as
      ResponseFor['sync-resume'] | undefined;
    const session = isRecord(answer) ? (answer.session ?? null) : null;
    if (session !== null) {
      await this.readLeg(session);
      return;
    }
    if (isRecord(answer) && answer.refresh === true) {
      await this.readWorkspaces();
      return;
    }
    if (isRecord(answer) && answer.refreshPlaylists === true) {
      await this.readPlaylists();
    }
  }

  /** The Available playlists n8Tracks knows (#449), as last asked or reported. */
  knownPlaylists(): KnownPlaylist[] {
    return [...this.playlists];
  }

  private showPlaylists(state: WorkspaceListState): void {
    this.options.showPlaylists?.(state);
  }

  /** The Playlists choice was opened (#449): asks n8Tracks for the playlists it knows. */
  async wantPlaylists(): Promise<void> {
    this.showPlaylists({ kind: 'loading' });
    let answer: SyncReply<{ playlists: KnownPlaylist[] }>;
    try {
      answer = reply(await this.options.send({ type: 'sync-playlists-known' }));
    } catch {
      answer = reply(undefined);
    }
    if (!answer.ok) {
      this.showPlaylists({
        kind: 'failed',
        message: `The playlists n8Tracks knows could not be read: ${answer.message}`,
      });
      return;
    }
    this.playlists = answer.playlists;
    this.showPlaylists({ kind: 'ready' });
  }

  /**
   * The Playlists choice's "Refresh from Suno" (#449): as for the workspaces, the service worker
   * holds the refresh for this tab and Library is opened; its page load reads the playlist list.
   */
  async refreshPlaylists(): Promise<void> {
    if (this.controller !== null) {
      return;
    }
    let answer: SyncReply;
    try {
      answer = reply(await this.options.send({ type: 'sync-playlists-refresh' }));
    } catch {
      answer = reply(undefined);
    }
    if (!answer.ok) {
      this.showPlaylists({ kind: 'failed', message: `Not refreshed: ${answer.message}` });
      return;
    }
    this.showPlaylists({ kind: 'refreshing' });
    this.options.page.go(addressOf({ list: 'playlists' }));
  }

  /**
   * Reads Suno's playlist list on Library, as the sync's playlist-list leg does, and reports it to
   * n8Tracks; the panel shows the Playlists choice with the list n8Tracks then knows.
   */
  private async readPlaylists(): Promise<void> {
    const { page, show } = this.options;
    show({ kind: 'choose', scope: 'playlists' });
    this.showPlaylists({ kind: 'refreshing' });
    if (!onPageOf({ list: 'playlists' }, page.address())) {
      this.showPlaylists({
        kind: 'failed',
        message: `Not refreshed: step '${PLAYLISTS_STEP}' expected the Suno tab to stay on Library.`,
      });
      return;
    }
    const controller = new AbortController();
    this.controller = controller;
    let read: { playlists: Record<string, unknown>[]; complete: boolean };
    try {
      read = await readPlaylistList(PLAYLISTS_STEP, {
        observations: this.options.observations,
        openPlaylistList: (openStep) => this.runStep(openPlaylistList, openStep, {}),
        more: (moreStep) => this.runStep(loadMore, moreStep, {}),
        signal: controller.signal,
      });
    } catch (error) {
      this.showPlaylists({
        kind: 'failed',
        message:
          error instanceof ReadStop
            ? `Not refreshed: ${report(error.step, error.expected)}.`
            : 'Not refreshed: the read did not finish.',
      });
      return;
    } finally {
      this.controller = null;
    }
    const answer = await this.reportPlaylists(read.complete, read.playlists);
    if (!answer.ok) {
      this.showPlaylists({
        kind: 'failed',
        message: `Your playlist list was read but not recorded: ${answer.message}`,
      });
      return;
    }
    this.showPlaylists({
      kind: 'refreshed',
      count: read.playlists.length,
      complete: read.complete,
    });
  }

  /** Sends a playlist list read to n8Tracks (#449), keeping the Available playlists it answers. */
  private async reportPlaylists(
    complete: boolean,
    playlists: Record<string, unknown>[],
  ): Promise<SyncReply> {
    let answer: SyncReply<{ playlists: KnownPlaylist[] }>;
    try {
      answer = reply(
        await this.options.send({ type: 'sync-playlists-report', complete, playlists }),
      );
    } catch {
      answer = reply(undefined);
    }
    if (answer.ok) {
      this.playlists = answer.playlists;
    }
    return answer;
  }

  /** The Available workspaces n8Tracks knows (#448), as last asked or reported. */
  knownWorkspaces(): KnownWorkspace[] {
    return [...this.known];
  }

  private showWorkspaces(state: WorkspaceListState): void {
    this.options.showWorkspaces?.(state);
  }

  /** The Workspaces choice was opened (#448): asks n8Tracks for the workspaces it knows. */
  async wantWorkspaces(): Promise<void> {
    this.showWorkspaces({ kind: 'loading' });
    let answer: SyncReply<{ workspaces: KnownWorkspace[] }>;
    try {
      answer = reply(await this.options.send({ type: 'sync-workspaces-known' }));
    } catch {
      answer = reply(undefined);
    }
    if (!answer.ok) {
      this.showWorkspaces({
        kind: 'failed',
        message: `The workspaces n8Tracks knows could not be read: ${answer.message}`,
      });
      return;
    }
    this.known = answer.workspaces;
    this.showWorkspaces({ kind: 'ready' });
  }

  /**
   * "Refresh from Suno" (#448): the service worker holds the refresh for this tab, and Library
   * (`/me`) is opened; its page load reads the list ({@link resume}). Refused while a sync runs.
   */
  async refreshWorkspaces(): Promise<void> {
    if (this.controller !== null) {
      return;
    }
    let answer: SyncReply;
    try {
      answer = reply(await this.options.send({ type: 'sync-workspaces-refresh' }));
    } catch {
      answer = reply(undefined);
    }
    if (!answer.ok) {
      this.showWorkspaces({ kind: 'failed', message: `Not refreshed: ${answer.message}` });
      return;
    }
    this.showWorkspaces({ kind: 'refreshing' });
    this.options.page.go(addressOf({ list: 'workspaces' }));
  }

  /**
   * Reads Suno's workspace list on Library, as the sync's first leg does, and reports it to
   * n8Tracks; the panel shows the Workspaces choice with the list n8Tracks then knows.
   */
  private async readWorkspaces(): Promise<void> {
    const { page, show } = this.options;
    show({ kind: 'choose', scope: 'workspaces' });
    this.showWorkspaces({ kind: 'refreshing' });
    // The page the sync's workspace-list leg is read on: Library (`/me`).
    if (!onPageOf({ list: 'workspaces' }, page.address())) {
      this.showWorkspaces({
        kind: 'failed',
        message: `Not refreshed: step '${REFRESH_STEP}' expected the Suno tab to stay on Library.`,
      });
      return;
    }
    const controller = new AbortController();
    this.controller = controller;
    let read: { workspaces: Record<string, unknown>[]; complete: boolean };
    try {
      read = await readWorkspaceList(REFRESH_STEP, {
        observations: this.options.observations,
        openWorkspaceList: (openStep) => this.runStep(openWorkspaceList, openStep, {}),
        more: (moreStep) => this.runStep(loadMore, moreStep, {}),
        signal: controller.signal,
      });
    } catch (error) {
      this.showWorkspaces({
        kind: 'failed',
        message:
          error instanceof ReadStop
            ? `Not refreshed: ${report(error.step, error.expected)}.`
            : 'Not refreshed: the read did not finish.',
      });
      return;
    } finally {
      this.controller = null;
    }
    let answer: SyncReply<{ workspaces: KnownWorkspace[] }>;
    try {
      answer = reply(
        await this.options.send({
          type: 'sync-workspaces-report',
          complete: read.complete,
          workspaces: read.workspaces,
        }),
      );
    } catch {
      answer = reply(undefined);
    }
    if (!answer.ok) {
      this.showWorkspaces({
        kind: 'failed',
        message: `Your workspace list was read but not recorded: ${answer.message}`,
      });
      return;
    }
    this.known = answer.workspaces;
    this.showWorkspaces({
      kind: 'refreshed',
      count: read.workspaces.length,
      complete: read.complete,
    });
  }

  private async discard(): Promise<void> {
    try {
      await this.options.send({ type: 'sync-discard' });
    } catch {
      // The service worker discards the export when the tab closes or leaves Suno.
    }
  }

  private async stop(step: string, expected: string): Promise<void> {
    await this.discard();
    this.options.show({ kind: 'stopped', report: report(step, expected) });
  }

  private async save(progress: SyncProgress): Promise<boolean> {
    try {
      return reply(await this.options.send({ type: 'sync-save', progress })).ok;
    } catch {
      return false;
    }
  }

  /** Sends the voices read to n8Tracks (#439); null when it took them, else why not. */
  private async sendVoices(
    complete: boolean,
    voices: Record<string, unknown>[],
  ): Promise<string | null> {
    let answer: SyncReply;
    try {
      answer = reply(await this.options.send({ type: 'sync-voices', complete, voices }));
    } catch {
      answer = reply(undefined);
    }
    return answer.ok ? null : answer.message;
  }

  private sink(): ExportSink {
    const send = async (request: Request, step: string) => {
      let answer: SyncReply;
      try {
        answer = reply(await this.options.send(request));
      } catch {
        answer = reply(undefined);
      }
      if (!answer.ok) {
        throw new ReadStop(step, `n8Tracks to take the records (${answer.message})`);
      }
    };
    return {
      create: (header) => send({ type: 'sync-create', header }, 'Send to n8Tracks'),
      part: (part) => send({ type: 'sync-part', part }, 'Send to n8Tracks'),
    };
  }

  /**
   * Runs one of the reader's workflows on the page; a run that does not finish stops the leg at
   * `step`, saying what was expected.
   */
  private async runStep<C extends StepContext>(
    workflow: Workflow<C>,
    step: string,
    values: Omit<C, keyof StepContext>,
  ): Promise<void> {
    const result = await this.options.session.run(
      workflow,
      values,
      this.options.clock === undefined ? {} : { clock: this.options.clock },
    );
    if (!result.ok) {
      throw new ReadStop(step, result.failure.expected);
    }
  }

  private async readLeg(session: SyncSession): Promise<void> {
    const { page, show } = this.options;
    const leg = session.legs[session.leg];
    if (leg === undefined) {
      await this.stop(TAB_LOST, 'a list still to read');
      return;
    }
    const step = stepOf(leg, session.legs);
    if (!onPageOf(leg, page.address())) {
      await this.stop(TAB_LOST, `the Suno tab to stay on the page being read (${step})`);
      return;
    }
    show({ kind: 'reading', step, counts: session.counts });

    const controller = new AbortController();
    this.controller = controller;
    let next: SyncProgress;
    try {
      next = await readLeg(session.scope, session.legs, progressOf(session), {
        observations: this.options.observations,
        openWorkspaceList: (openStep) => this.runStep(openWorkspaceList, openStep, {}),
        more: (moreStep) => this.runStep(loadMore, moreStep, {}),
        openVoices: async (openStep) => {
          // "+ Voice" is on the Songs form in Advanced mode (TS-005).
          await this.runStep(switchForm, openStep, { mode: 'advanced' });
          await this.runStep(openVoiceList, openStep, {});
        },
        moreVoices: (moreStep) => this.runStep(scrollVoiceList, moreStep, {}),
        closeVoices: (closeStep) => this.runStep(closeVoiceList, closeStep, {}),
        sendVoices: (complete, voices) => this.sendVoices(complete, voices),
        openPlaylistList: (openStep) => this.runStep(openPlaylistList, openStep, {}),
        sendPlaylists: async (complete, playlists) => {
          const answer = await this.reportPlaylists(complete, playlists);
          return answer.ok ? null : answer.message;
        },
        readPlaylistPage: () => readPlaylistPage(page),
        settle: () => (this.options.clock ?? realClock).sleep(PLAYLIST_SETTLE_MS),
        sink: this.sink(),
        signal: controller.signal,
        progress: (counts) => {
          show({ kind: 'reading', step, counts });
        },
        versions: this.options.versions,
        now: this.options.now ?? (() => new Date().toISOString()),
      });
    } catch (error) {
      this.controller = null;
      if (error instanceof ReadCancelled || controller.signal.aborted) {
        await this.discard();
        show({ kind: 'cancelled' });
        return;
      }
      if (error instanceof ReadStop && error.reopen && session.attempt < PAGE_RETRIES) {
        // No first page came: open the page again, as the step's retry.
        if (await this.save({ ...progressOf(session), attempt: session.attempt + 1 })) {
          page.go(addressOf(leg));
          return;
        }
      }
      if (error instanceof ReadStop) {
        await this.stop(error.step, error.expected);
        return;
      }
      await this.stop(step, 'the read to finish without an error');
      return;
    }
    this.controller = null;

    const following = session.legs[next.leg];
    if (following !== undefined) {
      if (!(await this.save(next))) {
        await this.stop(step, 'the extension to keep the sync between pages');
        return;
      }
      show({ kind: 'reading', step: stepOf(following, session.legs), counts: next.counts });
      page.go(addressOf(following));
      return;
    }

    let finished: SyncReply<{ reviewUrl: string }>;
    try {
      finished = reply(await this.options.send({ type: 'sync-complete' }));
    } catch {
      finished = reply(undefined);
    }
    show(
      finished.ok
        ? {
            kind: 'finished',
            counts: next.counts,
            ...(next.voicesNote === undefined ? {} : { note: next.voicesNote }),
            ...(next.notes === undefined ? {} : { notes: next.notes }),
          }
        : {
            kind: 'stopped',
            report: report('Finish the export', `n8Tracks to accept it (${finished.message})`),
          },
    );
    if (finished.ok) {
      await this.watchImages();
    }
  }

  /**
   * Follows the cover images the service worker sends after the export is ready (#152), showing
   * their counts until they are done, for at most {@link IMAGES_WATCH_MS}.
   */
  private async watchImages(): Promise<void> {
    const clock = this.options.clock ?? realClock;
    const deadline = clock.now() + IMAGES_WATCH_MS;
    for (;;) {
      const answer: unknown = await this.options
        .send({ type: 'sync-images' })
        .catch(() => undefined);
      const found: unknown = isRecord(answer) ? answer.images : null;
      if (!isRecord(found)) {
        return;
      }
      const images = found as unknown as ImageProgress;
      this.options.showImages?.(images);
      if (IMAGES_DONE.has(images.state) || clock.now() >= deadline) {
        return;
      }
      await clock.sleep(IMAGES_POLL_MS);
    }
  }
}
