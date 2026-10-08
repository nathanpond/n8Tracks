import {
  Anchor,
  Button,
  Group,
  Loader,
  Stack,
  Table,
  Text,
  UnstyledButton,
  VisuallyHidden,
} from '@mantine/core';
import { useState } from 'react';
import { Link } from 'react-router';
import {
  kindLabel,
  readSongMatches,
  SONG_MATCHES_LIMIT,
  type Song,
  type SongMatch,
  type SongQuery,
  type SongSort,
} from '../api/songs';
import { formatDate } from '../api/timeZone';
import { workspaceName } from '../api/sunoWorkspaces';
import { ArtworkImage } from '../common/ArtworkImage';
import { SongPlayButton } from '../player/SongPlayButton';
import { MatchExcerpt } from '../search/MatchExcerpt';
import {
  matchAddress,
  matchFieldName,
  matchOwnerText,
  matchStateText,
} from '../search/searchRules';
import { RelativeTime, StateBadge, TagLabels, TruncatedConcept } from './SongParts';
import { SORT_LABELS } from './songSortRules';

/** How many Tags a row of the table shows before "+N". */
const TAGS_PER_ROW = 3;

/** How many columns a row has: a Song's match rows span them all. */
const COLUMN_COUNT = 16;

/** What a Song page is told about the list it was opened from, so it can link back to that view. */
export interface FromSongs {
  songsSearch: string;
}

/**
 * A column header that sorts the table by it (#226): once in its starting direction, again
 * reversed. The sorted column says so: `aria-sort`, and an arrow to the eye.
 */
function SortHeader({
  sort,
  label = SORT_LABELS[sort],
  query,
  onSort,
  ta,
}: {
  sort: Exclude<SongSort, 'relevance'>;
  label?: string;
  query: SongQuery;
  onSort: (sort: SongSort) => void;
  ta?: 'end';
}) {
  const active = query.sort === sort;
  const ascending = query.direction === 'asc';
  return (
    <Table.Th
      scope="col"
      ta={ta}
      aria-sort={active ? (ascending ? 'ascending' : 'descending') : undefined}
      data-testid={`sort-header-${sort}`}
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

/** A Song's highest Generation rating (#226): its stars, or a dash when none is rated. */
function RatingCell({ rating }: { rating: number | null | undefined }) {
  return (
    <Table.Td ta="end" style={{ whiteSpace: 'nowrap' }} data-testid="song-rating">
      {rating === null || rating === undefined ? (
        <NoneDash />
      ) : (
        <>
          <span aria-hidden="true">★ </span>
          {rating}
          <VisuallyHidden> of 5</VisuallyHidden>
        </>
      )}
    </Table.Td>
  );
}

/** A blank cell's content: a dash to the eye, "None" to a screen reader. */
function NoneDash() {
  return (
    <>
      <span aria-hidden="true">—</span>
      <VisuallyHidden>None</VisuallyHidden>
    </>
  );
}

function SongRow({ song, timeZone, from }: { song: Song; timeZone: string; from: FromSongs }) {
  return (
    <Table.Tr data-song={song.shortcode}>
      <Table.Td>
        <SongPlayButton song={song} />
      </Table.Td>
      <Table.Th scope="row" style={{ whiteSpace: 'nowrap' }}>
        {song.shortcode}
      </Table.Th>
      <Table.Td>
        <Group gap="sm" wrap="nowrap">
          <ArtworkImage artwork={song.artwork} title={song.title} size="96" pixels={40} />
          <Anchor component={Link} to={`/songs/${song.shortcode}`} state={from}>
            {song.title}
          </Anchor>
        </Group>
      </Table.Td>
      <Table.Td style={{ maxWidth: 200 }}>{song.credits.primary?.name}</Table.Td>
      <Table.Td style={{ maxWidth: 260 }}>
        {song.concept !== null && <TruncatedConcept concept={song.concept} />}
      </Table.Td>
      <Table.Td>
        <StateBadge name={song.state.name} colour={song.state.colour} />
      </Table.Td>
      <Table.Td>{kindLabel(song.currentVersion.kind)}</Table.Td>
      <Table.Td ta="end">{song.versionCount}</Table.Td>
      <Table.Td style={{ maxWidth: 200 }} data-testid="song-genres">
        {song.genres.map((genre) => genre.name).join(', ')}
      </Table.Td>
      <Table.Td visibleFrom="md" style={{ maxWidth: 240 }}>
        {song.tags.length > 0 && <TagLabels tags={song.tags} limit={TAGS_PER_ROW} />}
      </Table.Td>
      <Table.Td style={{ whiteSpace: 'nowrap' }} data-testid="song-created">
        {formatDate(song.createdAt, timeZone)}
      </Table.Td>
      <Table.Td style={{ whiteSpace: 'nowrap' }}>
        <RelativeTime utc={song.updatedAt} timeZone={timeZone} />
      </Table.Td>
      <RatingCell rating={song.highestRating} />
      <Table.Td visibleFrom="md" style={{ maxWidth: 200 }} data-testid="song-workspace">
        {song.sunoWorkspace === null ? <NoneDash /> : workspaceName(song.sunoWorkspace)}
      </Table.Td>
      <Table.Td style={{ whiteSpace: 'nowrap' }} data-testid="song-selected">
        {song.selectedGeneration?.shortcode ?? 'None'}
      </Table.Td>
      <Table.Td ta="end" data-testid="song-audio-file-count">
        {song.audioFileCount === undefined || song.audioFileCount === 0 ? '' : song.audioFileCount}
      </Table.Td>
    </Table.Tr>
  );
}

/** One match: the field, its Version or Generation and mark, and the excerpt; it opens where it matched. */
function MatchItem({ song, match, from }: { song: Song; match: SongMatch; from: FromSongs }) {
  const owner = matchOwnerText(match);
  const state = matchStateText(match);
  return (
    <li data-testid="song-match" data-field={match.field} data-owner={match.owner?.reference}>
      <Anchor component={Link} to={matchAddress(song.shortcode, match)} state={from} size="sm">
        <Text span fw={700} size="sm">
          {matchFieldName(match.field)}
        </Text>
        {owner !== undefined && (
          <>
            {' '}
            <Text span size="sm" data-testid="match-owner">
              {owner}
            </Text>
          </>
        )}
        {state !== undefined && (
          <>
            {' '}
            <Text span size="sm" fs="italic" data-testid="match-state">
              {`(${state})`}
            </Text>
          </>
        )}
      </Anchor>
      <Text span size="sm">
        {': '}
        <MatchExcerpt excerpt={match.excerpt} />
      </Text>
    </li>
  );
}

type MoreState =
  | { phase: 'closed' }
  | { phase: 'loading' }
  | { phase: 'failed' }
  | { phase: 'open'; matches: SongMatch[]; matchCount: number };

/**
 * Where a searched Song matched, in a row beneath it: its best three matches, and "n more" that
 * fetches the rest (up to fifty) in place.
 */
function MatchRows({ song, search, from }: { song: Song; search: string; from: FromSongs }) {
  const [more, setMore] = useState<MoreState>({ phase: 'closed' });
  const best = song.matches ?? [];
  const count = song.matchCount ?? best.length;
  const shown = more.phase === 'open' ? more.matches : best;
  const total = more.phase === 'open' ? more.matchCount : count;
  const hidden = count - best.length;

  const loadMore = async () => {
    setMore({ phase: 'loading' });
    const answer = await readSongMatches(song.id, search);
    setMore(answer === undefined ? { phase: 'failed' } : { phase: 'open', ...answer });
  };

  if (best.length === 0) {
    return null;
  }
  return (
    <Table.Tr data-testid="song-matches" data-matches-for={song.shortcode}>
      <Table.Td colSpan={COLUMN_COUNT} pt={0} pl="xl">
        <Stack gap={4}>
          <ul
            aria-label={`Where ${song.shortcode} matched`}
            style={{ margin: 0, paddingLeft: '1.25rem', display: 'grid', gap: 2 }}
          >
            {shown.map((match, index) => (
              // Matches have no ID of their own; their order is fixed for one search.
              <MatchItem key={index} song={song} match={match} from={from} />
            ))}
          </ul>
          {more.phase === 'closed' && hidden > 0 && (
            <Group>
              <Button
                variant="subtle"
                size="compact-sm"
                onClick={() => {
                  void loadMore();
                }}
              >
                {`${String(hidden)} more ${hidden === 1 ? 'match' : 'matches'}`}{' '}
                <VisuallyHidden>{`in ${song.shortcode}`}</VisuallyHidden>
              </Button>
            </Group>
          )}
          {more.phase === 'loading' && <Loader size="xs" aria-label="Loading more matches" />}
          {more.phase === 'failed' && (
            <Group gap="xs">
              <Text size="sm" role="alert">
                The other matches could not be loaded.
              </Text>
              <Button
                variant="default"
                size="compact-sm"
                onClick={() => {
                  void loadMore();
                }}
              >
                Try again
              </Button>
            </Group>
          )}
          {more.phase === 'open' && total > shown.length && (
            <Text size="sm" data-testid="matches-capped">
              {`Showing the first ${String(SONG_MATCHES_LIMIT)} of ${String(total)} matches.`}
            </Text>
          )}
        </Stack>
      </Table.Td>
    </Table.Tr>
  );
}

/**
 * The Songs table (#59, #224): each Song with its shortcode, title, Artist, Concept, state, kind,
 * Versions, Genres, Tags, creation date, last update, highest Generation rating (#226), Suno
 * workspace, Selected Generation (its shortcode, or "None"), and local audio files. Title, state,
 * created, updated, rating, and audio files sort from their headers (#226). While a search is active (`search`), each Song has a
 * row beneath it showing where it matched. On a narrow screen Tags and workspace are hidden first.
 */
export function SongsTable({
  songs,
  query,
  onSort,
  timeZone,
  from,
}: {
  songs: Song[];
  query: SongQuery;
  onSort: (sort: SongSort) => void;
  timeZone: string;
  from: FromSongs;
}) {
  const search = query.search;
  return (
    <Table.ScrollContainer minWidth={1000}>
      <Table withTableBorder aria-label="Songs">
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col">
              <VisuallyHidden>Play</VisuallyHidden>
            </Table.Th>
            <Table.Th scope="col">Shortcode</Table.Th>
            <SortHeader sort="title" query={query} onSort={onSort} />
            <Table.Th scope="col">Artist</Table.Th>
            <Table.Th scope="col">Concept</Table.Th>
            <SortHeader sort="state" label="State" query={query} onSort={onSort} />
            <Table.Th scope="col">Kind</Table.Th>
            <Table.Th scope="col" ta="end">
              Versions
            </Table.Th>
            <Table.Th scope="col">Genre</Table.Th>
            <Table.Th scope="col" visibleFrom="md">
              Tags
            </Table.Th>
            <SortHeader sort="created" query={query} onSort={onSort} />
            <SortHeader sort="updated" query={query} onSort={onSort} />
            <SortHeader sort="rating" query={query} onSort={onSort} ta="end" />
            <Table.Th scope="col" visibleFrom="md">
              Workspace
            </Table.Th>
            <Table.Th scope="col">Selected Generation</Table.Th>
            <SortHeader sort="audioFiles" query={query} onSort={onSort} />
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {songs.map((song) => (
            <SongEntry
              key={`${song.id}:${search ?? ''}`}
              song={song}
              search={search}
              timeZone={timeZone}
              from={from}
            />
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

function SongEntry({
  song,
  search,
  timeZone,
  from,
}: {
  song: Song;
  search: string | undefined;
  timeZone: string;
  from: FromSongs;
}) {
  return (
    <>
      <SongRow song={song} timeZone={timeZone} from={from} />
      {search !== undefined && <MatchRows song={song} search={search} from={from} />}
    </>
  );
}
