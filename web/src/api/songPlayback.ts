import { apiFetch } from './client';
import type { GenerationState, RemoteState } from './generations';
import { body, isRecord, isSongPlaybackState, type SongPlaybackState } from './songs';

const SONGS_PATH = 'api/v1/songs';

/** A file the playback read (#212) names: its ID, name, format, length, and where its audio is served. */
export interface SongPlaybackFile {
  id: string;
  fileName: string;
  format: string;
  durationSeconds: number | null;
  contentUrl: string;
}

/**
 * One entry of the chooser (#219): one of the Song's Generations (its Version number, the user's
 * rating, Suno's duration, its states) or one of its available Song-level files; whether it can play,
 * and why not (a playback reason code) when it cannot.
 */
export type SongPlaybackCandidate =
  | {
      kind: 'generation';
      generation: { id: string; shortcode: string };
      versionNumber: string;
      rating: number | null;
      durationSeconds: number | null;
      state: GenerationState;
      remoteState: RemoteState;
      playable: boolean;
      reason: string | null;
    }
  | { kind: 'file'; audioFile: SongPlaybackFile; playable: boolean; reason: string | null };

/**
 * What Play on a Song does, as `GET /songs/{reference}/playback` answers it (#212, #219): the file
 * that plays (`source` `local`) or none, why (a playback reason code), the Selected Generation it
 * comes from (or that has nothing to play, with its Suno ID), the state, and the chooser's candidates
 * when it needs a choice.
 */
export interface SongPlaybackAnswer {
  source: 'local' | 'none';
  audioFile: SongPlaybackFile | null;
  reason: string;
  generation: { id: string; shortcode: string; sunoId: string | null } | null;
  state: SongPlaybackState;
  candidates: SongPlaybackCandidate[];
}

function numberOrNull(value: unknown): value is number | null {
  return value === null || typeof value === 'number';
}

function textOrNull(value: unknown): value is string | null {
  return value === null || typeof value === 'string';
}

export function songPlaybackFileOf(value: unknown): SongPlaybackFile | undefined {
  return isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.fileName === 'string' &&
    typeof value.format === 'string' &&
    numberOrNull(value.durationSeconds) &&
    typeof value.contentUrl === 'string'
    ? {
        id: value.id,
        fileName: value.fileName,
        format: value.format,
        durationSeconds: value.durationSeconds,
        contentUrl: value.contentUrl,
      }
    : undefined;
}

function candidateOf(value: unknown): SongPlaybackCandidate | undefined {
  if (!isRecord(value) || typeof value.playable !== 'boolean' || !textOrNull(value.reason)) {
    return undefined;
  }
  if (value.kind === 'file') {
    const audioFile = songPlaybackFileOf(value.audioFile);
    return audioFile === undefined
      ? undefined
      : { kind: 'file', audioFile, playable: value.playable, reason: value.reason };
  }
  const generation = value.generation;
  if (
    value.kind !== 'generation' ||
    !isRecord(generation) ||
    typeof generation.id !== 'string' ||
    typeof generation.shortcode !== 'string' ||
    typeof value.versionNumber !== 'string' ||
    !numberOrNull(value.rating) ||
    !numberOrNull(value.durationSeconds) ||
    (value.state !== 'active' && value.state !== 'archived') ||
    (value.remoteState !== 'present' &&
      value.remoteState !== 'trashed' &&
      value.remoteState !== 'missing')
  ) {
    return undefined;
  }
  return {
    kind: 'generation',
    generation: { id: generation.id, shortcode: generation.shortcode },
    versionNumber: value.versionNumber,
    rating: value.rating,
    durationSeconds: value.durationSeconds,
    state: value.state,
    remoteState: value.remoteState,
    playable: value.playable,
    reason: value.reason,
  };
}

function selectedOf(value: unknown): SongPlaybackAnswer['generation'] | undefined {
  if (value === null) {
    return null;
  }
  return isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.shortcode === 'string' &&
    textOrNull(value.sunoId)
    ? { id: value.id, shortcode: value.shortcode, sunoId: value.sunoId }
    : undefined;
}

/** The Song's playback answer, or undefined when it is not one. */
export function songPlaybackAnswerOf(value: unknown): SongPlaybackAnswer | undefined {
  if (
    !isRecord(value) ||
    (value.source !== 'local' && value.source !== 'none') ||
    typeof value.reason !== 'string' ||
    !isSongPlaybackState(value.state) ||
    !Array.isArray(value.candidates)
  ) {
    return undefined;
  }
  const audioFile = value.audioFile === null ? null : songPlaybackFileOf(value.audioFile);
  const generation = selectedOf(value.generation);
  const candidates = value.candidates.map(candidateOf);
  if (
    audioFile === undefined ||
    (value.source === 'local') !== (audioFile !== null) ||
    generation === undefined ||
    candidates.some((candidate) => candidate === undefined)
  ) {
    return undefined;
  }
  return {
    source: value.source,
    audioFile,
    reason: value.reason,
    generation,
    state: value.state,
    candidates: candidates.filter((candidate) => candidate !== undefined),
  };
}

/** Asks what Play on the Song (its ID or shortcode) does; undefined when the answer is not one. */
export async function readSongPlayback(
  reference: string,
  signal?: AbortSignal,
): Promise<SongPlaybackAnswer | undefined> {
  const response = await apiFetch(`${SONGS_PATH}/${encodeURIComponent(reference)}/playback`, {
    signal,
  });
  const answer = await body(response);
  return response.ok ? songPlaybackAnswerOf(answer) : undefined;
}

/** Why a Song's Play cannot start, in words, from the reason code (`no_generations`, `nothing_available`). */
export function songUnplayableText(reason: string | null): string {
  return reason === 'no_generations'
    ? 'Nothing to play: this Song has no Generations and no audio files.'
    : 'Nothing to play: none of this Song’s Generations or audio files can be played.';
}
