import {
  Anchor,
  Badge,
  Button,
  Group,
  List,
  Loader,
  NativeSelect,
  Pagination,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import {
  UNMATCHED_PAGE_SIZE,
  unmatchedParameters,
  unmatchedQueryFrom,
  useUnmatchedFiles,
  type MatchSuggestion,
  type SortDirection,
  type UnmatchedFile,
  type UnmatchedPage,
  type UnmatchedQuery,
  type UnmatchedSort,
} from '../api/audioFiles';
import { formatSize } from '../api/backups';
import { formatDateTime, useConfiguredTimeZone } from '../api/timeZone';
import { Notice } from '../components/Notice';
import { countText } from './mediaRules';
import { clockText, folderText, reasonText, unmatchedReasonText } from './unmatchedRules';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

const PAGE_CONTROL_LABELS: Record<'first' | 'previous' | 'next' | 'last', string> = {
  first: 'First page',
  previous: 'Previous page',
  next: 'Next page',
  last: 'Last page',
};

/** The orders offered, each with its direction, as one choice. */
const ORDERS: { value: string; label: string; sort: UnmatchedSort; direction: SortDirection }[] = [
  {
    value: 'firstSeen-desc',
    label: 'First seen, newest first',
    sort: 'firstSeen',
    direction: 'desc',
  },
  {
    value: 'firstSeen-asc',
    label: 'First seen, oldest first',
    sort: 'firstSeen',
    direction: 'asc',
  },
  { value: 'name-asc', label: 'Name, A to Z', sort: 'name', direction: 'asc' },
  { value: 'name-desc', label: 'Name, Z to A', sort: 'name', direction: 'desc' },
  { value: 'folder-asc', label: 'Folder, A to Z', sort: 'folder', direction: 'asc' },
  { value: 'folder-desc', label: 'Folder, Z to A', sort: 'folder', direction: 'desc' },
];

const fileCountText = (count: number) => `${countText(count)} ${count === 1 ? 'file' : 'files'}`;

/** The search and the order, kept in the address so a reload or a shared link shows the same list. */
function Filters({
  query,
  onChange,
}: {
  query: UnmatchedQuery;
  onChange: (query: UnmatchedQuery) => void;
}) {
  const [search, setSearch] = useState(query.q);
  return (
    <Group align="flex-end" gap="sm">
      <Group
        component="form"
        role="search"
        aria-label="Unmatched files"
        align="flex-end"
        gap="sm"
        onSubmit={(event) => {
          event.preventDefault();
          onChange({ ...query, q: search, page: 1 });
        }}
      >
        <TextInput
          label="Search"
          description="Text in the file name or folder"
          value={search}
          onChange={(event) => {
            setSearch(event.currentTarget.value);
          }}
        />
        <Button type="submit" variant="default">
          Search
        </Button>
      </Group>
      <NativeSelect
        label="Sort by"
        data={ORDERS.map(({ value, label }) => ({ value, label }))}
        value={`${query.sort}-${query.direction}`}
        onChange={(event) => {
          const order = ORDERS.find((option) => option.value === event.currentTarget.value);
          if (order !== undefined) {
            onChange({ ...query, sort: order.sort, direction: order.direction, page: 1 });
          }
        }}
      />
    </Group>
  );
}

/** One suggested Song: the Song, the Generation if the evidence points at one, and every reason. */
function Suggestion({ suggestion }: { suggestion: MatchSuggestion }) {
  const { song, generation } = suggestion;
  return (
    <List.Item data-testid="suggestion" data-song={song.shortcode}>
      <Text size="sm" span>
        <Anchor component={Link} to={`/go/${song.shortcode}`} underline="always">
          {song.title}
        </Anchor>{' '}
        ({song.shortcode})
        {generation !== null && (
          <>
            {', Generation '}
            <Anchor
              component={Link}
              to={`/go/${generation.shortcode}`}
              underline="always"
              data-testid="suggested-generation"
            >
              {generation.shortcode}
            </Anchor>
          </>
        )}
      </Text>
      <List size="xs" withPadding listStyleType="disc" data-testid="suggestion-reasons">
        {suggestion.reasons.map((reason, index) => (
          <List.Item key={`${reason.code}-${String(index)}`}>{reasonText(reason)}</List.Item>
        ))}
      </List>
    </List.Item>
  );
}

/** One file's row: what it is, where, its status, when it was first seen, and its suggestions. */
function FileRow({ file, timeZone }: { file: UnmatchedFile; timeZone: string }) {
  const why = unmatchedReasonText(file.unmatchedReason);
  return (
    <Table.Tr data-testid="unmatched-file" data-file={file.path}>
      <Table.Th scope="row" fw="normal">
        <Text size="sm" fw={600} style={{ wordBreak: 'break-word' }}>
          {file.fileName}
        </Text>
        {why !== null && (
          <Text size="xs" data-testid="unmatched-reason" data-reason={file.unmatchedReason}>
            {why}
          </Text>
        )}
      </Table.Th>
      <Table.Td style={{ wordBreak: 'break-word' }}>{folderText(file)}</Table.Td>
      <Table.Td tt="uppercase">{file.format}</Table.Td>
      <Table.Td>{clockText(file.durationSeconds)}</Table.Td>
      <Table.Td style={{ whiteSpace: 'nowrap' }}>{formatSize(file.sizeBytes)}</Table.Td>
      <Table.Td>
        {file.status === 'available' ? (
          <Text size="sm" data-testid="file-status" data-status={file.status}>
            Available
          </Text>
        ) : (
          <Badge
            variant="filled"
            radius="sm"
            tt="none"
            data-testid="file-status"
            data-status={file.status}
          >
            {file.status === 'missing' ? 'Missing' : 'Unavailable'}
          </Badge>
        )}
      </Table.Td>
      <Table.Td>{formatDateTime(file.firstSeenAt, timeZone)}</Table.Td>
      <Table.Td>
        {file.suggestions.length === 0 ? (
          <Text size="sm" data-testid="no-suggestions">
            None
          </Text>
        ) : (
          <List
            type="ordered"
            size="sm"
            spacing={4}
            aria-label={`Suggested Songs for ${file.fileName}`}
          >
            {file.suggestions.map((suggestion) => (
              <Suggestion key={suggestion.song.id} suggestion={suggestion} />
            ))}
          </List>
        )}
      </Table.Td>
    </Table.Tr>
  );
}

function FileTable({ page, timeZone }: { page: UnmatchedPage; timeZone: string }) {
  return (
    <Table.ScrollContainer minWidth={960}>
      <Table withTableBorder verticalSpacing="sm" aria-label="Unmatched files">
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col">File</Table.Th>
            <Table.Th scope="col">Folder</Table.Th>
            <Table.Th scope="col">Format</Table.Th>
            <Table.Th scope="col">Duration</Table.Th>
            <Table.Th scope="col">Size</Table.Th>
            <Table.Th scope="col">Status</Table.Th>
            <Table.Th scope="col">First seen</Table.Th>
            <Table.Th scope="col">Suggested Songs</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {page.items.map((file) => (
            <FileRow key={file.id} file={file} timeZone={timeZone} />
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

/**
 * Library → Unmatched Files (#209), at `/library/unmatched`: every audio file with no association,
 * Missing ones included, with up to three suggested Songs each and the evidence for every suggestion.
 * Suggestions are only suggestions: nothing here associates a file, and a file stays listed until it
 * is associated (there is no way to hide one). Searched by text in the name or folder, sorted by first
 * seen (newest first by default), name, or folder, and paged at 100, all of it in the address.
 */
export function UnmatchedFilesPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const query = unmatchedQueryFrom(searchParams);
  const { state, reload } = useUnmatchedFiles(query);
  const timeZone = useConfiguredTimeZone();
  const page = state.phase === 'ready' ? state.data : undefined;
  const pages = page === undefined ? 1 : Math.max(1, Math.ceil(page.total / UNMATCHED_PAGE_SIZE));

  const change = (next: UnmatchedQuery) => {
    setSearchParams(unmatchedParameters(next));
  };

  return (
    <Stack gap="lg">
      <Title order={2}>Unmatched Files</Title>
      <Text>
        Audio files in the media folder that are not associated with a Song. Each shows the Songs it
        may belong to and why; a suggestion is only a suggestion, and nothing is associated until
        you choose. A file stays here, scan after scan, until it is associated.
      </Text>
      <Filters key={searchParams.toString()} query={query} onChange={change} />
      {state.phase === 'loading' && <Loader aria-label="Loading the unmatched files" size="sm" />}
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Notice title="The unmatched files could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <div>
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
          </div>
        </Notice>
      )}
      {page?.total === 0 && (
        <Text data-testid="no-unmatched-files">
          {query.q.trim() !== '' ? 'No unmatched file matches.' : 'No unmatched files'}
        </Text>
      )}
      {page !== undefined && page.total > 0 && (
        <>
          <Text size="sm" data-testid="unmatched-total">
            {fileCountText(page.total)}
          </Text>
          <FileTable page={page} timeZone={timeZone} />
          {pages > 1 && (
            <Group component="nav" aria-label="Pages" justify="flex-end">
              <Pagination
                total={pages}
                value={query.page}
                onChange={(next) => {
                  change({ ...query, page: next });
                }}
                withEdges
                getItemProps={(item) => ({ 'aria-label': `Page ${String(item)}` })}
                getControlProps={(control) => ({ 'aria-label': PAGE_CONTROL_LABELS[control] })}
              />
            </Group>
          )}
        </>
      )}
    </Stack>
  );
}
