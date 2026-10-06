import {
  Button,
  Combobox,
  Group,
  Loader,
  Stack,
  Text,
  TextInput,
  useCombobox,
} from '@mantine/core';
import { useEffect, useState } from 'react';
import { createArtist, searchArtists, type Artist, type ArtistMatch } from '../api/artists';
import type { SongArtist } from '../api/songs';
import { artistNameError } from '../artists/artistRules';
import { SAVE_FAILED_MESSAGE } from './useInPlaceEdit';

/** The option value that stands for "create a new Artist with the typed name". */
const CREATE = 'create-new';

/** A name the user asked to create that other Artists already have, waiting for the user's choice. */
interface PendingDuplicate {
  name: string;
  matches: ArtistMatch[];
}

/**
 * Chooses one Artist: a text field that suggests Artists whose name or an alias contains what is
 * typed (asked of the API as the user types, up to ten, by name), leaving out `exclude`. Choosing
 * one calls `onChoose` and empties the field, so the picker adds or replaces rather than holding a
 * value.
 *
 * With `allowCreate`, a last option creates an Artist with the typed name (Artist names need not be
 * unique, so it is offered even when a suggestion has that name). The Artist is saved at once (the
 * user needs `collections.write`); when other Artists already have that name or alias the picker
 * asks, in place, whether to use one of them or create another with the same name.
 */
export function ArtistPicker({
  label,
  description,
  placeholder = 'Type to find an Artist',
  exclude = [],
  onChoose,
  allowCreate = false,
  busy = false,
  disabled = false,
  error,
}: {
  label: string;
  description?: string;
  placeholder?: string;
  /** IDs of Artists not to suggest (already chosen). */
  exclude?: readonly string[];
  onChoose: (artist: SongArtist) => void;
  allowCreate?: boolean;
  busy?: boolean;
  disabled?: boolean;
  error?: string;
}) {
  const [search, setSearch] = useState('');
  const [open, setOpen] = useState(false);
  const [found, setFound] = useState<{ search: string; artists: Artist[] | undefined } | null>(
    null,
  );
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | undefined>();
  const [duplicate, setDuplicate] = useState<PendingDuplicate | null>(null);
  const combobox = useCombobox({
    onDropdownClose: () => {
      combobox.resetSelectedOption();
    },
  });

  // The suggestions follow what is typed, asked of the API while the suggestions are open.
  useEffect(() => {
    if (!open) {
      return;
    }
    const controller = new AbortController();
    void searchArtists(search, controller.signal).then((artists) => {
      if (!controller.signal.aborted) {
        setFound({ search, artists });
      }
    });
    return () => {
      controller.abort();
    };
  }, [search, open]);

  const loading = open && found?.search !== search;
  const suggestions = (found?.artists ?? []).filter((artist) => !exclude.includes(artist.id));
  const typed = search.trim() !== '';
  // Artist names need not be unique, so the typed name can always be created; one another Artist
  // has is confirmed in place.
  const offerCreate = allowCreate && typed && !loading;
  const createProblem = offerCreate ? artistNameError(search) : undefined;
  const failedToLoad = open && !loading && found?.artists === undefined;

  // As the user types, the best suggestion (or the create option) is the one Enter takes.
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

  const choose = (artist: SongArtist) => {
    onChoose({ id: artist.id, name: artist.name });
    setSearch('');
    close();
  };

  const create = async (name: string, confirmDuplicate: boolean) => {
    setCreating(true);
    setCreateError(undefined);
    const result = await createArtist({ name: name.trim() }, confirmDuplicate);
    setCreating(false);
    switch (result.kind) {
      case 'created':
        setDuplicate(null);
        choose(result.artist);
        return;
      case 'duplicate':
        setDuplicate({ name: name.trim(), matches: result.matches });
        close();
        return;
      case 'invalid':
        setCreateError(result.errors.name?.join(' ') ?? SAVE_FAILED_MESSAGE);
        return;
      case 'failed':
        setCreateError(SAVE_FAILED_MESSAGE);
    }
  };

  const submit = (value: string) => {
    if (busy || creating) {
      return;
    }
    if (value === CREATE) {
      if (createProblem === undefined) {
        void create(search, false);
      }
      return;
    }
    const artist = suggestions.find((candidate) => candidate.id === value);
    if (artist !== undefined) {
      choose(artist);
    }
  };

  const working = busy || creating;
  const sameNamed = duplicate?.matches.filter(
    (match, index, all) => all.findIndex((other) => other.id === match.id) === index,
  );

  return (
    <Stack gap={4}>
      <Combobox store={combobox} onOptionSubmit={submit} withinPortal={false} hideDetached={false}>
        <Combobox.Target>
          <TextInput
            label={label}
            description={description}
            placeholder={placeholder}
            value={search}
            error={error ?? createError}
            disabled={disabled}
            readOnly={working}
            rightSection={working || loading ? <Loader size="xs" /> : undefined}
            onFocus={() => {
              setOpen(true);
              combobox.openDropdown();
            }}
            onClick={() => {
              setOpen(true);
              combobox.openDropdown();
            }}
            onBlur={close}
            onChange={(event) => {
              setSearch(event.currentTarget.value);
              setCreateError(undefined);
              setOpen(true);
              combobox.openDropdown();
            }}
          />
        </Combobox.Target>
        <Combobox.Dropdown hidden={!open || (loading && suggestions.length === 0 && !typed)}>
          <Combobox.Options aria-label={`${label} suggestions`}>
            {suggestions.map((artist) => (
              <Combobox.Option value={artist.id} key={artist.id}>
                {artist.name}
              </Combobox.Option>
            ))}
            {offerCreate && createProblem === undefined && (
              <Combobox.Option value={CREATE}>Create Artist “{search.trim()}”</Combobox.Option>
            )}
            {offerCreate && createProblem !== undefined && (
              <Combobox.Empty>
                <Text size="sm" c="var(--mantine-color-error)">
                  {createProblem}
                </Text>
              </Combobox.Empty>
            )}
            {failedToLoad && <Combobox.Empty>The Artists could not be loaded.</Combobox.Empty>}
            {!failedToLoad && !loading && suggestions.length === 0 && !offerCreate && (
              <Combobox.Empty>
                {typed ? 'No other Artist has that name.' : 'There are no other Artists yet.'}
              </Combobox.Empty>
            )}
          </Combobox.Options>
        </Combobox.Dropdown>
      </Combobox>
      {duplicate !== null && sameNamed !== undefined && (
        <Stack
          gap={6}
          role="group"
          aria-label="An Artist with this name exists"
          data-testid="artist-duplicate"
        >
          <Text size="sm">
            {sameNamed.length === 1 ? 'An Artist' : 'Artists'} already{' '}
            {sameNamed.length === 1 ? 'has' : 'have'} the name “{duplicate.name}”. Use{' '}
            {sameNamed.length === 1 ? 'it' : 'one of them'}, or create another Artist with the same
            name?
          </Text>
          <Group gap="xs">
            {sameNamed.map((match) => (
              <Button
                key={match.id}
                size="compact-sm"
                variant="default"
                disabled={working}
                onClick={() => {
                  setDuplicate(null);
                  choose({ id: match.id, name: match.name });
                }}
              >
                Use {match.name}
                {match.matchedOn === 'alias' ? ` (alias ${match.matchedText})` : ''}
              </Button>
            ))}
            <Button
              size="compact-sm"
              loading={creating}
              onClick={() => {
                void create(duplicate.name, true);
              }}
            >
              Create another “{duplicate.name}”
            </Button>
            <Button
              size="compact-sm"
              variant="subtle"
              disabled={creating}
              onClick={() => {
                setDuplicate(null);
              }}
            >
              Cancel
            </Button>
          </Group>
        </Stack>
      )}
    </Stack>
  );
}
