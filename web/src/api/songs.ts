import { useCallback, useEffect, useState } from 'react';
import { apiFetch } from './client';
import { patchWithRevision, type SaveResult } from './saves';

const SONGS_PATH = 'api/v1/songs';
export const WORKFLOW_STATES_PATH = 'api/v1/workflow-states';

/** The longest title and concept the API takes, in UTF-16 code units after trimming. */
export const TITLE_MAXIMUM_LENGTH = 300;
export const CONCEPT_MAXIMUM_LENGTH = 2000;

/** The page size the list is asked for: the API's default. */
export const SONGS_PAGE_SIZE = 50;

/** A Song as the API answers it. Times are UTC ISO 8601. */
export interface Song {
  id: string;
  shortcode: string;
  title: string;
  concept: string | null;
  state: { id: string; name: string; colour: string };
  currentVersion: { id: string; number: string; shortcode: string };
  versionCount: number;
  createdAt: string;
  updatedAt: string;
  revision: number;
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
  page: number;
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
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
    typeof value.versionCount === 'number' &&
    typeof value.createdAt === 'string' &&
    typeof value.updatedAt === 'string' &&
    typeof value.revision === 'number'
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

export interface NewSong {
  title: string;
  concept: string;
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

/** An edit of a Song's details: only the fields given change. A null or blank concept clears it. */
export interface SongEdit {
  title?: string;
  concept?: string | null;
  stateId?: string;
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
