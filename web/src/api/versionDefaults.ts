import { apiFetch } from './client';
import type { OptionValue } from './createFields';
import { writeWithRevision, type SaveResult } from './saves';
import { body, isRecord, useResource } from './songs';

export const VERSION_DEFAULTS_PATH = 'api/v1/settings/version-defaults';

/**
 * The user's defaults for a new Song's Version 1. `defaults` holds each option the user set, by
 * its `inputs` key (one left out uses Suno's default); `ignored` each of them that is no longer
 * valid (a retired model), with why: it is kept, but new Songs do not use it until it is valid
 * again. `keys` is every option that takes a default, in order, as the API decides it from Suno's
 * field inventory. `models` is the models offered, in order: what a model default may now be.
 */
export interface VersionDefaults {
  revision: number;
  defaults: Record<string, OptionValue>;
  ignored: Record<string, string>;
  keys: string[];
  models: string[];
}

const isOptionValue = (value: unknown): value is OptionValue =>
  value === null || ['string', 'number', 'boolean'].includes(typeof value);

function isVersionDefaults(value: unknown): value is VersionDefaults {
  return (
    isRecord(value) &&
    typeof value.revision === 'number' &&
    isRecord(value.defaults) &&
    Object.values(value.defaults).every(isOptionValue) &&
    isRecord(value.ignored) &&
    Object.values(value.ignored).every((reason) => typeof reason === 'string') &&
    Array.isArray(value.keys) &&
    value.keys.every((key) => typeof key === 'string') &&
    Array.isArray(value.models) &&
    value.models.every((model) => typeof model === 'string')
  );
}

const accept = (answer: unknown) => (isVersionDefaults(answer) ? answer : undefined);

/** The defaults for new Versions. */
export function useVersionDefaults() {
  return useResource(VERSION_DEFAULTS_PATH, accept);
}

/** The defaults as they are now, read again (after the model list changed); undefined when they cannot be read. */
export async function readVersionDefaults(): Promise<VersionDefaults | undefined> {
  try {
    const response = await apiFetch(VERSION_DEFAULTS_PATH);
    const answer = await body(response);
    return response.ok ? accept(answer) : undefined;
  } catch {
    return undefined;
  }
}

/**
 * Replaces the defaults, based on `revision`: a key left out goes back to Suno's default. Errors
 * of an `invalid` answer are keyed `defaults.<key>`.
 */
export function saveVersionDefaults(
  revision: number,
  defaults: Record<string, OptionValue>,
): Promise<SaveResult<VersionDefaults>> {
  return writeWithRevision('PUT', VERSION_DEFAULTS_PATH, revision, { defaults }, accept);
}
