import { useCallback, useEffect, useState } from 'react';
import { apiFetch } from './client';
import { body, isRecord, type LoadState } from './songs';

const SONGS_PATH = 'api/v1/songs';

/** How often a Song's Generations are read again while one of them is still being made in Suno. */
export const GENERATING_REFRESH_MS = 10_000;

/** Where Suno shows a clip, by its Suno ID. */
export function sunoSongUrl(sunoId: string): string {
  return `https://suno.com/song/${encodeURIComponent(sunoId)}`;
}

/** A Generation's own state: the user's choice, independent of its Version's. */
export type GenerationState = 'active' | 'archived';

/** Whether Suno still lists the clip: `present`, `trashed` (in Suno's Trash), or `missing`. */
export type RemoteState = 'present' | 'trashed' | 'missing';

/**
 * A Generation as the Song's Generation list answers it. Every field Suno reported is null for a
 * Generation with no Suno data. `rating` (1 to 5, or null) and `commentCount` are the user's own
 * judgement: until the rating story adds them to the answer they read as null and 0. Times are UTC
 * ISO 8601.
 */
export interface Generation {
  id: string;
  shortcode: string;
  ordinal: number;
  song: { id: string; shortcode: string };
  version: { id: string; shortcode: string };
  sunoId: string | null;
  providerStatus: string | null;
  state: GenerationState;
  remoteState: RemoteState;
  title: string | null;
  durationSeconds: number | null;
  modelVersion: string | null;
  modelName: string | null;
  modelLabel: string | null;
  sunoCreatedAt: string | null;
  isSelected: boolean;
  createdAt: string;
  revision: number;
  rating: number | null;
  commentCount: number;
}

function isOwner(value: unknown): value is { id: string; shortcode: string } {
  return isRecord(value) && typeof value.id === 'string' && typeof value.shortcode === 'string';
}

function textOrNull(value: unknown): value is string | null {
  return value === null || typeof value === 'string';
}

function numberOrNull(value: unknown): value is number | null {
  return value === null || typeof value === 'number';
}

/** The comments the answer embeds, counted; 0 while the answer has none (before the comment story). */
function commentCountOf(value: Record<string, unknown>): number | undefined {
  if (typeof value.commentCount === 'number') {
    return value.commentCount;
  }
  if (value.comments === undefined) {
    return 0;
  }
  return Array.isArray(value.comments) ? value.comments.length : undefined;
}

/** A Generation from the API's answer, or undefined when the answer is not one. */
export function generationOf(value: unknown): Generation | undefined {
  if (
    !isRecord(value) ||
    typeof value.id !== 'string' ||
    typeof value.shortcode !== 'string' ||
    typeof value.ordinal !== 'number' ||
    !isOwner(value.song) ||
    !isOwner(value.version) ||
    !textOrNull(value.sunoId) ||
    !textOrNull(value.providerStatus) ||
    (value.state !== 'active' && value.state !== 'archived') ||
    (value.remoteState !== 'present' &&
      value.remoteState !== 'trashed' &&
      value.remoteState !== 'missing') ||
    !textOrNull(value.title) ||
    !numberOrNull(value.durationSeconds) ||
    !textOrNull(value.modelVersion) ||
    !textOrNull(value.modelName) ||
    !textOrNull(value.modelLabel) ||
    !textOrNull(value.sunoCreatedAt) ||
    typeof value.isSelected !== 'boolean' ||
    typeof value.createdAt !== 'string' ||
    typeof value.revision !== 'number'
  ) {
    return undefined;
  }
  const rating = value.rating ?? null;
  const commentCount = commentCountOf(value);
  if (!numberOrNull(rating) || commentCount === undefined) {
    return undefined;
  }
  return {
    id: value.id,
    shortcode: value.shortcode,
    ordinal: value.ordinal,
    song: { id: value.song.id, shortcode: value.song.shortcode },
    version: { id: value.version.id, shortcode: value.version.shortcode },
    sunoId: value.sunoId,
    providerStatus: value.providerStatus,
    state: value.state,
    remoteState: value.remoteState,
    title: value.title,
    durationSeconds: value.durationSeconds,
    modelVersion: value.modelVersion,
    modelName: value.modelName,
    modelLabel: value.modelLabel,
    sunoCreatedAt: value.sunoCreatedAt,
    isSelected: value.isSelected,
    createdAt: value.createdAt,
    revision: value.revision,
    rating,
    commentCount,
  };
}

function generationsOf(answer: unknown): Generation[] | undefined {
  if (!isRecord(answer) || !Array.isArray(answer.items)) {
    return undefined;
  }
  const items = answer.items.map(generationOf);
  return items.every((item) => item !== undefined) ? items : undefined;
}

/** Whether Suno is still making the Generation's clip (it was submitted, or it is streaming). */
export function isGenerating(generation: Generation): boolean {
  return generation.providerStatus === 'submitted' || generation.providerStatus === 'streaming';
}

/** The model Suno reported for the clip, in the most readable form it gave; null when it gave none. */
export function reportedModel(generation: Generation): string | null {
  return generation.modelLabel ?? generation.modelName ?? generation.modelVersion;
}

/** A clip's length as minutes and seconds (`3:07`), or hours too when it is that long (`1:02:05`). */
export function formatDuration(seconds: number): string {
  const whole = Math.max(0, Math.round(seconds));
  const hours = Math.floor(whole / 3600);
  const minutes = Math.floor((whole % 3600) / 60);
  const rest = String(whole % 60).padStart(2, '0');
  return hours > 0
    ? `${String(hours)}:${String(minutes).padStart(2, '0')}:${rest}`
    : `${String(minutes)}:${rest}`;
}

/** A clip's length, "Generating" while Suno is still making it, or "Unknown" when Suno gave none. */
export function generationDuration(generation: Generation): string {
  if (isGenerating(generation)) {
    return 'Generating';
  }
  return generation.durationSeconds === null
    ? 'Unknown'
    : formatDuration(generation.durationSeconds);
}

/** A rating as words: "4 of 5 stars", or "Not rated". */
export function ratingText(rating: number | null): string {
  return rating === null ? 'Not rated' : `${String(rating)} of 5 stars`;
}

/** The highest rating among `generations`, whatever their state; null when none is rated. */
export function highestRating(generations: readonly Generation[]): number | null {
  return generations.reduce<number | null>(
    (highest, generation) =>
      generation.rating !== null && (highest === null || generation.rating > highest)
        ? generation.rating
        : highest,
    null,
  );
}

/** Whether `reference` (a shortcode in any letter case, or a stable ID) names `generation`. */
export function isNamedBy(generation: Generation, reference: string): boolean {
  const key = reference.toLowerCase();
  return generation.shortcode.toLowerCase() === key || generation.id.toLowerCase() === key;
}

function songGenerationsPath(reference: string): string {
  return `${SONGS_PATH}/${encodeURIComponent(reference)}/generations`;
}

/**
 * A Song's Generations (by its ID or shortcode), every state included, by Version number in tree
 * order and then ordinal, as one request. While any of them is still being made in Suno the list is
 * read again every {@link GENERATING_REFRESH_MS}; a read again that fails keeps the list already
 * shown. `reload` reads it again now.
 */
export function useSongGenerations(reference: string): {
  state: LoadState<Generation[]>;
  reload: () => void;
} {
  const [loaded, setLoaded] = useState<{ reference: string; state: LoadState<Generation[]> }>({
    reference,
    state: { phase: 'loading' },
  });
  const [attempt, setAttempt] = useState(0);
  const state: LoadState<Generation[]> =
    loaded.reference === reference ? loaded.state : { phase: 'loading' };

  useEffect(() => {
    const controller = new AbortController();
    const settle = (next: LoadState<Generation[]>) => {
      if (controller.signal.aborted) {
        return;
      }
      // A list already shown stays when reading it again fails.
      setLoaded((previous) =>
        next.phase !== 'ready' &&
        previous.reference === reference &&
        previous.state.phase === 'ready'
          ? previous
          : { reference, state: next },
      );
    };
    const load = async () => {
      try {
        const response = await apiFetch(songGenerationsPath(reference), {
          signal: controller.signal,
        });
        const answer = await body(response);
        const data = response.ok ? generationsOf(answer) : undefined;
        if (data !== undefined) {
          settle({ phase: 'ready', data });
        } else {
          settle(
            response.status === 404 ? { phase: 'not-found', problem: answer } : { phase: 'error' },
          );
        }
      } catch {
        settle({ phase: 'error' });
      }
    };
    void load();
    return () => {
      controller.abort();
    };
  }, [reference, attempt]);

  // While a clip is being made, the list is read again after a while; each answer starts the wait anew.
  const generating = state.phase === 'ready' && state.data.some(isGenerating);
  useEffect(() => {
    if (!generating) {
      return undefined;
    }
    const timer = setTimeout(() => {
      setAttempt((previous) => previous + 1);
    }, GENERATING_REFRESH_MS);
    return () => {
      clearTimeout(timer);
    };
  }, [generating, loaded]);

  const reload = useCallback(() => {
    setLoaded((previous) =>
      previous.state.phase === 'ready' ? previous : { reference, state: { phase: 'loading' } },
    );
    setAttempt((previous) => previous + 1);
  }, [reference]);

  return { state, reload };
}
