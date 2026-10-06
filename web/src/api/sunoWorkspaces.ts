import { isRecord, useResource } from './songs';

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
