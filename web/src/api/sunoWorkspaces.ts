import { apiFetch } from './client';
import { body, isErrorMap, isRecord, isSongPage, useResource } from './songs';

/** Whether Suno still offers a workspace: `unavailable` when a complete list left it out or it is trashed in Suno. */
export type SunoWorkspaceState = 'available' | 'unavailable';

/**
 * A Suno workspace n8Tracks knows (#129), by Suno's ID for it: its name and description as last seen
 * (either may be empty), its state, when it was first and last seen (UTC), and how many live Songs
 * are in it.
 */
export interface SunoWorkspace {
  id: string;
  name: string;
  description: string;
  state: SunoWorkspaceState;
  firstSeen: string;
  lastSeen: string;
  songCount: number;
}

/** The Suno workspace a Song lives in, as the Song shows it. */
export interface SongWorkspace {
  id: string;
  name: string;
  state: SunoWorkspaceState;
}

/** The Song field that sets its workspace: a known workspace's Suno ID, or null for none. */
export const SUNO_WORKSPACE_KEY = 'sunoWorkspaceId';

/** Where Settings lists the workspaces (#151); each workspace's own page is under it, by Suno ID. */
export const SUNO_WORKSPACES_PATH = '/settings/suno-workspaces';

/** The Settings page of the workspace with Suno ID `id`. */
export function workspacePath(id: string): string {
  return `${SUNO_WORKSPACES_PATH}/${encodeURIComponent(id)}`;
}

/** What a workspace with a blank name is shown as. */
export const UNNAMED_WORKSPACE = '(unnamed)';

function isState(value: unknown): value is SunoWorkspaceState {
  return value === 'available' || value === 'unavailable';
}

export function isSongWorkspace(value: unknown): value is SongWorkspace {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.name === 'string' &&
    isState(value.state)
  );
}

function isSunoWorkspace(value: unknown): value is SunoWorkspace {
  return (
    isSongWorkspace(value) &&
    isRecord(value) &&
    typeof value.description === 'string' &&
    typeof value.firstSeen === 'string' &&
    typeof value.lastSeen === 'string' &&
    typeof value.songCount === 'number'
  );
}

const acceptWorkspaces = (answer: unknown) =>
  isRecord(answer) && Array.isArray(answer.items) && answer.items.every(isSunoWorkspace)
    ? answer.items
    : undefined;

/** Every Suno workspace n8Tracks knows, by name; empty until the extension has reported some. */
export function useSunoWorkspaces() {
  return useResource('api/v1/suno/workspaces', acceptWorkspaces);
}

/** The name a workspace is shown by: its name, or {@link UNNAMED_WORKSPACE} when blank. */
export function workspaceName(workspace: { name: string }): string {
  return workspace.name.trim() === '' ? UNNAMED_WORKSPACE : workspace.name;
}

/**
 * The workspaces a Song may be put in: every Available one, and the one it has even when that is
 * Unavailable (keeping it is no change), in the list's order.
 */
export function workspaceChoices(
  workspaces: readonly SunoWorkspace[],
  current: SongWorkspace | null,
): SongWorkspace[] {
  const choices: SongWorkspace[] = workspaces
    .filter((workspace) => workspace.state === 'available' || workspace.id === current?.id)
    .map(({ id, name, state }) => ({ id, name, state }));
  return current === null || choices.some((choice) => choice.id === current.id)
    ? choices
    : [current, ...choices];
}

/** How many of a workspace's Songs its page lists at once: the Songs list's largest page. */
export const WORKSPACE_SONGS_PAGE_SIZE = 100;

const acceptSongPage = (answer: unknown) => (isSongPage(answer) ? answer : undefined);

/** A page of the Songs in the workspace with Suno ID `id` (#151), by title. */
export function useWorkspaceSongs(id: string, page: number) {
  const parameters = new URLSearchParams({
    workspace: id,
    sort: 'title',
    pageSize: String(WORKSPACE_SONGS_PAGE_SIZE),
  });
  if (page !== 1) {
    parameters.set('page', String(page));
  }
  return useResource(`api/v1/songs?${parameters.toString()}`, acceptSongPage);
}

/**
 * Which of a workspace's Songs a bulk move takes: these Songs (by ID), or every one, with the count
 * the user confirmed (#346), so the move is refused if the workspace holds another number by then.
 */
export type WorkspaceSongSelection = { songIds: string[] } | { all: true; expectedCount: number };

/**
 * How a bulk move ended: `moved` (how many); `invalid` with the errors by field (the target is no
 * longer an Available other workspace, say); `not-in-workspace` with the Songs, as sent, that are no
 * longer in it; `too-many` past the limit; `count-changed` when an "all" move found another number of
 * Songs than confirmed (with the number now there); `gone` when the workspace is not known; `failed`
 * otherwise. Nothing moved unless the kind is `moved`: a move is all or nothing.
 */
export type MoveSongsResult =
  | { kind: 'moved'; moved: number }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'not-in-workspace'; songs: string[] }
  | { kind: 'too-many'; limit: number }
  | { kind: 'count-changed'; count: number }
  | { kind: 'gone' }
  | { kind: 'failed' };

/** Moves the selected Songs out of the workspace with Suno ID `from` into `targetWorkspaceId`, in one command. */
export async function moveWorkspaceSongs(
  from: string,
  selection: WorkspaceSongSelection,
  targetWorkspaceId: string,
): Promise<MoveSongsResult> {
  try {
    const response = await apiFetch(
      `api/v1/suno/workspaces/${encodeURIComponent(from)}/move-songs`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ...selection, targetWorkspaceId }),
      },
    );
    const answer = await body(response);
    if (response.ok && isRecord(answer) && typeof answer.moved === 'number') {
      return { kind: 'moved', moved: answer.moved };
    }
    if (response.status === 404) {
      return { kind: 'gone' };
    }
    if (
      response.status === 409 &&
      isRecord(answer) &&
      answer.code === 'song_count_changed' &&
      typeof answer.count === 'number'
    ) {
      return { kind: 'count-changed', count: answer.count };
    }
    if (response.status === 422 && isRecord(answer)) {
      if (answer.code === 'song_not_in_workspace' && Array.isArray(answer.songs)) {
        return {
          kind: 'not-in-workspace',
          songs: answer.songs.filter((song): song is string => typeof song === 'string'),
        };
      }
      if (answer.code === 'too_many_songs' && typeof answer.limit === 'number') {
        return { kind: 'too-many', limit: answer.limit };
      }
      if (isErrorMap(answer.errors)) {
        return { kind: 'invalid', errors: answer.errors };
      }
    }
    return { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}
