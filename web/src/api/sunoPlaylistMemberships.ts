import { apiFetch } from './client';
import { ifMatch } from './saves';
import { body, isRecord, useResource } from './songs';
import { isSunoImport } from './sunoImports';

/**
 * The playlists a playlist sync read (#449), as the review lists them: each playlist's songs as read
 * from its page, how they compare with the members n8Tracks last recorded, which of them n8Tracks has
 * and which it does not, and whether Confirm records them.
 */

/** How the songs read compare with the members n8Tracks recorded. */
export type MembershipChange = 'new' | 'changed' | 'same';

export interface PlaylistMembership {
  sunoId: string;
  name: string;
  /** How many songs the playlist's page said it holds; null when it said none. */
  songCount: number | null;
  /** How many songs were read. */
  read: number;
  /** False when the read fell short of `songCount`: read in part. */
  complete: boolean;
  change: MembershipChange;
  /** Songs read that the recorded members lack, and recorded members the read lacks. */
  added: number;
  dropped: number;
  /** Songs n8Tracks has as Generations. */
  inCatalog: number;
  /** Songs this import holds as records (not in n8Tracks yet). */
  inExport: number;
  /** Songs in neither, by Suno ID: not imported. */
  notImported: string[];
  /** Recorded at Confirm. */
  record: boolean;
  /** The user chose `record`, rather than the default. */
  chosen: boolean;
}

/** A playlist sync's playlist as the commit's result lists it. */
export interface CommittedPlaylist {
  sunoId: string;
  outcome: 'applied' | 'skipped';
}

const CHANGES: readonly string[] = ['new', 'changed', 'same'];

function isCount(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0;
}

export function isPlaylistMembership(value: unknown): value is PlaylistMembership {
  return (
    isRecord(value) &&
    typeof value.sunoId === 'string' &&
    typeof value.name === 'string' &&
    (value.songCount === null || isCount(value.songCount)) &&
    isCount(value.read) &&
    typeof value.complete === 'boolean' &&
    typeof value.change === 'string' &&
    CHANGES.includes(value.change) &&
    isCount(value.added) &&
    isCount(value.dropped) &&
    isCount(value.inCatalog) &&
    isCount(value.inExport) &&
    Array.isArray(value.notImported) &&
    value.notImported.every((id) => typeof id === 'string') &&
    typeof value.record === 'boolean' &&
    typeof value.chosen === 'boolean'
  );
}

const acceptList = (answer: unknown) =>
  isRecord(answer) &&
  Array.isArray(answer.items) &&
  answer.items.every(isPlaylistMembership) &&
  typeof answer.revision === 'number'
    ? (answer as unknown as { items: PlaylistMembership[]; revision: number })
    : undefined;

function membershipsPath(id: string): string {
  return `api/v1/suno/exports/${encodeURIComponent(id)}/playlists`;
}

/** The playlists the export's playlist sync read. */
export function usePlaylistMemberships(id: string) {
  return useResource(membershipsPath(id), acceptList);
}

/** How choosing ended, as for the Suno state changes. */
export type SetMembershipsResult =
  | { kind: 'changed'; revision: number }
  | { kind: 'conflict' }
  | { kind: 'not-ready' }
  | { kind: 'failed' };

/** Chooses whether Confirm records the songs of the playlists `sunoIds`, at `revision`. */
export async function setPlaylistMemberships(
  id: string,
  revision: number,
  sunoIds: string[],
  record: boolean,
): Promise<SetMembershipsResult> {
  try {
    const response = await apiFetch(membershipsPath(id), {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json', 'If-Match': ifMatch(revision) },
      body: JSON.stringify({ sunoIds, record }),
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

function songs(count: number): string {
  return count === 1 ? '1 song' : `${count.toLocaleString('en')} songs`;
}

/** What was read of the playlist, against what its page said it holds. */
export function readText(row: PlaylistMembership): string {
  if (row.songCount === null) {
    return `Read ${songs(row.read)}.`;
  }
  return row.complete
    ? `Read all ${songs(row.read)}.`
    : `Read in part: ${row.read.toLocaleString('en')} of ${songs(row.songCount)} (Suno showed no more).`;
}

/** How the songs read compare with what n8Tracks recorded. */
export function changeText(row: PlaylistMembership): string {
  switch (row.change) {
    case 'new':
      return 'n8Tracks has not recorded its songs before.';
    case 'same':
      return 'The same songs as n8Tracks recorded.';
    case 'changed':
      return `Changed since n8Tracks recorded it: ${songs(row.added)} added, ${songs(row.dropped)} gone.`;
  }
}

/** Which songs n8Tracks has, which this import holds, and which are not imported. */
export function membersText(row: PlaylistMembership): string {
  const parts = [`${songs(row.inCatalog)} in n8Tracks`];
  if (row.inExport > 0) {
    parts.push(`${songs(row.inExport)} in this import`);
  }
  parts.push(`${songs(row.notImported.length)} not imported`);
  return `${parts.join(', ')}.`;
}

/** The playlists of a commit's result, recorded and left, in plain words. */
export function playlistResultText(rows: readonly CommittedPlaylist[]): string {
  const recorded = rows.filter((row) => row.outcome === 'applied').length;
  const left = rows.length - recorded;
  const playlists = (count: number) => (count === 1 ? '1 playlist' : `${String(count)} playlists`);
  return left === 0
    ? `Recorded the songs of ${playlists(recorded)}.`
    : `Recorded the songs of ${playlists(recorded)}; left ${playlists(left)} as they were.`;
}
