import {
  Anchor,
  Button,
  Chip,
  Group,
  Loader,
  Pagination,
  Paper,
  Stack,
  Table,
  Text,
  Title,
  UnstyledButton,
} from '@mantine/core';
import { useState } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router';
import {
  defaultDirection,
  songListParameters,
  songQueryFrom,
  useSongs,
  useWorkflowStates,
  type Song,
  type SongQuery,
  type SongSort,
  type WorkflowState,
} from '../api/songs';
import { useConfiguredTimeZone } from '../api/timeZone';
import { statesForFilter } from '../api/workflow';
import { Notice } from '../components/Notice';
import { NewSongDialog } from './NewSongDialog';
import { RelativeTime, StateBadge, TruncatedConcept } from './SongParts';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

/** What a Song page is told about the list it was opened from, so it can link back to that view. */
export interface FromSongs {
  songsSearch: string;
}

const PAGE_CONTROL_LABELS: Record<'first' | 'previous' | 'next' | 'last', string> = {
  first: 'First page',
  previous: 'Previous page',
  next: 'Next page',
  last: 'Last page',
};

/** A column header that sorts the table by it: once in its starting direction, again reversed. */
function SortHeader({
  label,
  sort,
  query,
  onSort,
}: {
  label: string;
  sort: SongSort;
  query: SongQuery;
  onSort: (sort: SongSort) => void;
}) {
  const active = query.sort === sort;
  const ascending = query.direction === 'asc';
  return (
    <Table.Th scope="col" aria-sort={active ? (ascending ? 'ascending' : 'descending') : undefined}>
      <UnstyledButton
        fw={700}
        fz="sm"
        onClick={() => {
          onSort(sort);
        }}
      >
        {label}
        <span aria-hidden="true">{active ? (ascending ? ' ▲' : ' ▼') : ''}</span>
      </UnstyledButton>
    </Table.Th>
  );
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

function SongRow({ song, timeZone, from }: { song: Song; timeZone: string; from: FromSongs }) {
  return (
    <Table.Tr data-song={song.shortcode}>
      <Table.Th scope="row" style={{ whiteSpace: 'nowrap' }}>
        {song.shortcode}
      </Table.Th>
      <Table.Td>
        <Anchor component={Link} to={`/songs/${song.shortcode}`} state={from}>
          {song.title}
        </Anchor>
      </Table.Td>
      <Table.Td style={{ maxWidth: 260 }}>
        {song.concept !== null && <TruncatedConcept concept={song.concept} />}
      </Table.Td>
      <Table.Td>
        <StateBadge name={song.state.name} colour={song.state.colour} />
      </Table.Td>
      <Table.Td ta="end">{song.versionCount}</Table.Td>
      <Table.Td style={{ whiteSpace: 'nowrap' }}>
        <RelativeTime utc={song.updatedAt} timeZone={timeZone} />
      </Table.Td>
    </Table.Tr>
  );
}

/**
 * Songs: every Song in a table, newest first, sortable by title and by last update, filtered by
 * workflow state, fifty to a page. The view (sort, direction, states, page) is the page URL's query
 * string, the list API's own parameters, so going back to it or reloading shows the same rows.
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
  const from: FromSongs = { songsSearch: location.search };

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

  const page = state.phase === 'ready' ? state.data : undefined;
  const filtered = query.states.length > 0;
  const empty = page?.total === 0 && !filtered;
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

      {statesState.phase === 'ready' && !empty && (
        <StateFilter states={statesState.data} selected={query.states} onChange={filter} />
      )}

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
            {page.total === 0 ? (
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
          <Table.ScrollContainer minWidth={760}>
            <Table withTableBorder aria-label="Songs">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th scope="col">Shortcode</Table.Th>
                  <SortHeader label="Title" sort="title" query={query} onSort={sortBy} />
                  <Table.Th scope="col">Concept</Table.Th>
                  <Table.Th scope="col">State</Table.Th>
                  <Table.Th scope="col" ta="end">
                    Versions
                  </Table.Th>
                  <SortHeader label="Updated" sort="updated" query={query} onSort={sortBy} />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {page.items.map((song) => (
                  <SongRow key={song.id} song={song} timeZone={timeZone} from={from} />
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
          <Group justify="space-between">
            <Text size="sm">
              {page.items.length === page.total
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
