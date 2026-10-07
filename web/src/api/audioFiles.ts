import { useCallback, useEffect, useState } from 'react';
import { apiFetch } from './client';
import { writeWithRevision, type SaveResult } from './saves';
import { body, isRecord, useResource, type LoadState } from './songs';

/** Where Library → Unmatched Files is (#209). */
export const UNMATCHED_PATH = '/library/unmatched';

/** How many files a page of Unmatched Files holds; the API's limit with suggestions. */
export const UNMATCHED_PAGE_SIZE = 100;

/** The status an audio file reports: unavailable while the media folder cannot be read. */
export type AudioFileStatus = 'available' | 'missing' | 'unavailable';

/** Why a file has no association (#206), or null when nothing in particular. */
export type UnmatchedReason =
  'generation_deleted' | 'multiple_suno_ids' | 'unassociated_by_user' | 'song_deleted';

/** A suggested Generation: its shortcode and, when it has them, its Suno title and duration. */
export interface SuggestedGeneration {
  id: string;
  shortcode: string;
  sunoTitle: string | null;
  durationSeconds: number | null;
}

/** One piece of evidence for a suggestion, as a code with what it names; the page writes the sentence. */
export interface MatchReason {
  code: string;
  generation?: SuggestedGeneration;
  folder?: string;
  artist?: string;
  artistSource?: string;
  differenceSeconds?: number;
}

/** A suggested Song, the Generation the evidence points at (or null), the score, and the reasons. */
export interface MatchSuggestion {
  song: { id: string; shortcode: string; title: string };
  generation: SuggestedGeneration | null;
  score: number;
  reasons: MatchReason[];
}

/** How an association was made: by a scan, from the Suno ID in the file name, or by the user. */
export type AssociationOrigin = 'suno-id' | 'user';

/**
 * An audio file as the list answers it, with its suggestions (best first, up to three; none once it
 * is associated). `song` and `generation` are its association (#210), or null; `autoMatchBlocked` is
 * true once the user removed or replaced the association of a file whose name holds a UUID, so scans
 * no longer match it by Suno ID.
 */
export interface UnmatchedFile {
  id: string;
  path: string;
  fileName: string;
  format: string;
  sizeBytes: number;
  firstSeenAt: string;
  status: AudioFileStatus;
  durationSeconds: number | null;
  song: { id: string; shortcode: string; title: string } | null;
  generation: { id: string; shortcode: string } | null;
  associationOrigin: AssociationOrigin | null;
  unmatchedReason: UnmatchedReason | null;
  revision: number;
  autoMatchBlocked: boolean;
  suggestions: MatchSuggestion[];
}

export interface UnmatchedPage {
  items: UnmatchedFile[];
  total: number;
  offset: number;
  limit: number;
}

/** The orders the page offers: first seen (newest first by default), name, and folder. */
export type UnmatchedSort = 'firstSeen' | 'name' | 'folder';

export type SortDirection = 'asc' | 'desc';

/** Which files the page lists (#210): the unmatched ones (the default), the associated ones, or all. */
export type UnmatchedShow = 'unmatched' | 'associated' | 'all';

/** What the page shows: which files, a search in the name or folder, an order, and a page (from 1). */
export interface UnmatchedQuery {
  show: UnmatchedShow;
  q: string;
  sort: UnmatchedSort;
  direction: SortDirection;
  page: number;
}

const SHOWS: readonly UnmatchedShow[] = ['unmatched', 'associated', 'all'];

/** The API's association filter for each choice. */
const ASSOCIATION_FILTER: Record<UnmatchedShow, string> = {
  unmatched: 'none',
  associated: 'associated',
  all: 'any',
};

const SORTS: readonly UnmatchedSort[] = ['firstSeen', 'name', 'folder'];

/** The direction each order reads in unless the address says otherwise. */
export function defaultDirection(sort: UnmatchedSort): SortDirection {
  return sort === 'firstSeen' ? 'desc' : 'asc';
}

const textOrNull = (value: unknown) => value === null || typeof value === 'string';
const numberOrNull = (value: unknown) => value === null || typeof value === 'number';

function isGeneration(value: unknown): value is SuggestedGeneration {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.shortcode === 'string' &&
    textOrNull(value.sunoTitle) &&
    numberOrNull(value.durationSeconds)
  );
}

function isReason(value: unknown): value is MatchReason {
  return (
    isRecord(value) &&
    typeof value.code === 'string' &&
    (value.generation === undefined || isGeneration(value.generation)) &&
    (value.folder === undefined || typeof value.folder === 'string') &&
    (value.artist === undefined || typeof value.artist === 'string') &&
    (value.artistSource === undefined || typeof value.artistSource === 'string') &&
    (value.differenceSeconds === undefined || typeof value.differenceSeconds === 'number')
  );
}

function isSuggestion(value: unknown): value is MatchSuggestion {
  return (
    isRecord(value) &&
    isRecord(value.song) &&
    typeof value.song.id === 'string' &&
    typeof value.song.shortcode === 'string' &&
    typeof value.song.title === 'string' &&
    (value.generation === null || isGeneration(value.generation)) &&
    typeof value.score === 'number' &&
    Array.isArray(value.reasons) &&
    value.reasons.every(isReason)
  );
}

const isLink = (value: unknown) =>
  value === null ||
  (isRecord(value) && typeof value.id === 'string' && typeof value.shortcode === 'string');

/** A file as answered: with suggestions only when the list was asked for them. */
type AnsweredFile = Omit<UnmatchedFile, 'suggestions'> & { suggestions?: MatchSuggestion[] };

function isFile(value: unknown): value is AnsweredFile {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.path === 'string' &&
    typeof value.fileName === 'string' &&
    typeof value.format === 'string' &&
    typeof value.sizeBytes === 'number' &&
    typeof value.firstSeenAt === 'string' &&
    (value.status === 'available' ||
      value.status === 'missing' ||
      value.status === 'unavailable') &&
    numberOrNull(value.durationSeconds) &&
    isLink(value.song) &&
    (value.song === null || (isRecord(value.song) && typeof value.song.title === 'string')) &&
    isLink(value.generation) &&
    (value.associationOrigin === null ||
      value.associationOrigin === 'suno-id' ||
      value.associationOrigin === 'user') &&
    textOrNull(value.unmatchedReason) &&
    typeof value.revision === 'number' &&
    typeof value.autoMatchBlocked === 'boolean' &&
    (value.suggestions === undefined ||
      (Array.isArray(value.suggestions) && value.suggestions.every(isSuggestion)))
  );
}

/** One file as the API answers it; a file answered without suggestions gets none. */
function acceptFile(answer: unknown): UnmatchedFile | undefined {
  return isFile(answer) ? { ...answer, suggestions: answer.suggestions ?? [] } : undefined;
}

function acceptPage(answer: unknown): UnmatchedPage | undefined {
  if (
    !isRecord(answer) ||
    !Array.isArray(answer.items) ||
    typeof answer.total !== 'number' ||
    typeof answer.offset !== 'number' ||
    typeof answer.limit !== 'number'
  ) {
    return undefined;
  }
  const items: unknown[] = answer.items;
  const files = items.map((item) =>
    isFile(item) && item.suggestions !== undefined ? acceptFile(item) : undefined,
  );
  return files.every((file): file is UnmatchedFile => file !== undefined)
    ? { items: files, total: answer.total, offset: answer.offset, limit: answer.limit }
    : undefined;
}

/** The query the address holds; anything unknown is left at its default. */
export function unmatchedQueryFrom(parameters: URLSearchParams): UnmatchedQuery {
  const sortText = parameters.get('sort');
  const sort = SORTS.includes(sortText as UnmatchedSort)
    ? (sortText as UnmatchedSort)
    : 'firstSeen';
  const directionText = parameters.get('direction');
  const direction =
    directionText === 'asc' || directionText === 'desc' ? directionText : defaultDirection(sort);
  const page = Number(parameters.get('page') ?? '1');
  const showText = parameters.get('show');
  return {
    show: SHOWS.includes(showText as UnmatchedShow) ? (showText as UnmatchedShow) : 'unmatched',
    q: parameters.get('q') ?? '',
    sort,
    direction,
    page: Number.isInteger(page) && page >= 1 ? page : 1,
  };
}

/** The page's address for `query`, defaults left out. */
export function unmatchedParameters(query: UnmatchedQuery): URLSearchParams {
  const parameters = new URLSearchParams();
  if (query.show !== 'unmatched') {
    parameters.set('show', query.show);
  }
  if (query.q.trim() !== '') {
    parameters.set('q', query.q.trim());
  }
  if (query.sort !== 'firstSeen') {
    parameters.set('sort', query.sort);
  }
  if (query.direction !== defaultDirection(query.sort)) {
    parameters.set('direction', query.direction);
  }
  if (query.page !== 1) {
    parameters.set('page', String(query.page));
  }
  return parameters;
}

/** The API's query for `query`: the files chosen, with their suggestions, one page of 100. */
export function unmatchedApiPath(query: UnmatchedQuery): string {
  const parameters = new URLSearchParams({
    association: ASSOCIATION_FILTER[query.show],
    include: 'suggestions',
    sort: query.sort,
    direction: query.direction,
    offset: String((query.page - 1) * UNMATCHED_PAGE_SIZE),
    limit: String(UNMATCHED_PAGE_SIZE),
  });
  if (query.q.trim() !== '') {
    parameters.set('q', query.q.trim());
  }
  return `api/v1/audio-files?${parameters.toString()}`;
}

/** A page of Unmatched Files, with suggestions. */
export function useUnmatchedFiles(query: UnmatchedQuery) {
  return useResource(unmatchedApiPath(query), acceptPage);
}

/** The Song's list (#211): every file, without suggestions. */
function acceptSongFiles(answer: unknown): UnmatchedFile[] | undefined {
  if (!isRecord(answer) || !Array.isArray(answer.items)) {
    return undefined;
  }
  const items: unknown[] = answer.items;
  const files = items.map(acceptFile);
  return files.every((file): file is UnmatchedFile => file !== undefined) ? files : undefined;
}

const songFilesPath = (reference: string) =>
  `api/v1/songs/${encodeURIComponent(reference)}/audio-files`;

/**
 * Every local audio file associated with a Song (#211), by its ID or shortcode: Song-level files
 * first, then by Version tree order, Generation ordinal, and format, as the API orders them. Not
 * paged. `reload` reads it again, keeping the list already shown until the answer comes (and when
 * reading again fails).
 */
export function useSongAudioFiles(reference: string): {
  state: LoadState<UnmatchedFile[]>;
  reload: () => void;
} {
  const [loaded, setLoaded] = useState<{ reference: string; state: LoadState<UnmatchedFile[]> }>({
    reference,
    state: { phase: 'loading' },
  });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    const settle = (next: LoadState<UnmatchedFile[]>) => {
      if (controller.signal.aborted) {
        return;
      }
      setLoaded((previous) =>
        next.phase !== 'ready' &&
        previous.reference === reference &&
        previous.state.phase === 'ready'
          ? previous
          : { reference, state: next },
      );
    };
    const load = async () => {
      try {
        const response = await apiFetch(songFilesPath(reference), { signal: controller.signal });
        const answer = await body(response);
        const data = response.ok ? acceptSongFiles(answer) : undefined;
        if (data !== undefined) {
          settle({ phase: 'ready', data });
        } else {
          settle(
            response.status === 404 ? { phase: 'not-found', problem: answer } : { phase: 'error' },
          );
        }
      } catch {
        settle({ phase: 'error' });
      }
    };
    void load();
    return () => {
      controller.abort();
    };
  }, [reference, attempt]);

  const reload = useCallback(() => {
    setLoaded((previous) =>
      previous.state.phase === 'ready' ? previous : { reference, state: { phase: 'loading' } },
    );
    setAttempt((previous) => previous + 1);
  }, [reference]);

  return {
    state: loaded.reference === reference ? loaded.state : { phase: 'loading' },
    reload,
  };
}

const filePath = (id: string, action: string) =>
  `api/v1/audio-files/${encodeURIComponent(id)}/${action}`;

/**
 * Associates the file with a Song (by ID) and, when `generation` is not null, one of its Generations
 * (#210), based on the revision the page read; any association it has is replaced.
 */
export function associateFile(
  file: Pick<UnmatchedFile, 'id' | 'revision'>,
  song: string,
  generation: string | null,
): Promise<SaveResult<UnmatchedFile>> {
  return writeWithRevision(
    'PUT',
    filePath(file.id, 'association'),
    file.revision,
    { song, generation },
    acceptFile,
  );
}

/**
 * Removes the file's association, based on the revision the page read: it is back among the
 * unmatched, "unassociated by you". The API answers no body, so a saved result carries null.
 */
export function removeAssociation(
  file: Pick<UnmatchedFile, 'id' | 'revision'>,
): Promise<SaveResult<UnmatchedFile | null>> {
  return writeWithRevision(
    'DELETE',
    filePath(file.id, 'association'),
    file.revision,
    undefined,
    (answer) => (answer === undefined ? null : acceptFile(answer)),
  );
}

/** "Match by Suno ID again": clears the file's block on automatic matching and matches it at once. */
export function rematchFile(
  file: Pick<UnmatchedFile, 'id' | 'revision'>,
): Promise<SaveResult<UnmatchedFile>> {
  return writeWithRevision(
    'POST',
    filePath(file.id, 'rematch'),
    file.revision,
    undefined,
    acceptFile,
  );
}
