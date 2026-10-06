import { apiFetch } from './client';
import { genreNameError, genreNameKey, normaliseGenreName } from './genres';
import type { AudioAction } from './lineage';
import { ifMatch } from './saves';
import {
  body,
  isErrorMap,
  isRecord,
  isSong,
  useResource,
  type RelationshipDirection,
  type Song,
} from './songs';

const TYPES_PATH = 'api/v1/relationship-types';

/** The longest relationship type name the API takes, in UTF-16 code units once normalised. */
export const RELATIONSHIP_NAME_MAXIMUM_LENGTH = 50;

/**
 * A relationship type as the API answers it: a name for each direction ("Sequel to" and "Has
 * sequel"), equal for a symmetric type; whether it ships with n8Tracks (and so cannot be renamed
 * or deleted); how many relationships use it; and its revision, which every change sends.
 */
export interface RelationshipType {
  id: string;
  name: string;
  reverseName: string;
  system: boolean;
  symmetric: boolean;
  /**
   * The Suno lineage action the type stands for, or null: fixed for a system type; for one of the
   * user's, the audio action they mapped it to (#126), which makes it usable as a source's type.
   */
  sunoAction: string | null;
  relationshipCount: number;
  revision: number;
}

export function isRelationshipType(value: unknown): value is RelationshipType {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.name === 'string' &&
    typeof value.reverseName === 'string' &&
    typeof value.system === 'boolean' &&
    typeof value.symmetric === 'boolean' &&
    (value.sunoAction === null || typeof value.sunoAction === 'string') &&
    typeof value.relationshipCount === 'number' &&
    typeof value.revision === 'number'
  );
}

const acceptTypes = (answer: unknown) =>
  isRecord(answer) && Array.isArray(answer.items) && answer.items.every(isRelationshipType)
    ? answer.items
    : undefined;

/** Every relationship type: the system types in their order, then the user's alphabetically. */
export function useRelationshipTypes() {
  return useResource(TYPES_PATH, acceptTypes);
}

/** Every relationship type as it is now; undefined when the list cannot be read. */
export async function readRelationshipTypes(): Promise<RelationshipType[] | undefined> {
  try {
    const response = await apiFetch(TYPES_PATH);
    return response.ok ? acceptTypes(await body(response)) : undefined;
  } catch {
    return undefined;
  }
}

/** A type name as the API stores it: trimmed, inner white space as one space (the Genre rule). */
export const normaliseRelationshipName = normaliseGenreName;

/** What type names are compared by, as the API compares them: normalised, NFC, ignoring case. */
export const relationshipNameKey = genreNameKey;

/** A name's error before it is sent, by the API's rule: 1 to 50 once normalised. */
export const relationshipNameError = genreNameError;

/**
 * The type that already has `name` as its forward or reverse name, ignoring case, among `types`
 * other than `except`; undefined when none has. Names are unique across both directions of every
 * type, system types included.
 */
export function typeWithName(
  types: readonly RelationshipType[],
  name: string,
  except?: string,
): RelationshipType | undefined {
  const key = relationshipNameKey(name);
  return types.find(
    (type) =>
      type.id !== except &&
      (relationshipNameKey(type.name) === key || relationshipNameKey(type.reverseName) === key),
  );
}

/**
 * How adding, renaming, mapping, or deleting a type ended. Never a rejection. `conflict` means the
 * type changed elsewhere; `in-use` gives the count of relationships a delete would remove;
 * `mapping-in-use` and `sources-in-use` give the count of Versions whose sources are of the type,
 * which keeps its Suno action, and the type itself, as they are.
 */
export type RelationshipTypeResult =
  | { kind: 'saved'; type: RelationshipType }
  | { kind: 'deleted' }
  | { kind: 'conflict' }
  | { kind: 'system' }
  | { kind: 'in-use'; relationshipCount: number }
  | { kind: 'mapping-in-use'; versionCount: number }
  | { kind: 'sources-in-use'; versionCount: number }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'not-found' }
  | { kind: 'failed' };

async function sendType(
  method: 'POST' | 'PATCH' | 'DELETE',
  path: string,
  revision: number | undefined,
  payload?: Record<string, unknown>,
): Promise<RelationshipTypeResult> {
  try {
    const headers: Record<string, string> = {};
    if (revision !== undefined) {
      headers['If-Match'] = ifMatch(revision);
    }
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
      return isRelationshipType(answer) ? { kind: 'saved', type: answer } : { kind: 'failed' };
    }
    if (!isRecord(answer)) {
      return { kind: 'failed' };
    }
    if (response.status === 409) {
      if (answer.code === 'revision_conflict') {
        return { kind: 'conflict' };
      }
      if (answer.code === 'system_type') {
        return { kind: 'system' };
      }
      if (
        answer.code === 'relationship_type_in_use' &&
        typeof answer.relationshipCount === 'number'
      ) {
        return { kind: 'in-use', relationshipCount: answer.relationshipCount };
      }
      if (answer.code === 'mapping_in_use' && typeof answer.versionCount === 'number') {
        return { kind: 'mapping-in-use', versionCount: answer.versionCount };
      }
      if (answer.code === 'type_in_use' && typeof answer.versionCount === 'number') {
        return { kind: 'sources-in-use', versionCount: answer.versionCount };
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

const typePath = (id: string) => `${TYPES_PATH}/${encodeURIComponent(id)}`;

/** Adds a type; a reverse name equal to the name (ignoring case) makes it symmetric. */
export function createRelationshipType(
  name: string,
  reverseName: string,
): Promise<RelationshipTypeResult> {
  return sendType('POST', TYPES_PATH, undefined, { name, reverseName });
}

/** Renames one of the user's types; no Song changes. */
export function renameRelationshipType(
  type: RelationshipType,
  name: string,
  reverseName: string,
): Promise<RelationshipTypeResult> {
  return sendType('PATCH', typePath(type.id), type.revision, { name, reverseName });
}

/**
 * Maps one of the user's types to a Suno audio action, or clears the mapping (null). Refused while
 * any Version's source is of the type (`mapping-in-use`).
 */
export function mapRelationshipType(
  type: RelationshipType,
  sunoAction: AudioAction | null,
): Promise<RelationshipTypeResult> {
  return sendType('PATCH', typePath(type.id), type.revision, { sunoAction });
}

/** Deletes one of the user's types: directly when unused; one in use only with `removeRelationships`. */
export function deleteRelationshipType(
  type: RelationshipType,
  removeRelationships = false,
): Promise<RelationshipTypeResult> {
  return sendType(
    'DELETE',
    `${typePath(type.id)}${removeRelationships ? '?removeRelationships=true' : ''}`,
    type.revision,
  );
}

/**
 * How relating Songs, or removing a relationship, ended. Never a rejection. `exists` means the two
 * Songs are already related under the type, either way round, and carries the Song as it is now.
 */
export type RelateResult =
  | { kind: 'saved'; song: Song }
  | { kind: 'exists'; song: Song }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'not-found' }
  | { kind: 'failed' };

async function sendRelationship(
  method: 'POST' | 'DELETE',
  path: string,
  payload?: Record<string, unknown>,
): Promise<RelateResult> {
  try {
    const response = await apiFetch(path, {
      method,
      ...(payload === undefined
        ? {}
        : { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) }),
    });
    const answer = await body(response);
    if (response.ok) {
      return isSong(answer) ? { kind: 'saved', song: answer } : { kind: 'failed' };
    }
    if (!isRecord(answer)) {
      return { kind: 'failed' };
    }
    if (
      response.status === 409 &&
      answer.code === 'relationship_exists' &&
      isSong(answer.current)
    ) {
      return { kind: 'exists', song: answer.current };
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

const relationshipsPath = (songId: string) =>
  `api/v1/songs/${encodeURIComponent(songId)}/relationships`;

/** Relates `song` to `otherSongId` under `typeId`, read from `song` in `direction`; answers `song` as it is now. */
export function relateSongs(
  song: Pick<Song, 'id'>,
  typeId: string,
  direction: RelationshipDirection,
  otherSongId: string,
): Promise<RelateResult> {
  return sendRelationship('POST', relationshipsPath(song.id), {
    typeId,
    direction,
    otherSong: otherSongId,
  });
}

/** Removes one of `song`'s relationships (from either of its Songs); answers `song` as it is now. */
export function removeRelationship(
  song: Pick<Song, 'id'>,
  relationshipId: string,
): Promise<RelateResult> {
  return sendRelationship(
    'DELETE',
    `${relationshipsPath(song.id)}/${encodeURIComponent(relationshipId)}`,
  );
}

/** One way a Song can be related, as the type picker offers it: a type read in one direction. */
export interface RelationshipChoice {
  /** `<typeId>:<direction>`, the picker's value. */
  value: string;
  typeId: string;
  direction: RelationshipDirection;
  /** The type's name in that direction, as this Song will show it. */
  label: string;
  system: boolean;
}

/**
 * Every way to relate a Song, in the types' order (system first): each type's forward name, then,
 * unless it is symmetric (shown once), its reverse name.
 */
export function relationshipChoices(types: readonly RelationshipType[]): RelationshipChoice[] {
  return types.flatMap((type) => {
    const forward: RelationshipChoice = {
      value: `${type.id}:forward`,
      typeId: type.id,
      direction: 'forward',
      label: type.name,
      system: type.system,
    };
    if (type.symmetric) {
      return [forward];
    }
    return [
      forward,
      {
        value: `${type.id}:reverse`,
        typeId: type.id,
        direction: 'reverse',
        label: type.reverseName,
        system: type.system,
      },
    ];
  });
}
