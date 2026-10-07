import type { ExportPart, SyncCounts, SyncLeg, SyncProgress, SyncScope } from '../messages.ts';
import type { ObservedKind, ObservedMessage, ObservedRequest } from './observed.ts';

/**
 * The library reader (#134): reads the lists a sync needs by watching Suno's own responses
 * (`observed.ts`) while the page is operated, and hands the records to n8Tracks in parts. A sync
 * is a list of legs, each read on its own Suno page: the workspace list, then the library or each
 * chosen playlist, then the Trash. Navigating between them belongs to the content script; this
 * module reads one leg on the page it is given.
 *
 * Paging and end of list are TS-003's. A list counts as read only when its end was seen; a page
 * that does not look as expected, one that comes twice, or one that does not come stops the read
 * with the step named, and the content script discards the export. Nothing partial is offered for
 * review, so a completed export's `libraryComplete`, `trashedComplete`, and `workspacesComplete`
 * are what the read set out to read: a whole-library read is the only one with `libraryComplete`.
 */

/** How long a page of a list may take to come, after the action that asks for it (20 seconds). */
export const PAGE_WAIT_MS = 20_000;
/** How many times the action that asks for a page is repeated before the read stops. */
export const PAGE_RETRIES = 2;
/** The most clips in one part (`docs/suno-integration.md`, Limits). */
export const PART_CLIPS = 200;
/** Workspaces per page of `/api/project/me` (TS-003). */
export const WORKSPACES_PER_PAGE = 20;

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

/** The legs a scope is read in. The workspace and Trash lists are read for every scope. */
export function legsFor(scope: SyncScope): SyncLeg[] {
  switch (scope.kind) {
    case 'library':
    case 'workspaces':
      return [{ list: 'workspaces' }, { list: 'library' }, { list: 'trash' }];
    case 'playlists':
      return [
        { list: 'workspaces' },
        ...scope.playlists.map((playlist): SyncLeg => ({ list: 'playlist', ...playlist })),
        { list: 'trash' },
      ];
  }
}

/** How the panel and the failure report name a leg's step. */
export function stepOf(leg: SyncLeg, legs: readonly SyncLeg[]): string {
  switch (leg.list) {
    case 'workspaces':
      return 'Read the workspace list';
    case 'library':
      return 'Read the library';
    case 'trash':
      return 'Read the Trash';
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
 * The workspace list: `GET /api/project/me?page=N`, 20 per page; it has ended when the pages seen
 * hold `num_total_results`.
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
  ended: (body, _records, pages) =>
    isRecord(body) &&
    typeof body.num_total_results === 'number' &&
    pages * WORKSPACES_PER_PAGE >= body.num_total_results,
};

function songCountOf(body: unknown): number | null {
  const feed = isRecord(body) ? body.feed : undefined;
  const metadata = isRecord(feed) ? feed.feed_metadata : undefined;
  return isRecord(metadata) && typeof metadata.song_count === 'number' ? metadata.song_count : null;
}

/**
 * A playlist's songs: `POST /api/unified/feed` for `generic_playlist:<id>`, 50 per page. Paging
 * past 50 is unverified (TS-003), so the list has ended only when the songs read equal the
 * playlist's `song_count`; until then the reader scrolls for more, and stops if none comes.
 */
function playlistSpec(id: string): ListSpec {
  const feedId = `generic_playlist:${id}`;
  return {
    kind: 'playlist-feed',
    what: "a page of the playlist's songs",
    accepts: (message) => message.request.feedId === feedId,
    parse: (body, request) => {
      const feed = isRecord(body) ? body.feed : undefined;
      if (!isRecord(feed) || !Array.isArray(feed.items) || songCountOf(body) === null) {
        return { problem: "a page of the playlist's songs with the playlist's song count" };
      }
      const clips = feed.items.map((item) => (isRecord(item) ? item.content_item : undefined));
      const records = recordsOf(clips, "a page of the playlist's songs");
      return 'problem' in records ? records : { key: cursorKey(request), records };
    },
    ended: (body, records) => records >= (songCountOf(body) ?? Number.POSITIVE_INFINITY),
  };
}

/**
 * Reads one list to its end: takes each page Suno sends, asks for the next by `more`, and hands
 * the new records of each page to `take`. Throws {@link ReadStop} with `step` named, or
 * {@link ReadCancelled}.
 */
async function readList(
  spec: ListSpec,
  step: string,
  context: ListContext,
  take: (records: Raw[]) => Promise<void>,
): Promise<number> {
  const keys = new Set<string>();
  const ids = new Set<string>();
  const cancelled = () => context.signal.aborted;
  let first = true;
  for (;;) {
    let message = await context.observations.next(
      spec.kind,
      spec.accepts,
      PAGE_WAIT_MS,
      context.signal,
    );
    for (let retry = 0; message === null && !cancelled(); retry += 1) {
      if (first) {
        throw new ReadStop(step, `${spec.what} within 20 seconds of opening the page`, true);
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
    if (keys.has(page.key)) {
      throw new ReadStop(step, `each page of the list once (Suno sent ${spec.what} again)`);
    }
    keys.add(page.key);
    const fresh = page.records.filter((record) => !ids.has(record.id));
    for (const record of fresh) {
      ids.add(record.id);
    }
    await take(fresh);
    if (spec.ended(message.body, ids.size, keys.size)) {
      return ids.size;
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

/** The export's header (`docs/suno-integration.md`, Export format), sent before the first part. */
export function exportHeader(
  scope: SyncScope,
  workspaces: readonly unknown[],
  libraryFilters: unknown,
  versions: LegContext['versions'],
  capturedAt: string,
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
    trashedComplete: true,
    workspacesComplete: true,
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
  const buffer: Raw[] = [];
  let created = false;

  const ensureExport = async (filters: unknown) => {
    if (!created) {
      await context.sink.create(
        exportHeader(scope, workspaces, filters, context.versions, context.now()),
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

  switch (leg.list) {
    case 'workspaces': {
      const projects: Raw[] = [];
      await readList(WORKSPACES, step, context, (records) => {
        projects.push(...records);
        counts.workspaces = projects.length;
        context.progress({ ...counts });
        return Promise.resolve();
      });
      workspaces = projects;
      break;
    }
    case 'library': {
      const chosen = scope.kind === 'workspaces' ? new Set(scope.ids) : null;
      let filters: unknown = null;
      const spec: ListSpec = {
        ...LIBRARY,
        accepts: (message) => {
          const accepted = LIBRARY.accepts(message);
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
    case 'playlist': {
      const clipIds: string[] = [];
      await readList(playlistSpec(leg.id), step, context, async (records) => {
        buffer.push(...records);
        clipIds.push(...records.map((clip) => clip.id));
        counts.clips += records.length;
        context.progress({ ...counts });
        while (buffer.length >= PART_CLIPS) {
          const rest = buffer.splice(PART_CLIPS);
          await flush(false);
          buffer.push(...rest);
        }
      });
      counts.playlists += 1;
      context.progress({ ...counts });
      await flush(false, [{ id: leg.id, name: leg.name, clipIds }]);
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
  return { leg: start.leg + 1, attempt: 0, partNumber, counts, workspaces };
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
  return readList(LIBRARY, LIBRARY_STEP, context, (records) => {
    take(records);
    return Promise.resolve();
  });
}
