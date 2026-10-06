import { apiFetch } from './client';
import { ifMatch } from './saves';
import { body, isRecord } from './songs';

/**
 * How deleting an Album or a Playlist ended: `deleted`; `conflict` when it changed since it was
 * read (with the record as it is now, its Song count included); `gone` when it is no longer there;
 * `failed` otherwise. Nothing was deleted unless the kind is `deleted`, and no Song ever is.
 */
export type DeleteCollectionResult<T> =
  { kind: 'deleted' } | { kind: 'conflict'; current: T } | { kind: 'gone' } | { kind: 'failed' };

/** Deletes the Album or Playlist at `path`, based on the revision it was read at. */
export async function deleteCollection<T>(
  path: string,
  revision: number,
  accept: (answer: unknown) => T | undefined,
): Promise<DeleteCollectionResult<T>> {
  try {
    const response = await apiFetch(path, {
      method: 'DELETE',
      headers: { 'If-Match': ifMatch(revision) },
    });
    if (response.status === 204) {
      return { kind: 'deleted' };
    }
    const answer = await body(response);
    if (response.status === 409 && isRecord(answer) && answer.code === 'revision_conflict') {
      const current = accept(answer.current);
      if (current !== undefined) {
        return { kind: 'conflict', current };
      }
    }
    return response.status === 404 ? { kind: 'gone' } : { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}
