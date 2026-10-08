import { apiFetch } from './client';
import type { GenerationState, RemoteState } from './generations';
import { songPlaybackFileOf, type SongPlaybackFile } from './songPlayback';
import { body, isRecord } from './songs';

const SONGS_PATH = 'api/v1/songs';

/** One file the player can switch to (#220): the file, and whether it is the one its owner plays. */
export interface PlaybackSourceFile {
  audioFile: SongPlaybackFile;
  isPlaybackFile: boolean;
}

/**
 * One Generation the player can switch to (#220): what the bar names and writes (its Version number,
 * rating and revision), Suno's duration, its states, and its available files, the playback file first.
 */
export interface PlaybackSourceGeneration {
  generation: { id: string; shortcode: string };
  versionNumber: string;
  rating: number | null;
  revision: number;
  durationSeconds: number | null;
  state: GenerationState;
  remoteState: RemoteState;
  files: PlaybackSourceFile[];
}

/**
 * Everything of a Song the player can switch to while comparing, as
 * `GET /songs/{reference}/playback-sources` answers it (#220): the Song, its available Song-level
 * files, then its Generations with an available file, in comparison order (the server's one rule).
 */
export interface PlaybackSources {
  song: { id: string; shortcode: string; title: string };
  songFiles: PlaybackSourceFile[];
  generations: PlaybackSourceGeneration[];
}

function sourceFileOf(value: unknown): PlaybackSourceFile | undefined {
  if (!isRecord(value) || typeof value.isPlaybackFile !== 'boolean') {
    return undefined;
  }
  const audioFile = songPlaybackFileOf(value.audioFile);
  return audioFile === undefined ? undefined : { audioFile, isPlaybackFile: value.isPlaybackFile };
}

function filesOf(value: unknown): PlaybackSourceFile[] | undefined {
  if (!Array.isArray(value)) {
    return undefined;
  }
  const files = value.map(sourceFileOf);
  return files.every((file) => file !== undefined) ? files : undefined;
}

function sourceGenerationOf(value: unknown): PlaybackSourceGeneration | undefined {
  if (!isRecord(value)) {
    return undefined;
  }
  const generation = value.generation;
  const files = filesOf(value.files);
  if (
    !isRecord(generation) ||
    typeof generation.id !== 'string' ||
    typeof generation.shortcode !== 'string' ||
    typeof value.versionNumber !== 'string' ||
    (value.rating !== null && typeof value.rating !== 'number') ||
    typeof value.revision !== 'number' ||
    (value.durationSeconds !== null && typeof value.durationSeconds !== 'number') ||
    (value.state !== 'active' && value.state !== 'archived') ||
    (value.remoteState !== 'present' &&
      value.remoteState !== 'trashed' &&
      value.remoteState !== 'missing') ||
    files === undefined
  ) {
    return undefined;
  }
  return {
    generation: { id: generation.id, shortcode: generation.shortcode },
    versionNumber: value.versionNumber,
    rating: value.rating,
    revision: value.revision,
    durationSeconds: value.durationSeconds,
    state: value.state,
    remoteState: value.remoteState,
    files,
  };
}

/** The Song's sources, or undefined when the value is not that answer. */
export function playbackSourcesOf(value: unknown): PlaybackSources | undefined {
  if (!isRecord(value) || !Array.isArray(value.generations)) {
    return undefined;
  }
  const song = value.song;
  const songFiles = filesOf(value.songFiles);
  const generations = value.generations.map(sourceGenerationOf);
  if (
    !isRecord(song) ||
    typeof song.id !== 'string' ||
    typeof song.shortcode !== 'string' ||
    typeof song.title !== 'string' ||
    songFiles === undefined ||
    generations.some((generation) => generation === undefined)
  ) {
    return undefined;
  }
  return {
    song: { id: song.id, shortcode: song.shortcode, title: song.title },
    songFiles,
    generations: generations.filter((generation) => generation !== undefined),
  };
}

/** Reads what the Song (its ID or shortcode) can switch between; undefined when it could not be read. */
export async function readPlaybackSources(
  reference: string,
  signal?: AbortSignal,
): Promise<PlaybackSources | undefined> {
  const response = await apiFetch(
    `${SONGS_PATH}/${encodeURIComponent(reference)}/playback-sources`,
    { signal },
  );
  const answer = await body(response);
  return response.ok ? playbackSourcesOf(answer) : undefined;
}
