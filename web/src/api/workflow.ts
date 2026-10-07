import { apiFetch } from './client';
import { ifMatch } from './saves';
import {
  body,
  isErrorMap,
  isRecord,
  isWorkflowState,
  useResource,
  WORKFLOW_STATES_PATH,
  type WorkflowState,
} from './songs';

/**
 * The ID of the seeded Archived workflow state: fixed, whatever the state is renamed to. A Song in it
 * is archived.
 */
export const ARCHIVED_STATE_ID = '01a10a6e-dc86-7006-8000-000000000007';

/** The longest state name the API takes, in UTF-16 code units after trimming. */
export const STATE_NAME_MAXIMUM_LENGTH = 50;

/** A workflow state with its Song count, as the management list has it. */
export type ManagedState = WorkflowState & { songCount: number };

/** Every workflow state in order, and the revision of the workflow as a whole. */
export interface WorkflowList {
  revision: number;
  items: ManagedState[];
}

function isWorkflowList(value: unknown): value is WorkflowList {
  return (
    isRecord(value) &&
    typeof value.revision === 'number' &&
    Array.isArray(value.items) &&
    value.items.every((item) => isWorkflowState(item) && typeof item.songCount === 'number')
  );
}

const acceptList = (answer: unknown) => (isWorkflowList(answer) ? answer : undefined);

/** Every state with its Song count, and the workflow revision. */
export function useWorkflowList() {
  return useResource(WORKFLOW_STATES_PATH, acceptList);
}

/**
 * The states the Songs table's filter offers: every visible state, and a hidden one while Songs
 * are in it (or while it is chosen, so it can be unchosen).
 */
export function statesForFilter(states: WorkflowState[], selected: string[]): WorkflowState[] {
  return states.filter(
    (state) => !state.hidden || (state.songCount ?? 0) > 0 || selected.includes(state.id),
  );
}

/**
 * How a change to the workflow ended. Never a rejection. `conflict` means the workflow changed
 * elsewhere: nothing was applied, and `current` is the list as it is now.
 */
export type WorkflowResult =
  | { kind: 'saved'; list: WorkflowList }
  | { kind: 'conflict'; current: WorkflowList }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'last-visible' }
  | { kind: 'in-use'; songCount: number }
  | { kind: 'not-found' }
  | { kind: 'failed' };

async function change(
  method: 'POST' | 'PATCH' | 'PUT' | 'DELETE',
  path: string,
  revision: number,
  payload?: Record<string, unknown>,
): Promise<WorkflowResult> {
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
      return isWorkflowList(answer) ? { kind: 'saved', list: answer } : { kind: 'failed' };
    }
    if (!isRecord(answer)) {
      return { kind: 'failed' };
    }
    if (response.status === 409) {
      if (
        (answer.code === 'revision_conflict' || answer.code === 'order_mismatch') &&
        isWorkflowList(answer.current)
      ) {
        return { kind: 'conflict', current: answer.current };
      }
      if (answer.code === 'last_visible_state') {
        return { kind: 'last-visible' };
      }
      if (answer.code === 'state_in_use' && typeof answer.songCount === 'number') {
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
    if (response.status === 404) {
      return { kind: 'not-found' };
    }
    return { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

const statePath = (id: string) => `${WORKFLOW_STATES_PATH}/${encodeURIComponent(id)}`;

/** Adds a visible state at the end of the order, in the first palette colour not in use. */
export function addState(revision: number, name: string): Promise<WorkflowResult> {
  return change('POST', WORKFLOW_STATES_PATH, revision, { name });
}

/** An edit of one state: only the fields given change. */
export interface StateEdit {
  name?: string;
  colour?: string;
  hidden?: boolean;
}

/** Renames, recolours, hides, or shows a state. */
export function updateState(
  revision: number,
  id: string,
  edit: StateEdit,
): Promise<WorkflowResult> {
  return change('PATCH', statePath(id), revision, { ...edit });
}

/** Puts the states in the order of `ids`, which holds every state's ID once. */
export function reorderStates(revision: number, ids: string[]): Promise<WorkflowResult> {
  return change('PUT', `${WORKFLOW_STATES_PATH}/order`, revision, { ids });
}

/** Deletes a state; its Songs, if any, move to `replacement`. */
export function deleteState(
  revision: number,
  id: string,
  replacement?: string,
): Promise<WorkflowResult> {
  const query = replacement === undefined ? '' : `?replacement=${encodeURIComponent(replacement)}`;
  return change('DELETE', `${statePath(id)}${query}`, revision);
}

/** What names are compared by: trimmed, NFC-normalised, and in one case, as the API compares them. */
function nameKey(name: string): string {
  return name.trim().normalize('NFC').toUpperCase();
}

/**
 * Why `name` cannot be a state's name, or undefined when it can, by the rules the API applies:
 * trimmed, 1 to 50 characters, and unique among `states` (hidden ones included, the one being
 * renamed, `exceptId`, left out) ignoring case.
 */
export function stateNameError(
  name: string,
  states: readonly WorkflowState[],
  exceptId?: string,
): string | undefined {
  const trimmed = name.trim();
  if (trimmed === '') {
    return 'Enter a name.';
  }
  if (trimmed.length > STATE_NAME_MAXIMUM_LENGTH) {
    return `Use at most ${String(STATE_NAME_MAXIMUM_LENGTH)} characters.`;
  }
  const key = nameKey(trimmed);
  if (states.some((state) => state.id !== exceptId && nameKey(state.name) === key)) {
    return 'Another state already has this name.';
  }
  return undefined;
}

/** `items` with the one at `from` moved to `to`. */
export function moved<T>(items: readonly T[], from: number, to: number): T[] {
  const next = [...items];
  const [item] = next.splice(from, 1);
  if (item !== undefined) {
    next.splice(to, 0, item);
  }
  return next;
}
