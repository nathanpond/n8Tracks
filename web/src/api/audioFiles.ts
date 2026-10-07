import { isRecord, useResource } from './songs';

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

/** An unmatched audio file as the list answers it, with its suggestions (best first, up to three). */
export interface UnmatchedFile {
  id: string;
  path: string;
  fileName: string;
  format: string;
  sizeBytes: number;
  firstSeenAt: string;
  status: AudioFileStatus;
  durationSeconds: number | null;
  unmatchedReason: UnmatchedReason | null;
  revision: number;
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

/** What the page shows: a search in the name or folder, an order, and a page (from 1). */
export interface UnmatchedQuery {
  q: string;
  sort: UnmatchedSort;
  direction: SortDirection;
  page: number;
}

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

function isFile(value: unknown): value is UnmatchedFile {
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
    textOrNull(value.unmatchedReason) &&
    typeof value.revision === 'number' &&
    Array.isArray(value.suggestions) &&
    value.suggestions.every(isSuggestion)
  );
}

const acceptPage = (answer: unknown): UnmatchedPage | undefined =>
  isRecord(answer) &&
  Array.isArray(answer.items) &&
  answer.items.every(isFile) &&
  typeof answer.total === 'number' &&
  typeof answer.offset === 'number' &&
  typeof answer.limit === 'number'
    ? (answer as unknown as UnmatchedPage)
    : undefined;

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
  return {
    q: parameters.get('q') ?? '',
    sort,
    direction,
    page: Number.isInteger(page) && page >= 1 ? page : 1,
  };
}

/** The page's address for `query`, defaults left out. */
export function unmatchedParameters(query: UnmatchedQuery): URLSearchParams {
  const parameters = new URLSearchParams();
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

/** The API's query for `query`: unassociated files with their suggestions, one page of 100. */
export function unmatchedApiPath(query: UnmatchedQuery): string {
  const parameters = new URLSearchParams({
    association: 'none',
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
