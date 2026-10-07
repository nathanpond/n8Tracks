import { useCallback, useRef } from 'react';
import { rateGeneration, type Generation } from '../api/generations';

/** What became of a rating write that did not go through: changed elsewhere, or failed. */
export type RatingProblem =
  { kind: 'conflict'; generation: Generation } | { kind: 'failed'; generation: Generation };

/** One Generation's rating writes: the rating last asked for, the revision to send next, and the Generation before them. */
interface Pending {
  wanted: number | null;
  revision: number;
  base: Generation;
}

/**
 * Rating Generations from the Song page. A rating shows at once (`update` changes the one cached
 * copy the row and the panel share), and is sent to n8Tracks one write at a time per Generation:
 * a change asked for while a write is out is sent when it returns, so the latest value wins. A
 * write refused because the Generation changed elsewhere (409) is sent again once with the current
 * revision; refused again, or failed, the Generation is shown as n8Tracks has it and `onProblem` says why.
 */
export function useRateGeneration(
  update: (id: string, change: (generation: Generation) => Generation) => void,
  onProblem: (problem: RatingProblem) => void,
  reload: () => void,
): (generation: Generation, rating: number | null) => void {
  const pending = useRef(new Map<string, Pending>());

  const send = useCallback(
    async (id: string, entry: Pending) => {
      let retried = false;
      for (;;) {
        const wanted = entry.wanted;
        const result = await rateGeneration(id, wanted, entry.revision);
        if (result.kind === 'saved') {
          entry.revision = result.record.revision;
          if (entry.wanted !== wanted) {
            continue;
          }
          // The comments shown are kept: each comment write already brought its own answer back.
          const saved = result.record;
          update(id, (current) => ({ ...saved, comments: current.comments }));
          break;
        }
        if (result.kind === 'conflict') {
          const current = result.current;
          if (!retried) {
            retried = true;
            entry.revision = current.revision;
            continue;
          }
          update(id, () => current);
          onProblem({ kind: 'conflict', generation: current });
          break;
        }
        onProblem({ kind: 'failed', generation: entry.base });
        reload();
        break;
      }
      pending.current.delete(id);
    },
    [update, onProblem, reload],
  );

  return useCallback(
    (generation: Generation, rating: number | null) => {
      update(generation.id, (current) => ({ ...current, rating }));
      const entry = pending.current.get(generation.id);
      if (entry !== undefined) {
        entry.wanted = rating;
        return;
      }
      const started: Pending = {
        wanted: rating,
        revision: generation.revision,
        base: generation,
      };
      pending.current.set(generation.id, started);
      void send(generation.id, started);
    },
    [update, send],
  );
}
