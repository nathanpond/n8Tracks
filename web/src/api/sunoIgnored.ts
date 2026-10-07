import { apiFetch } from './client';
import { body, isRecord, useResource } from './songs';

/** Where the Ignored Suno Items screen is (#143). */
export const IGNORED_PATH = '/suno/ignored';

/** Suno's status for an ignored clip as last seen: in the library, in Suno's Trash, gone from both, or left out of a whole-library sync. */
export type IgnoredStatus = 'present' | 'trashed' | 'missing' | 'not_seen';

export const IGNORED_STATUSES: readonly IgnoredStatus[] = [
  'present',
  'trashed',
  'missing',
  'not_seen',
];

/**
 * A Suno clip on the ignore list: its Suno ID, Suno's title and workspace as last seen (the
 * workspace's name when n8Tracks knows it), when it was ignored (UTC), its status as last seen
 * (null before a sync saw it), and when a sync last included it.
 */
export interface IgnoredItem {
  sunoId: string;
  title: string | null;
  workspaceId: string | null;
  workspaceName: string | null;
  ignoredAt: string;
  status: IgnoredStatus | null;
  lastSeenAt: string | null;
}

/** A workspace ignored items are in, with how many. */
export interface IgnoredWorkspace {
  id: string;
  name: string | null;
  count: number;
}

/** A page of the ignore list (50 to a page), how many match, and the workspaces of the whole list. */
export interface IgnoredPage {
  items: IgnoredItem[];
  page: number;
  pageSize: number;
  total: number;
  workspaces: IgnoredWorkspace[];
}

/** What the list shows: a search (title or the start of a Suno ID), a workspace, a status, and a page. */
export interface IgnoredQuery {
  q: string;
  workspace: string;
  status: IgnoredStatus | '';
  page: number;
}

function isStatus(value: unknown): value is IgnoredStatus {
  return IGNORED_STATUSES.includes(value as IgnoredStatus);
}

const textOrNull = (value: unknown) => value === null || typeof value === 'string';

function isItem(value: unknown): value is IgnoredItem {
  return (
    isRecord(value) &&
    typeof value.sunoId === 'string' &&
    textOrNull(value.title) &&
    textOrNull(value.workspaceId) &&
    textOrNull(value.workspaceName) &&
    typeof value.ignoredAt === 'string' &&
    (value.status === null || isStatus(value.status)) &&
    textOrNull(value.lastSeenAt)
  );
}

function isWorkspace(value: unknown): value is IgnoredWorkspace {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    textOrNull(value.name) &&
    typeof value.count === 'number'
  );
}

const acceptPage = (answer: unknown) =>
  isRecord(answer) &&
  Array.isArray(answer.items) &&
  answer.items.every(isItem) &&
  typeof answer.page === 'number' &&
  typeof answer.pageSize === 'number' &&
  typeof answer.total === 'number' &&
  Array.isArray(answer.workspaces) &&
  answer.workspaces.every(isWorkspace)
    ? (answer as unknown as IgnoredPage)
    : undefined;

/** The query the address holds; anything unknown is left at its default. */
export function ignoredQueryFrom(parameters: URLSearchParams): IgnoredQuery {
  const page = Number(parameters.get('page') ?? '1');
  const status = parameters.get('status');
  return {
    q: parameters.get('q') ?? '',
    workspace: parameters.get('workspace') ?? '',
    status: isStatus(status) ? status : '',
    page: Number.isInteger(page) && page >= 1 ? page : 1,
  };
}

/** The query string of `query`, defaults left out: the screen's address and the API's both use it. */
export function ignoredParameters(query: IgnoredQuery): URLSearchParams {
  const parameters = new URLSearchParams();
  if (query.q.trim() !== '') {
    parameters.set('q', query.q.trim());
  }
  if (query.workspace !== '') {
    parameters.set('workspace', query.workspace);
  }
  if (query.status !== '') {
    parameters.set('status', query.status);
  }
  if (query.page !== 1) {
    parameters.set('page', String(query.page));
  }
  return parameters;
}

/** A page of the ignore list. */
export function useIgnoredItems(query: IgnoredQuery) {
  const parameters = ignoredParameters(query).toString();
  return useResource(`api/v1/suno/ignored${parameters === '' ? '' : `?${parameters}`}`, acceptPage);
}

/** How a removal ended: how many were removed and how many were no longer on the list, or that it failed (nothing removed). */
export type RemoveIgnoredResult =
  { kind: 'removed'; removed: number; unknown: number } | { kind: 'failed' };

/** Removes `sunoIds` from the ignore list in one command. Nothing is imported. */
export async function removeIgnoredItems(sunoIds: readonly string[]): Promise<RemoveIgnoredResult> {
  try {
    const response = await apiFetch('api/v1/suno/ignored/remove', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sunoIds }),
    });
    const answer = await body(response);
    return response.ok &&
      isRecord(answer) &&
      typeof answer.removed === 'number' &&
      typeof answer.unknown === 'number'
      ? { kind: 'removed', removed: answer.removed, unknown: answer.unknown }
      : { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}
