import { apiFetch } from './client';
import { ifMatch } from './saves';
import { body, isErrorMap, isRecord, useResource } from './songs';

export const SUNO_MODELS_PATH = 'api/v1/suno/models';

/** The longest model name the API takes, in UTF-16 code units after trimming. */
export const MODEL_NAME_MAXIMUM_LENGTH = 50;

/** The longest model note the API takes, in UTF-16 code units after trimming. */
export const MODEL_NOTE_MAXIMUM_LENGTH = 200;

/**
 * A Suno model of the list. A retired one is not offered for a new choice, but Versions that name it
 * keep it; `discovered` is true for one n8Tracks added from an imported clip; `versionCount` is how
 * many Versions name it (a model they name cannot be renamed or deleted).
 */
export interface SunoModel {
  id: string;
  name: string;
  note: string | null;
  order: number;
  retired: boolean;
  discovered: boolean;
  versionCount: number;
}

/** Every model in order, and the revision of the list as a whole. */
export interface SunoModelList {
  revision: number;
  items: SunoModel[];
}

function isSunoModel(value: unknown): value is SunoModel {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.name === 'string' &&
    (value.note === null || typeof value.note === 'string') &&
    typeof value.order === 'number' &&
    typeof value.retired === 'boolean' &&
    typeof value.discovered === 'boolean' &&
    typeof value.versionCount === 'number'
  );
}

function isSunoModelList(value: unknown): value is SunoModelList {
  return (
    isRecord(value) &&
    typeof value.revision === 'number' &&
    Array.isArray(value.items) &&
    value.items.every(isSunoModel)
  );
}

const acceptList = (answer: unknown) => (isSunoModelList(answer) ? answer : undefined);

/** Every model, retired ones included, with its Version count, and the list revision. */
export function useSunoModels() {
  return useResource(SUNO_MODELS_PATH, acceptList);
}

/**
 * How a change to the model list ended. Never a rejection. `conflict` means the list changed
 * elsewhere: nothing was applied, and `current` is the list as it is now.
 */
export type ModelResult =
  | { kind: 'saved'; list: SunoModelList }
  | { kind: 'conflict'; current: SunoModelList }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'last-offered' }
  | { kind: 'in-use'; versionCount: number }
  | { kind: 'not-found' }
  | { kind: 'failed' };

async function change(
  method: 'POST' | 'PATCH' | 'PUT' | 'DELETE',
  path: string,
  revision: number,
  payload?: Record<string, unknown>,
): Promise<ModelResult> {
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
    const answer = await body(response);
    if (response.ok) {
      return isSunoModelList(answer) ? { kind: 'saved', list: answer } : { kind: 'failed' };
    }
    if (!isRecord(answer)) {
      return { kind: 'failed' };
    }
    if (response.status === 409) {
      if (
        (answer.code === 'revision_conflict' || answer.code === 'order_mismatch') &&
        isSunoModelList(answer.current)
      ) {
        return { kind: 'conflict', current: answer.current };
      }
      if (answer.code === 'last_offered_model') {
        return { kind: 'last-offered' };
      }
      if (answer.code === 'model_in_use' && typeof answer.versionCount === 'number') {
        return { kind: 'in-use', versionCount: answer.versionCount };
      }
    }
    if (
      response.status === 422 &&
      answer.code === 'validation_failed' &&
      isErrorMap(answer.errors)
    ) {
      return { kind: 'invalid', errors: answer.errors };
    }
    if (response.status === 404) {
      return { kind: 'not-found' };
    }
    return { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

const modelPath = (id: string) => `${SUNO_MODELS_PATH}/${encodeURIComponent(id)}`;

/** Adds a model at the end of the list, not retired. A blank note is none. */
export function addModel(revision: number, name: string, note: string): Promise<ModelResult> {
  return change('POST', SUNO_MODELS_PATH, revision, { name, note });
}

/** An edit of one model: only the fields given change. An empty note removes it. */
export interface ModelEdit {
  name?: string;
  note?: string;
  retired?: boolean;
}

/** Renames, annotates, retires, or restores a model. */
export function updateModel(revision: number, id: string, edit: ModelEdit): Promise<ModelResult> {
  return change('PATCH', modelPath(id), revision, { ...edit });
}

/** Puts the models in the order of `ids`, which holds every model's ID once. */
export function reorderModels(revision: number, ids: string[]): Promise<ModelResult> {
  return change('PUT', `${SUNO_MODELS_PATH}/order`, revision, { ids });
}

/** Deletes a model no Version names. */
export function deleteModel(revision: number, id: string): Promise<ModelResult> {
  return change('DELETE', modelPath(id), revision);
}

/** What names are compared by: trimmed, NFC-normalised, and in one case, as the API compares them. */
function nameKey(name: string): string {
  return name.trim().normalize('NFC').toUpperCase();
}

const CONTROL = /\p{Cc}/u;

/**
 * Why `name` cannot be a model's name, or undefined when it can, by the rules the API applies:
 * trimmed, 1 to 50 characters, one line, and unique among `models` (retired ones included, the one
 * being renamed, `exceptId`, left out) ignoring case.
 */
export function modelNameError(
  name: string,
  models: readonly SunoModel[],
  exceptId?: string,
): string | undefined {
  const trimmed = name.trim();
  if (trimmed === '') {
    return 'Enter a name.';
  }
  if (CONTROL.test(trimmed)) {
    return 'A name is one line, with no control characters.';
  }
  if (trimmed.length > MODEL_NAME_MAXIMUM_LENGTH) {
    return `Use at most ${String(MODEL_NAME_MAXIMUM_LENGTH)} characters.`;
  }
  const key = nameKey(trimmed);
  if (models.some((model) => model.id !== exceptId && nameKey(model.name) === key)) {
    return 'Another model already has this name.';
  }
  return undefined;
}

/** Why `note` cannot be a model's note, or undefined when it can: optional, one line, at most 200 characters. */
export function modelNoteError(note: string): string | undefined {
  const trimmed = note.trim();
  if (CONTROL.test(trimmed)) {
    return 'A note is one line, with no control characters.';
  }
  if (trimmed.length > MODEL_NOTE_MAXIMUM_LENGTH) {
    return `Use at most ${String(MODEL_NOTE_MAXIMUM_LENGTH)} characters.`;
  }
  return undefined;
}

/** Whether `model` is the only one of `models` not retired, so it can be neither retired nor deleted. */
export function isLastOffered(models: readonly SunoModel[], model: SunoModel): boolean {
  return !model.retired && models.every((other) => other.id === model.id || other.retired);
}
