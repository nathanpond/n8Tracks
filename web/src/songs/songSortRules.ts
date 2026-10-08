import type { SongQuery, SongSort, SortDirection } from '../api/songs';

/** What each sort is called, in the control and on the column headers that have one. */
export const SORT_LABELS: Record<SongSort, string> = {
  relevance: 'Relevance',
  title: 'Title',
  created: 'Created',
  updated: 'Updated',
  rating: 'Rating',
  state: 'Workflow state',
  lastGeneration: 'Last Generation date',
  audioFiles: 'Audio files',
};

/** The sorts the control offers, in this order; relevance only while searching (#226). */
const SORTS: readonly SongSort[] = [
  'relevance',
  'title',
  'created',
  'updated',
  'rating',
  'state',
  'lastGeneration',
  'audioFiles',
];

/** How each direction of a sort reads: ascending first, then descending. */
const DIRECTION_LABELS: Record<Exclude<SongSort, 'relevance'>, Record<SortDirection, string>> = {
  title: { asc: 'A to Z', desc: 'Z to A' },
  created: { asc: 'Oldest first', desc: 'Newest first' },
  updated: { asc: 'Oldest first', desc: 'Newest first' },
  rating: { asc: 'Lowest first', desc: 'Highest first' },
  state: { asc: 'Workflow order', desc: 'Reverse workflow order' },
  lastGeneration: { asc: 'Oldest first', desc: 'Newest first' },
  audioFiles: { asc: 'Fewest first', desc: 'Most first' },
};

/** The sorts offered for `query`: every key, with relevance first only while searching. */
export function sortsOffered(query: Pick<SongQuery, 'search'>): SongSort[] {
  return SORTS.filter((sort) => sort !== 'relevance' || query.search !== undefined);
}

/** How `direction` of `sort` reads ("Newest first"); relevance has none. */
export function directionLabel(sort: SongSort, direction: SortDirection): string | undefined {
  return sort === 'relevance' ? undefined : DIRECTION_LABELS[sort][direction];
}
