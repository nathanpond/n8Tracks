import { apiFetch } from './client';
import { body, isRecord, isSong, useResource, type Song } from './songs';

const SONGS_PATH = 'api/v1/songs';
const VERSIONS_PATH = 'api/v1/versions';

/** The longest Version name the API takes, in UTF-16 code units after trimming. */
export const VERSION_NAME_MAXIMUM_LENGTH = 200;

/** A Version as the tree shows it: no creation inputs. Times are UTC ISO 8601. */
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
    typeof value.revision === 'number'
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

const acceptVersions = (answer: unknown) =>
  isRecord(answer) && Array.isArray(answer.items) && answer.items.every(isVersion)
    ? answer.items
    : undefined;

/** Every Version of a Song (by ID or shortcode), archived ones included, flat, in tree order. */
export function useSongVersions(reference: string) {
  return useResource(`${SONGS_PATH}/${encodeURIComponent(reference)}/versions`, acceptVersions);
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

/** Creates a Version of Song `songId` from one of its Versions; it becomes the current one. */
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
