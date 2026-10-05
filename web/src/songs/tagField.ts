import type { FieldValue } from '../api/saves';
import { isSongTag, type SongTag } from '../api/songs';

// A Song's Tags as one field of the shared save helper (`useRevisionedSave`), whose values are
// text: the set as JSON, sorted by ID, so two equal sets are equal text. Like Genres
// (`genreField.ts`), a Tags-only conflict is merged (the user's adds and removes reapplied onto
// the current set) rather than shown.

/** The edit key of a Song's Tags, as the API's PATCH spells it. */
export const TAGS_KEY = 'tagIds';

function canonical(tags: readonly SongTag[]): SongTag[] {
  const byId = new Map(
    tags.map((tag) => [tag.id, { id: tag.id, name: tag.name, colour: tag.colour }]),
  );
  return [...byId.values()].sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
}

/** A set of Tags as the field's value. */
export function tagsValue(tags: readonly SongTag[]): string {
  return JSON.stringify(canonical(tags));
}

/** The Tags a field value holds (none for null or anything unreadable). */
export function tagsOf(value: FieldValue): SongTag[] {
  if (value === null) {
    return [];
  }
  try {
    const parsed: unknown = JSON.parse(value);
    return Array.isArray(parsed) ? parsed.filter(isSongTag) : [];
  } catch {
    return [];
  }
}

/** Tags alphabetically ignoring case, as the API lists them; exact order breaks ties. */
export function alphabeticalTags<T extends { name: string }>(tags: readonly T[]): T[] {
  return [...tags].sort(
    (a, b) =>
      a.name.localeCompare(b.name, 'en', { sensitivity: 'base' }) ||
      (a.name < b.name ? -1 : a.name > b.name ? 1 : 0),
  );
}

/**
 * The user's change from `base` to `mine` (the Tags added and taken off) reapplied onto
 * `current`, the set the Song has now.
 */
export function mergeTags(base: FieldValue, current: FieldValue, mine: FieldValue): FieldValue {
  const before = new Set(tagsOf(base).map((tag) => tag.id));
  const wanted = tagsOf(mine);
  const wantedIds = new Set(wanted.map((tag) => tag.id));
  const added = wanted.filter((tag) => !before.has(tag.id));
  const removed = new Set([...before].filter((id) => !wantedIds.has(id)));
  return tagsValue([...tagsOf(current).filter((tag) => !removed.has(tag.id)), ...added]);
}
