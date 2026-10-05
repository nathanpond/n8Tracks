import { apiFetch } from './client';
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
