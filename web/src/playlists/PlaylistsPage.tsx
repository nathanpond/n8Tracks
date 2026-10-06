import {
  Anchor,
  Button,
  Group,
  Loader,
  Pagination,
  Paper,
  Stack,
  Table,
  Text,
  Title,
} from '@mantine/core';
import { useState } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router';
import { usePlaylists, type PlaylistSummary } from '../api/playlists';
import { Notice } from '../components/Notice';
import { NewPlaylistDialog } from './NewPlaylistDialog';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

/** What a Playlist page is told about the list it was opened from, so it can link back to that page. */
export interface FromPlaylists {
  playlistsSearch: string;
}

const PAGE_CONTROL_LABELS: Record<'first' | 'previous' | 'next' | 'last', string> = {
  first: 'First page',
  previous: 'Previous page',
  next: 'Next page',
  last: 'Last page',
};

function PlaylistRow({ playlist, from }: { playlist: PlaylistSummary; from: FromPlaylists }) {
  return (
    <Table.Tr data-playlist-title={playlist.title}>
      <Table.Th scope="row">
        <Anchor component={Link} to={`/playlists/${playlist.id}`} state={from}>
          {playlist.title}
        </Anchor>
      </Table.Th>
      <Table.Td ta="end">{playlist.songCount}</Table.Td>
    </Table.Tr>
  );
}

/**
 * Playlists: every Playlist by title, fifty to a page, with how many Songs it holds. "New Playlist"
 * asks for a title, creates the Playlist, and opens it.
 */
export function PlaylistsPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const requested = Number(searchParams.get('page') ?? '1');
  const pageNumber = Number.isSafeInteger(requested) && requested >= 1 ? requested : 1;
  const { state, reload } = usePlaylists(pageNumber);
  const [creating, setCreating] = useState(false);
  const from: FromPlaylists = { playlistsSearch: location.search };

  const show = (next: number) => {
    setSearchParams(next === 1 ? {} : { page: String(next) });
  };

  const page = state.phase === 'ready' ? state.data : undefined;
  const empty = page?.total === 0;
  const pages = page === undefined ? 0 : Math.ceil(page.total / page.pageSize);
  const openNew = () => {
    setCreating(true);
  };

  return (
    <Stack gap="lg">
      <Group justify="space-between">
        <Title order={2}>Playlists</Title>
        {!empty && <Button onClick={openNew}>New Playlist</Button>}
      </Group>

      {state.phase === 'loading' && <Loader aria-label="Loading Playlists" />}
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Notice title="Playlists could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <Group gap="xs">
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
          </Group>
        </Notice>
      )}

      {empty && (
        <Paper p="lg" withBorder>
          <Stack gap="sm" align="flex-start">
            <Text fw={700}>There are no Playlists yet.</Text>
            <Text>
              A Playlist gathers Songs in an order you arrange. A Song can be on any number of
              Playlists, and changing a Playlist never changes its Songs.
            </Text>
            <Button onClick={openNew}>New Playlist</Button>
          </Stack>
        </Paper>
      )}
      {page !== undefined && !empty && page.items.length === 0 && (
        <Paper p="sm" withBorder>
          <Stack gap="xs" align="flex-start">
            <Text>There are no Playlists on this page.</Text>
            <Button
              variant="default"
              size="xs"
              onClick={() => {
                show(1);
              }}
            >
              Go to the first page
            </Button>
          </Stack>
        </Paper>
      )}
      {page !== undefined && page.items.length > 0 && (
        <>
          <Table.ScrollContainer minWidth={360}>
            <Table withTableBorder aria-label="Playlists">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th scope="col">Title</Table.Th>
                  <Table.Th scope="col" ta="end">
                    Songs
                  </Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {page.items.map((playlist) => (
                  <PlaylistRow key={playlist.id} playlist={playlist} from={from} />
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
          <Group justify="space-between">
            <Text size="sm">
              {page.items.length === page.total
                ? `${String(page.total)} ${page.total === 1 ? 'Playlist' : 'Playlists'}`
                : `Playlists ${String((page.page - 1) * page.pageSize + 1)}–${String(
                    (page.page - 1) * page.pageSize + page.items.length,
                  )} of ${String(page.total)}`}
            </Text>
            {pages > 1 && (
              <Group component="nav" aria-label="Pages">
                <Pagination
                  total={pages}
                  value={page.page}
                  onChange={show}
                  withEdges
                  getItemProps={(item) => ({ 'aria-label': `Page ${String(item)}` })}
                  getControlProps={(control) => ({ 'aria-label': PAGE_CONTROL_LABELS[control] })}
                />
              </Group>
            )}
          </Group>
        </>
      )}

      <NewPlaylistDialog
        opened={creating}
        onClose={() => {
          setCreating(false);
        }}
        onCreated={(playlist) => {
          setCreating(false);
          void navigate(`/playlists/${playlist.id}`, { state: from });
        }}
      />
    </Stack>
  );
}
