import { apiFetch } from './client';
import { ifMatch } from './saves';
import { body, isErrorMap, isRecord, useResource } from './songs';

const GENRES_PATH = 'api/v1/genres';

/** The longest Genre name the API takes, in UTF-16 code units once normalised. */
export const GENRE_NAME_MAXIMUM_LENGTH = 50;

/** A Genre of the user's list, as the API answers it, with how many Songs have it. */
export interface Genre {
  id: string;
  name: string;
  songCount: number;
}

export function isGenre(value: unknown): value is Genre {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.name === 'string' &&
    typeof value.songCount === 'number'
  );
}

const acceptGenres = (answer: unknown) =>
  isRecord(answer) && Array.isArray(answer.items) && answer.items.every(isGenre)
    ? answer.items
    : undefined;

/** Every Genre, alphabetically, with Song counts. */
export function useGenres() {
  return useResource(GENRES_PATH, acceptGenres);
}

/** A Genre name as the API stores it: trimmed, each inner run of white space one space. */
export function normaliseGenreName(name: string): string {
  return name.trim().replace(/\s+/g, ' ');
}

/** What Genre names are compared by, as the API compares them: normalised, NFC, ignoring case. */
export function genreNameKey(name: string): string {
  return normaliseGenreName(name).normalize('NFC').toUpperCase();
}

/** A new Genre name's error before it is sent, by the API's rule: 1 to 50 once normalised. */
export function genreNameError(name: string): string | undefined {
  const normalised = normaliseGenreName(name);
  if (normalised === '') {
    return 'Enter a name.';
  }
  return normalised.length > GENRE_NAME_MAXIMUM_LENGTH
    ? `Use at most ${String(GENRE_NAME_MAXIMUM_LENGTH)} characters.`
    : undefined;
}

/** How creating a Genre ended. An existing Genre with the name (in any letter case) is `created` too. */
export type CreateGenreResult =
  | { kind: 'created'; genre: Genre }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'failed' };

/** Creates a Genre, or answers the one that already has the name. */
export async function createGenre(name: string): Promise<CreateGenreResult> {
  try {
    const response = await apiFetch(GENRES_PATH, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name }),
    });
    const answer = await body(response);
    if ((response.status === 201 || response.status === 200) && isGenre(answer)) {
      return { kind: 'created', genre: answer };
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

/** A Genre as Settings → Genres manages it: with its revision, which every change sends. */
export type ManagedGenre = Genre & { revision: number };

export function isManagedGenre(value: unknown): value is ManagedGenre {
  return isGenre(value) && isRecord(value) && typeof value.revision === 'number';
}

const acceptManaged = (answer: unknown) =>
  isRecord(answer) && Array.isArray(answer.items) && answer.items.every(isManagedGenre)
    ? answer.items
    : undefined;

/** Every Genre with its Song count and revision, for Settings → Genres. */
export function useManagedGenres() {
  return useResource(GENRES_PATH, acceptManaged);
}

/** Every Genre as it is now; undefined when the list cannot be read. */
export async function readManagedGenres(): Promise<ManagedGenre[] | undefined> {
  try {
    const response = await apiFetch(GENRES_PATH);
    return response.ok ? acceptManaged(await body(response)) : undefined;
  } catch {
    return undefined;
  }
}

/**
 * How many Songs have any of `genreIds` (each counted once), from the Songs list's total; undefined
 * when it cannot be read.
 */
export async function countSongsWithAny(genreIds: readonly string[]): Promise<number | undefined> {
  const parameters = new URLSearchParams({ pageSize: '1' });
  for (const id of genreIds) {
    parameters.append('genre', id);
  }
  try {
    const response = await apiFetch(`api/v1/songs?${parameters.toString()}`);
    const answer = await body(response);
    return response.ok && isRecord(answer) && typeof answer.total === 'number'
      ? answer.total
      : undefined;
  } catch {
    return undefined;
  }
}

/**
 * How a rename, merge, or delete ended. Never a rejection. `conflict` means the Genre changed
 * elsewhere and nothing was applied; `name-taken` names the Genre that already has the name.
 */
export type GenreChangeResult =
  | { kind: 'saved'; genre: ManagedGenre }
  | { kind: 'deleted' }
  | { kind: 'conflict' }
  | { kind: 'name-taken'; genreId: string }
  | { kind: 'in-use'; songCount: number }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'not-found' }
  | { kind: 'failed' };

async function changeGenre(
  method: 'PATCH' | 'POST' | 'DELETE',
  path: string,
  revision: number,
  payload?: Record<string, unknown>,
): Promise<GenreChangeResult> {
  try {
    const headers: Record<string, string> = { 'If-Match': ifMatch(revision) };
    if (payload !== undefined) {
      headers['Content-Type'] = 'application/json';
    }
    const response = await apiFetch(path, {
      method,
      headers,
      ...(payload === undefined ? {} : { body: JSON.stringify(payload) }),
    });
    if (response.status === 204) {
      return { kind: 'deleted' };
    }
    const answer = await body(response);
    if (response.ok) {
      return isManagedGenre(answer) ? { kind: 'saved', genre: answer } : { kind: 'failed' };
    }
    if (!isRecord(answer)) {
      return { kind: 'failed' };
    }
    if (response.status === 409) {
      if (answer.code === 'revision_conflict') {
        return { kind: 'conflict' };
      }
      if (answer.code === 'genre_name_taken' && typeof answer.genreId === 'string') {
        return { kind: 'name-taken', genreId: answer.genreId };
      }
      if (answer.code === 'genre_in_use' && typeof answer.songCount === 'number') {
        return { kind: 'in-use', songCount: answer.songCount };
      }
    }
    if (
      response.status === 422 &&
      answer.code === 'validation_failed' &&
      isErrorMap(answer.errors)
    ) {
      return { kind: 'invalid', errors: answer.errors };
    }
    return response.status === 404 ? { kind: 'not-found' } : { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

const genrePath = (id: string) => `${GENRES_PATH}/${encodeURIComponent(id)}`;

/** Renames a Genre everywhere; no Song changes. */
export function renameGenre(genre: ManagedGenre, name: string): Promise<GenreChangeResult> {
  return changeGenre('PATCH', genrePath(genre.id), genre.revision, { name });
}

/** Merges `sourceIds` into `target`: their Songs get `target` (once) and they are removed. */
export function mergeGenres(
  target: ManagedGenre,
  sourceIds: readonly string[],
): Promise<GenreChangeResult> {
  return changeGenre('POST', `${genrePath(target.id)}/merge`, target.revision, {
    sourceIds: [...sourceIds],
  });
}

/** What happens to the Songs of a Genre in use when it is deleted. */
export type GenreDeleteChoice = { reassignTo: string } | { removeFromSongs: true };

/** Deletes a Genre: directly when no Song has it, otherwise as `choice` says. */
export function deleteGenre(
  genre: ManagedGenre,
  choice?: GenreDeleteChoice,
): Promise<GenreChangeResult> {
  let query = '';
  if (choice !== undefined) {
    query =
      'reassignTo' in choice
        ? `?reassignTo=${encodeURIComponent(choice.reassignTo)}`
        : '?removeFromSongs=true';
  }
  return changeGenre('DELETE', `${genrePath(genre.id)}${query}`, genre.revision);
}
