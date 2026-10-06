import { useCallback, useEffect, useState } from 'react';
import { apiFetch } from './client';
import { patchWithRevision, writeWithRevision, type SaveResult } from './saves';

const SONGS_PATH = 'api/v1/songs';
export const WORKFLOW_STATES_PATH = 'api/v1/workflow-states';

/** The longest title and concept the API takes, in UTF-16 code units after trimming. */
export const TITLE_MAXIMUM_LENGTH = 300;
export const CONCEPT_MAXIMUM_LENGTH = 2000;

/** The longest Song notes the API takes, in UTF-16 code units once normalised. */
export const SONG_NOTES_MAXIMUM_LENGTH = 10_000;

/** The `genre` filter value that matches Songs with no Genre. */
export const NO_GENRE = 'none';

/** The `tag` filter value that matches Songs with no Tag. */
export const NO_TAG = 'none';

/** The `artist` filter value that matches Songs credited to no one. */
export const NO_ARTIST = 'none';

/** The most featured Artists a Song may have. */
export const FEATURED_ARTISTS_MAXIMUM = 50;

/** The page size the list is asked for: the API's default. */
export const SONGS_PAGE_SIZE = 50;

/** What a Version creates, as its `kind` option and the API's `kind` say it. */
export type VersionKind = 'song' | 'speech' | 'sound';

const KINDS: readonly string[] = ['song', 'speech', 'sound'];

export function isVersionKind(value: unknown): value is VersionKind {
  return typeof value === 'string' && KINDS.includes(value);
}

/** A kind as the page writes it: Song, Speech, or Sound. */
export function kindLabel(kind: VersionKind): string {
  return kind.charAt(0).toUpperCase() + kind.slice(1);
}

/** A Genre as a Song shows it. */
export interface SongGenre {
  id: string;
  name: string;
}

/** A Tag as a Song shows it: its name and the name of its palette colour. */
export interface SongTag {
  id: string;
  name: string;
  colour: string;
}

/** An Artist as a Song's credits show it. */
export interface SongArtist {
  id: string;
  name: string;
}

/** Who a Song is credited to: one primary Artist or none, and featured Artists in the user's order. */
export interface SongCredits {
  primary: SongArtist | null;
  featured: SongArtist[];
}

/** A Playlist as a Song names it. */
export interface SongPlaylist {
  id: string;
  title: string;
}

/** A Song as the API answers it. Times are UTC ISO 8601. */
export interface Song {
  id: string;
  shortcode: string;
  title: string;
  concept: string | null;
  state: { id: string; name: string; colour: string };
  /** The Version the user is working from, and what it creates. */
  currentVersion: { id: string; number: string; shortcode: string; kind: VersionKind };
  versionCount: number;
  createdAt: string;
  updatedAt: string;
  revision: number;
  /** Free-form notes; null when there are none. */
  notes: string | null;
  /** Its Genres, alphabetically. */
  genres: SongGenre[];
  /** Its Tags, alphabetically ignoring case. */
  tags: SongTag[];
  /** Its primary and featured Artists. */
  credits: SongCredits;
  /** The Playlists it is on, by title. */
  playlists: SongPlaylist[];
}

export interface SongPage {
  items: Song[];
  page: number;
  pageSize: number;
  total: number;
}

export interface WorkflowState {
  id: string;
  name: string;
  colour: string;
  order: number;
  hidden: boolean;
  /** How many Songs are in it. The API always sends it; test fixtures may leave it out. */
  songCount?: number;
}

export type SongSort = 'updated' | 'title';
export type SortDirection = 'asc' | 'desc';

/** What the Songs table shows: the list's own query parameters. */
export interface SongQuery {
  sort: SongSort;
  direction: SortDirection;
  states: string[];
  /** Genre IDs, and {@link NO_GENRE} for Songs with none: Songs with any of them. */
  genres: string[];
  /** Tag IDs, and {@link NO_TAG} for Songs with none: Songs with any of them. */
  tags: string[];
  /** Artist IDs, and {@link NO_ARTIST} for Songs credited to no one: Songs crediting any of them. */
  artists: string[];
  page: number;
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

export function isSongGenre(value: unknown): value is SongGenre {
  return isRecord(value) && typeof value.id === 'string' && typeof value.name === 'string';
}

export function isSongTag(value: unknown): value is SongTag {
  return isSongGenre(value) && isRecord(value) && typeof value.colour === 'string';
}

export function isSongArtist(value: unknown): value is SongArtist {
  return isSongGenre(value);
}

export function isSongCredits(value: unknown): value is SongCredits {
  return (
    isRecord(value) &&
    (value.primary === null || isSongArtist(value.primary)) &&
    Array.isArray(value.featured) &&
    value.featured.every(isSongArtist)
  );
}

export function isSong(value: unknown): value is Song {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.shortcode === 'string' &&
    typeof value.title === 'string' &&
    (value.concept === null || typeof value.concept === 'string') &&
    isRecord(value.state) &&
    typeof value.state.id === 'string' &&
    typeof value.state.name === 'string' &&
    typeof value.state.colour === 'string' &&
    isRecord(value.currentVersion) &&
    typeof value.currentVersion.shortcode === 'string' &&
    isVersionKind(value.currentVersion.kind) &&
    typeof value.versionCount === 'number' &&
    typeof value.createdAt === 'string' &&
    typeof value.updatedAt === 'string' &&
    typeof value.revision === 'number' &&
    (value.notes === null || typeof value.notes === 'string') &&
    Array.isArray(value.genres) &&
    value.genres.every(isSongGenre) &&
    Array.isArray(value.tags) &&
    value.tags.every(isSongTag) &&
    isSongCredits(value.credits) &&
    Array.isArray(value.playlists) &&
    value.playlists.every(
      (playlist) =>
        isRecord(playlist) && typeof playlist.id === 'string' && typeof playlist.title === 'string',
    )
  );
}

function isSongPage(value: unknown): value is SongPage {
  return (
    isRecord(value) &&
    Array.isArray(value.items) &&
    value.items.every(isSong) &&
    typeof value.page === 'number' &&
    typeof value.pageSize === 'number' &&
    typeof value.total === 'number'
  );
}

export function isWorkflowState(value: unknown): value is WorkflowState {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.name === 'string' &&
    typeof value.colour === 'string' &&
    typeof value.order === 'number' &&
    typeof value.hidden === 'boolean' &&
    (value.songCount === undefined || typeof value.songCount === 'number')
  );
}

export function isErrorMap(value: unknown): value is Record<string, string[]> {
  return (
    isRecord(value) &&
    Object.values(value).every(
      (messages) => Array.isArray(messages) && messages.every((m) => typeof m === 'string'),
    )
  );
}

export async function body(response: Response): Promise<unknown> {
  try {
    return await response.json();
  } catch {
    return undefined;
  }
}

/** The list's query string for `query`; values that are the API's defaults are left out. */
export function songListParameters(query: SongQuery): URLSearchParams {
  const parameters = new URLSearchParams();
  if (query.sort !== 'updated') {
    parameters.set('sort', query.sort);
  }
  if (query.direction !== defaultDirection(query.sort)) {
    parameters.set('direction', query.direction);
  }
  for (const state of query.states) {
    parameters.append('state', state);
  }
  for (const genre of query.genres) {
    parameters.append('genre', genre);
  }
  for (const tag of query.tags) {
    parameters.append('tag', tag);
  }
  for (const artist of query.artists) {
    parameters.append('artist', artist);
  }
  if (query.page !== 1) {
    parameters.set('page', String(query.page));
  }
  return parameters;
}

/** The direction a sort starts in: newest first by updated time, A to Z by title. */
export function defaultDirection(sort: SongSort): SortDirection {
  return sort === 'updated' ? 'desc' : 'asc';
}

/**
 * Reads a table view out of a page URL's query string. Anything it does not understand is left at
 * its default, so a hand-edited URL still shows a table.
 */
export function songQueryFrom(parameters: URLSearchParams): SongQuery {
  const sort: SongSort = parameters.get('sort') === 'title' ? 'title' : 'updated';
  const direction = parameters.get('direction');
  const page = Number(parameters.get('page') ?? '1');
  return {
    sort,
    direction: direction === 'asc' || direction === 'desc' ? direction : defaultDirection(sort),
    states: [...new Set(parameters.getAll('state'))],
    genres: [...new Set(parameters.getAll('genre'))],
    tags: [...new Set(parameters.getAll('tag'))],
    artists: [...new Set(parameters.getAll('artist'))],
    page: Number.isSafeInteger(page) && page >= 1 ? page : 1,
  };
}

export type LoadState<T> =
  { phase: 'loading' } | { phase: 'error' } | { phase: 'not-found' } | { phase: 'ready'; data: T };

/**
 * GETs `path` and keeps what `accept` takes from the answer. A new path starts loading again; a 404
 * is `not-found`; anything else unexpected is `error`. `reload` asks again.
 */
export function useResource<T>(
  path: string,
  accept: (answer: unknown) => T | undefined,
): { state: LoadState<T>; reload: () => void } {
  const [state, setState] = useState<{ path: string; state: LoadState<T> }>({
    path,
    state: { phase: 'loading' },
  });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    const settle = (next: LoadState<T>) => {
      if (!controller.signal.aborted) {
        setState({ path, state: next });
      }
    };
    const load = async () => {
      try {
        const response = await apiFetch(path, { signal: controller.signal });
        const answer = await body(response);
        const data = response.ok ? accept(answer) : undefined;
        if (data !== undefined) {
          settle({ phase: 'ready', data });
        } else {
          settle({ phase: response.status === 404 ? 'not-found' : 'error' });
        }
      } catch {
        settle({ phase: 'error' });
      }
    };
    void load();
    return () => {
      controller.abort();
    };
  }, [path, accept, attempt]);

  const reload = useCallback(() => {
    setState({ path, state: { phase: 'loading' } });
    setAttempt((previous) => previous + 1);
  }, [path]);

  return { state: state.path === path ? state.state : { phase: 'loading' }, reload };
}

const acceptSongPage = (answer: unknown) => (isSongPage(answer) ? answer : undefined);
const acceptSong = (answer: unknown) => (isSong(answer) ? answer : undefined);
const acceptStates = (answer: unknown) =>
  isRecord(answer) && Array.isArray(answer.items) && answer.items.every(isWorkflowState)
    ? answer.items
    : undefined;

/** One page of Songs for the table's view. */
export function useSongs(query: SongQuery) {
  const parameters = songListParameters(query).toString();
  return useResource(parameters ? `${SONGS_PATH}?${parameters}` : SONGS_PATH, acceptSongPage);
}

/** One Song, by its ID or shortcode. */
export function useSong(reference: string) {
  return useResource(`${SONGS_PATH}/${encodeURIComponent(reference)}`, acceptSong);
}

/** Every workflow state, hidden ones included, in order. */
export function useWorkflowStates() {
  return useResource(WORKFLOW_STATES_PATH, acceptStates);
}

/** How many Songs a search answers at once: the API's default for `q`. */
export const SONG_SEARCH_RESULTS = 10;

/**
 * The first Songs whose title contains `search` (ignoring case) or whose shortcode starts with it,
 * by title, and how many match in all. Nothing for blank text; undefined when the list cannot be
 * read.
 */
export async function searchSongs(
  search: string,
  signal?: AbortSignal,
): Promise<{ songs: Song[]; total: number } | undefined> {
  if (search.trim() === '') {
    return { songs: [], total: 0 };
  }
  const parameters = new URLSearchParams({ q: search.trim(), sort: 'title' });
  try {
    const response = await apiFetch(`${SONGS_PATH}?${parameters.toString()}`, { signal });
    const answer = await body(response);
    return response.ok && isSongPage(answer)
      ? { songs: answer.items, total: answer.total }
      : undefined;
  } catch {
    return undefined;
  }
}

/** One Song as it is now, by its ID or shortcode; undefined when it cannot be read. */
export async function readSong(reference: string): Promise<Song | undefined> {
  try {
    const response = await apiFetch(`${SONGS_PATH}/${encodeURIComponent(reference)}`);
    const answer = await body(response);
    return response.ok && isSong(answer) ? answer : undefined;
  } catch {
    return undefined;
  }
}

export interface NewSong {
  title: string;
  concept: string;
  /** The primary Artist's ID, or null for none; left out, the default Artist is used. */
  primaryArtistId?: string | null;
}

/** How a create ended, by the API's answer. Never a rejection. */
export type CreateSongResult =
  | { kind: 'created'; song: Song }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'failed' };

/** Creates a Song; it comes with its Version 1. */
export async function createSong(request: NewSong): Promise<CreateSongResult> {
  try {
    const response = await apiFetch(SONGS_PATH, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    });
    const answer = await body(response);
    if (response.status === 201 && isSong(answer)) {
      return { kind: 'created', song: answer };
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

/**
 * An edit of a Song's details: only the fields given change. A null or blank concept or notes
 * clears them; `genreIds` and `tagIds` replace the Song's Genres and Tags.
 */
export interface SongEdit {
  title?: string;
  concept?: string | null;
  stateId?: string;
  notes?: string | null;
  genreIds?: string[];
  tagIds?: string[];
}

/** Edits a Song, based on `song`'s revision; a stale revision comes back as a conflict. */
export function updateSong(
  song: Pick<Song, 'id' | 'revision'>,
  edit: SongEdit,
): Promise<SaveResult<Song>> {
  return patchWithRevision(
    `${SONGS_PATH}/${encodeURIComponent(song.id)}`,
    song.revision,
    { ...edit },
    acceptSong,
  );
}

/**
 * Replaces a Song's credits as a whole, based on `song`'s revision; a stale revision comes back as
 * a conflict, and a refused set (an Artist gone, one featured twice) as invalid.
 */
export function setSongCredits(
  song: Pick<Song, 'id' | 'revision'>,
  credits: SongCredits,
): Promise<SaveResult<Song>> {
  return writeWithRevision(
    'PUT',
    `${SONGS_PATH}/${encodeURIComponent(song.id)}/credits`,
    song.revision,
    {
      primaryArtistId: credits.primary?.id ?? null,
      featuredArtistIds: credits.featured.map((artist) => artist.id),
    },
    acceptSong,
  );
}
