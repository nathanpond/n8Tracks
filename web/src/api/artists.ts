import { apiFetch } from './client';
import { failureOf, ifMatch, type SaveResult } from './saves';
import { body, isErrorMap, isRecord, useResource } from './songs';

const ARTISTS_PATH = 'api/v1/artists';

/** The limits the API checks, in UTF-16 code units once normalised. */
export const ARTIST_NAME_MAXIMUM_LENGTH = 200;
export const ARTIST_ALIAS_MAXIMUM_COUNT = 20;
export const ARTIST_NOTES_MAXIMUM_LENGTH = 10_000;
export const ARTIST_LINK_MAXIMUM_COUNT = 20;
export const ARTIST_LINK_LABEL_MAXIMUM_LENGTH = 100;
export const ARTIST_LINK_URL_MAXIMUM_LENGTH = 2_000;

/** The page size the list is asked for: the API's default. */
export const ARTISTS_PAGE_SIZE = 50;

/** An external link of an Artist: an http or https URL and an optional label. */
export interface ArtistLink {
  label: string | null;
  url: string;
}

/** An Artist as the API answers it. Times are UTC ISO 8601. */
export interface Artist {
  id: string;
  name: string;
  aliases: string[];
  /** Plain text; null when there are none. */
  notes: string | null;
  links: ArtistLink[];
  /** Songs credited to the Artist (primary or featured, in every workflow state). */
  songCount: number;
  albumCount: number;
  createdAt: string;
  updatedAt: string;
  revision: number;
}

export interface ArtistPage {
  items: Artist[];
  page: number;
  pageSize: number;
  total: number;
}

/** Another Artist that already has a name being given, and which of its names matched. */
export interface ArtistMatch {
  id: string;
  name: string;
  matchedText: string;
  matchedOn: 'name' | 'alias';
}

/** What the Artists list shows: the list's own query parameters. */
export interface ArtistQuery {
  /** A part of a name or alias; empty for every Artist. */
  search: string;
  page: number;
}

function isArtistLink(value: unknown): value is ArtistLink {
  return (
    isRecord(value) &&
    (value.label === null || typeof value.label === 'string') &&
    typeof value.url === 'string'
  );
}

export function isArtist(value: unknown): value is Artist {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.name === 'string' &&
    Array.isArray(value.aliases) &&
    value.aliases.every((alias) => typeof alias === 'string') &&
    (value.notes === null || typeof value.notes === 'string') &&
    Array.isArray(value.links) &&
    value.links.every(isArtistLink) &&
    typeof value.songCount === 'number' &&
    typeof value.albumCount === 'number' &&
    typeof value.createdAt === 'string' &&
    typeof value.updatedAt === 'string' &&
    typeof value.revision === 'number'
  );
}

function isArtistPage(value: unknown): value is ArtistPage {
  return (
    isRecord(value) &&
    Array.isArray(value.items) &&
    value.items.every(isArtist) &&
    typeof value.page === 'number' &&
    typeof value.pageSize === 'number' &&
    typeof value.total === 'number'
  );
}

function isArtistMatch(value: unknown): value is ArtistMatch {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.name === 'string' &&
    typeof value.matchedText === 'string' &&
    (value.matchedOn === 'name' || value.matchedOn === 'alias')
  );
}

/** The matches of a 409 `duplicate_artist_name`, or undefined when `answer` is not one. */
function duplicateMatches(status: number, answer: unknown): ArtistMatch[] | undefined {
  return status === 409 &&
    isRecord(answer) &&
    answer.code === 'duplicate_artist_name' &&
    Array.isArray(answer.matches) &&
    answer.matches.every(isArtistMatch)
    ? answer.matches
    : undefined;
}

/** The list's query string for `query`; values that are the API's defaults are left out. */
export function artistListParameters(query: ArtistQuery): URLSearchParams {
  const parameters = new URLSearchParams();
  if (query.search.trim() !== '') {
    parameters.set('search', query.search);
  }
  if (query.page !== 1) {
    parameters.set('page', String(query.page));
  }
  return parameters;
}

/** Reads the list's view out of a page URL's query string; anything not understood is the default. */
export function artistQueryFrom(parameters: URLSearchParams): ArtistQuery {
  const page = Number(parameters.get('page') ?? '1');
  return {
    search: parameters.get('search') ?? '',
    page: Number.isSafeInteger(page) && page >= 1 ? page : 1,
  };
}

const acceptPage = (answer: unknown) => (isArtistPage(answer) ? answer : undefined);
const acceptArtist = (answer: unknown) => (isArtist(answer) ? answer : undefined);

/** One page of Artists for the list's view. */
export function useArtists(query: ArtistQuery) {
  const parameters = artistListParameters({ ...query, search: query.search.trim() }).toString();
  return useResource(parameters ? `${ARTISTS_PATH}?${parameters}` : ARTISTS_PATH, acceptPage);
}

/** One Artist, by its ID. */
export function useArtist(id: string) {
  return useResource(`${ARTISTS_PATH}/${encodeURIComponent(id)}`, acceptArtist);
}

/** A new Artist: the name, and optionally aliases, notes, and links. */
export interface NewArtist {
  name: string;
  aliases?: string[];
  notes?: string | null;
  links?: ArtistLink[];
}

/** How a create ended, by the API's answer. Never a rejection. */
export type CreateArtistResult =
  | { kind: 'created'; artist: Artist }
  | { kind: 'duplicate'; matches: ArtistMatch[] }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'failed' };

/** Creates an Artist; a name another Artist has is `duplicate` unless `confirmDuplicate`. */
export async function createArtist(
  artist: NewArtist,
  confirmDuplicate = false,
): Promise<CreateArtistResult> {
  try {
    const response = await apiFetch(ARTISTS_PATH, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ...artist, ...(confirmDuplicate ? { confirmDuplicate } : {}) }),
    });
    const answer = await body(response);
    if (response.status === 201 && isArtist(answer)) {
      return { kind: 'created', artist: answer };
    }
    const matches = duplicateMatches(response.status, answer);
    if (matches !== undefined) {
      return { kind: 'duplicate', matches };
    }
    if (
      response.status === 422 &&
      isRecord(answer) &&
      answer.code === 'validation_failed' &&
      isErrorMap(answer.errors)
    ) {
      return { kind: 'invalid', errors: answer.errors };
    }
    return { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/** An edit of an Artist: only the fields given change; aliases and links replace the whole lists. */
export interface ArtistEdit {
  name?: string;
  aliases?: string[];
  notes?: string | null;
  links?: ArtistLink[];
}

/** How an edit ended: a save's result, or the other Artists that already have a new name. */
export type UpdateArtistResult = SaveResult<Artist> | { kind: 'duplicate'; matches: ArtistMatch[] };

/**
 * Edits an Artist, based on `artist`'s revision; a stale revision comes back as a conflict, and a
 * new name or alias another Artist has as `duplicate` unless `confirmDuplicate`.
 */
export async function updateArtist(
  artist: Pick<Artist, 'id' | 'revision'>,
  edit: ArtistEdit,
  confirmDuplicate = false,
): Promise<UpdateArtistResult> {
  try {
    const response = await apiFetch(`${ARTISTS_PATH}/${encodeURIComponent(artist.id)}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json', 'If-Match': ifMatch(artist.revision) },
      body: JSON.stringify({ ...edit, ...(confirmDuplicate ? { confirmDuplicate } : {}) }),
    });
    const answer = await body(response);
    if (response.ok) {
      return isArtist(answer)
        ? { kind: 'saved', record: answer }
        : { kind: 'failed', reason: 'server' };
    }
    if (response.status === 409 && isRecord(answer) && answer.code === 'revision_conflict') {
      return isArtist(answer.current)
        ? { kind: 'conflict', current: answer.current }
        : { kind: 'failed', reason: 'server' };
    }
    const matches = duplicateMatches(response.status, answer);
    if (matches !== undefined) {
      return { kind: 'duplicate', matches };
    }
    if (
      response.status === 422 &&
      isRecord(answer) &&
      answer.code === 'validation_failed' &&
      isErrorMap(answer.errors)
    ) {
      return { kind: 'invalid', errors: answer.errors };
    }
    return { kind: 'failed', reason: failureOf(response.status, answer) };
  } catch {
    return { kind: 'failed', reason: 'unreachable' };
  }
}
