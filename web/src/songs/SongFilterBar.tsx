import {
  Badge,
  Box,
  Button,
  Checkbox,
  Chip,
  CloseButton,
  Group,
  Radio,
  Select,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import { useEffect, useState } from 'react';
import { readArtist } from '../api/artists';
import {
  activeFilterCount,
  NO_ARTIST,
  NO_GENRE,
  NO_TAG,
  RATING_SCALE,
  type ArchivedFilter,
  type AudioFilter,
  type SelectedFilter,
  type SongQuery,
  type WorkflowState,
} from '../api/songs';
import { statesForFilter } from '../api/workflow';
import { ArtistFilter } from './ArtistFilter';
import { MultiFilterValuePicker, SingleFilterValuePicker } from './FilterValuePicker';
import { DELETED_ITEM, useFilterValueNames } from './filterValueNames';

const ARCHIVED_LABELS: Record<ArchivedFilter, string> = {
  active: 'Active only',
  archived: 'Archived only',
};

const SELECTED_LABELS: Record<SelectedFilter, string> = {
  yes: 'Has a Selected Generation',
  no: 'Has no Selected Generation',
};

const AUDIO_LABELS: Record<AudioFilter, string> = {
  available: 'Has an available local file',
  unavailable: 'Has files, none available',
  none: 'Has no local audio',
};

function stars(rating: number): string {
  return rating === 1 ? '1 star' : `${String(rating)} stars`;
}

/** The state filter: any number of states, none meaning every state. */
function StateFilter({
  states,
  selected,
  onChange,
}: {
  states: WorkflowState[];
  selected: string[];
  onChange: (states: string[]) => void;
}) {
  return (
    <Stack gap={6}>
      <Text id="songs-state-filter" size="sm" fw={500}>
        Workflow state
      </Text>
      <Group gap="xs" role="group" aria-labelledby="songs-state-filter">
        <Chip.Group multiple value={selected} onChange={onChange}>
          {statesForFilter(states, selected).map((state) => (
            <Chip key={state.id} value={state.id} size="sm">
              {state.name}
            </Chip>
          ))}
        </Chip.Group>
        {selected.length > 0 && (
          <Button
            variant="subtle"
            size="compact-sm"
            onClick={() => {
              onChange([]);
            }}
          >
            Show every state
          </Button>
        )}
      </Group>
    </Stack>
  );
}

/** The names of the chosen Artists, read once each, for their chips. */
function useArtistNames(ids: readonly string[]): ReadonlyMap<string, string> {
  const key = ids.filter((id) => id !== NO_ARTIST).join(',');
  const [names, setNames] = useState<ReadonlyMap<string, string>>(new Map());
  useEffect(() => {
    if (key === '') {
      return;
    }
    let live = true;
    void Promise.all(key.split(',').map(async (id) => [id, await readArtist(id)] as const)).then(
      (found) => {
        if (live) {
          setNames(
            new Map(
              found.map(([id, artist]) => [id, artist === undefined ? DELETED_ITEM : artist.name]),
            ),
          );
        }
      },
    );
    return () => {
      live = false;
    };
  }, [key]);
  return names;
}

/** The filters that are one optional value each. */
type OptionalFilter =
  | 'archived'
  | 'album'
  | 'playlist'
  | 'createdFrom'
  | 'createdTo'
  | 'minRating'
  | 'rated'
  | 'selected'
  | 'audio';

/** One active filter, as a chip removes it. */
interface ActiveFilter {
  key: string;
  label: string;
  remove: SongQuery;
}

/** A value's name for a chip: "…" while it is being read. */
function named(names: ReadonlyMap<string, { name: string }> | undefined, id: string): string {
  return names?.get(id)?.name ?? '…';
}

/**
 * The Songs table's filter bar (#225): every filter the list offers (workflow state, archived
 * status, Genre, Tags (all of or any of), Artist, Album, Playlist, model, creation date, Generation
 * rating, Selected Generation, and local audio), each active one as a removable chip, and Clear all.
 * Each control changes the page address, which is the list's own query, so a reload keeps the
 * filters. On a narrow screen the controls fold behind a Filters button that counts them.
 */
export function SongFilterBar({
  query,
  states,
  onChange,
  onClearAll,
}: {
  query: SongQuery;
  /** The workflow states, or undefined while they load. */
  states: WorkflowState[] | undefined;
  /** Shows `next` (already on page 1). */
  onChange: (next: SongQuery) => void;
  /** Clears every filter and the search. */
  onClearAll: () => void;
}) {
  const [open, setOpen] = useState(false);
  const change = (next: Partial<SongQuery>) => {
    onChange({ ...query, ...next, page: 1 });
  };
  const without = (key: OptionalFilter): SongQuery => ({ ...query, [key]: undefined, page: 1 });

  const genreNames = useFilterValueNames(
    'genre',
    query.genres.filter((id) => id !== NO_GENRE),
  );
  const tagNames = useFilterValueNames(
    'tag',
    query.tags.filter((id) => id !== NO_TAG),
  );
  const albumNames = useFilterValueNames('album', query.album === undefined ? [] : [query.album]);
  const playlistNames = useFilterValueNames(
    'playlist',
    query.playlist === undefined ? [] : [query.playlist],
  );
  const modelNames = useFilterValueNames('model', query.models ?? []);
  const artistNames = useArtistNames(query.artists);
  const stateNames = new Map((states ?? []).map((state) => [state.id, state.name]));

  const setTags = (tags: string[]) => {
    // "No Tags" with other Tags can only mean any of them: every one of them would match nothing.
    const mixed = tags.includes(NO_TAG) && tags.length > 1;
    change({
      tags,
      ...(mixed ? { tagMode: 'any' as const } : {}),
      ...(tags.length === 0 ? { tagMode: undefined } : {}),
    });
  };

  const active: ActiveFilter[] = [
    ...query.states.map((id) => ({
      key: `state:${id}`,
      label: `State: ${stateNames.get(id) ?? DELETED_ITEM}`,
      remove: { ...query, states: query.states.filter((state) => state !== id), page: 1 },
    })),
    ...(query.archived === undefined
      ? []
      : [{ key: 'archived', label: ARCHIVED_LABELS[query.archived], remove: without('archived') }]),
    ...query.genres.map((id) => ({
      key: `genre:${id}`,
      label: id === NO_GENRE ? 'No Genre' : `Genre: ${named(genreNames, id)}`,
      remove: { ...query, genres: query.genres.filter((genre) => genre !== id), page: 1 },
    })),
    ...query.tags.map((id) => ({
      key: `tag:${id}`,
      label: id === NO_TAG ? 'No Tags' : `Tag: ${named(tagNames, id)}`,
      remove: { ...query, tags: query.tags.filter((tag) => tag !== id), page: 1 },
    })),
    ...query.artists.map((id) => ({
      key: `artist:${id}`,
      label: id === NO_ARTIST ? 'No Artist' : `Artist: ${artistNames.get(id) ?? '…'}`,
      remove: { ...query, artists: query.artists.filter((artist) => artist !== id), page: 1 },
    })),
    ...(query.album === undefined
      ? []
      : [
          {
            key: 'album',
            label: `Album: ${named(albumNames, query.album)}`,
            remove: without('album'),
          },
        ]),
    ...(query.playlist === undefined
      ? []
      : [
          {
            key: 'playlist',
            label: `Playlist: ${named(playlistNames, query.playlist)}`,
            remove: without('playlist'),
          },
        ]),
    ...(query.models ?? []).map((model) => ({
      key: `model:${model}`,
      label: `Model: ${modelNames?.get(model)?.name ?? model}`,
      remove: {
        ...query,
        models: (query.models ?? []).filter((other) => other !== model),
        page: 1,
      },
    })),
    ...(query.createdFrom === undefined
      ? []
      : [
          {
            key: 'createdFrom',
            label: `Created from ${query.createdFrom}`,
            remove: without('createdFrom'),
          },
        ]),
    ...(query.createdTo === undefined
      ? []
      : [
          {
            key: 'createdTo',
            label: `Created to ${query.createdTo}`,
            remove: without('createdTo'),
          },
        ]),
    ...(query.minRating === undefined
      ? []
      : [
          {
            key: 'minRating',
            label: `Rated at least ${stars(query.minRating)}`,
            remove: without('minRating'),
          },
        ]),
    ...(query.rated === undefined
      ? []
      : [{ key: 'rated', label: 'No rated Generation', remove: without('rated') }]),
    ...(query.selected === undefined
      ? []
      : [{ key: 'selected', label: SELECTED_LABELS[query.selected], remove: without('selected') }]),
    ...(query.audio === undefined
      ? []
      : [{ key: 'audio', label: AUDIO_LABELS[query.audio], remove: without('audio') }]),
  ];
  const count = activeFilterCount(query);

  return (
    <Stack gap="sm" data-testid="song-filter-bar">
      <Group gap="xs" hiddenFrom="sm">
        <Button
          variant="default"
          aria-expanded={open}
          aria-controls="song-filters"
          onClick={() => {
            setOpen((previous) => !previous);
          }}
        >
          {count === 0 ? 'Filters' : `Filters (${String(count)})`}
        </Button>
      </Group>
      <Box
        id="song-filters"
        role="group"
        aria-label="Filters"
        {...(open ? {} : { visibleFrom: 'sm' })}
      >
        <Stack gap="sm">
          {states !== undefined && (
            <StateFilter
              states={states}
              selected={query.states}
              onChange={(next) => {
                change({ states: next });
              }}
            />
          )}
          <Radio.Group
            label="Archived Songs"
            value={query.archived ?? 'both'}
            onChange={(value) => {
              change({ archived: value === 'active' || value === 'archived' ? value : undefined });
            }}
          >
            <Group gap="md" mt={4}>
              <Radio value="both" label="Active and archived" />
              <Radio value="active" label="Active only" />
              <Radio value="archived" label="Archived only" />
            </Group>
          </Radio.Group>
          <Group gap="md" align="flex-end">
            <MultiFilterValuePicker
              kind="genre"
              label="Genre"
              placeholder="Any Genre"
              nothingFound="No Genre has that name."
              names={genreNames}
              extras={[{ value: NO_GENRE, label: 'No Genre' }]}
              selected={query.genres}
              onChange={(genres) => {
                change({ genres });
              }}
            />
            <MultiFilterValuePicker
              kind="tag"
              label="Tag"
              placeholder="Any Tag"
              nothingFound="No Tag has that name."
              names={tagNames}
              extras={[{ value: NO_TAG, label: 'No Tags' }]}
              selected={query.tags}
              onChange={setTags}
            />
            {query.tags.length > 1 && (
              <Radio.Group
                label="Songs with"
                value={query.tagMode ?? 'all'}
                onChange={(value) => {
                  change({ tagMode: value === 'any' ? 'any' : undefined });
                }}
              >
                <Group gap="md" mt={4}>
                  <Radio
                    value="all"
                    label="All of these Tags"
                    disabled={query.tags.includes(NO_TAG)}
                  />
                  <Radio value="any" label="Any of these Tags" />
                </Group>
              </Radio.Group>
            )}
          </Group>
          <Group gap="md" align="flex-end">
            <ArtistFilter
              selected={query.artists}
              onChange={(artists) => {
                change({ artists });
              }}
            />
            <SingleFilterValuePicker
              kind="album"
              label="Album"
              placeholder="Any Album"
              nothingFound="No Album has that title."
              names={albumNames}
              selected={query.album}
              onChange={(album) => {
                change({ album });
              }}
            />
            <SingleFilterValuePicker
              kind="playlist"
              label="Playlist"
              placeholder="Any Playlist"
              nothingFound="No Playlist has that title."
              names={playlistNames}
              selected={query.playlist}
              onChange={(playlist) => {
                change({ playlist });
              }}
            />
            <MultiFilterValuePicker
              kind="model"
              label="Model"
              placeholder="Any model"
              nothingFound="No Generation reports that model."
              names={modelNames}
              selected={query.models ?? []}
              onChange={(models) => {
                change({ models: models.length === 0 ? undefined : models });
              }}
            />
          </Group>
          <Group gap="md" align="flex-end">
            <TextInput
              type="date"
              label="Created from"
              value={query.createdFrom ?? ''}
              max={query.createdTo}
              onChange={(event) => {
                const value = event.currentTarget.value;
                change({ createdFrom: value === '' ? undefined : value });
              }}
            />
            <TextInput
              type="date"
              label="Created to"
              value={query.createdTo ?? ''}
              min={query.createdFrom}
              onChange={(event) => {
                const value = event.currentTarget.value;
                change({ createdTo: value === '' ? undefined : value });
              }}
            />
            <Select
              label="Generation rating"
              data={[
                { value: '', label: 'Any rating' },
                ...RATING_SCALE.map((rating) => ({
                  value: String(rating),
                  label: `At least ${stars(rating)}`,
                })),
              ]}
              value={query.minRating === undefined ? '' : String(query.minRating)}
              onChange={(value) => {
                change({ minRating: value === null || value === '' ? undefined : Number(value) });
              }}
              allowDeselect={false}
              w={{ base: '100%', sm: 200 }}
              comboboxProps={{ withinPortal: false, hideDetached: false }}
            />
            <Checkbox
              label={
                query.minRating === undefined ? 'No rated Generation' : 'Or no rated Generation'
              }
              checked={query.rated === 'none'}
              onChange={(event) => {
                change({ rated: event.currentTarget.checked ? 'none' : undefined });
              }}
              mb={8}
            />
          </Group>
          <Group gap="md" align="flex-end">
            <Select
              label="Selected Generation"
              data={[
                { value: '', label: 'Either' },
                { value: 'yes', label: SELECTED_LABELS.yes },
                { value: 'no', label: SELECTED_LABELS.no },
              ]}
              value={query.selected ?? ''}
              onChange={(value) => {
                change({
                  selected: value === 'yes' || value === 'no' ? value : undefined,
                });
              }}
              allowDeselect={false}
              w={{ base: '100%', sm: 260 }}
              comboboxProps={{ withinPortal: false, hideDetached: false }}
            />
            <Select
              label="Local audio"
              data={[
                { value: '', label: 'Any' },
                { value: 'available', label: AUDIO_LABELS.available },
                { value: 'unavailable', label: AUDIO_LABELS.unavailable },
                { value: 'none', label: AUDIO_LABELS.none },
              ]}
              value={query.audio ?? ''}
              onChange={(value) => {
                change({
                  audio:
                    value === 'available' || value === 'unavailable' || value === 'none'
                      ? value
                      : undefined,
                });
              }}
              allowDeselect={false}
              w={{ base: '100%', sm: 260 }}
              comboboxProps={{ withinPortal: false, hideDetached: false }}
            />
          </Group>
        </Stack>
      </Box>
      {active.length > 0 && (
        <Group gap="xs" data-testid="active-filters">
          <Text size="sm" id="songs-active-filters">
            Filters:
          </Text>
          <Group gap={6} role="list" aria-labelledby="songs-active-filters">
            {active.map((filter) => (
              <Badge
                key={filter.key}
                role="listitem"
                size="lg"
                variant="light"
                tt="none"
                fw={500}
                data-testid="filter-chip"
                rightSection={
                  <CloseButton
                    size="xs"
                    aria-label={`Remove the filter ${filter.label}`}
                    onClick={() => {
                      onChange(filter.remove);
                    }}
                  />
                }
              >
                {filter.label}
              </Badge>
            ))}
          </Group>
          <Button variant="subtle" size="compact-sm" onClick={onClearAll}>
            Clear all
          </Button>
        </Group>
      )}
    </Stack>
  );
}
