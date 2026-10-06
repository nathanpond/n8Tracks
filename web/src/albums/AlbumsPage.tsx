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
  UnstyledButton,
  VisuallyHidden,
} from '@mantine/core';
import { useState } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router';
import {
  albumListParameters,
  albumQueryFrom,
  useAlbums,
  type Album,
  type AlbumQuery,
  type AlbumSort,
} from '../api/albums';
import { ArtworkImage } from '../common/ArtworkImage';
import { useDeletedCollection } from '../common/collectionDeletion';
import { DeletedCollectionNotice } from '../common/DeleteCollection';
import { Notice } from '../components/Notice';
import { formatAlbumDate, shownDate } from './albumRules';
import { NewAlbumDialog } from './NewAlbumDialog';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

/** What an Album page is told about the list it was opened from, so it can link back to that view. */
export interface FromAlbums {
  albumsSearch: string;
}

const PAGE_CONTROL_LABELS: Record<'first' | 'previous' | 'next' | 'last', string> = {
  first: 'First page',
  previous: 'Previous page',
  next: 'Next page',
  last: 'Last page',
};

/** What a missing value shows. */
const NONE = '—';

/** A column header that sorts the table by it: ascending first, again reversed. */
function SortHeader({
  label,
  sort,
  query,
  onSort,
  end = false,
}: {
  label: string;
  sort: AlbumSort;
  query: AlbumQuery;
  onSort: (sort: AlbumSort) => void;
  end?: boolean;
}) {
  const active = query.sort === sort;
  const ascending = query.direction === 'asc';
  return (
    <Table.Th
      scope="col"
      ta={end ? 'end' : undefined}
      aria-sort={active ? (ascending ? 'ascending' : 'descending') : undefined}
    >
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

function AlbumRow({ album, from }: { album: Album; from: FromAlbums }) {
  const date = shownDate(album);
  return (
    <Table.Tr data-album-title={album.title}>
      <Table.Td w={56}>
        <ArtworkImage artwork={album.artwork} title={album.title} size="96" pixels={40} />
      </Table.Td>
      <Table.Th scope="row">
        <Anchor component={Link} to={`/albums/${album.id}`} state={from}>
          {album.title}
        </Anchor>
      </Table.Th>
      <Table.Td>{album.albumArtist?.name ?? NONE}</Table.Td>
      <Table.Td ta="end">{album.songCount}</Table.Td>
      <Table.Td>{date === null ? NONE : formatAlbumDate(date)}</Table.Td>
    </Table.Tr>
  );
}

/**
 * Albums: every Album, fifty to a page, with its own artwork (a placeholder when it has none), its Album Artist, how many Songs it holds, and its
 * release date (else its original release date), sorted by title, Album Artist, or date; a missing
 * value shows a dash and sorts last. The view is the page URL's query string, the list API's own
 * parameters. "New Album" asks for a title, creates the Album, and opens it.
 */
export function AlbumsPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const query = albumQueryFrom(searchParams);
  const { state, reload } = useAlbums(query);
  const [creating, setCreating] = useState(false);
  const [deleted, dismissDeleted] = useDeletedCollection();
  const from: FromAlbums = { albumsSearch: location.search };

  const show = (next: AlbumQuery) => {
    setSearchParams(albumListParameters(next));
  };
  const sortBy = (sort: AlbumSort) => {
    show({
      sort,
      direction: query.sort === sort && query.direction === 'asc' ? 'desc' : 'asc',
      page: 1,
    });
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
        <Title order={2}>Albums</Title>
        {!empty && <Button onClick={openNew}>New Album</Button>}
      </Group>

      {deleted !== undefined && (
        <DeletedCollectionNotice deleted={deleted} onDismiss={dismissDeleted} />
      )}

      {state.phase === 'loading' && <Loader aria-label="Loading Albums" />}
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Notice title="Albums could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <Group gap="xs">
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
            {searchParams.size > 0 && (
              <Button variant="default" size="xs" component={Link} to="/albums">
                Show the first page
              </Button>
            )}
          </Group>
        </Notice>
      )}

      {empty && (
        <Paper p="lg" withBorder>
          <Stack gap="sm" align="flex-start">
            <Text fw={700}>There are no Albums yet.</Text>
            <Text>
              An Album collects Songs under a title, with an optional Album Artist and release
              details. It needs only a title.
            </Text>
            <Button onClick={openNew}>New Album</Button>
          </Stack>
        </Paper>
      )}
      {page !== undefined && !empty && page.items.length === 0 && (
        <Paper p="sm" withBorder>
          <Stack gap="xs" align="flex-start">
            <Text>There are no Albums on this page.</Text>
            <Button
              variant="default"
              size="xs"
              onClick={() => {
                show({ ...query, page: 1 });
              }}
            >
              Go to the first page
            </Button>
          </Stack>
        </Paper>
      )}
      {page !== undefined && page.items.length > 0 && (
        <>
          <Table.ScrollContainer minWidth={560}>
            <Table withTableBorder aria-label="Albums">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th scope="col">
                    <VisuallyHidden>Artwork</VisuallyHidden>
                  </Table.Th>
                  <SortHeader label="Title" sort="title" query={query} onSort={sortBy} />
                  <SortHeader label="Album Artist" sort="artist" query={query} onSort={sortBy} />
                  <Table.Th scope="col" ta="end">
                    Songs
                  </Table.Th>
                  <SortHeader
                    label="Release date"
                    sort="releaseDate"
                    query={query}
                    onSort={sortBy}
                  />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {page.items.map((album) => (
                  <AlbumRow key={album.id} album={album} from={from} />
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
          <Group justify="space-between">
            <Text size="sm">
              {page.items.length === page.total
                ? `${String(page.total)} ${page.total === 1 ? 'Album' : 'Albums'}`
                : `Albums ${String((page.page - 1) * page.pageSize + 1)}–${String(
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

      <NewAlbumDialog
        opened={creating}
        onClose={() => {
          setCreating(false);
        }}
        onCreated={(album) => {
          setCreating(false);
          void navigate(`/albums/${album.id}`, { state: from });
        }}
      />
    </Stack>
  );
}
