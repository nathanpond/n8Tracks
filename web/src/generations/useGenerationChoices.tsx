import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  clearSelectedGeneration,
  selectGeneration,
  setGenerationState,
  type Generation,
  type GenerationState,
} from '../api/generations';
import type { FieldValue, SaveResult } from '../api/saves';
import type { Song } from '../api/songs';
import { ConflictValue } from '../common/ConflictDialog';
import { useRevisionedSave, type SavedField } from '../common/useRevisionedSave';

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
  /** The conflict dialog a Select or Clear refused by a newer selection asks with: render it once. */
  dialog: ReactNode;
}

/** The compared field of a Select or Clear: the Selected Generation, by ID. */
const SELECTION = 'selectedGeneration';

/** Notes the shortcode of a Generation a selection may name, by its ID. */
function remember(
  names: Map<string, string>,
  generation: { id: string; shortcode: string } | null | undefined,
) {
  if (generation) {
    names.set(generation.id, generation.shortcode);
  }
}

/**
 * Sends `write` based on `revision`; a state change refused because the Generation changed
 * elsewhere (a rating, a comment) is sent once more based on the revision it has now. Select and
 * Clear do not come here: they compare what changed first ({@link useRevisionedSave}).
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
 *
 * Select and Clear go through the shared save helper (project conventions, "Concurrency"): when the
 * Song changed elsewhere, only a change of its Selected Generation stops the choice, and then the
 * conflict dialog shows the two selections side by side; any other change (a title, a rating) is
 * retried on the Song as it is now without asking (#317).
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

  // Shortcodes of the Generations a selection may name, so the conflict dialog can show them.
  const names = useRef(new Map<string, string>());
  useEffect(() => {
    remember(names.current, song.selectedGeneration);
  }, [song.selectedGeneration]);

  const fields = useMemo(
    (): SavedField<Song>[] => [
      {
        key: SELECTION,
        label: 'Selected Generation',
        read: (record) => record.selectedGeneration?.id ?? null,
        show: (value: FieldValue) => (
          <ConflictValue value={value === null ? 'None' : (names.current.get(value) ?? value)} />
        ),
      },
    ],
    [],
  );
  const send = useCallback((base: Song, edit: Readonly<Record<string, FieldValue>>) => {
    const chosen = edit[SELECTION] ?? null;
    return chosen === null
      ? clearSelectedGeneration(base.id, base.revision)
      : selectGeneration(base.id, chosen, base.revision);
  }, []);
  const onRecord = useCallback(
    (record: Song) => {
      remember(names.current, record.selectedGeneration);
      onSong(record);
      markSelected(record.selectedGeneration?.id ?? null);
    },
    [markSelected, onSong],
  );
  const { save, dialog } = useRevisionedSave({
    record: song,
    onRecord,
    fields,
    send,
    subject: 'This Song',
  });

  const choose = useCallback(
    (generation: Generation | null) => {
      setBusy(true);
      onProblem(undefined);
      remember(names.current, generation);
      void save(SELECTION, generation?.id ?? null).then((outcome) => {
        setBusy(false);
        // Reloaded, or back to the page: the Song as it is now is already shown.
        if (
          outcome.kind === 'saved' ||
          outcome.kind === 'reloaded' ||
          outcome.kind === 'keep-editing'
        ) {
          return;
        }
        onProblem(
          generation === null
            ? `The selection was not cleared: ${FAILED_SUFFIX}`
            : `${generation.shortcode} was not selected: ${FAILED_SUFFIX}`,
        );
      });
    },
    [onProblem, save],
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

  return { setState, select, clear, busy, dialog };
}
