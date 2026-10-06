import { Combobox, Group, Loader, Stack, Text, TextInput, useCombobox } from '@mantine/core';
import { useEffect, useState } from 'react';
import { searchSongs, type Song } from '../api/songs';

/**
 * Finds one Song: a text field that suggests the Songs whose title contains what is typed (ignoring
 * case) or whose shortcode starts with it, asked of the API as the user types, up to ten, by title.
 * Each suggestion shows the shortcode, title, and primary Artist. Songs in `unavailable` are shown
 * but cannot be chosen, with `unavailableNote` saying why; Songs in `exclude` are not shown at all. Choosing one calls `onChoose` and empties
 * the field, so the search adds rather than holding a value. Shared by every screen that adds Songs
 * to something (Playlists, Album tracks, relationships).
 */
export function SongSearch({
  label,
  description,
  placeholder = 'Type a title or a shortcode',
  unavailable = [],
  unavailableNote = 'already added',
  exclude = [],
  onChoose,
  busy = false,
  disabled = false,
  error,
}: {
  label: string;
  description?: string;
  placeholder?: string;
  /** IDs of Songs to show disabled. */
  unavailable?: readonly string[];
  unavailableNote?: string;
  /** IDs of Songs never to suggest (the Song being related, say). */
  exclude?: readonly string[];
  onChoose: (song: Song) => void;
  busy?: boolean;
  /** Whether the field cannot be used yet (something else must be chosen first). */
  disabled?: boolean;
  error?: string;
}) {
  const [search, setSearch] = useState('');
  const [open, setOpen] = useState(false);
  const [found, setFound] = useState<{
    search: string;
    result: { songs: Song[]; total: number } | undefined;
  } | null>(null);
  const combobox = useCombobox({
    onDropdownClose: () => {
      combobox.resetSelectedOption();
    },
  });

  // The suggestions follow what is typed, asked of the API while the field has the focus.
  useEffect(() => {
    if (!open) {
      return;
    }
    const controller = new AbortController();
    void searchSongs(search, controller.signal).then((result) => {
      if (!controller.signal.aborted) {
        setFound({ search, result });
      }
    });
    return () => {
      controller.abort();
    };
  }, [search, open]);

  const typed = search.trim() !== '';
  const loading = open && typed && found?.search !== search;
  const songs =
    found?.search === search
      ? (found.result?.songs ?? []).filter((song) => !exclude.includes(song.id))
      : [];
  const total = found?.search === search ? (found.result?.total ?? 0) : 0;
  const failed = open && typed && !loading && found?.result === undefined;

  // As the user types, the first Song that can be chosen is the one Enter takes.
  const { selectFirstOption, resetSelectedOption } = combobox;
  useEffect(() => {
    if (search.trim() === '') {
      resetSelectedOption();
    } else {
      selectFirstOption();
    }
  }, [search, found, selectFirstOption, resetSelectedOption]);

  const close = () => {
    setOpen(false);
    combobox.closeDropdown();
  };

  const submit = (value: string) => {
    if (busy || unavailable.includes(value)) {
      return;
    }
    const song = songs.find((candidate) => candidate.id === value);
    if (song !== undefined) {
      onChoose(song);
      setSearch('');
      close();
    }
  };

  const openDropdown = () => {
    setOpen(true);
    combobox.openDropdown();
  };

  return (
    <Combobox store={combobox} onOptionSubmit={submit} withinPortal={false} hideDetached={false}>
      <Combobox.Target>
        <TextInput
          label={label}
          description={description}
          placeholder={placeholder}
          value={search}
          error={error}
          readOnly={busy}
          disabled={disabled}
          rightSection={busy || loading ? <Loader size="xs" /> : undefined}
          onFocus={openDropdown}
          onClick={openDropdown}
          onBlur={close}
          onChange={(event) => {
            setSearch(event.currentTarget.value);
            openDropdown();
          }}
        />
      </Combobox.Target>
      <Combobox.Dropdown hidden={!open || !typed}>
        <Combobox.Options aria-label={`${label} results`}>
          {songs.map((song) => {
            const taken = unavailable.includes(song.id);
            return (
              <Combobox.Option value={song.id} key={song.id} disabled={taken} aria-disabled={taken}>
                <Stack gap={0}>
                  <Group gap={6} wrap="nowrap">
                    <Text size="sm" ff="monospace">
                      {song.shortcode}
                    </Text>
                    <Text size="sm" fw={500}>
                      {song.title}
                    </Text>
                  </Group>
                  <Text size="xs">
                    {[song.credits.primary?.name, taken ? unavailableNote : undefined]
                      .filter((part) => part !== undefined)
                      .join(' · ')}
                  </Text>
                </Stack>
              </Combobox.Option>
            );
          })}
          {loading && songs.length === 0 && <Combobox.Empty>Searching…</Combobox.Empty>}
          {failed && <Combobox.Empty>The Songs could not be searched.</Combobox.Empty>}
          {!loading && !failed && songs.length === 0 && (
            <Combobox.Empty>No Song has that title or shortcode.</Combobox.Empty>
          )}
        </Combobox.Options>
        {total > songs.length && (
          <Combobox.Footer>
            <Text size="xs">
              {`Showing ${String(songs.length)} of ${String(total)}. Type more to narrow it down.`}
            </Text>
          </Combobox.Footer>
        )}
      </Combobox.Dropdown>
    </Combobox>
  );
}
