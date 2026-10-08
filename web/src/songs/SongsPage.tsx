import {
  Anchor,
  Button,
  CloseButton,
  Group,
  Loader,
  Pagination,
  Paper,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useEffect, useRef, useState, type SyntheticEvent } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router';
import {
  activeFilterCount,
  defaultDirection,
  defaultSort,
  SEARCH_MAXIMUM_LENGTH,
  searchTextOf,
  songListParameters,
  songQueryFrom,
  useSongs,
  useWorkflowStates,
  type SongQuery,
  type SongSort,
  type SortDirection,
} from '../api/songs';
import { useConfiguredTimeZone } from '../api/timeZone';
import { Notice } from '../components/Notice';
import { SONGS_SEARCH_ID } from '../search/searchRules';
import { NewSongDialog } from './NewSongDialog';
import { SongFilterBar } from './SongFilterBar';
import { SongSortControl } from './SongSortControl';
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
 * Songs: every Song in a table ({@link SongsTable}), newest first, sortable (#226, from the column
 * headers and from {@link SongSortControl}) by title, creation date, last update, highest Generation
 * rating, workflow state, last Generation date, and (#211) how many local audio files it has (the
 * last column, blank for none); a page past the end shows an empty table and a link to the last
 * page. It is filtered by
 * the filter bar's filters ({@link SongFilterBar}, #225: workflow state, archived status, Genre, Tags,
 * Artist, Album, Playlist, model, creation date, rating, Selected Generation, local audio) and by
 * title (ignoring case and spacing; set from a Song page and cleared here), fifty to a page. Each row shows its primary
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
  // The Sort control (#226): a key in its first direction, or a direction of the key shown.
  const sortTo = (sort: SongSort, direction: SortDirection) => {
    show({ ...query, sort, direction, page: 1 });
  };
  const filter = (states: string[]) => {
    show({ ...query, states, page: 1 });
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
  // Every filter (#225's too) and the search; the sort is kept unless it was the search's.
  const clearAll = () => {
    show({
      sort: query.sort === 'relevance' ? 'updated' : query.sort,
      direction: query.sort === 'relevance' ? defaultDirection('updated') : query.direction,
      states: [],
      genres: [],
      tags: [],
      artists: [],
      page: 1,
    });
  };
  // Every filter but the search.
  const clearFilters = () => {
    show({
      sort: query.sort,
      direction: query.direction,
      states: [],
      genres: [],
      tags: [],
      artists: [],
      ...(query.search === undefined ? {} : { search: query.search }),
      page: 1,
    });
  };

  const page = state.phase === 'ready' ? state.data : undefined;
  const filtered = activeFilterCount(query) > 0;
  // Filters beyond the states, which have their own empty state.
  const filteredBeyondStates = activeFilterCount(query) > query.states.length;
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

      {!empty && (
        <SongFilterBar
          query={query}
          states={statesState.phase === 'ready' ? statesState.data : undefined}
          onChange={show}
          onClearAll={clearAll}
        />
      )}
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
            ) : page.total === 0 && filteredBeyondStates ? (
              <>
                <Text>No Songs match the chosen filters.</Text>
                <Button variant="default" size="xs" onClick={clearFilters}>
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
                <Text data-testid="page-past-end">There are no Songs on this page.</Text>
                <Anchor
                  component={Link}
                  to={{ search: songListParameters({ ...query, page: pages }).toString() }}
                >
                  {`Go to the last page (page ${String(pages)})`}
                </Anchor>
              </>
            )}
          </Stack>
        </Paper>
      )}
      {page !== undefined && !empty && page.items.length === 0 && page.total > 0 && (
        <SongsTable songs={[]} query={query} onSort={sortBy} timeZone={timeZone} from={from} />
      )}
      {page !== undefined && !empty && page.total > 0 && (
        <SongSortControl query={query} onChange={sortTo} />
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
