import { apiFetch } from './client';
import { localAudioFilesOf, type LocalAudioFiles } from './localAudioFiles';
import { ifMatch } from './saves';
import { body, isRecord, isSong, type Song } from './songs';

const SONGS_PATH = 'api/v1/songs';

/** The code a read of a deleted Song answers with, for its 30-day retention. */
export const SONG_DELETED_CODE = 'song_deleted';

/** What deleting a Song would take with it, as its confirmation shows it. */
export interface SongDeletionImpact {
  id: string;
  shortcode: string;
  title: string;
  versionCount: number;
  generationCount: number;
  artworkCount: number;
  albumCount: number;
  playlistCount: number;
  relationshipCount: number;
  audioFileCount: number;
  /** Its local audio files (#213), Song-level ones included; `audioFileCount` is their total. */
  localAudioFiles: LocalAudioFiles;
  /** Whether the title must be typed to confirm; the server decides again at delete time. */
  titleRequired: boolean;
  revision: number;
}

const COUNTS = [
  'versionCount',
  'generationCount',
  'artworkCount',
  'albumCount',
  'playlistCount',
  'relationshipCount',
  'audioFileCount',
  'revision',
] as const;

function hasImpactFields(value: unknown): value is Omit<SongDeletionImpact, 'localAudioFiles'> {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.shortcode === 'string' &&
    typeof value.title === 'string' &&
    typeof value.titleRequired === 'boolean' &&
    COUNTS.every((key) => Number.isInteger(value[key]))
  );
}

/**
 * The impact an answer holds, or undefined when it is not one. An answer without
 * `localAudioFiles` counts none.
 */
export function songDeletionImpactOf(value: unknown): SongDeletionImpact | undefined {
  if (!hasImpactFields(value)) {
    return undefined;
  }
  const localAudioFiles = localAudioFilesOf(
    'localAudioFiles' in value ? value.localAudioFiles : undefined,
  );
  return localAudioFiles === undefined ? undefined : { ...value, localAudioFiles };
}

/** A Song that was deleted, as a read of it says: its ID, shortcode, title, and when. */
export interface DeletedSong {
  songId: string;
  shortcode: string;
  title: string;
  deletedAt: string;
}

/** The deleted Song a 404 answer names, or undefined when it is a plain not-found. */
export function deletedSongOf(problem: unknown): DeletedSong | undefined {
  return isRecord(problem) &&
    problem.code === SONG_DELETED_CODE &&
    typeof problem.songId === 'string' &&
    typeof problem.shortcode === 'string' &&
    typeof problem.title === 'string' &&
    typeof problem.deletedAt === 'string'
    ? {
        songId: problem.songId,
        shortcode: problem.shortcode,
        title: problem.title,
        deletedAt: problem.deletedAt,
      }
    : undefined;
}

/** How asking what deleting a Song would do ended. Never a rejection. */
export type SongDeletionImpactResult =
  { kind: 'found'; impact: SongDeletionImpact } | { kind: 'gone' } | { kind: 'failed' };

/** What deleting the Song with `songId` would do now. */
export async function fetchSongDeletionImpact(
  songId: string,
  signal?: AbortSignal,
): Promise<SongDeletionImpactResult> {
  try {
    const response = await apiFetch(`${SONGS_PATH}/${encodeURIComponent(songId)}/deletion-impact`, {
      signal,
    });
    const impact = response.ok ? songDeletionImpactOf(await body(response)) : undefined;
    if (impact !== undefined) {
      return { kind: 'found', impact };
    }
    return response.status === 404 ? { kind: 'gone' } : { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/**
 * How deleting a Song ended: `deleted`; `conflict` when the Song changed since it was read (with
 * the Song as it is now); `confirmation` when the title is required now and was missing or wrong
 * (with what deleting would do now); `gone` when it is no longer there; `failed` otherwise. Nothing
 * was deleted unless the kind is `deleted`.
 */
export type DeleteSongResult =
  | { kind: 'deleted' }
  | { kind: 'conflict'; current: Song }
  | { kind: 'confirmation'; impact: SongDeletionImpact }
  | { kind: 'gone' }
  | { kind: 'failed' };

/**
 * Deletes a Song, based on the revision its impact was read at, repeating the typed title when
 * one was asked for.
 */
export async function deleteSong(
  song: Pick<SongDeletionImpact, 'id' | 'revision'>,
  confirmTitle?: string,
): Promise<DeleteSongResult> {
  try {
    const response = await apiFetch(`${SONGS_PATH}/${encodeURIComponent(song.id)}`, {
      method: 'DELETE',
      headers: { 'If-Match': ifMatch(song.revision), 'Content-Type': 'application/json' },
      body: JSON.stringify(confirmTitle === undefined ? {} : { confirmTitle }),
    });
    if (response.status === 204) {
      return { kind: 'deleted' };
    }
    const answer = await body(response);
    if (
      response.status === 409 &&
      isRecord(answer) &&
      answer.code === 'revision_conflict' &&
      isSong(answer.current)
    ) {
      return { kind: 'conflict', current: answer.current };
    }
    const impact =
      response.status === 422 && isRecord(answer) && answer.code === 'confirmation_required'
        ? songDeletionImpactOf(answer.impact)
        : undefined;
    if (impact !== undefined) {
      return { kind: 'confirmation', impact };
    }
    return response.status === 404 ? { kind: 'gone' } : { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}
