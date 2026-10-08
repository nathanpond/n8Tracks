import { useEffect, useState } from 'react';
import { readFilterValues, type FilterValue, type FilterValueKind } from '../api/songs';

/** What a chosen value is called: its name once read, "Deleted item" when it no longer exists. */
export const DELETED_ITEM = 'Deleted item';

/**
 * The names of the values of `kind` with `ids` (#225), read once each set of IDs: a map from ID to
 * name, holding {@link DELETED_ITEM} for an ID the catalog no longer has. Undefined while reading
 * (or when the API did not answer), so a chip can say it is loading rather than deleted.
 */
export function useFilterValueNames(
  kind: FilterValueKind,
  ids: readonly string[],
): ReadonlyMap<string, FilterValue> | undefined {
  const key = ids.join('\n');
  const [names, setNames] = useState<{ key: string; values: Map<string, FilterValue> } | null>(
    null,
  );

  useEffect(() => {
    if (key === '') {
      return;
    }
    const controller = new AbortController();
    const wanted = key.split('\n');
    void readFilterValues(kind, { ids: wanted }, controller.signal).then((found) => {
      if (controller.signal.aborted || found === undefined) {
        return;
      }
      const values = new Map<string, FilterValue>();
      for (const id of wanted) {
        values.set(id, found.find((value) => value.id === id) ?? { id, name: DELETED_ITEM });
      }
      setNames({ key, values });
    });
    return () => {
      controller.abort();
    };
  }, [kind, key]);

  if (key === '') {
    return new Map();
  }
  return names?.key === key ? names.values : undefined;
}
