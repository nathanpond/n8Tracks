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
  TextInput,
  Title,
  VisuallyHidden,
} from '@mantine/core';
import { useState } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router';
import {
  artistListParameters,
  artistQueryFrom,
  useArtists,
  type Artist,
  type ArtistQuery,
} from '../api/artists';
import { ArtworkImage } from '../common/ArtworkImage';
import { useDeletedCollection } from '../common/collectionDeletion';
import { DeletedCollectionNotice } from '../common/DeleteCollection';
import { Notice } from '../components/Notice';
import { NewArtistDialog } from './NewArtistDialog';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

/** What an Artist page is told about the list it was opened from, so it can link back to that view. */
export interface FromArtists {
  artistsSearch: string;
}

const PAGE_CONTROL_LABELS: Record<'first' | 'previous' | 'next' | 'last', string> = {
  first: 'First page',
  previous: 'Previous page',
  next: 'Next page',
  last: 'Last page',
};

function ArtistRow({ artist, from }: { artist: Artist; from: FromArtists }) {
  return (
    <Table.Tr data-artist-name={artist.name}>
      <Table.Td w={56}>
        <ArtworkImage artwork={artist.artwork} title={artist.name} size="96" pixels={40} />
      </Table.Td>
      <Table.Th scope="row">
        <Anchor component={Link} to={`/artists/${artist.id}`} state={from}>
          {artist.name}
        </Anchor>
      </Table.Th>
      <Table.Td>{artist.aliases.join(', ')}</Table.Td>
      <Table.Td ta="end">{artist.songCount}</Table.Td>
      <Table.Td ta="end">{artist.albumCount}</Table.Td>
    </Table.Tr>
  );
}

/**
 * Artists: every Artist by name (ignoring case), fifty to a page, with its own artwork (a
 * placeholder when it has none), its aliases, and how many
 * Songs and Albums are credited to it, and a search box matching any part of a name or alias. The
 * view (search and page) is the page URL's query string, the list API's own parameters, so going
 * back to it or reloading shows the same rows. "New Artist" creates one and opens it.
 */
export function ArtistsPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const query = artistQueryFrom(searchParams);
  const { state, reload } = useArtists(query);
  const [creating, setCreating] = useState(false);
  const [deleted, dismissDeleted] = useDeletedCollection();
  const from: FromArtists = { artistsSearch: location.search };

  const show = (next: ArtistQuery, replace = false) => {
    setSearchParams(artistListParameters(next), { replace });
  };

  const page = state.phase === 'ready' ? state.data : undefined;
  const searching = query.search.trim() !== '';
  const empty = page?.total === 0 && !searching;
  const pages = page === undefined ? 0 : Math.ceil(page.total / page.pageSize);
  const openNew = () => {
    setCreating(true);
  };

  return (
    <Stack gap="lg">
      <Group justify="space-between">
        <Title order={2}>Artists</Title>
        {!empty && <Button onClick={openNew}>New Artist</Button>}
      </Group>

      {deleted !== undefined && (
        <DeletedCollectionNotice deleted={deleted} onDismiss={dismissDeleted} />
      )}

      {!empty && (
        <TextInput
          type="search"
          label="Search Artists"
          description="Any part of a name or alias."
          value={query.search}
          onChange={(event) => {
            // Each keystroke replaces the view rather than adding a history entry per letter.
            show({ search: event.currentTarget.value, page: 1 }, true);
          }}
          maw={420}
        />
      )}

      {state.phase === 'loading' && <Loader aria-label="Loading Artists" />}
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Notice title="Artists could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <Group gap="xs">
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
            {searchParams.size > 0 && (
              <Button variant="default" size="xs" component={Link} to="/artists">
                Show every Artist
              </Button>
            )}
          </Group>
        </Notice>
      )}

      {empty && (
        <Paper p="lg" withBorder>
          <Stack gap="sm" align="flex-start">
            <Text fw={700}>There are no Artists yet.</Text>
            <Text>
              An Artist is one record per performer or act, credited on Songs and Albums. It needs
              only a name.
            </Text>
            <Button onClick={openNew}>New Artist</Button>
          </Stack>
        </Paper>
      )}
      {page !== undefined && !empty && page.items.length === 0 && (
        <Paper p="sm" withBorder>
          <Stack gap="xs" align="flex-start">
            {page.total === 0 ? (
              <>
                <Text>No Artist has a name or alias containing “{query.search.trim()}”.</Text>
                <Button
                  variant="default"
                  size="xs"
                  onClick={() => {
                    show({ search: '', page: 1 });
                  }}
                >
                  Show every Artist
                </Button>
              </>
            ) : (
              <>
                <Text>There are no Artists on this page.</Text>
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
          <Table.ScrollContainer minWidth={560}>
            <Table withTableBorder aria-label="Artists">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th scope="col">
                    <VisuallyHidden>Artwork</VisuallyHidden>
                  </Table.Th>
                  <Table.Th scope="col">Name</Table.Th>
                  <Table.Th scope="col">Aliases</Table.Th>
                  <Table.Th scope="col" ta="end">
                    Songs
                  </Table.Th>
                  <Table.Th scope="col" ta="end">
                    Albums
                  </Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {page.items.map((artist) => (
                  <ArtistRow key={artist.id} artist={artist} from={from} />
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
          <Group justify="space-between">
            <Text size="sm">
              {page.items.length === page.total
                ? `${String(page.total)} ${page.total === 1 ? 'Artist' : 'Artists'}`
                : `Artists ${String((page.page - 1) * page.pageSize + 1)}–${String(
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

      <NewArtistDialog
        opened={creating}
        onClose={() => {
          setCreating(false);
        }}
        onCreated={(artist) => {
          setCreating(false);
          void navigate(`/artists/${artist.id}`, { state: from });
        }}
      />
    </Stack>
  );
}
