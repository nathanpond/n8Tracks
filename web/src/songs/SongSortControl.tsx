import { Group, NativeSelect } from '@mantine/core';
import { defaultDirection, type SongQuery, type SongSort, type SortDirection } from '../api/songs';
import { directionLabel, SORT_LABELS, sortsOffered } from './songSortRules';

/**
 * The Songs table's Sort control (#226): every key the list sorts by, those without a column
 * (last Generation date) included, and Relevance while searching; and the direction, in words for
 * the key (relevance has none). Choosing a key starts it in its own first direction. The column
 * headers ({@link SongsTable}) change the same values.
 */
export function SongSortControl({
  query,
  onChange,
}: {
  query: SongQuery;
  onChange: (sort: SongSort, direction: SortDirection) => void;
}) {
  const sort = query.sort;
  return (
    <Group gap="md" align="flex-end" role="group" aria-label="Sort" data-testid="songs-sort">
      <NativeSelect
        label="Sort by"
        value={sort}
        data={sortsOffered(query).map((value) => ({ value, label: SORT_LABELS[value] }))}
        onChange={(event) => {
          const next = event.currentTarget.value as SongSort;
          onChange(next, defaultDirection(next));
        }}
        w={{ base: '100%', xs: 220 }}
      />
      {sort !== 'relevance' && (
        <NativeSelect
          label="Order"
          value={query.direction}
          data={(['asc', 'desc'] as const).map((value) => ({
            value,
            label: directionLabel(sort, value) ?? value,
          }))}
          onChange={(event) => {
            onChange(sort, event.currentTarget.value === 'asc' ? 'asc' : 'desc');
          }}
          w={{ base: '100%', xs: 220 }}
        />
      )}
    </Group>
  );
}
