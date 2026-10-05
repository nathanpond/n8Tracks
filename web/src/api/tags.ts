import { apiFetch } from './client';
import { ifMatch } from './saves';
import {
  GENRE_NAME_MAXIMUM_LENGTH,
  genreNameError,
  genreNameKey,
  normaliseGenreName,
} from './genres';
import { body, isErrorMap, isRecord, useResource } from './songs';

const TAGS_PATH = 'api/v1/tags';

/** The longest Tag name the API takes (the Genre rule), in UTF-16 code units once normalised. */
export const TAG_NAME_MAXIMUM_LENGTH = GENRE_NAME_MAXIMUM_LENGTH;

/** A Tag of the user's list, as the API answers it: its palette colour and how many Songs have it. */
export interface Tag {
  id: string;
  name: string;
  /** The name of one of the twelve palette colours (`stateColours`). */
  colour: string;
  songCount: number;
}

export function isTag(value: unknown): value is Tag {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.name === 'string' &&
    typeof value.colour === 'string' &&
    typeof value.songCount === 'number'
  );
}

const acceptTags = (answer: unknown) =>
  isRecord(answer) && Array.isArray(answer.items) && answer.items.every(isTag)
    ? answer.items
    : undefined;

/** Every Tag, alphabetically, with colours and Song counts. */
export function useTags() {
  return useResource(TAGS_PATH, acceptTags);
}

/** A Tag name as the API stores it: the Genre rule (trimmed, inner white space as one space). */
export const normaliseTagName = normaliseGenreName;

/** What Tag names are compared by, as the API compares them: normalised, NFC, ignoring case. */
export const tagNameKey = genreNameKey;

/** A new Tag name's error before it is sent, by the API's rule: 1 to 50 once normalised. */
export const tagNameError = genreNameError;

/** How creating a Tag ended. An existing Tag with the name (in any letter case) is `created` too. */
export type CreateTagResult =
  | { kind: 'created'; tag: Tag }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'failed' };

/** Creates a Tag in the next palette colour, or answers the one that already has the name. */
export async function createTag(name: string): Promise<CreateTagResult> {
  try {
    const response = await apiFetch(TAGS_PATH, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name }),
    });
    const answer = await body(response);
    if ((response.status === 201 || response.status === 200) && isTag(answer)) {
      return { kind: 'created', tag: answer };
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

/** A Tag as Settings → Tags manages it: with its revision, which every change sends. */
export type ManagedTag = Tag & { revision: number };

export function isManagedTag(value: unknown): value is ManagedTag {
  return isTag(value) && isRecord(value) && typeof value.revision === 'number';
}

const acceptManaged = (answer: unknown) =>
  isRecord(answer) && Array.isArray(answer.items) && answer.items.every(isManagedTag)
    ? answer.items
    : undefined;

/** Every Tag with its colour, Song count, and revision, for Settings → Tags. */
export function useManagedTags() {
  return useResource(TAGS_PATH, acceptManaged);
}

/** Every Tag as it is now; undefined when the list cannot be read. */
export async function readManagedTags(): Promise<ManagedTag[] | undefined> {
  try {
    const response = await apiFetch(TAGS_PATH);
    return response.ok ? acceptManaged(await body(response)) : undefined;
  } catch {
    return undefined;
  }
}

/**
 * How many Songs have any of `tagIds` (each counted once), from the Songs list's total; undefined
 * when it cannot be read.
 */
export async function countSongsWithAnyTag(tagIds: readonly string[]): Promise<number | undefined> {
  const parameters = new URLSearchParams({ pageSize: '1' });
  for (const id of tagIds) {
    parameters.append('tag', id);
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
 * How an edit, merge, or delete ended. Never a rejection. `conflict` means the Tag changed
 * elsewhere and nothing was applied; `name-taken` names the Tag that already has the name.
 */
export type TagChangeResult =
  | { kind: 'saved'; tag: ManagedTag }
  | { kind: 'deleted' }
  | { kind: 'conflict' }
  | { kind: 'name-taken'; tagId: string }
  | { kind: 'in-use'; songCount: number }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'not-found' }
  | { kind: 'failed' };

async function changeTag(
  method: 'PATCH' | 'POST' | 'DELETE',
  path: string,
  revision: number,
  payload?: Record<string, unknown>,
): Promise<TagChangeResult> {
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
      return isManagedTag(answer) ? { kind: 'saved', tag: answer } : { kind: 'failed' };
    }
    if (!isRecord(answer)) {
      return { kind: 'failed' };
    }
    if (response.status === 409) {
      if (answer.code === 'revision_conflict') {
        return { kind: 'conflict' };
      }
      if (answer.code === 'tag_name_taken' && typeof answer.tagId === 'string') {
        return { kind: 'name-taken', tagId: answer.tagId };
      }
      if (answer.code === 'tag_in_use' && typeof answer.songCount === 'number') {
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

const tagPath = (id: string) => `${TAGS_PATH}/${encodeURIComponent(id)}`;

/** What an edit changes: the name, the colour, or both (only what is sent changes). */
export interface TagEdit {
  name?: string;
  colour?: string;
}

/** Renames and/or recolours a Tag everywhere; no Song changes. */
export function updateTag(tag: ManagedTag, edit: TagEdit): Promise<TagChangeResult> {
  return changeTag('PATCH', tagPath(tag.id), tag.revision, { ...edit });
}

/** Merges `sourceIds` into `target`: their Songs get `target` (once), it keeps its colour, and they are removed. */
export function mergeTags(
  target: ManagedTag,
  sourceIds: readonly string[],
): Promise<TagChangeResult> {
  return changeTag('POST', `${tagPath(target.id)}/merge`, target.revision, {
    sourceIds: [...sourceIds],
  });
}

/** Deletes a Tag: directly when no Song has it; one in use only with `removeFromSongs`. */
export function deleteTag(tag: ManagedTag, removeFromSongs = false): Promise<TagChangeResult> {
  return changeTag(
    'DELETE',
    `${tagPath(tag.id)}${removeFromSongs ? '?removeFromSongs=true' : ''}`,
    tag.revision,
  );
}
