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
  VisuallyHidden,
} from '@mantine/core';
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import {
  associateFile,
  rematchFile,
  UNMATCHED_PAGE_SIZE,
  unmatchedParameters,
  unmatchedQueryFrom,
  useUnmatchedFiles,
  type MatchSuggestion,
  type SortDirection,
  type UnmatchedFile,
  type UnmatchedPage,
  type UnmatchedQuery,
  type UnmatchedShow,
  type UnmatchedSort,
} from '../api/audioFiles';
import type { SaveResult } from '../api/saves';
import { formatSize } from '../api/backups';
import { formatDateTime, useConfiguredTimeZone } from '../api/timeZone';
import { Notice } from '../components/Notice';
import { PlayButton } from '../player/PlayButton';
import { AssociateFileDialog } from './AssociateFileDialog';
import { countText } from './mediaRules';
import {
  associationText,
  clockText,
  folderText,
  originText,
  reasonText,
  unmatchedReasonText,
} from './unmatchedRules';

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

/** Which files are listed (#210): the unmatched ones by default. */
const SHOWS: { value: UnmatchedShow; label: string }[] = [
  { value: 'unmatched', label: 'Unmatched' },
  { value: 'associated', label: 'Associated' },
  { value: 'all', label: 'All' },
];

const fileCountText = (count: number) => `${countText(count)} ${count === 1 ? 'file' : 'files'}`;

/** What the empty list says, by what is listed and whether there is a search. */
function emptyText(query: UnmatchedQuery): string {
  const searched = query.q.trim() !== '';
  switch (query.show) {
    case 'associated':
      return searched ? 'No associated file matches.' : 'No associated files';
    case 'all':
      return searched ? 'No file matches.' : 'No files';
    default:
      return searched ? 'No unmatched file matches.' : 'No unmatched files';
  }
}

/** The search, the files listed, and the order, kept in the address so a reload or a shared link shows the same list. */
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
        label="Show"
        data={SHOWS}
        value={query.show}
        onChange={(event) => {
          const show = SHOWS.find((option) => option.value === event.currentTarget.value);
          if (show !== undefined) {
            onChange({ ...query, show: show.value, page: 1 });
          }
        }}
      />
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

/** A suggestion as the Associate button names it: the Song, and the Generation when there is one. */
function suggestionText({ song, generation }: MatchSuggestion): string {
  const named = `${song.title} (${song.shortcode})`;
  return generation === null ? named : `${named}, Generation ${generation.shortcode}`;
}

/**
 * One suggested Song: the Song, the Generation if the evidence points at one, every reason, and the
 * button that accepts it in one action (to that Generation when the suggestion names one).
 */
function Suggestion({
  suggestion,
  fileName,
  busy,
  onAccept,
}: {
  suggestion: MatchSuggestion;
  fileName: string;
  busy: boolean;
  onAccept: () => void;
}) {
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
      <Button
        size="compact-xs"
        variant="default"
        mt={4}
        disabled={busy}
        aria-label={`Associate ${fileName} with ${suggestionText(suggestion)}`}
        onClick={onAccept}
      >
        Associate
      </Button>
    </List.Item>
  );
}

/** What a row can ask of the page: accept a suggestion, open the dialog, or match by Suno ID again. */
interface RowActions {
  busy: boolean;
  onAccept: (file: UnmatchedFile, suggestion: MatchSuggestion) => void;
  onOpen: (file: UnmatchedFile) => void;
  onRematch: (file: UnmatchedFile) => void;
}

/** An associated file's association: the Song and Generation, linked, and who made it. */
function Association({ file }: { file: UnmatchedFile }) {
  const { song, generation } = file;
  if (song === null) {
    return null;
  }
  return (
    <Text size="sm" data-testid="file-association" data-song={song.shortcode}>
      <Anchor component={Link} to={`/go/${song.shortcode}`} underline="always">
        {song.title}
      </Anchor>{' '}
      ({song.shortcode})
      {generation !== null && (
        <>
          {', Generation '}
          <Anchor component={Link} to={`/go/${generation.shortcode}`} underline="always">
            {generation.shortcode}
          </Anchor>
        </>
      )}
      {`, ${originText(file.associationOrigin)}`}
    </Text>
  );
}

/**
 * One file's row: what it is, where, its status, when it was first seen, and its Song: the association
 * with Change or remove for an associated file; for an unmatched one, its suggestions (each accepted in
 * one action), a way to choose any Song, and, when the user blocked automatic matching, Match by Suno
 * ID again.
 */
function FileRow({
  file,
  timeZone,
  actions,
}: {
  file: UnmatchedFile;
  timeZone: string;
  actions: RowActions;
}) {
  const why = unmatchedReasonText(file.unmatchedReason);
  return (
    <Table.Tr data-testid="unmatched-file" data-file={file.path}>
      <Table.Td>
        <PlayButton target={{ kind: 'file', file }} />
      </Table.Td>
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
        {file.song !== null ? (
          <Stack gap={4} align="flex-start">
            <Association file={file} />
            <Button
              size="compact-xs"
              variant="default"
              disabled={actions.busy}
              aria-label={`Change or remove the association of ${file.fileName}`}
              onClick={() => {
                actions.onOpen(file);
              }}
            >
              Change or remove…
            </Button>
          </Stack>
        ) : (
          <Stack gap={6} align="flex-start">
            {file.suggestions.length === 0 ? (
              <Text size="sm" data-testid="no-suggestions">
                No suggestions
              </Text>
            ) : (
              <List
                type="ordered"
                size="sm"
                spacing={4}
                aria-label={`Suggested Songs for ${file.fileName}`}
              >
                {file.suggestions.map((suggestion) => (
                  <Suggestion
                    key={suggestion.song.id}
                    suggestion={suggestion}
                    fileName={file.fileName}
                    busy={actions.busy}
                    onAccept={() => {
                      actions.onAccept(file, suggestion);
                    }}
                  />
                ))}
              </List>
            )}
            <Group gap="xs">
              <Button
                size="compact-xs"
                variant="default"
                disabled={actions.busy}
                aria-label={`Choose a Song for ${file.fileName}`}
                onClick={() => {
                  actions.onOpen(file);
                }}
              >
                Choose a Song…
              </Button>
              {file.autoMatchBlocked && (
                <Button
                  size="compact-xs"
                  variant="default"
                  disabled={actions.busy}
                  aria-label={`Match by Suno ID again: ${file.fileName}`}
                  onClick={() => {
                    actions.onRematch(file);
                  }}
                >
                  Match by Suno ID again
                </Button>
              )}
            </Group>
          </Stack>
        )}
      </Table.Td>
    </Table.Tr>
  );
}

function FileTable({
  page,
  timeZone,
  actions,
}: {
  page: UnmatchedPage;
  timeZone: string;
  actions: RowActions;
}) {
  return (
    <Table.ScrollContainer minWidth={960}>
      <Table withTableBorder verticalSpacing="sm" aria-label="Audio files">
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col">
              <VisuallyHidden>Play</VisuallyHidden>
            </Table.Th>
            <Table.Th scope="col">File</Table.Th>
            <Table.Th scope="col">Folder</Table.Th>
            <Table.Th scope="col">Format</Table.Th>
            <Table.Th scope="col">Duration</Table.Th>
            <Table.Th scope="col">Size</Table.Th>
            <Table.Th scope="col">Status</Table.Th>
            <Table.Th scope="col">First seen</Table.Th>
            <Table.Th scope="col">Song</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {page.items.map((file) => (
            <FileRow key={file.id} file={file} timeZone={timeZone} actions={actions} />
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

/** What a failed row action says, by how it failed. */
function actionFailureText(result: Exclude<SaveResult<unknown>, { kind: 'saved' }>): string {
  if (result.kind === 'conflict') {
    return 'That file changed since the list was loaded. The list now shows it as it is: check it and try again.';
  }
  if (result.kind === 'failed' && result.reason === 'gone') {
    return 'That file, Song, or Generation is no longer there. The list has been read again.';
  }
  return 'n8Tracks did not answer as expected. Check that it is running and try again.';
}

/**
 * Library → Unmatched Files (#209), at `/library/unmatched`: every audio file with no association,
 * Missing ones included, with up to three suggested Songs each and the evidence for every suggestion.
 * A suggestion is only a suggestion until the user accepts it (#210): its Associate button associates
 * the file in one action, and "Choose a Song…" opens a dialog to find any Song and, optionally, one of
 * its Generations. The Show filter lists the associated files (or all) too, each with its association
 * and a way to change or remove it; a file the user unassociated, whose name holds a UUID, offers
 * "Match by Suno ID again". After each change the list is read again, so an associated file leaves the
 * unmatched list at once. Searched by text in the name or folder, sorted by first seen (newest first by
 * default), name, or folder, and paged at 100, all of it in the address.
 */
export function UnmatchedFilesPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const query = unmatchedQueryFrom(searchParams);
  const { state, reload } = useUnmatchedFiles(query);
  const timeZone = useConfiguredTimeZone();
  const page = state.phase === 'ready' ? state.data : undefined;
  const pages = page === undefined ? 1 : Math.max(1, Math.ceil(page.total / UNMATCHED_PAGE_SIZE));
  const [open, setOpen] = useState<UnmatchedFile | undefined>();
  const [busy, setBusy] = useState(false);
  const [announcement, setAnnouncement] = useState('');
  const [failure, setFailure] = useState<string | undefined>();

  const change = (next: UnmatchedQuery) => {
    setSearchParams(unmatchedParameters(next));
  };

  const changed = (message?: string) => {
    if (message !== undefined) {
      setAnnouncement(message);
      setFailure(undefined);
    }
    reload();
  };

  const act = async (
    write: () => Promise<SaveResult<UnmatchedFile>>,
    message: (saved: UnmatchedFile) => string,
  ) => {
    setBusy(true);
    setFailure(undefined);
    const result = await write();
    setBusy(false);
    if (result.kind === 'saved') {
      setAnnouncement(message(result.record));
    } else {
      setAnnouncement('');
      setFailure(actionFailureText(result));
    }
    reload();
  };

  const actions: RowActions = {
    busy,
    onAccept: (file, suggestion) => {
      void act(
        () => associateFile(file, suggestion.song.id, suggestion.generation?.id ?? null),
        (saved) => `Associated ${file.fileName} with ${associationText(saved)}.`,
      );
    },
    onOpen: (file) => {
      setOpen(file);
    },
    onRematch: (file) => {
      void act(
        () => rematchFile(file),
        (saved) =>
          saved.song === null
            ? `${file.fileName} matches no Generation by its Suno ID; it stays unmatched.`
            : `Matched ${file.fileName} by its Suno ID with ${associationText(saved)}.`,
      );
    },
  };

  return (
    <Stack gap="lg">
      <Title order={2}>Unmatched Files</Title>
      <Text>
        Audio files in the media folder that are not associated with a Song. Each shows the Songs it
        may belong to and why; a suggestion is only a suggestion, and nothing is associated until
        you choose. A file stays here, scan after scan, until it is associated. Show the associated
        files to change or remove an association. Associating changes nothing in the media folder.
      </Text>
      <Filters key={searchParams.toString()} query={query} onChange={change} />
      <Text size="sm" role="status" aria-live="polite" data-testid="association-announcement">
        {announcement}
      </Text>
      {failure !== undefined && (
        <Notice title="Not saved">
          <Text>{failure}</Text>
        </Notice>
      )}
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
      {page?.total === 0 && <Text data-testid="no-unmatched-files">{emptyText(query)}</Text>}
      {page !== undefined && page.total > 0 && (
        <>
          <Text size="sm" data-testid="unmatched-total">
            {fileCountText(page.total)}
          </Text>
          <FileTable page={page} timeZone={timeZone} actions={actions} />
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
      <AssociateFileDialog
        file={open}
        onClose={() => {
          setOpen(undefined);
        }}
        onChanged={changed}
      />
    </Stack>
  );
}
