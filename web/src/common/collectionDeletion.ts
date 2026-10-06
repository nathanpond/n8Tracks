import { useState } from 'react';
import { useLocation, useNavigate } from 'react-router';

/**
 * What can be deleted this way: a collection of Songs, or an Artist credited on them, never the
 * Songs themselves.
 */
export type CollectionNoun = 'Album' | 'Playlist' | 'Artist';

/**
 * What the list page is handed after a deletion, as router state, to tell the user: the noun, the
 * title (an Artist's name), and, when given, what became of the rest (otherwise "Its Songs were
 * not deleted.").
 */
export interface DeletedCollectionState {
  deletedCollection: { noun: CollectionNoun; title: string; note?: string };
}

function isDeletedCollectionState(value: unknown): value is DeletedCollectionState {
  if (typeof value !== 'object' || value === null || !('deletedCollection' in value)) {
    return false;
  }
  const deleted = value.deletedCollection;
  return (
    typeof deleted === 'object' &&
    deleted !== null &&
    'noun' in deleted &&
    (deleted.noun === 'Album' || deleted.noun === 'Playlist' || deleted.noun === 'Artist') &&
    'title' in deleted &&
    typeof deleted.title === 'string' &&
    (!('note' in deleted) || deleted.note === undefined || typeof deleted.note === 'string')
  );
}

/** "3 Songs", "1 Song", "no Songs". */
export function songCountText(count: number): string {
  if (count === 0) {
    return 'no Songs';
  }
  return `${count.toLocaleString('en-US')} ${count === 1 ? 'Song' : 'Songs'}`;
}

/**
 * The record deleted just before the list page opened, from its router state, and a way to dismiss
 * the notice (which also forgets it in this history entry, so going back does not show it again).
 */
export function useDeletedCollection(): [
  DeletedCollectionState['deletedCollection'] | undefined,
  () => void,
] {
  const location = useLocation();
  const navigate = useNavigate();
  const [deleted, setDeleted] = useState(() =>
    isDeletedCollectionState(location.state) ? location.state.deletedCollection : undefined,
  );
  const dismiss = () => {
    setDeleted(undefined);
    void navigate({ search: location.search }, { replace: true, state: null });
  };
  return [deleted, dismiss];
}
