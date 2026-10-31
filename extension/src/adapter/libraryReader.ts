import type { ExportPart, SyncCounts, SyncLeg, SyncProgress, SyncScope } from '../messages.ts';
import type { ObservedKind, ObservedMessage, ObservedRequest } from './observed.ts';
import type { PlaylistPageRead } from './playlistPage.ts';

/**
 * The library reader (#134): reads the lists a sync needs by watching Suno's own responses
 * (`observed.ts`) while the page is operated, and hands the records to n8Tracks in parts. A sync
 * is a list of legs, each read on its own Suno page: the workspace list, then the library, each
 * chosen workspace's own songs (#422), or each chosen playlist, then the Trash when the scope reads
 * it. Navigating between them belongs to the content script; this module reads one leg on the page
 * it is given.
 *
 * Paging and end of list are TS-003's. A list counts as read only when its end was seen; a page
 * that does not look as expected, one that comes twice, or one that does not come stops the read
 * with the step named, and the content script discards the export. Nothing partial is offered for
 * review, so a completed export's `libraryComplete` and `trashedComplete` are what the read set out
 * to read: a whole-library read is the only one with `libraryComplete`, and `trashedComplete` is set
 * only when a Trash leg was read (#420). The one exception is the workspace list: Suno loads its
 * next page only on the user's own scroll (TS-007-15), so when no next page comes after a scroll
 * the sync goes on with what it read, and the header says `workspacesComplete: false` (#434). The
 * server then marks no workspace Unavailable.
 *
 * The user's voice list (#439) is read in a leg of its own, after the workspace list, on Create's
 * Voice picker, and sent to n8Tracks on its own (`sync-voices`), not in the export. That leg never
 * stops the sync: when it cannot be read or sent, the sync goes on and the panel says why
 * ({@link SyncProgress.voicesNote}). Like the workspace list, it goes on short, as incomplete, when
 * no next page comes after a scroll, and the server then marks no voice Unavailable.
 *
 * The user's playlist list (#449) is read the same way, third, on Library's Playlists tab, and sent
 * on its own (`sync-playlists-report`). A playlist's songs are no longer fetched by its page
 * (TS-007-24): the playlist leg reads their IDs from the rows on the page, scrolling while fewer
 * rows are read than the count in its header, and sends only the membership. A playlist read short
 * of its count is sent as read, with the count, and the panel says it was read in part.
 */

/** How long a page of a list may take to come, after the action that asks for it (20 seconds). */
export const PAGE_WAIT_MS = 20_000;
/** How many times the action that asks for a page is repeated before the read stops. */
export const PAGE_RETRIES = 2;
/** The most clips in one part (`docs/suno-integration.md`, Limits). */
export const PART_CLIPS = 200;

export const EXPORT_FORMAT = 'n8tracks.suno-export';
export const EXPORT_FORMAT_VERSION = 1;

/** The read stopped: the step that failed and what it expected, in plain words. */
export class ReadStop extends Error {
  readonly step: string;
  readonly expected: string;
  /** No first page came after the page opened: opening it again may help. */
  readonly reopen: boolean;

  constructor(step: string, expected: string, reopen = false) {
    super(`${step}: expected ${expected}`);
    this.name = 'ReadStop';
    this.step = step;
    this.expected = expected;
    this.reopen = reopen;
  }
}

/** The user cancelled the sync: nothing more is read. */
export class ReadCancelled extends Error {
  constructor() {
    super('The sync was cancelled.');
    this.name = 'ReadCancelled';
  }
}

/** Suno's responses as the content script receives them from the observer. */
export interface Observations {
  /**
   * The next response of `kind` that `accept` takes, in the order they came, each given once; null
   * when none comes within `timeoutMs` or the signal is aborted.
   */
  next(
    kind: ObservedKind,
    accept: (message: ObservedMessage) => boolean,
    timeoutMs: number,
    signal: AbortSignal,
  ): Promise<ObservedMessage | null>;
}

/** Where the records go: the service worker, which calls n8Tracks. Each throws a {@link ReadStop}. */
export interface ExportSink {
  /** Creates the export from its header, once; later calls do nothing. */
  create(header: Record<string, unknown>): Promise<void>;
  part(part: ExportPart): Promise<void>;
}

/** What reading one list needs: Suno's responses, a way to ask for more, and the cancel signal. */
export interface ListContext {
  observations: Observations;
  /** Makes the page ask for more of its list (the `load-more` workflow); throws a {@link ReadStop}. */
  more(step: string): Promise<void>;
  signal: AbortSignal;
}

export interface LegContext extends ListContext {
  /**
   * Makes the page ask Suno for the workspace list: loading `/me/workspaces` does not (#421), so
   * the leg opens `/me` and this presses Library's Workspaces tab there (the `open-workspace-list`
   * workflow, TS-007-6). Called once by the workspace-list leg, before its first page is waited
   * for. Throws a {@link ReadStop}.
   */
  openWorkspaceList(step: string): Promise<void>;
  /**
   * Makes the page ask Suno for the user's voice list (#439): opens Create's Songs form in Advanced
   * mode (`switch-form`) and presses "+ Voice" (`open-voice-list`). Throws a {@link ReadStop}.
   */
  openVoices(step: string): Promise<void>;
  /** Asks for the voice list's next page: scrolls the picker (`scroll-voice-list`). Throws a {@link ReadStop}. */
  moreVoices(step: string): Promise<void>;
  /** Closes the Voice picker without choosing (`close-voice-list`). Throws a {@link ReadStop}. */
  closeVoices(step: string): Promise<void>;
  /** Sends the voices read to n8Tracks; null when it took them, else why not, in plain words. */
  sendVoices(complete: boolean, voices: Record<string, unknown>[]): Promise<string | null>;
  /**
   * Makes the page ask Suno for the user's playlist list (#449): presses Library's Playlists tab
   * on `/me` (the `open-playlist-list` workflow). Throws a {@link ReadStop}.
   */
  openPlaylistList(step: string): Promise<void>;
  /** Sends the playlist list read to n8Tracks (#449); null when it took it, else why not. */
  sendPlaylists(complete: boolean, playlists: Record<string, unknown>[]): Promise<string | null>;
  /** Reads the songs and the count a playlist page shows now (TS-007-24). Reads only. */
  readPlaylistPage(): PlaylistPageRead;
  /** Waits a moment for the page to draw, after it opened or was scrolled. */
  settle(): Promise<void>;
  sink: ExportSink;
  /** Counts so far, after each page. */
  progress(counts: SyncCounts): void;
  /** The versions the export reports. */
  versions: { extension: string; adapter: number };
  /** Now, as an ISO 8601 time, for `capturedAt`. */
  now(): string;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** A raw Suno record with its ID: a clip or a project. */
type Raw = Record<string, unknown> & { id: string };

function isRaw(value: unknown): value is Raw {
  return isRecord(value) && typeof value.id === 'string' && value.id !== '';
}

/**
 * Whether a scope reads Suno's Trash: a whole-library sync always does; a workspace or playlist sync
 * only when the user asked (#420), since reading the whole Trash is slow and serves only to archive
 * trashed linked clips, which the next sync that reads it does.
 */
export function readsTrash(scope: SyncScope): boolean {
  return scope.kind === 'library' || scope.readTrash;
}

/**
 * The legs a scope is read in. The workspace list, the user's voices (#439), and the user's
 * playlist list (#449) are read for every scope, the Trash last when read. A workspace sync reads
 * each chosen workspace's own feed rather than the whole library (#422).
 */
export function legsFor(scope: SyncScope): SyncLeg[] {
  const first: SyncLeg[] = [{ list: 'workspaces' }, { list: 'voices' }, { list: 'playlists' }];
  const trash: SyncLeg[] = readsTrash(scope) ? [{ list: 'trash' }] : [];
  switch (scope.kind) {
    case 'library':
      return [...first, { list: 'library' }, ...trash];
    case 'workspaces':
      return [...first, ...scope.ids.map((id): SyncLeg => ({ list: 'workspace', id })), ...trash];
    case 'playlists':
      return [
        ...first,
        ...scope.playlists.map((playlist): SyncLeg => ({ list: 'playlist', ...playlist })),
        ...trash,
      ];
  }
}

/** The step the panel and the failure report name for the voice leg (#439). */
export const VOICES_STEP = 'Read your voices';

/** The step the panel and the failure report name for the playlist-list leg (#449). */
export const PLAYLISTS_STEP = 'Read your playlists';

/** How the panel and the failure report name a leg's step. */
export function stepOf(leg: SyncLeg, legs: readonly SyncLeg[]): string {
  switch (leg.list) {
    case 'workspaces':
      return 'Read the workspace list';
    case 'voices':
      return VOICES_STEP;
    case 'playlists':
      return PLAYLISTS_STEP;
    case 'library':
      return 'Read the library';
    case 'trash':
      return 'Read the Trash';
    case 'workspace': {
      const workspaces = legs.filter((other) => other.list === 'workspace');
      return `Read workspace ${String(workspaces.indexOf(leg) + 1)} of ${String(workspaces.length)}`;
    }
    case 'playlist': {
      const playlists = legs.filter((other) => other.list === 'playlist');
      return `Read playlist ${String(playlists.indexOf(leg) + 1)} of ${String(playlists.length)}`;
    }
  }
}

/** The page of one list: a key that names it, its records, and whether it was the last. */
interface ListPage {
  key: string;
  records: Raw[];
}

/** How one list is recognised, read, and known to have ended (TS-003). */
interface ListSpec {
  kind: ObservedKind;
  /** "the library feed": read after "expected". */
  what: string;
  accepts: (message: ObservedMessage) => boolean;
  /** A page, or what the response should have looked like. */
  parse: (body: unknown, request: ObservedRequest) => ListPage | { problem: string };
  /** Whether the list has ended, after this page was taken. */
  ended: (body: unknown, seenRecords: number, seenPages: number) => boolean;
  /**
   * Whether the read goes on with what it has, as an incomplete list, when no next page comes after
   * the scroll that asked for it, instead of stopping (the workspace list, TS-007-15). It then asks
   * once, not {@link PAGE_RETRIES} times more: a scripted scroll never brings Suno's next page.
   */
  goesOnShort?: boolean;
}

/** What reading one list came to: the records read, and whether its end was seen. */
interface ListRead {
  count: number;
  complete: boolean;
}

function hasFilter(filters: unknown, name: string): boolean {
  return isRecord(filters) && isRecord(filters[name]);
}

function recordsOf(list: unknown, what: string): Raw[] | { problem: string } {
  if (!Array.isArray(list) || !list.every(isRaw)) {
    return { problem: `${what} with a list of records, each with an ID` };
  }
  return list;
}

function cursorKey(request: ObservedRequest): string {
  return `cursor:${request.cursor ?? '(first)'}`;
}

/**
 * Library › Songs: `POST /api/feed/v3` with a `user` filter. Cursor paging; the list ends when
 * `has_more` is false. An empty page ends it only then.
 */
const LIBRARY: ListSpec = {
  kind: 'library-feed',
  what: 'a page of the library feed',
  accepts: (message) =>
    hasFilter(message.request.filters, 'user') && !hasFilter(message.request.filters, 'workspace'),
  parse: (body, request) => {
    if (!isRecord(body) || typeof body.has_more !== 'boolean') {
      return { problem: 'a page of the library feed that says whether there is more' };
    }
    const records = recordsOf(body.clips, 'a page of the library feed');
    return 'problem' in records ? records : { key: cursorKey(request), records };
  },
  ended: (body) => isRecord(body) && body.has_more === false,
};

/** The workspace a feed request was filtered to (`filters.workspace.workspaceId`), or null. */
function workspaceFilterOf(filters: unknown): string | null {
  const workspace = isRecord(filters) ? filters.workspace : undefined;
  return isRecord(workspace) && typeof workspace.workspaceId === 'string'
    ? workspace.workspaceId
    : null;
}

/**
 * One workspace's songs on `/create?wid=<id>` (#422): `POST /api/feed/v3` with a `workspace`
 * filter naming that workspace, and no other. Cursor paging, 20 a page, each next page asked for
 * by the pager's › in the list's header (TS-007-23; by scroll until then); the list ends when
 * `has_more` is false (live, 2026-10-11: pages of 20, 20, and 5 for 45 clips).
 */
function workspaceSpec(id: string): ListSpec {
  return {
    ...LIBRARY,
    what: "a page of the workspace's songs",
    accepts: (message) => workspaceFilterOf(message.request.filters) === id,
    parse: (body, request) => {
      if (!isRecord(body) || typeof body.has_more !== 'boolean') {
        return { problem: "a page of the workspace's songs that says whether there is more" };
      }
      const records = recordsOf(body.clips, "a page of the workspace's songs");
      return 'problem' in records ? records : { key: cursorKey(request), records };
    },
  };
}

/**
 * The Trash: `GET /api/clips/trashed_v2`, cursor in the query; the list ends when no `next_cursor`
 * comes back (`num_total_results` is not trusted).
 */
const TRASH: ListSpec = {
  kind: 'trash',
  what: 'a page of the Trash list',
  accepts: () => true,
  parse: (body, request) => {
    if (!isRecord(body)) {
      return { problem: 'a page of the Trash list' };
    }
    const records = recordsOf(body.clips, 'a page of the Trash list');
    return 'problem' in records ? records : { key: cursorKey(request), records };
  },
  ended: (body) =>
    !isRecord(body) || typeof body.next_cursor !== 'string' || body.next_cursor === '',
};

/**
 * The workspace list: `GET /api/project/me?page=N`, 20 a page; it has ended when the workspaces read
 * number `num_total_results` (counted, not pages, TS-007-14). Suno loads a next page only on the
 * user's own scroll (TS-007-15), so the read goes on short when none comes.
 */
const WORKSPACES: ListSpec = {
  kind: 'workspaces',
  what: 'a page of the workspace list',
  accepts: () => true,
  parse: (body) => {
    if (
      !isRecord(body) ||
      typeof body.current_page !== 'number' ||
      typeof body.num_total_results !== 'number'
    ) {
      return { problem: 'a page of the workspace list with its page number and total' };
    }
    const records = recordsOf(body.projects, 'a page of the workspace list');
    return 'problem' in records ? records : { key: `page:${String(body.current_page)}`, records };
  },
  ended: (body, records) =>
    isRecord(body) &&
    typeof body.num_total_results === 'number' &&
    records >= body.num_total_results,
  goesOnShort: true,
};

/**
 * The user's voice list (#439): `GET /api/persona/get-personas/?page=N`, its answer
 * `{ personas, current_page, total_results }` as the observer forwards it. Only page 1 was seen
 * live (a few voices); it has ended when the voices read number `total_results`. Paging beyond it
 * is assumed to be by page number on scroll, as the workspace list's, so the read goes on short
 * when no next page comes.
 */
const VOICES: ListSpec = {
  kind: 'personas',
  what: 'a page of your voice list',
  accepts: () => true,
  parse: (body) => {
    if (
      !isRecord(body) ||
      typeof body.current_page !== 'number' ||
      typeof body.total_results !== 'number'
    ) {
      return { problem: 'a page of your voice list with its page number and total' };
    }
    const records = recordsOf(body.personas, 'a page of your voice list');
    return 'problem' in records ? records : { key: `page:${String(body.current_page)}`, records };
  },
  ended: (body, records) =>
    isRecord(body) && typeof body.total_results === 'number' && records >= body.total_results,
  goesOnShort: true,
};

/** What reading the workspace list came to: Suno's raw projects, and whether its end was seen. */
export interface WorkspaceListRead {
  workspaces: Record<string, unknown>[];
  complete: boolean;
}

/** What reading the workspace list needs: the list context, and the press that makes Suno send it. */
export type WorkspaceListContext = ListContext & Pick<LegContext, 'openWorkspaceList'>;

/**
 * Reads Suno's workspace list on Library (`/me`): presses its Workspaces tab (a direct load of the
 * list makes no request for it, #421), takes each page Suno sends, and scrolls once for each next
 * one, going on short, as incomplete, when none comes (TS-007-15, #434). The sync's first leg reads
 * it so, and so does the sync panel's "Refresh from Suno" (#448). `counted` hears the number read
 * after each page. Throws {@link ReadStop} or {@link ReadCancelled}.
 */
export async function readWorkspaceList(
  step: string,
  context: WorkspaceListContext,
  counted: (count: number) => void = () => undefined,
): Promise<WorkspaceListRead> {
  const projects: Raw[] = [];
  await context.openWorkspaceList(step);
  const read = await readList(
    WORKSPACES,
    step,
    context,
    (records) => {
      projects.push(...records);
      counted(projects.length);
      return Promise.resolve();
    },
    'opening the Workspaces tab',
  );
  return { workspaces: projects, complete: read.complete };
}

/** Plain words for why the voice leg did not finish, after "Your voices were not read: ". */
function voicesFailure(error: unknown): string {
  return error instanceof ReadStop
    ? `step '${error.step}' expected ${error.expected}`
    : 'the read did not finish';
}

/**
 * The voice leg (#439): opens the Voice picker, reads the voice list Suno sends, closes the picker,
 * and sends what was read to n8Tracks. Never throws but for {@link ReadCancelled}: whatever goes
 * wrong becomes the note the panel shows when the sync finishes, and the sync goes on.
 */
async function readVoices(
  step: string,
  context: LegContext,
  counts: SyncCounts,
): Promise<string | undefined> {
  const voices: Raw[] = [];
  let complete = false;
  let note: string | undefined;
  const list: ListContext = { ...context, more: (moreStep) => context.moreVoices(moreStep) };
  try {
    await context.openVoices(step);
    const read = await readList(
      VOICES,
      step,
      list,
      (records) => {
        voices.push(...records);
        counts.voices = voices.length;
        context.progress({ ...counts });
        return Promise.resolve();
      },
      'opening the Voice picker',
    );
    complete = read.complete;
  } catch (error) {
    if (error instanceof ReadCancelled || context.signal.aborted) {
      throw error instanceof ReadCancelled ? error : new ReadCancelled();
    }
    note = `Your voices were not read: ${voicesFailure(error)}.`;
  }
  try {
    await context.closeVoices(step);
  } catch {
    // The next leg's page replaces this one, picker and all.
  }
  if (note !== undefined && voices.length === 0) {
    return note;
  }
  const refused = await context.sendVoices(complete, voices);
  if (refused !== null) {
    return `Your voices were read but not recorded: ${refused}`;
  }
  return (
    note ??
    (complete
      ? undefined
      : `Only some of your voices were read (Suno sent no further page), so none was marked unavailable.`)
  );
}

/**
 * The user's playlist list (#449): `GET /api/playlist/me?page=N`, its answer `{ playlists,
 * current_page, num_total_results }` as the observer forwards it. One page held all ten playlists
 * of the capture; it has ended when the playlists read number `num_total_results`. Paging beyond it
 * is assumed to be by page number on scroll, as the workspace list's, so the read goes on short when
 * no next page comes.
 */
const PLAYLIST_LIST: ListSpec = {
  kind: 'playlists',
  what: 'a page of your playlist list',
  accepts: () => true,
  parse: (body) => {
    if (
      !isRecord(body) ||
      typeof body.current_page !== 'number' ||
      typeof body.num_total_results !== 'number'
    ) {
      return { problem: 'a page of your playlist list with its page number and total' };
    }
    const records = recordsOf(body.playlists, 'a page of your playlist list');
    return 'problem' in records ? records : { key: `page:${String(body.current_page)}`, records };
  },
  ended: (body, records) =>
    isRecord(body) &&
    typeof body.num_total_results === 'number' &&
    records >= body.num_total_results,
  goesOnShort: true,
};

/** What reading the playlist list came to: Suno's playlists (as forwarded), and whether its end was seen. */
export interface PlaylistListRead {
  playlists: Record<string, unknown>[];
  complete: boolean;
}

/** What reading the playlist list needs: the list context, and the press that makes Suno send it. */
export type PlaylistListContext = ListContext & Pick<LegContext, 'openPlaylistList'>;

/**
 * Reads Suno's playlist list on Library (`/me`), as {@link readWorkspaceList} reads the workspace
 * list: presses its Playlists tab, takes each page, and scrolls once for each next one, going on
 * short, as incomplete, when none comes. The sync's playlist-list leg reads it so, and so does the
 * Playlists choice's "Refresh from Suno" (#449). Throws {@link ReadStop} or {@link ReadCancelled}.
 */
export async function readPlaylistList(
  step: string,
  context: PlaylistListContext,
  counted: (count: number) => void = () => undefined,
): Promise<PlaylistListRead> {
  const playlists: Raw[] = [];
  await context.openPlaylistList(step);
  const read = await readList(
    PLAYLIST_LIST,
    step,
    context,
    (records) => {
      playlists.push(...records);
      counted(playlists.length);
      return Promise.resolve();
    },
    'opening the Playlists tab',
  );
  return { playlists, complete: read.complete };
}

/**
 * The playlist-list leg (#449): reads the list and sends it to n8Tracks. Never throws but for
 * {@link ReadCancelled}: what goes wrong becomes a note the panel shows when the sync finishes.
 */
async function readPlaylists(step: string, context: LegContext): Promise<string | undefined> {
  let read: PlaylistListRead;
  try {
    read = await readPlaylistList(step, context);
  } catch (error) {
    if (error instanceof ReadCancelled || context.signal.aborted) {
      throw error instanceof ReadCancelled ? error : new ReadCancelled();
    }
    return `Your playlist list was not read: ${voicesFailure(error)}.`;
  }
  const refused = await context.sendPlaylists(read.complete, read.playlists);
  if (refused !== null) {
    return `Your playlist list was read but not recorded: ${refused}`;
  }
  return read.complete
    ? undefined
    : 'Only some of your playlists were read (Suno sent no further page), so none was marked unavailable.';
}

/** How many scrolls in a row may bring no new row before a playlist is read as far as it goes. */
export const PLAYLIST_IDLE_SCROLLS = PAGE_RETRIES + 1;

/** How many waits for a playlist's first rows, after its page opened (20 seconds at 1 a second). */
export const PLAYLIST_FIRST_WAITS = 20;

/**
 * A playlist's songs, from its page (TS-007-24): the song IDs of its rows, and the count in its
 * header. It waits for the first rows to be drawn, then, while fewer songs are read than the
 * count, scrolls for more, keeping every row seen (Suno may draw only part of a long list at a
 * time); it ends when the count is reached, when a scroll brings nothing new
 * {@link PLAYLIST_IDLE_SCROLLS} times in a row, or (with no count shown) after the first scroll
 * that brings nothing. Paging past one load was not seen live; a read short of the count is
 * returned as it is, never stopped. Throws {@link ReadStop} when no row comes at all (and the
 * count is not 0), or {@link ReadCancelled}.
 */
async function readPlaylistSongs(step: string, context: LegContext): Promise<PlaylistPageRead> {
  const cancelled = () => context.signal.aborted;
  let read = context.readPlaylistPage();
  for (
    let wait = 0;
    read.clipIds.length === 0 && read.songCount !== 0 && wait < PLAYLIST_FIRST_WAITS;
    wait += 1
  ) {
    await context.settle();
    if (cancelled()) {
      throw new ReadCancelled();
    }
    read = context.readPlaylistPage();
  }
  if (read.clipIds.length === 0 && read.songCount !== 0) {
    throw new ReadStop(
      step,
      "the playlist's songs on its page within 20 seconds of opening it",
      true,
    );
  }
  const clipIds = [...read.clipIds];
  const seen = new Set(clipIds);
  let songCount = read.songCount;
  let idle = 0;
  while (
    songCount === null ? idle === 0 : clipIds.length < songCount && idle < PLAYLIST_IDLE_SCROLLS
  ) {
    if (cancelled()) {
      throw new ReadCancelled();
    }
    await context.more(step);
    await context.settle();
    const again = context.readPlaylistPage();
    songCount = songCount ?? again.songCount;
    const fresh = again.clipIds.filter((id) => !seen.has(id));
    for (const id of fresh) {
      seen.add(id);
      clipIds.push(id);
    }
    idle = fresh.length === 0 ? idle + 1 : 0;
  }
  return { clipIds, songCount };
}

/**
 * Reads one list to its end: takes each page Suno sends, asks for the next by `more`, and hands
 * the new records of each page to `take`. A list that {@link ListSpec.goesOnShort} ends short,
 * incomplete, when no next page comes. Throws {@link ReadStop} with `step` named, or
 * {@link ReadCancelled}.
 */
async function readList(
  spec: ListSpec,
  step: string,
  context: ListContext,
  take: (records: Raw[]) => Promise<void>,
  opening = 'opening the page',
): Promise<ListRead> {
  const keys = new Set<string>();
  const ids = new Set<string>();
  const cancelled = () => context.signal.aborted;
  let first = true;
  let repeats = 0;
  for (;;) {
    let message = await context.observations.next(
      spec.kind,
      spec.accepts,
      PAGE_WAIT_MS,
      context.signal,
    );
    for (let retry = 0; message === null && !cancelled(); retry += 1) {
      if (first) {
        throw new ReadStop(step, `${spec.what} within 20 seconds of ${opening}`, true);
      }
      if (spec.goesOnShort === true) {
        // No next page came after the scroll: the list goes on as read so far (TS-007-15).
        return { count: ids.size, complete: false };
      }
      if (retry === PAGE_RETRIES) {
        throw new ReadStop(
          step,
          `${spec.what} within 20 seconds (asked ${String(PAGE_RETRIES + 1)} times)`,
        );
      }
      await context.more(step);
      message = await context.observations.next(
        spec.kind,
        spec.accepts,
        PAGE_WAIT_MS,
        context.signal,
      );
    }
    if (cancelled() || message === null) {
      throw new ReadCancelled();
    }
    const page = spec.parse(message.body, message.request);
    if ('problem' in page) {
      throw new ReadStop(step, page.problem);
    }
    // Suno fetches the next page ahead when Library › Songs opens, and pressing › then asks for
    // that page again (TS-007-5). A page seen before is skipped without asking for more; only a
    // run of repeats with no new page between them stops the read.
    if (keys.has(page.key)) {
      repeats += 1;
      if (repeats > PAGE_RETRIES) {
        throw new ReadStop(step, `each page of the list once (Suno sent ${spec.what} again)`);
      }
      continue;
    }
    repeats = 0;
    keys.add(page.key);
    const fresh = page.records.filter((record) => !ids.has(record.id));
    for (const record of fresh) {
      ids.add(record.id);
    }
    await take(fresh);
    if (spec.ended(message.body, ids.size, keys.size)) {
      return { count: ids.size, complete: true };
    }
    first = false;
    if (cancelled()) {
      throw new ReadCancelled();
    }
    await context.more(step);
  }
}

function workspaceOf(clip: Raw): string | null {
  const project = clip.project;
  return isRecord(project) && typeof project.id === 'string' ? project.id : null;
}

/**
 * The export's header (`docs/suno-integration.md`, Export format), sent before the first part.
 * `trashed` says whether the sync's legs read the Trash: decided by the legs the service worker
 * holds, not the scope, so a sync begun before #420 still reports what it reads.
 */
export function exportHeader(
  scope: SyncScope,
  trashed: boolean,
  workspaces: readonly unknown[],
  libraryFilters: unknown,
  versions: LegContext['versions'],
  capturedAt: string,
  workspacesComplete = true,
): Record<string, unknown> {
  return {
    format: EXPORT_FORMAT,
    formatVersion: EXPORT_FORMAT_VERSION,
    extensionVersion: versions.extension,
    adapterVersion: String(versions.adapter),
    capturedAt,
    scope: {
      kind: scope.kind,
      ids:
        scope.kind === 'library'
          ? []
          : scope.kind === 'workspaces'
            ? scope.ids
            : scope.playlists.map((playlist) => playlist.id),
    },
    // Promises: the export is completed only if every list it reads was seen to its end.
    libraryComplete: scope.kind === 'library',
    // Without a Trash leg nothing is promised about the Trash, and no trashed clips are sent (#420).
    trashedComplete: trashed,
    // False when Suno sent no further page of the workspace list (TS-007-15, #434): the server then
    // changes no workspace's availability.
    workspacesComplete,
    workspaces,
    playlists: [],
    // The library filters Suno applied (TS-003: by default no disliked clips, stems, or Studio
    // clips), so the review can say which kinds were left out.
    libraryFilters,
  };
}

/**
 * Reads one leg on the page it is on, from the progress the service worker held, and returns the
 * progress to hand on. Clips are uploaded in parts of up to 200 as they are read; the export is
 * created before the first part, with the workspace list read in the first leg.
 */
export async function readLeg(
  scope: SyncScope,
  legs: readonly SyncLeg[],
  start: SyncProgress,
  context: LegContext,
): Promise<SyncProgress> {
  const leg = legs[start.leg];
  if (leg === undefined) {
    throw new ReadStop('Read Suno', 'a list still to read');
  }
  const step = stepOf(leg, legs);
  const counts = { ...start.counts };
  let partNumber = start.partNumber;
  let workspaces = start.workspaces;
  let workspacesComplete = start.workspacesComplete ?? true;
  const buffer: Raw[] = [];
  let created = false;

  const ensureExport = async (filters: unknown) => {
    if (!created) {
      await context.sink.create(
        exportHeader(
          scope,
          legs.some((other) => other.list === 'trash'),
          workspaces,
          filters,
          context.versions,
          context.now(),
          workspacesComplete,
        ),
      );
      created = true;
      workspaces = [];
    }
  };
  const flush = async (
    trashed: boolean,
    playlists: ExportPart['playlists'] = [],
    filters: unknown = null,
  ) => {
    if (buffer.length === 0 && playlists.length === 0) {
      return;
    }
    await ensureExport(filters);
    partNumber += 1;
    const clips = buffer.splice(0, buffer.length);
    await context.sink.part({
      partNumber,
      clips: trashed ? [] : clips,
      trashedClips: trashed ? clips : [],
      playlists,
    });
  };

  let voicesNote = start.voicesNote;
  let notes = start.notes ?? [];
  switch (leg.list) {
    case 'voices': {
      voicesNote = await readVoices(step, context, counts);
      break;
    }
    case 'workspaces': {
      const read = await readWorkspaceList(step, context, (count) => {
        counts.workspaces = count;
        context.progress({ ...counts });
      });
      workspaces = read.workspaces;
      workspacesComplete = read.complete;
      break;
    }
    case 'library':
    case 'workspace': {
      // A `library` leg in a workspace sync was begun before #422: it keeps the chosen workspaces'
      // clips. A `workspace` leg reads that workspace's own feed, which holds only its clips.
      const chosen =
        leg.list === 'library' && scope.kind === 'workspaces' ? new Set(scope.ids) : null;
      const list = leg.list === 'workspace' ? workspaceSpec(leg.id) : LIBRARY;
      let filters: unknown = null;
      const spec: ListSpec = {
        ...list,
        accepts: (message) => {
          const accepted = list.accepts(message);
          if (accepted && filters === null) {
            filters = message.request.filters;
          }
          return accepted;
        },
      };
      await readList(spec, step, context, async (records) => {
        const kept =
          chosen === null ? records : records.filter((clip) => chosen.has(workspaceOf(clip) ?? ''));
        buffer.push(...kept);
        counts.clips += kept.length;
        context.progress({ ...counts });
        while (buffer.length >= PART_CLIPS) {
          const rest = buffer.splice(PART_CLIPS);
          await flush(false, [], filters);
          buffer.push(...rest);
        }
      });
      // The export exists even when nothing was kept, so an empty library is still reviewed.
      await ensureExport(filters);
      await flush(false, [], filters);
      break;
    }
    case 'playlists': {
      const note = await readPlaylists(step, context);
      if (note !== undefined) {
        notes = [...notes, note];
      }
      break;
    }
    case 'playlist': {
      // Membership only (#449): the songs' own data comes from library and workspace syncs.
      const read = await readPlaylistSongs(step, context);
      counts.playlists += 1;
      context.progress({ ...counts });
      if (read.songCount !== null && read.clipIds.length < read.songCount) {
        const name = leg.name.trim() === '' ? 'A playlist' : `Playlist “${leg.name}”`;
        notes = [
          ...notes,
          `${name} was read in part: ${String(read.clipIds.length)} of ${String(read.songCount)} songs (Suno showed no more). The review shows it so, and does not record it unless you choose to.`,
        ];
      }
      await flush(false, [
        { id: leg.id, name: leg.name, clipIds: read.clipIds, songCount: read.songCount },
      ]);
      break;
    }
    case 'trash': {
      await ensureExport(null);
      await readList(TRASH, step, context, async (records) => {
        buffer.push(...records);
        counts.trashed += records.length;
        context.progress({ ...counts });
        while (buffer.length >= PART_CLIPS) {
          const rest = buffer.splice(PART_CLIPS);
          await flush(true);
          buffer.push(...rest);
        }
      });
      await flush(true);
      break;
    }
  }
  return {
    leg: start.leg + 1,
    attempt: 0,
    partNumber,
    counts,
    workspaces,
    workspacesComplete,
    ...(voicesNote === undefined ? {} : { voicesNote }),
    ...(notes.length === 0 ? {} : { notes }),
  };
}

/** The step the Download view (#215) names when reading the library stops. */
export const LIBRARY_STEP = 'Read the library';

/**
 * Reads Library › Songs to its end for the Download view (#215): the same list, read the same way,
 * as a sync's library leg (every workspace's clips; Suno leaves trashed clips out). Each page's new
 * records go to `take` as they come. Nothing is sent anywhere. Throws {@link ReadStop} or
 * {@link ReadCancelled}; the records taken before then stay with the caller.
 */
export async function readLibrary(
  context: ListContext,
  take: (records: readonly Record<string, unknown>[]) => void,
): Promise<number> {
  const read = await readList(LIBRARY, LIBRARY_STEP, context, (records) => {
    take(records);
    return Promise.resolve();
  });
  return read.count;
}
