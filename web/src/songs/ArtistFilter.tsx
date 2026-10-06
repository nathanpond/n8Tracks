import { MultiSelect } from '@mantine/core';
import { useEffect, useState } from 'react';
import { readArtist, searchArtists } from '../api/artists';
import { NO_ARTIST } from '../api/songs';

/** How many Artists the filter suggests for what is typed. */
const SUGGESTIONS = 20;

/**
 * The Artist filter: any number of Artists and "No Artist", matching Songs crediting any of them
 * as primary or featured (or credited to no one); none chosen means every Song. Artists are
 * suggested from the API as the user types (by name or alias), so a large list is never loaded
 * whole; the names of the Artists already chosen (from the page URL) are read once each.
 */
export function ArtistFilter({
  selected,
  onChange,
}: {
  selected: string[];
  onChange: (artists: string[]) => void;
}) {
  const [search, setSearch] = useState('');
  const [suggested, setSuggested] = useState<{ id: string; name: string }[]>([]);
  const [names, setNames] = useState<ReadonlyMap<string, string>>(new Map());

  useEffect(() => {
    const controller = new AbortController();
    void searchArtists(search, controller.signal, SUGGESTIONS).then((artists) => {
      if (!controller.signal.aborted && artists !== undefined) {
        setSuggested(artists.map((artist) => ({ id: artist.id, name: artist.name })));
      }
    });
    return () => {
      controller.abort();
    };
  }, [search]);

  // An Artist chosen before this page was opened (a link, a reload) is named once it is read.
  const missing = selected.filter(
    (id) => id !== NO_ARTIST && !names.has(id) && !suggested.some((artist) => artist.id === id),
  );
  const missingKey = missing.join(',');
  useEffect(() => {
    if (missingKey === '') {
      return;
    }
    let live = true;
    void Promise.all(missingKey.split(',').map((id) => readArtist(id))).then((artists) => {
      if (live) {
        setNames((previous) => {
          const next = new Map(previous);
          for (const artist of artists) {
            if (artist !== undefined) {
              next.set(artist.id, artist.name);
            }
          }
          return next;
        });
      }
    });
    return () => {
      live = false;
    };
  }, [missingKey]);

  const known = new Map(names);
  for (const artist of suggested) {
    known.set(artist.id, artist.name);
  }
  const data = [
    { value: NO_ARTIST, label: 'No Artist' },
    ...selected
      .filter((id) => id !== NO_ARTIST)
      .map((id) => ({ value: id, label: known.get(id) ?? 'Artist' })),
    ...suggested
      .filter((artist) => !selected.includes(artist.id))
      .map((artist) => ({ value: artist.id, label: artist.name })),
  ];

  return (
    <MultiSelect
      label="Artist"
      placeholder={selected.length === 0 ? 'Any Artist' : undefined}
      data={data}
      value={selected}
      onChange={(artists) => {
        // Remember the names of the Artists chosen, so they stay named when the suggestions change.
        setNames((previous) => {
          const next = new Map(previous);
          for (const id of artists) {
            const name = known.get(id);
            if (name !== undefined) {
              next.set(id, name);
            }
          }
          return next;
        });
        onChange(artists);
      }}
      searchable
      searchValue={search}
      onSearchChange={setSearch}
      // The API has already matched what is typed (aliases too); the list is shown as it answered.
      filter={({ options }) => options}
      clearable
      clearButtonProps={{ 'aria-label': 'Clear the Artist filter' }}
      nothingFoundMessage="No Artist has that name."
      maw={420}
      comboboxProps={{ withinPortal: false, hideDetached: false }}
    />
  );
}
