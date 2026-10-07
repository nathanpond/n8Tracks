import { useCallback, useEffect, useState } from 'react';
import { isArtwork, type Artwork } from './artwork';
import { apiFetch } from './client';
import {
  failureOf,
  ifMatch,
  isErrorMap,
  patchWithRevision,
  writeWithRevision,
  type FailureReason,
  type SaveResult,
} from './saves';
import { body, isRecord, isSong, type LoadState, type Song } from './songs';

const SONGS_PATH = 'api/v1/songs';
const GENERATIONS_PATH = 'api/v1/generations';

/** The longest comment kept, counted as the API counts it (UTF-16 code units, `String.length`). */
export const COMMENT_MAXIMUM_LENGTH = 2000;

/** The highest rating: five stars. */
export const RATING_MAXIMUM = 5;

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
 * A comment the user keeps on a Generation: plain text, when it was written, and when its text last
 * changed (`editedAt`, null when it never has). It has its own revision. Times are UTC ISO 8601.
 */
export interface GenerationComment {
  id: string;
  text: string;
  createdAt: string;
  editedAt: string | null;
  revision: number;
}

/**
 * A Generation as the Song's Generation list answers it. Every field Suno reported is null for a
 * Generation with no Suno data. `rating` (1 to 5, or null) and `comments` (oldest first) are the
 * user's own judgement. Times are UTC ISO 8601.
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
  comments: GenerationComment[];
  /** Its cover image in n8Tracks' artwork store (#121), shown whole (`crop` is always null); null when it has none. */
  artwork: Artwork | null;
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

/** A comment from the API's answer, or undefined when the answer is not one. */
export function commentOf(value: unknown): GenerationComment | undefined {
  if (
    !isRecord(value) ||
    typeof value.id !== 'string' ||
    typeof value.text !== 'string' ||
    typeof value.createdAt !== 'string' ||
    !textOrNull(value.editedAt) ||
    typeof value.revision !== 'number'
  ) {
    return undefined;
  }
  const { id, text, createdAt, editedAt, revision } = value;
  return { id, text, createdAt, editedAt, revision };
}

/** The comments the answer embeds, or undefined when one of them is not a comment. */
function commentsOf(value: unknown): GenerationComment[] | undefined {
  if (!Array.isArray(value)) {
    return undefined;
  }
  const comments = value.map(commentOf);
  return comments.every((comment) => comment !== undefined) ? comments : undefined;
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
  const rating = value.rating;
  const comments = commentsOf(value.comments);
  // An answer from before #121 has no image field: it has no image.
  const artwork = value.artwork ?? null;
  if (
    !numberOrNull(rating) ||
    comments === undefined ||
    (artwork !== null && !isArtwork(artwork))
  ) {
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
    comments,
    artwork,
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

/**
 * How long after it was recorded the extension watches a Generation for Suno to finish it (#154):
 * ten minutes. One still generating after that is filled in by the next sync.
 */
export const COMPLETION_WATCH_MS = 10 * 60_000;

/** What a Generation still generating after the completion watch says (#154). */
export const STILL_GENERATING = 'Still generating in Suno: sync to update';

/**
 * Whether the Generation is still generating ten minutes after it was recorded, when the extension
 * no longer watches for it (#154): a sync brings its clip.
 */
export function isStillGenerating(generation: Generation, now: number = Date.now()): boolean {
  return isGenerating(generation) && now - Date.parse(generation.createdAt) >= COMPLETION_WATCH_MS;
}

/** Whether Suno's clip ended in error (#154): the Generation shows as Failed, and can be deleted. */
export function isFailed(generation: Generation): boolean {
  return generation.providerStatus === 'error';
}

/**
 * A clip's length: "Generating" while Suno is still making it ("Still generating in Suno: sync to
 * update" ten minutes after it was recorded), or "Unknown" when Suno gave none.
 */
export function generationDuration(generation: Generation, now: number = Date.now()): string {
  if (isGenerating(generation)) {
    return isStillGenerating(generation, now) ? STILL_GENERATING : 'Generating';
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

function generationPath(reference: string): string {
  return `${GENERATIONS_PATH}/${encodeURIComponent(reference)}`;
}

function commentPath(generation: string, comment: string): string {
  return `${generationPath(generation)}/comments/${encodeURIComponent(comment)}`;
}

/**
 * Sets (1 to 5) or clears (null) a Generation's rating, based on the Generation's `revision`. A
 * stale revision is a conflict with the Generation as it is now, its comments included.
 */
export function rateGeneration(
  reference: string,
  rating: number | null,
  revision: number,
): Promise<SaveResult<Generation>> {
  return patchWithRevision(generationPath(reference), revision, { rating }, generationOf);
}

/**
 * Archives (`archived`) or reactivates (`active`) a Generation, based on the Generation's
 * `revision`; its Version's own state is not touched. A stale revision is a conflict with the
 * Generation as it is now.
 */
export function setGenerationState(
  reference: string,
  state: GenerationState,
  revision: number,
): Promise<SaveResult<Generation>> {
  return patchWithRevision(generationPath(reference), revision, { state }, generationOf);
}

function selectedGenerationPath(song: string): string {
  return `${SONGS_PATH}/${encodeURIComponent(song)}/selected-generation`;
}

/**
 * Makes `generation` (its ID or shortcode) the Song's Selected Generation, replacing any other,
 * based on the Song's `revision`: the Song as it is now, or a conflict with it.
 */
export function selectGeneration(
  song: string,
  generation: string,
  revision: number,
): Promise<SaveResult<Song>> {
  return writeWithRevision(
    'PUT',
    selectedGenerationPath(song),
    revision,
    { generation },
    (answer) => (isSong(answer) ? answer : undefined),
  );
}

/** How picking a Generation's image as the Song's artwork ended. A refusal carries the API's reason. */
export type PickArtworkResult =
  | { kind: 'saved'; record: Song }
  | { kind: 'conflict'; current: Song }
  | { kind: 'refused'; message: string }
  | { kind: 'failed'; reason: FailureReason };

/**
 * Makes the image of `generation` (its ID or shortcode, one of the Song's) the Song's own artwork,
 * based on the Song's `revision` (#121): n8Tracks copies it, so the Song keeps it whatever happens to
 * the Generation. The Song as it is now, a conflict with it, or a refusal saying why (the Generation
 * has no image, is another Song's, or its image is no longer stored).
 */
export async function pickGenerationArtwork(
  song: string,
  generation: string,
  revision: number,
): Promise<PickArtworkResult> {
  try {
    const response = await apiFetch(
      `${SONGS_PATH}/${encodeURIComponent(song)}/artwork/from-generation`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'If-Match': ifMatch(revision) },
        body: JSON.stringify({ generation }),
      },
    );
    const answer = await body(response);
    if (response.ok) {
      return isSong(answer)
        ? { kind: 'saved', record: answer }
        : { kind: 'failed', reason: 'server' };
    }
    if (response.status === 409 && isRecord(answer) && answer.code === 'revision_conflict') {
      return isSong(answer.current)
        ? { kind: 'conflict', current: answer.current }
        : { kind: 'failed', reason: 'server' };
    }
    if (
      (response.status === 409 || response.status === 422) &&
      isRecord(answer) &&
      typeof answer.title === 'string'
    ) {
      return { kind: 'refused', message: answer.title };
    }
    return { kind: 'failed', reason: failureOf(response.status, answer) };
  } catch {
    return { kind: 'failed', reason: 'unreachable' };
  }
}

/**
 * What the Song a Selected Generation leaves selects instead (#123): another of its Generations (ID
 * or shortcode), or a workflow state (its ID) to move the Song to, left with no Selected Generation.
 */
export type SelectionChoice =
  { replacementGeneration: string } | { workflowState: string } | Record<string, never>;

/** How creating a new Song from a Generation ended. */
export type MoveToNewSongResult =
  | { kind: 'moved'; song: Song; generation: Generation; alias: string }
  | { kind: 'conflict'; current: Generation }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  /** The Generation is its Song's Selected Generation: say what that Song selects instead. */
  | { kind: 'choice-required' }
  | { kind: 'failed'; reason: FailureReason };

/**
 * Moves `generation` (never a copy) into a new Song titled `title`, whose Version 1 holds a copy of
 * its Version's creation inputs, based on the Generation's `revision` (#123). Its old shortcode keeps
 * finding it. When it is its Song's Selected Generation, `choice` says what that Song selects instead.
 */
export async function moveGenerationToNewSong(
  generation: Generation,
  title: string,
  choice: SelectionChoice,
): Promise<MoveToNewSongResult> {
  try {
    const response = await apiFetch(`${generationPath(generation.id)}/move-to-new-song`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'If-Match': ifMatch(generation.revision) },
      body: JSON.stringify({ title, ...choice }),
    });
    const answer = await body(response);
    if (response.ok) {
      const moved = isRecord(answer) ? generationOf(answer.generation) : undefined;
      return isRecord(answer) &&
        isSong(answer.song) &&
        moved !== undefined &&
        typeof answer.alias === 'string'
        ? { kind: 'moved', song: answer.song, generation: moved, alias: answer.alias }
        : { kind: 'failed', reason: 'server' };
    }
    if (response.status === 409 && isRecord(answer) && answer.code === 'revision_conflict') {
      const current = generationOf(answer.current);
      return current === undefined
        ? { kind: 'failed', reason: 'server' }
        : { kind: 'conflict', current };
    }
    if (
      response.status === 422 &&
      isRecord(answer) &&
      answer.code === 'selection_choice_required'
    ) {
      return { kind: 'choice-required' };
    }
    if (response.status === 422 && isRecord(answer) && isErrorMap(answer.errors)) {
      return { kind: 'invalid', errors: answer.errors };
    }
    return { kind: 'failed', reason: failureOf(response.status, answer) };
  } catch {
    return { kind: 'failed', reason: 'unreachable' };
  }
}

/**
 * What deleting a Generation would take with it (#124): whether it is its Song's Selected Generation
 * and, if so, the Song's other Generations it may select instead (`replacements`); its comments and
 * its own Suno artwork (`artworkCount`, none or one), deleted with it; how many Versions use it as a
 * source (they will show it as Deleted); and the `revision` to delete it under.
 */
export interface GenerationDeletionImpact {
  id: string;
  shortcode: string;
  isSelected: boolean;
  replacements: Generation[];
  commentCount: number;
  artworkCount: number;
  sourceVersionCount: number;
  revision: number;
}

function deletionImpactOf(value: unknown): GenerationDeletionImpact | undefined {
  if (
    !isRecord(value) ||
    typeof value.id !== 'string' ||
    typeof value.shortcode !== 'string' ||
    typeof value.isSelected !== 'boolean' ||
    typeof value.commentCount !== 'number' ||
    typeof value.artworkCount !== 'number' ||
    typeof value.sourceVersionCount !== 'number' ||
    typeof value.revision !== 'number'
  ) {
    return undefined;
  }
  const replacements = generationsOf({ items: value.replacements });
  return replacements === undefined
    ? undefined
    : {
        id: value.id,
        shortcode: value.shortcode,
        isSelected: value.isSelected,
        replacements,
        commentCount: value.commentCount,
        artworkCount: value.artworkCount,
        sourceVersionCount: value.sourceVersionCount,
        revision: value.revision,
      };
}

/** What a read of a Generation's deletion impact found: it, `gone` (no such Generation), or `failed`. */
export type GenerationDeletionImpactResult =
  { kind: 'found'; impact: GenerationDeletionImpact } | { kind: 'gone' } | { kind: 'failed' };

/** Reads what deleting the Generation with `id` would do now. */
export async function fetchGenerationDeletionImpact(
  id: string,
  signal?: AbortSignal,
): Promise<GenerationDeletionImpactResult> {
  try {
    const response = await apiFetch(`${generationPath(id)}/deletion-impact`, { signal });
    const answer = await body(response);
    const impact = response.ok ? deletionImpactOf(answer) : undefined;
    if (impact !== undefined) {
      return { kind: 'found', impact };
    }
    return response.status === 404 ? { kind: 'gone' } : { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/**
 * How deleting a Generation ended: `deleted` with its Song as it is now; `conflict` when it changed
 * since its impact was read; `choice-required` when it has become the Song's Selected Generation
 * meanwhile; `invalid` when the choice was refused (errors by field); `gone` when it is no longer
 * there; `failed` otherwise. Only `deleted` changed anything.
 */
export type DeleteGenerationResult =
  | { kind: 'deleted'; song: Song }
  | { kind: 'conflict' }
  | { kind: 'choice-required' }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'gone' }
  | { kind: 'failed' };

/**
 * Deletes a Generation from n8Tracks (#124; nothing in Suno changes), based on the `revision` its
 * impact was read at. When it is its Song's Selected Generation, `choice` says what the Song selects instead.
 */
export async function deleteGeneration(
  generation: { id: string; revision: number },
  choice: SelectionChoice,
): Promise<DeleteGenerationResult> {
  try {
    const sent = Object.keys(choice).length > 0;
    const response = await apiFetch(generationPath(generation.id), {
      method: 'DELETE',
      headers: {
        'If-Match': ifMatch(generation.revision),
        ...(sent ? { 'Content-Type': 'application/json' } : {}),
      },
      ...(sent ? { body: JSON.stringify(choice) } : {}),
    });
    const answer = await body(response);
    if (response.ok) {
      return isRecord(answer) && isSong(answer.song)
        ? { kind: 'deleted', song: answer.song }
        : { kind: 'failed' };
    }
    if (response.status === 404) {
      return { kind: 'gone' };
    }
    if (response.status === 409 && isRecord(answer) && answer.code === 'revision_conflict') {
      return { kind: 'conflict' };
    }
    if (response.status === 422 && isRecord(answer)) {
      if (answer.code === 'selection_choice_required') {
        return { kind: 'choice-required' };
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

/** Leaves the Song with no Selected Generation, based on its `revision`. */
export function clearSelectedGeneration(song: string, revision: number): Promise<SaveResult<Song>> {
  return writeWithRevision('DELETE', selectedGenerationPath(song), revision, undefined, (answer) =>
    isSong(answer) ? answer : undefined,
  );
}

/** Adds a comment to a Generation: the new comment, or why it was not added. */
export async function addComment(
  generation: string,
  text: string,
): Promise<
  | { kind: 'saved'; record: GenerationComment }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'failed'; reason: FailureReason }
> {
  try {
    const response = await apiFetch(`${generationPath(generation)}/comments`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text }),
    });
    const answer = await body(response);
    if (response.ok) {
      const record = commentOf(answer);
      return record === undefined
        ? { kind: 'failed', reason: 'server' }
        : { kind: 'saved', record };
    }
    if (response.status === 422 && isRecord(answer) && isErrorMap(answer.errors)) {
      return { kind: 'invalid', errors: answer.errors };
    }
    return { kind: 'failed', reason: failureOf(response.status, answer) };
  } catch {
    return { kind: 'failed', reason: 'unreachable' };
  }
}

/** Replaces a comment's text, based on the comment's own revision. */
export function editComment(
  generation: string,
  comment: GenerationComment,
  text: string,
): Promise<SaveResult<GenerationComment>> {
  return patchWithRevision(
    commentPath(generation, comment.id),
    comment.revision,
    { text },
    commentOf,
  );
}

/**
 * Deletes a comment, based on its own revision: `deleted` (also when it was already deleted
 * elsewhere, which is `gone`), a conflict with the comment as it is now, or why it failed.
 */
export async function deleteComment(
  generation: string,
  comment: GenerationComment,
): Promise<
  | { kind: 'deleted' }
  | { kind: 'gone' }
  | { kind: 'conflict'; current: GenerationComment }
  | { kind: 'failed'; reason: FailureReason }
> {
  try {
    const response = await apiFetch(commentPath(generation, comment.id), {
      method: 'DELETE',
      headers: { 'If-Match': ifMatch(comment.revision) },
    });
    if (response.status === 204) {
      return { kind: 'deleted' };
    }
    const answer = await body(response);
    if (response.status === 404 && isRecord(answer) && answer.code === 'not_found') {
      return { kind: 'gone' };
    }
    if (response.status === 409 && isRecord(answer) && answer.code === 'revision_conflict') {
      const current = commentOf(answer.current);
      if (current !== undefined) {
        return { kind: 'conflict', current };
      }
    }
    return { kind: 'failed', reason: failureOf(response.status, answer) };
  } catch {
    return { kind: 'failed', reason: 'unreachable' };
  }
}

function songGenerationsPath(reference: string): string {
  return `${SONGS_PATH}/${encodeURIComponent(reference)}/generations`;
}

/**
 * A Song's Generations (by its ID or shortcode) read once, as {@link useSongGenerations} reads them;
 * undefined when they cannot be read.
 */
export async function readSongGenerations(
  reference: string,
  signal?: AbortSignal,
): Promise<Generation[] | undefined> {
  try {
    const response = await apiFetch(songGenerationsPath(reference), { signal });
    return response.ok ? generationsOf(await body(response)) : undefined;
  } catch {
    return undefined;
  }
}

/**
 * A Song's Generations (by its ID or shortcode), every state included, by Version number in tree
 * order and then ordinal, as one request. While any of them is still being made in Suno the list is
 * read again every {@link GENERATING_REFRESH_MS}; a read again that fails keeps the list already
 * shown. `reload` reads it again now; `update` changes one Generation in the list in place (after a
 * rating, state, or comment write), so every view of it agrees; `markSelected` marks the Song's
 * Selected Generation (null for none) and unmarks every other.
 */
export function useSongGenerations(reference: string): {
  state: LoadState<Generation[]>;
  reload: () => void;
  update: (id: string, change: (generation: Generation) => Generation) => void;
  markSelected: (id: string | null) => void;
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

  // The one cached copy the table's rows and the panel both show: a write here changes it in place.
  const update = useCallback((id: string, change: (generation: Generation) => Generation) => {
    setLoaded((previous) =>
      previous.state.phase === 'ready'
        ? {
            ...previous,
            state: {
              phase: 'ready',
              data: previous.state.data.map((generation) =>
                generation.id === id ? change(generation) : generation,
              ),
            },
          }
        : previous,
    );
  }, []);

  const markSelected = useCallback((id: string | null) => {
    setLoaded((previous) =>
      previous.state.phase === 'ready'
        ? {
            ...previous,
            state: {
              phase: 'ready',
              data: previous.state.data.map((generation) =>
                generation.isSelected === (generation.id === id)
                  ? generation
                  : { ...generation, isSelected: generation.id === id },
              ),
            },
          }
        : previous,
    );
  }, []);

  return { state, reload, update, markSelected };
}
