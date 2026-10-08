import {
  Button,
  Chip,
  CloseButton,
  Group,
  Loader,
  MultiSelect,
  Pagination,
  Paper,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useEffect, useRef, useState, type SyntheticEvent } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router';
import { useGenres, type Genre } from '../api/genres';
import {
  defaultDirection,
  defaultSort,
  NO_GENRE,
  NO_TAG,
  SEARCH_MAXIMUM_LENGTH,
  searchTextOf,
  songListParameters,
  songQueryFrom,
  useSongs,
  useWorkflowStates,
  type SongQuery,
  type SongSort,
  type WorkflowState,
} from '../api/songs';
import { useTags, type Tag } from '../api/tags';
import { useConfiguredTimeZone } from '../api/timeZone';
import { statesForFilter } from '../api/workflow';
import { Notice } from '../components/Notice';
import { SONGS_SEARCH_ID } from '../search/searchRules';
import { ArtistFilter } from './ArtistFilter';
import { NewSongDialog } from './NewSongDialog';
import { paletteColour } from '../theme/palette';
import { SongsTable, type FromSongs } from './SongsTable';

export type { FromSongs } from './SongsTable';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

/** How long the table's search box waits after the last key before it searches. */
export const SEARCH_DEBOUNCE_MS = 300;

/** What a page hands the Songs table to tell the user: the Song just deleted. */
export interface SongsNotice {
  deletedSong: { title: string; shortcode: string };
}

function isSongsNotice(value: unknown): value is SongsNotice {
  return (
    typeof value === 'object' &&
    value !== null &&
    'deletedSong' in value &&
    typeof value.deletedSong === 'object' &&
    value.deletedSong !== null &&
    'title' in value.deletedSong &&
    typeof value.deletedSong.title === 'string' &&
    'shortcode' in value.deletedSong &&
    typeof value.deletedSong.shortcode === 'string'
  );
}

/** The dismissible notice naming the Song just deleted. */
function DeletedNotice({
  deleted,
  onDismiss,
}: {
  deleted: SongsNotice['deletedSong'];
  onDismiss: () => void;
}) {
  return (
    <Paper p="sm" withBorder data-testid="song-deleted-notice">
      <Group justify="space-between" wrap="nowrap" gap="sm">
        <Text role="status" style={{ overflowWrap: 'anywhere' }}>
          Deleted {deleted.shortcode} “{deleted.title}”.
        </Text>
        <CloseButton aria-label="Dismiss" onClick={onDismiss} />
      </Group>
    </Paper>
  );
}

const PAGE_CONTROL_LABELS: Record<'first' | 'previous' | 'next' | 'last', string> = {
  first: 'First page',
  previous: 'Previous page',
  next: 'Next page',
  last: 'Last page',
};

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

/**
 * The Genre filter: any number of Genres and "No Genre", matching Songs with any of them; none
 * chosen means every Song.
 */
function GenreFilter({
  genres,
  selected,
  onChange,
}: {
  genres: Genre[];
  selected: string[];
  onChange: (genres: string[]) => void;
}) {
  return (
    <MultiSelect
      label="Genre"
      placeholder={selected.length === 0 ? 'Any Genre' : undefined}
      data={[
        { value: NO_GENRE, label: 'No Genre' },
        ...genres.map((genre) => ({ value: genre.id, label: genre.name })),
      ]}
      value={selected}
      onChange={onChange}
      searchable
      clearable
      clearButtonProps={{ 'aria-label': 'Clear the Genre filter' }}
      nothingFoundMessage="No Genre has that name."
      maw={420}
      comboboxProps={{ withinPortal: false, hideDetached: false }}
    />
  );
}

/**
 * The Tag filter: any number of Tags and "No Tags", matching Songs with any of them; none chosen
 * means every Song. Each suggestion shows the Tag's colour beside its name.
 */
function TagFilter({
  tags,
  selected,
  onChange,
}: {
  tags: Tag[];
  selected: string[];
  onChange: (tags: string[]) => void;
}) {
  const colours = new Map(tags.map((tag) => [tag.id, tag.colour]));
  return (
    <MultiSelect
      label="Tag"
      placeholder={selected.length === 0 ? 'Any Tag' : undefined}
      data={[
        { value: NO_TAG, label: 'No Tags' },
        ...tags.map((tag) => ({ value: tag.id, label: tag.name })),
      ]}
      value={selected}
      onChange={onChange}
      renderOption={({ option }) => {
        const colour = colours.get(option.value);
        return (
          <Group gap={6} wrap="nowrap">
            {colour !== undefined && (
              <span
                aria-hidden="true"
                style={{
                  display: 'inline-block',
                  width: 8,
                  height: 8,
                  borderRadius: '50%',
                  background: paletteColour(colour),
                  flex: 'none',
                }}
              />
            )}
            {option.label}
          </Group>
        );
      }}
      searchable
      clearable
      clearButtonProps={{ 'aria-label': 'Clear the Tag filter' }}
      nothingFoundMessage="No Tag has that name."
      maw={420}
      comboboxProps={{ withinPortal: false, hideDetached: false }}
    />
  );
}

/**
 * The table's own search box (#224): it searches 300 ms after typing stops (replacing the current
 * history entry) and on Enter (adding one); clearing it ends the search. It follows the address, so a
 * header search or a step back shows here.
 */
function SongsSearch({
  active,
  onSearch,
}: {
  active: string | undefined;
  onSearch: (text: string, push: boolean) => void;
}) {
  const [text, setText] = useState(active ?? '');
  const [mirrored, setMirrored] = useState(active);
  // What this box last sent: its arrival in the address must not undo typing done since.
  const [sent, setSent] = useState<{ search: string | undefined } | null>(null);
  const timer = useRef<number | undefined>(undefined);

  if (active !== mirrored) {
    setMirrored(active);
    if (sent !== null && sent.search === active) {
      setSent(null);
    } else if (searchTextOf(text) !== active) {
      setText(active ?? '');
    }
  }

  const send = (next: string, push: boolean) => {
    setSent({ search: searchTextOf(next) });
    onSearch(next, push);
  };

  const cancel = () => {
    window.clearTimeout(timer.current);
    timer.current = undefined;
  };
  useEffect(() => cancel, []);

  const type = (next: string) => {
    setText(next);
    cancel();
    timer.current = window.setTimeout(() => {
      timer.current = undefined;
      send(next, false);
    }, SEARCH_DEBOUNCE_MS);
  };
  const submit = (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    cancel();
    send(text, true);
  };

  return (
    <form role="search" aria-label="Songs table search" onSubmit={submit}>
      <TextInput
        id={SONGS_SEARCH_ID}
        type="search"
        label="Search"
        description="Words in any of a Song's text; “quoted phrases” in order."
        placeholder="Lyrics, styles, titles, shortcodes…"
        value={text}
        maxLength={SEARCH_MAXIMUM_LENGTH}
        onChange={(event) => {
          type(event.currentTarget.value);
        }}
        spellCheck={false}
        autoComplete="off"
        maw={520}
        rightSection={
          text === '' ? undefined : (
            <CloseButton
              aria-label="Clear the search text"
              onClick={() => {
                cancel();
                setText('');
                send('', true);
              }}
            />
          )
        }
      />
    </form>
  );
}

/** The title filter, set from a Song page's "same title" list: shown, and cleared with a button. */
function TitleFilter({ title, onClear }: { title: string; onClear: () => void }) {
  return (
    <Group gap="xs" role="group" aria-label="Title filter" data-testid="title-filter">
      <Text size="sm">
        Title is <Text span fw={700}>{`“${title}”`}</Text>
      </Text>
      <Button variant="subtle" size="compact-sm" onClick={onClear}>
        Clear the title filter
      </Button>
    </Group>
  );
}

/**
 * Songs: every Song in a table ({@link SongsTable}), newest first, sortable by title, by last update, and (#211) by
 * how many local audio files it has (the last column, blank for none), filtered by
 * workflow state, by Genre, by Tag, by Artist (primary or featured), and by title (ignoring case and
 * spacing; set from a Song page and cleared here), fifty to a page. Each row shows its primary
 * Artist, its first three Tags, and "+N" for the rest. The view (sort, direction, states, Genres,
 * Tags, Artists, title, search, page) is the page URL's query string, the list API's own
 * parameters, so going back to it or reloading shows the same rows. A search (#224) keeps the Songs
 * matching it, by relevance unless a sort is chosen, each with where it matched beneath it; any new
 * search goes back to the first page, and ending it goes back to the ordinary order.
 */
export function SongsPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const query = songQueryFrom(searchParams);
  const { state, reload } = useSongs(query);
  const { state: statesState } = useWorkflowStates();
  const { state: genresState } = useGenres();
  const { state: tagsState } = useTags();
  const timeZone = useConfiguredTimeZone();
  const [creating, setCreating] = useState(false);
  const [deleted, setDeleted] = useState(() =>
    isSongsNotice(location.state) ? location.state.deletedSong : undefined,
  );
  const from: FromSongs = { songsSearch: location.search };

  const dismiss = () => {
    setDeleted(undefined);
    // Forget it in this history entry too, so going back here does not show it again.
    void navigate({ search: location.search }, { replace: true, state: null });
  };

  const show = (next: SongQuery) => {
    setSearchParams(songListParameters(next));
  };
  const sortBy = (sort: SongSort) => {
    const direction =
      query.sort === sort ? (query.direction === 'asc' ? 'desc' : 'asc') : defaultDirection(sort);
    show({ ...query, sort, direction, page: 1 });
  };
  const filter = (states: string[]) => {
    show({ ...query, states, page: 1 });
  };
  const filterGenres = (genres: string[]) => {
    show({ ...query, genres, page: 1 });
  };
  const filterTags = (tags: string[]) => {
    show({ ...query, tags, page: 1 });
  };
  const filterArtists = (artists: string[]) => {
    show({ ...query, artists, page: 1 });
  };
  const clearTitle = () => {
    show({ ...query, title: undefined, page: 1 });
  };
  const searchFor = (text: string, push: boolean) => {
    const search = searchTextOf(text);
    if (search === query.search) {
      return;
    }
    // A sort left at its default moves to the new default (relevance while searching); a chosen one stays.
    const sort = query.sort === defaultSort(query) ? defaultSort({ search }) : query.sort;
    const direction =
      query.direction === defaultDirection(query.sort) ? defaultDirection(sort) : query.direction;
    setSearchParams(songListParameters({ ...query, search, sort, direction, page: 1 }), {
      replace: !push,
    });
  };
  const clearAll = () => {
    show({
      ...query,
      sort: query.sort === 'relevance' ? 'updated' : query.sort,
      direction: query.sort === 'relevance' ? defaultDirection('updated') : query.direction,
      states: [],
      genres: [],
      tags: [],
      artists: [],
      title: undefined,
      search: undefined,
      page: 1,
    });
  };

  const page = state.phase === 'ready' ? state.data : undefined;
  const filtered =
    query.states.length > 0 ||
    query.genres.length > 0 ||
    query.tags.length > 0 ||
    query.artists.length > 0 ||
    query.title !== undefined;
  const searching = query.search !== undefined;
  const empty = page?.total === 0 && !filtered && !searching;
  const pages = page === undefined ? 0 : Math.ceil(page.total / page.pageSize);

  return (
    <Stack gap="lg">
      <Group justify="space-between">
        <Title order={2}>Songs</Title>
        {!empty && (
          <Button
            onClick={() => {
              setCreating(true);
            }}
          >
            New Song
          </Button>
        )}
      </Group>
      {deleted !== undefined && <DeletedNotice deleted={deleted} onDismiss={dismiss} />}

      {!empty && <SongsSearch active={query.search} onSearch={searchFor} />}

      {statesState.phase === 'ready' && !empty && (
        <StateFilter states={statesState.data} selected={query.states} onChange={filter} />
      )}
      {genresState.phase === 'ready' && !empty && (
        <GenreFilter genres={genresState.data} selected={query.genres} onChange={filterGenres} />
      )}
      {tagsState.phase === 'ready' && !empty && (
        <TagFilter tags={tagsState.data} selected={query.tags} onChange={filterTags} />
      )}
      {!empty && <ArtistFilter selected={query.artists} onChange={filterArtists} />}
      {query.title !== undefined && <TitleFilter title={query.title} onClear={clearTitle} />}

      {state.phase === 'loading' && <Loader aria-label="Loading Songs" />}
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Notice title="Songs could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <Group gap="xs">
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
            {searchParams.size > 0 && (
              <Button variant="default" size="xs" component={Link} to="/songs">
                Show every Song
              </Button>
            )}
          </Group>
        </Notice>
      )}

      {empty && (
        <Paper p="lg" withBorder>
          <Stack gap="sm" align="flex-start">
            <Text fw={700}>There are no Songs yet.</Text>
            <Text>A Song needs only a title. It starts with a Version 1 to write in.</Text>
            <Button
              onClick={() => {
                setCreating(true);
              }}
            >
              New Song
            </Button>
          </Stack>
        </Paper>
      )}
      {page !== undefined && !empty && page.items.length === 0 && (
        <Paper p="sm" withBorder>
          <Stack gap="xs" align="flex-start">
            {page.total === 0 && query.search !== undefined ? (
              <>
                <Text data-testid="no-search-results" role="status">
                  No Songs match “{query.search}”{filtered ? ' with the chosen filters' : ''}.
                </Text>
                <Group gap="xs">
                  <Button
                    variant="default"
                    size="xs"
                    onClick={() => {
                      searchFor('', true);
                    }}
                  >
                    Clear the search
                  </Button>
                  {filtered && (
                    <Button variant="default" size="xs" onClick={clearAll}>
                      Clear the search and filters
                    </Button>
                  )}
                </Group>
              </>
            ) : page.total === 0 &&
              (query.genres.length > 0 ||
                query.tags.length > 0 ||
                query.artists.length > 0 ||
                query.title !== undefined) ? (
              <>
                <Text>No Songs match the chosen filters.</Text>
                <Button
                  variant="default"
                  size="xs"
                  onClick={() => {
                    show({
                      ...query,
                      states: [],
                      genres: [],
                      tags: [],
                      artists: [],
                      title: undefined,
                      page: 1,
                    });
                  }}
                >
                  Show every Song
                </Button>
              </>
            ) : page.total === 0 ? (
              <>
                <Text>No Songs are in the chosen states.</Text>
                <Button
                  variant="default"
                  size="xs"
                  onClick={() => {
                    filter([]);
                  }}
                >
                  Show every state
                </Button>
              </>
            ) : (
              <>
                <Text>There are no Songs on this page.</Text>
                <Button
                  variant="default"
                  size="xs"
                  onClick={() => {
                    show({ ...query, page: 1 });
                  }}
                >
                  Go to the first page
                </Button>
              </>
            )}
          </Stack>
        </Paper>
      )}
      {page !== undefined && page.items.length > 0 && (
        <>
          {page.indexRebuilding === true && (
            <Text size="sm" data-testid="index-rebuilding">
              The search index is being rebuilt, so some Songs may be missing from these results.
            </Text>
          )}
          <SongsTable
            songs={page.items}
            query={query}
            onSort={sortBy}
            timeZone={timeZone}
            from={from}
          />
          <Group justify="space-between">
            <Text size="sm" data-testid="songs-total" role={searching ? 'status' : undefined}>
              {searching
                ? `${String(page.total)} ${page.total === 1 ? 'Song matches' : 'Songs match'} “${query.search ?? ''}”` +
                  (page.items.length === page.total
                    ? ''
                    : `, showing ${String((page.page - 1) * page.pageSize + 1)}–${String(
                        (page.page - 1) * page.pageSize + page.items.length,
                      )}`)
                : page.items.length === page.total
                  ? `${String(page.total)} ${page.total === 1 ? 'Song' : 'Songs'}`
                  : `Songs ${String((page.page - 1) * page.pageSize + 1)}–${String(
                      (page.page - 1) * page.pageSize + page.items.length,
                    )} of ${String(page.total)}`}
            </Text>
            {pages > 1 && (
              <Group component="nav" aria-label="Pages">
                <Pagination
                  total={pages}
                  value={page.page}
                  onChange={(next) => {
                    show({ ...query, page: next });
                  }}
                  withEdges
                  getItemProps={(item) => ({ 'aria-label': `Page ${String(item)}` })}
                  getControlProps={(control) => ({ 'aria-label': PAGE_CONTROL_LABELS[control] })}
                />
              </Group>
            )}
          </Group>
        </>
      )}

      <NewSongDialog
        opened={creating}
        onClose={() => {
          setCreating(false);
        }}
        onCreated={(song) => {
          setCreating(false);
          void navigate(`/songs/${song.shortcode}`, { state: from });
        }}
      />
    </Stack>
  );
}
