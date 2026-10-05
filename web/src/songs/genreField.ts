import type { FieldValue } from '../api/saves';
import type { SongGenre } from '../api/songs';

// A Song's Genres as one field of the shared save helper (`useRevisionedSave`), whose values are
// text: the set as JSON, sorted by ID, so two equal sets are equal text. A Genres-only conflict is
// merged (the user's adds and removes reapplied onto the current set) rather than shown.

/** The edit key of a Song's Genres, as the API's PATCH spells it. */
export const GENRES_KEY = 'genreIds';

function canonical(genres: readonly SongGenre[]): SongGenre[] {
  const byId = new Map(genres.map((genre) => [genre.id, { id: genre.id, name: genre.name }]));
  return [...byId.values()].sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
}

/** A set of Genres as the field's value. */
export function genresValue(genres: readonly SongGenre[]): string {
  return JSON.stringify(canonical(genres));
}

function isSongGenre(value: unknown): value is SongGenre {
  return (
    typeof value === 'object' &&
    value !== null &&
    'id' in value &&
    typeof value.id === 'string' &&
    'name' in value &&
    typeof value.name === 'string'
  );
}

/** The Genres a field value holds (none for null or anything unreadable). */
export function genresOf(value: FieldValue): SongGenre[] {
  if (value === null) {
    return [];
  }
  try {
    const parsed: unknown = JSON.parse(value);
    return Array.isArray(parsed) ? parsed.filter(isSongGenre) : [];
  } catch {
    return [];
  }
}

/** Genres alphabetically, as the page lists them. */
export function alphabetical(genres: readonly SongGenre[]): SongGenre[] {
  return [...genres].sort((a, b) =>
    a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }),
  );
}

/**
 * The user's change from `base` to `mine` (the Genres added and taken off) reapplied onto
 * `current`, the set the Song has now.
 */
export function mergeGenres(base: FieldValue, current: FieldValue, mine: FieldValue): FieldValue {
  const before = new Set(genresOf(base).map((genre) => genre.id));
  const wanted = genresOf(mine);
  const wantedIds = new Set(wanted.map((genre) => genre.id));
  const added = wanted.filter((genre) => !before.has(genre.id));
  const removed = new Set([...before].filter((id) => !wantedIds.has(id)));
  return genresValue([...genresOf(current).filter((genre) => !removed.has(genre.id)), ...added]);
}
