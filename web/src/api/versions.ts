import { resolveAppUrl } from './baseUrl';
import { apiFetch } from './client';
import type { OptionValue } from './createFields';
import { ifMatch, patchWithRevision, type FieldValue, type SaveResult } from './saves';
import { ANTIFORGERY_HEADER } from './session';
import {
  body,
  isRecord,
  isSong,
  isVersionKind,
  useResource,
  type Song,
  type VersionKind,
} from './songs';

export { isVersionKind, kindLabel, type VersionKind } from './songs';

const SONGS_PATH = 'api/v1/songs';
const VERSIONS_PATH = 'api/v1/versions';

/** The longest Version name the API takes, in UTF-16 code units after trimming. */
export const VERSION_NAME_MAXIMUM_LENGTH = 200;

/** The longest Version notes the API take, in UTF-16 code units once line endings are `\n` and trimmed. */
export const VERSION_NOTES_MAXIMUM_LENGTH = 10_000;

/** The longest lyrics the API takes, in UTF-16 code units once line endings are `\n` (Suno's limit). */
export const VERSION_LYRICS_MAXIMUM_LENGTH = 5_000;

/** The longest styles the API takes, in UTF-16 code units once line endings are `\n` (Suno's limit). */
export const VERSION_STYLES_MAXIMUM_LENGTH = 1_000;

/**
 * A Version as the tree shows it: no creation inputs but its kind. Times are UTC ISO 8601.
 * `isFrozen` is true once a Generation is attached: its lyrics and styles can no longer change.
 */
export interface Version {
  id: string;
  songId: string;
  number: string;
  shortcode: string;
  name: string | null;
  notes: string | null;
  archived: boolean;
  current: boolean;
  createdAt: string;
  updatedAt: string;
  revision: number;
  isFrozen: boolean;
  kind: VersionKind;
}

/** A number a new Version may take: the next `sibling` after its source, or a `child` under it. */
export interface NumberOption {
  number: string;
  kind: 'sibling' | 'child';
  proposed: boolean;
}

export function isVersion(value: unknown): value is Version {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.songId === 'string' &&
    typeof value.number === 'string' &&
    typeof value.shortcode === 'string' &&
    (value.name === null || typeof value.name === 'string') &&
    (value.notes === null || typeof value.notes === 'string') &&
    typeof value.archived === 'boolean' &&
    typeof value.current === 'boolean' &&
    typeof value.createdAt === 'string' &&
    typeof value.updatedAt === 'string' &&
    typeof value.revision === 'number' &&
    typeof value.isFrozen === 'boolean' &&
    isVersionKind(value.kind)
  );
}

/**
 * A Version's kind, modes, and Suno options, by the API's camelCase names (`kind`, `songMode`,
 * `weirdness`…), applicable to the kind and mode or not: every one is kept when they change.
 */
export type VersionOptions = Readonly<Record<string, OptionValue>>;

/** The option that says what a Version creates: `song`, `speech`, or `sound`. */
export const KIND_OPTION = 'kind';

/** The option that says which form a Song is described in: `simple` or `advanced`. */
export const SONG_MODE_OPTION = 'songMode';

/** The option that says which form a Speech is described in: `simple` or `advanced`. */
export const SPEECH_MODE_OPTION = 'speechMode';

/** A Version with its creation inputs, exactly as stored: empty strings when there are none. */
export interface VersionDetail extends Version {
  lyrics: string;
  styles: string;
  inputs: VersionOptions;
}

function isOptions(value: unknown): value is VersionOptions {
  return (
    isRecord(value) &&
    Object.values(value).every(
      (option) => option === null || ['string', 'number', 'boolean'].includes(typeof option),
    )
  );
}

export function isVersionDetail(value: unknown): value is VersionDetail {
  return (
    isVersion(value) &&
    isRecord(value) &&
    typeof value.lyrics === 'string' &&
    typeof value.styles === 'string' &&
    isOptions(value.inputs)
  );
}

function isNumberOption(value: unknown): value is NumberOption {
  return (
    isRecord(value) &&
    typeof value.number === 'string' &&
    (value.kind === 'sibling' || value.kind === 'child') &&
    typeof value.proposed === 'boolean'
  );
}

function optionsOf(value: unknown): NumberOption[] | undefined {
  return isRecord(value) && Array.isArray(value.options) && value.options.every(isNumberOption)
    ? value.options
    : undefined;
}

/**
 * A Song's Versions, archived ones included, flat, in tree order, and the numbers its tree draws as
 * "Deleted Version" placeholders: deleted Versions' numbers that still have a live descendant.
 */
export interface VersionList {
  items: Version[];
  deletedPlaceholders: string[];
}

const acceptVersions = (answer: unknown): VersionList | undefined =>
  isRecord(answer) &&
  Array.isArray(answer.items) &&
  answer.items.every(isVersion) &&
  Array.isArray(answer.deletedPlaceholders) &&
  answer.deletedPlaceholders.every((number) => typeof number === 'string')
    ? { items: answer.items, deletedPlaceholders: answer.deletedPlaceholders }
    : undefined;

function songVersionsPath(reference: string): string {
  return `${SONGS_PATH}/${encodeURIComponent(reference)}/versions`;
}

/** Every Version of a Song (by ID or shortcode), archived ones included, with its tree's placeholders. */
export function useSongVersions(reference: string) {
  return useResource(songVersionsPath(reference), acceptVersions);
}

/** Reads a Song's Versions again, as {@link useSongVersions} does; undefined when that fails. */
export async function readSongVersions(reference: string): Promise<VersionList | undefined> {
  try {
    const response = await apiFetch(songVersionsPath(reference));
    const answer = await body(response);
    return response.ok ? acceptVersions(answer) : undefined;
  } catch {
    return undefined;
  }
}

/** What deleting a Version would do, read when its confirmation opens. */
export interface DeletionImpact {
  /** Its Generations, deleted with it. */
  generationCount: number;
  /** Its descendant Versions, which remain. */
  remainingDescendantCount: number;
  /** Whether it is the Song's only Version: a new blank one is created. */
  isLastVersion: boolean;
  revision: number;
}

function isDeletionImpact(value: unknown): value is DeletionImpact {
  return (
    isRecord(value) &&
    typeof value.generationCount === 'number' &&
    typeof value.remainingDescendantCount === 'number' &&
    typeof value.isLastVersion === 'boolean' &&
    typeof value.revision === 'number'
  );
}

/** How reading a deletion's impact ended. Never a rejection. */
export type DeletionImpactResult =
  { kind: 'found'; impact: DeletionImpact } | { kind: 'gone' } | { kind: 'failed' };

/** What deleting the Version `versionId` would do now. */
export async function fetchDeletionImpact(
  versionId: string,
  signal?: AbortSignal,
): Promise<DeletionImpactResult> {
  try {
    const response = await apiFetch(
      `${VERSIONS_PATH}/${encodeURIComponent(versionId)}/deletion-impact`,
      { signal },
    );
    const answer = await body(response);
    if (response.ok && isDeletionImpact(answer)) {
      return { kind: 'found', impact: answer };
    }
    return response.status === 404 ? { kind: 'gone' } : { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/**
 * How deleting a Version ended: `deleted` with the Song's current Version now (a new blank one when
 * it was the last); `conflict` when it changed since it was read (nothing was deleted); `gone` when
 * it is no longer there; `failed` otherwise.
 */
export type DeleteVersionResult =
  | { kind: 'deleted'; current: VersionDetail }
  | { kind: 'conflict'; current: VersionDetail }
  | { kind: 'gone' }
  | { kind: 'failed' };

/** Deletes a Version, based on the revision it was read at. */
export async function deleteVersion(
  version: Pick<Version, 'id' | 'revision'>,
): Promise<DeleteVersionResult> {
  try {
    const response = await apiFetch(`${VERSIONS_PATH}/${encodeURIComponent(version.id)}`, {
      method: 'DELETE',
      headers: { 'If-Match': ifMatch(version.revision) },
    });
    const answer = await body(response);
    if (response.ok && isVersionDetail(answer)) {
      return { kind: 'deleted', current: answer };
    }
    if (
      response.status === 409 &&
      isRecord(answer) &&
      answer.code === 'revision_conflict' &&
      isVersionDetail(answer.current)
    ) {
      return { kind: 'conflict', current: answer.current };
    }
    return response.status === 404 ? { kind: 'gone' } : { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/** What asking for a source's next numbers came to. Never a rejection. */
export type NextNumbersResult =
  { kind: 'found'; options: NumberOption[] } | { kind: 'too-deep' } | { kind: 'failed' };

/** The numbers a new Version created from `versionId` may take, the proposal first. */
export async function fetchNextNumbers(
  versionId: string,
  signal?: AbortSignal,
): Promise<NextNumbersResult> {
  try {
    const response = await apiFetch(
      `${VERSIONS_PATH}/${encodeURIComponent(versionId)}/next-numbers`,
      { signal },
    );
    const answer = await body(response);
    const options = optionsOf(answer);
    if (response.ok && options !== undefined) {
      return { kind: 'found', options };
    }
    if (response.status === 409 && isRecord(answer) && answer.code === 'version_number_too_deep') {
      return { kind: 'too-deep' };
    }
    return { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

export interface NewVersion {
  sourceVersionId: string;
  number: string;
  name: string;
  /** Lyrics to start with instead of the source's (text a frozen source could not take). */
  lyrics?: string;
  /** Styles to start with instead of the source's. */
  styles?: string;
}

/**
 * How a create ended. `refused` means the number was taken meanwhile (`taken`) or is no longer
 * offered: nothing was created, and `options` are the source's options as they are now.
 */
export type CreateVersionResult =
  | { kind: 'created'; version: Version }
  | { kind: 'refused'; taken: boolean; options: NumberOption[] }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'failed' };

function isErrorMap(value: unknown): value is Record<string, string[]> {
  return (
    isRecord(value) &&
    Object.values(value).every(
      (messages) => Array.isArray(messages) && messages.every((m) => typeof m === 'string'),
    )
  );
}

/**
 * Creates a Version of Song `songId` from one of its Versions; it becomes the current one. It holds
 * the source's lyrics and styles, or the request's when it carries them.
 */
export async function createVersion(
  songId: string,
  request: NewVersion,
): Promise<CreateVersionResult> {
  try {
    const response = await apiFetch(`${SONGS_PATH}/${encodeURIComponent(songId)}/versions`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    });
    const answer = await body(response);
    if (response.status === 201 && isVersion(answer)) {
      return { kind: 'created', version: answer };
    }
    const options = optionsOf(answer);
    if (
      isRecord(answer) &&
      options !== undefined &&
      ((response.status === 409 && answer.code === 'version_number_taken') ||
        (response.status === 422 && answer.code === 'version_number_not_offered'))
    ) {
      return { kind: 'refused', taken: response.status === 409, options };
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

/** How making a Version current ended: the Song as it is now, or a failure. */
export type SetCurrentResult = { kind: 'saved'; song: Song } | { kind: 'failed' };

/** Makes `versionId` the current working Version of Song `songId`. It carries no revision. */
export async function setCurrentVersion(
  songId: string,
  versionId: string,
): Promise<SetCurrentResult> {
  try {
    const response = await apiFetch(`${SONGS_PATH}/${encodeURIComponent(songId)}/current-version`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ versionId }),
    });
    const answer = await body(response);
    return response.ok && isSong(answer) ? { kind: 'saved', song: answer } : { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/**
 * An edit of a Version: only the fields given change. Lyrics and styles are sent as typed; `inputs`
 * holds the options to change, each kept as it is when not sent.
 */
export interface VersionEdit {
  name?: string | null;
  notes?: string | null;
  archived?: boolean;
  lyrics?: string;
  styles?: string;
  inputs?: Record<string, OptionValue>;
}

/**
 * The prefix of an option's key in an edit as the shared save helper holds it (`inputs.weirdness`),
 * where every value is text: the option's value is held as its JSON ({@link optionText}).
 */
export const OPTION_EDIT_PREFIX = 'inputs.';

/** An option's value as an edit holds it, and as conflicts compare it: its JSON. */
export function optionText(value: OptionValue | undefined): string {
  return JSON.stringify(value ?? null);
}

/** The option an edit's text holds ({@link optionText}). */
export function optionFromText(text: FieldValue): OptionValue {
  return text === null ? null : (JSON.parse(text) as OptionValue);
}

/**
 * The edit the API takes for one the shared save helper holds: each `inputs.<key>` field goes into
 * `inputs`, as its value; the rest are sent as they are.
 */
export function versionEditOf(edit: Readonly<Record<string, FieldValue>>): VersionEdit {
  const fields: Record<string, unknown> = {};
  const inputs: Record<string, OptionValue> = {};
  for (const [key, value] of Object.entries(edit)) {
    if (key.startsWith(OPTION_EDIT_PREFIX)) {
      inputs[key.slice(OPTION_EDIT_PREFIX.length)] = optionFromText(value);
    } else {
      fields[key] = value;
    }
  }
  return {
    ...(fields as VersionEdit),
    ...(Object.keys(inputs).length > 0 ? { inputs } : {}),
  };
}

const acceptVersionDetail = (answer: unknown) => (isVersionDetail(answer) ? answer : undefined);

/** One Version with its lyrics, styles, and options. */
export function useVersionDetail(versionId: string) {
  return useResource(`${VERSIONS_PATH}/${encodeURIComponent(versionId)}`, acceptVersionDetail);
}

/**
 * Edits a Version's name, notes, archived flag, lyrics, or styles, based on the revision it was
 * read at. The answer (and a conflict's current Version) carries the lyrics and styles.
 */
export function updateVersion(
  version: Pick<Version, 'id' | 'revision'>,
  edit: VersionEdit,
): Promise<SaveResult<VersionDetail>> {
  return patchWithRevision(
    `${VERSIONS_PATH}/${encodeURIComponent(version.id)}`,
    version.revision,
    { ...edit },
    acceptVersionDetail,
  );
}

/**
 * Sends an edit of a Version as the page closes (the tab is closed or reloaded): a `keepalive`
 * PATCH, so the browser finishes it after the page is gone, carrying the anti-forgery header and
 * the revision. Nothing reads its answer: a failure (a conflict, no connection) is accepted, and
 * the shared fetch helper's sign-in prompt cannot help a page that is going away.
 */
export function sendVersionAsPageCloses(
  version: Pick<Version, 'id' | 'revision'>,
  edit: VersionEdit,
): void {
  try {
    fetch(resolveAppUrl(`${VERSIONS_PATH}/${encodeURIComponent(version.id)}`), {
      method: 'PATCH',
      keepalive: true,
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
        'If-Match': ifMatch(version.revision),
        [ANTIFORGERY_HEADER]: '1',
      },
      body: JSON.stringify(edit),
    }).catch(() => undefined);
  } catch {
    // The page is going away; there is no one left to tell.
  }
}

/** How many times archiving is retried on a newer revision before it gives up. */
const ARCHIVE_RETRIES = 3;

/** How archiving or unarchiving ended: the Version as it is now, or a failure. */
export type ArchiveResult = { kind: 'saved'; version: Version } | { kind: 'failed' };

/**
 * Archives or unarchives a Version. Archiving is a command, not an edit of text someone else may
 * have changed: when the Version moved to a newer revision meanwhile (a name or notes edit), it is
 * retried on that revision without asking, and when the newer revision already has the flag wanted,
 * that is the answer.
 */
export async function setVersionArchived(
  version: Pick<Version, 'id' | 'revision'>,
  archived: boolean,
): Promise<ArchiveResult> {
  let base = version;
  for (let attempt = 0; attempt <= ARCHIVE_RETRIES; attempt++) {
    const result = await updateVersion(base, { archived });
    if (result.kind === 'saved') {
      return { kind: 'saved', version: result.record };
    }
    if (result.kind !== 'conflict') {
      return { kind: 'failed' };
    }
    if (result.current.archived === archived) {
      return { kind: 'saved', version: result.current };
    }
    base = result.current;
  }
  return { kind: 'failed' };
}
