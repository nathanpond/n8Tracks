import { useCallback, useEffect, useRef, useState } from 'react';
import {
  clearSelectedGeneration,
  selectGeneration,
  setGenerationState,
  type Generation,
  type GenerationState,
} from '../api/generations';
import type { SaveResult } from '../api/saves';
import type { Song } from '../api/songs';

/** What the page says when a choice did not go through. */
const FAILED_SUFFIX =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

/** The user's choices about a Song's Generations, from the Generation panel and rows. */
export interface GenerationChoices {
  /** Archives or reactivates a Generation; its Version is not touched. */
  setState: (generation: Generation, state: GenerationState) => void;
  /** Makes `generation` the Song's Selected Generation, replacing any other. */
  select: (generation: Generation) => void;
  /** Leaves the Song with no Selected Generation. */
  clear: () => void;
  /** Whether a choice is on its way to n8Tracks: the controls wait for it. */
  busy: boolean;
}

/**
 * Sends `write` based on `revision`; a write refused because the record changed elsewhere (a
 * rating, or an edit of the Song) is sent once more based on the revision it has now, since the
 * user's choice does not depend on what changed.
 */
async function withOneRetry<T extends { revision: number }>(
  revision: number,
  write: (revision: number) => Promise<SaveResult<T>>,
): Promise<SaveResult<T>> {
  const first = await write(revision);
  return first.kind === 'conflict' ? write(first.current.revision) : first;
}

/**
 * Archiving and reactivating Generations and choosing the Song's Selected Generation (#120). A
 * state change keeps the Song's one cached Generation list in step (`update`) and, for the Selected
 * Generation, the state the Song's header shows (`onSong`); choosing or clearing gives the page the
 * Song as n8Tracks answers it and marks the chosen Generation in the list (`markSelected`). Nothing
 * else changes. `onProblem` says why a choice was not saved (undefined clears it).
 */
export function useGenerationChoices({
  song,
  onSong,
  update,
  markSelected,
  onProblem,
}: {
  song: Song;
  onSong: (song: Song) => void;
  update: (id: string, change: (generation: Generation) => Generation) => void;
  markSelected: (id: string | null) => void;
  onProblem: (problem: string | undefined) => void;
}): GenerationChoices {
  const [busy, setBusy] = useState(false);
  // The Song as the page has it now: a choice answered after an edit of the Song builds on that.
  const latest = useRef(song);
  useEffect(() => {
    latest.current = song;
  }, [song]);

  const setState = useCallback(
    (generation: Generation, state: GenerationState) => {
      setBusy(true);
      onProblem(undefined);
      void withOneRetry(generation.revision, (revision) =>
        setGenerationState(generation.id, state, revision),
      ).then((result) => {
        setBusy(false);
        if (result.kind !== 'saved') {
          onProblem(
            `${generation.shortcode} was not ${state === 'archived' ? 'archived' : 'reactivated'}: ${FAILED_SUFFIX}`,
          );
          return;
        }
        const saved = result.record;
        // The comments shown are kept: each comment write already brought its own answer back.
        update(saved.id, (current) => ({ ...saved, comments: current.comments }));
        const chosen = latest.current.selectedGeneration;
        if (chosen?.id === saved.id) {
          onSong({
            ...latest.current,
            selectedGeneration: { ...chosen, state: saved.state, remoteState: saved.remoteState },
          });
        }
      });
    },
    [onProblem, onSong, update],
  );

  const choose = useCallback(
    (generation: Generation | null) => {
      setBusy(true);
      onProblem(undefined);
      const songId = latest.current.id;
      void withOneRetry(latest.current.revision, (revision) =>
        generation === null
          ? clearSelectedGeneration(songId, revision)
          : selectGeneration(songId, generation.id, revision),
      ).then((result) => {
        setBusy(false);
        if (result.kind !== 'saved') {
          onProblem(
            generation === null
              ? `The selection was not cleared: ${FAILED_SUFFIX}`
              : `${generation.shortcode} was not selected: ${FAILED_SUFFIX}`,
          );
          return;
        }
        onSong(result.record);
        markSelected(result.record.selectedGeneration?.id ?? null);
      });
    },
    [markSelected, onProblem, onSong],
  );

  const select = useCallback(
    (generation: Generation) => {
      choose(generation);
    },
    [choose],
  );
  const clear = useCallback(() => {
    choose(null);
  }, [choose]);

  return { setState, select, clear, busy };
}
