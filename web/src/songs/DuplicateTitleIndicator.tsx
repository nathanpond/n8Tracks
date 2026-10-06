import { Anchor, Button, Popover, Stack, Table, Text, Title, VisuallyHidden } from '@mantine/core';
import { useId } from 'react';
import { Link } from 'react-router';
import { songListParameters, useSongsTitled, type Song, type SongQuery } from '../api/songs';
import { formatDate, useConfiguredTimeZone } from '../api/timeZone';
import { StateBadge, TruncatedConcept } from './SongParts';

/** How many Genres a row names before "+N". */
const GENRES_PER_ROW = 3;

/** What a row shows for a field the Song has nothing in. */
const NOTHING = '—';

/** The Songs table, showing every Song with `title` (the current one included). */
function tablePathFor(title: string): string {
  const query: SongQuery = {
    sort: 'updated',
    direction: 'desc',
    states: [],
    genres: [],
    tags: [],
    artists: [],
    title,
    page: 1,
  };
  return `/songs?${songListParameters(query).toString()}`;
}

/** A Song's Genres by name: the first three, then "+N" for the rest. */
function genreText(song: Song): string {
  if (song.genres.length === 0) {
    return NOTHING;
  }
  const shown = song.genres
    .slice(0, GENRES_PER_ROW)
    .map((genre) => genre.name)
    .join(', ');
  const rest = song.genres.length - GENRES_PER_ROW;
  return rest > 0 ? `${shown} +${String(rest)}` : shown;
}

function OtherRow({ song, timeZone }: { song: Song; timeZone: string }) {
  return (
    <Table.Tr data-song={song.shortcode}>
      <Table.Th scope="row" style={{ whiteSpace: 'nowrap' }}>
        <Anchor component={Link} to={`/songs/${song.shortcode}`}>
          {song.shortcode}
        </Anchor>
      </Table.Th>
      <Table.Td style={{ maxWidth: 220 }}>
        {song.concept === null ? NOTHING : <TruncatedConcept concept={song.concept} />}
      </Table.Td>
      <Table.Td style={{ maxWidth: 160 }}>{song.credits.primary?.name ?? NOTHING}</Table.Td>
      <Table.Td style={{ maxWidth: 200 }}>{genreText(song)}</Table.Td>
      <Table.Td>
        <StateBadge name={song.state.name} colour={song.state.colour} />
      </Table.Td>
      <Table.Td style={{ whiteSpace: 'nowrap' }}>
        <time dateTime={song.createdAt}>{formatDate(song.createdAt, timeZone)}</time>
      </Table.Td>
    </Table.Tr>
  );
}

/**
 * Beside a Song's title, when other Songs share it (ignoring case and spacing): "N others", which
 * opens a list of them, most recently updated first, up to twenty, each with its shortcode (a link
 * to it), concept, primary Artist, Genres, workflow state, and created date, and a link to the
 * Songs table filtered by the title. Nothing for a unique title, and nothing while the others load
 * or when they cannot be read. It asks again whenever the saved title changes.
 */
export function DuplicateTitleIndicator({ song }: { song: Song }) {
  const { state } = useSongsTitled(song.title, song.id);
  const timeZone = useConfiguredTimeZone();
  const headingId = useId();

  if (state.phase !== 'ready' || state.data.total === 0) {
    return null;
  }
  const { items, total } = state.data;
  const count = `${String(total)} ${total === 1 ? 'other' : 'others'}`;

  return (
    // Not hidden when the button scrolls out of view: the list is short-lived, and the check hides
    // it at once where nothing is laid out (jsdom).
    <Popover position="bottom-start" withinPortal shadow="md" hideDetached={false}>
      <Popover.Target>
        <Button variant="default" size="compact-sm" data-testid="same-title">
          {count} <VisuallyHidden>with this title</VisuallyHidden>
        </Button>
      </Popover.Target>
      <Popover.Dropdown
        aria-labelledby={headingId}
        style={{ width: 'min(46rem, calc(100vw - 2rem))' }}
      >
        <Stack gap="xs">
          <Title order={3} size="h5" id={headingId}>
            {total === 1 ? 'Another Song has' : `${String(total)} other Songs have`} this title
          </Title>
          <Table.ScrollContainer minWidth={560}>
            <Table aria-labelledby={headingId} data-testid="same-title-songs">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th scope="col">Shortcode</Table.Th>
                  <Table.Th scope="col">Concept</Table.Th>
                  <Table.Th scope="col">Artist</Table.Th>
                  <Table.Th scope="col">Genres</Table.Th>
                  <Table.Th scope="col">State</Table.Th>
                  <Table.Th scope="col">Created</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {items.map((other) => (
                  <OtherRow key={other.id} song={other} timeZone={timeZone} />
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
          {total > items.length && (
            <Text size="sm">
              Showing the {items.length} most recently updated of {total}.
            </Text>
          )}
          <Anchor component={Link} to={tablePathFor(song.title)} size="sm">
            Show every Song with this title in the Songs table
          </Anchor>
        </Stack>
      </Popover.Dropdown>
    </Popover>
  );
}
