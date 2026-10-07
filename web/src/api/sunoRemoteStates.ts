import { apiFetch } from './client';
import type { GenerationState, RemoteState } from './generations';
import { ifMatch } from './saves';
import { body, isRecord, useResource } from './songs';
import { isSunoImport } from './sunoImports';

/**
 * The review's "Suno state changes" (#142), chosen in its Class filter as this value: what a sync found
 * happened in Suno to clips already in n8Tracks. Not a class of record, so it never goes to the records list.
 */
export const REMOTE_STATE_CLASS = 'remote-state';

/** How many Suno state changes the review lists on one page. */
export const REMOTE_STATE_PAGE_SIZE = 100;

/** What a sync found: the clip is in Suno's Trash, listed again, or listed nowhere (Remote Missing). */
export type RemoteChange = 'trashed' | 'restored' | 'missing';

/** One Suno state change: the clip, the change, its Generation before and after, and whether Confirm applies it. */
export interface RemoteStateRow {
  sunoId: string;
  title: string | null;
  change: RemoteChange;
  remoteState: RemoteState;
  newRemoteState: RemoteState;
  state: GenerationState;
  newState: GenerationState;
  /** The Generation is archived by it (by sync). */
  archives: boolean;
  /** The Generation is reactivated by it (only one a sync archived). */
  reactivates: boolean;
  /** Applied at Confirm; false once set to Skip. */
  apply: boolean;
  generation: { id: string; shortcode: string; songShortcode: string };
}

/** A page of Suno state changes, their counts, and whether the sync could find clips missing. */
export interface RemoteStatePage {
  items: RemoteStateRow[];
  page: number;
  pageSize: number;
  total: number;
  counts: { trashed: number; restored: number; missing: number; applied: number; skipped: number };
  missingChecked: boolean;
  revision: number;
}

/** A Suno state change as the commit's result lists it (#142). */
export interface CommittedRemoteState {
  sunoId: string;
  change: RemoteChange;
  outcome: 'applied' | 'skipped';
  generation: { id: string; shortcode: string };
}

const CHANGES: readonly string[] = ['trashed', 'restored', 'missing'];
const REMOTE_STATES: readonly string[] = ['present', 'trashed', 'missing'];
const STATES: readonly string[] = ['active', 'archived'];

export function isRemoteStateRow(value: unknown): value is RemoteStateRow {
  return (
    isRecord(value) &&
    typeof value.sunoId === 'string' &&
    (value.title === null || typeof value.title === 'string') &&
    typeof value.change === 'string' &&
    CHANGES.includes(value.change) &&
    typeof value.remoteState === 'string' &&
    REMOTE_STATES.includes(value.remoteState) &&
    typeof value.newRemoteState === 'string' &&
    REMOTE_STATES.includes(value.newRemoteState) &&
    typeof value.state === 'string' &&
    STATES.includes(value.state) &&
    typeof value.newState === 'string' &&
    STATES.includes(value.newState) &&
    typeof value.archives === 'boolean' &&
    typeof value.reactivates === 'boolean' &&
    typeof value.apply === 'boolean' &&
    isRecord(value.generation) &&
    typeof value.generation.shortcode === 'string' &&
    typeof value.generation.songShortcode === 'string'
  );
}

const acceptPage = (answer: unknown) =>
  isRecord(answer) &&
  Array.isArray(answer.items) &&
  answer.items.every(isRemoteStateRow) &&
  typeof answer.total === 'number' &&
  typeof answer.page === 'number' &&
  typeof answer.pageSize === 'number' &&
  isRecord(answer.counts) &&
  typeof answer.missingChecked === 'boolean' &&
  typeof answer.revision === 'number'
    ? (answer as unknown as RemoteStatePage)
    : undefined;

function remoteStatesPath(id: string): string {
  return `api/v1/suno/exports/${encodeURIComponent(id)}/remote-states`;
}

/** A page of the export's Suno state changes whose title contains `q` (all when empty). */
export function useRemoteStates(id: string, q: string | undefined, page: number) {
  const parameters = new URLSearchParams({ pageSize: String(REMOTE_STATE_PAGE_SIZE) });
  if (q !== undefined && q !== '') {
    parameters.set('q', q);
  }
  if (page !== 1) {
    parameters.set('page', String(page));
  }
  return useResource(`${remoteStatesPath(id)}?${parameters.toString()}`, acceptPage);
}

/**
 * How setting Suno state changes ended: `changed` (the export's new revision); `conflict` when the
 * export changed elsewhere; `not-ready` when it is no longer open for review; `failed`.
 */
export type SetRemoteStatesResult =
  | { kind: 'changed'; revision: number }
  | { kind: 'conflict' }
  | { kind: 'not-ready' }
  | { kind: 'failed' };

/** Sets the Suno state changes of `sunoIds` to be applied at Confirm (`apply`) or skipped, at `revision`. */
export async function setRemoteStates(
  id: string,
  revision: number,
  sunoIds: string[],
  apply: boolean,
): Promise<SetRemoteStatesResult> {
  try {
    const response = await apiFetch(remoteStatesPath(id), {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json', 'If-Match': ifMatch(revision) },
      body: JSON.stringify({ sunoIds, apply }),
    });
    const answer = await body(response);
    if (response.ok && isSunoImport(answer)) {
      return { kind: 'changed', revision: answer.revision };
    }
    if (response.status === 409) {
      const code = isRecord(answer) ? answer.code : undefined;
      return code === 'revision_conflict' ? { kind: 'conflict' } : { kind: 'not-ready' };
    }
    return { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/** The plain words of a change, as the review lists it. */
export function remoteChangeText(row: RemoteStateRow): string {
  switch (row.change) {
    case 'trashed':
      return row.archives ? 'In Suno Trash: will be archived' : 'In Suno Trash';
    case 'restored':
      return row.reactivates ? 'Restored in Suno: will be reactivated' : 'Restored in Suno';
    case 'missing':
      return 'Remote Missing';
  }
}

/** Why a change leaves the Generation's state alone, when it does; undefined when it needs no note. */
export function remoteChangeNote(row: RemoteStateRow): string | undefined {
  if (row.change === 'trashed' && !row.archives) {
    return 'It is archived already; only its Suno state changes.';
  }
  if (row.change === 'restored' && !row.reactivates && row.state === 'archived') {
    return 'You archived it yourself, so it stays archived.';
  }
  if (row.change === 'missing') {
    return 'Suno lists it nowhere. Nothing is deleted, and it stays as it is.';
  }
  return undefined;
}

/** The Suno state changes of a commit's result, applied and skipped, in plain words. */
export function remoteResultText(rows: readonly CommittedRemoteState[]): string {
  const applied = rows.filter((row) => row.outcome === 'applied').length;
  const skipped = rows.length - applied;
  const changes = (count: number) =>
    count === 1 ? '1 Suno state change' : `${count.toLocaleString('en')} Suno state changes`;
  return skipped === 0
    ? `Followed ${changes(applied)}.`
    : `Followed ${changes(applied)}; skipped ${String(skipped)}.`;
}
