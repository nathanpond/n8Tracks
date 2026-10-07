import {
  Anchor,
  Button,
  Checkbox,
  Group,
  Loader,
  Modal,
  NativeSelect,
  Pagination,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useEffect, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router';
import type { LoadState } from '../api/songs';
import {
  changeImportChoices,
  discardImport,
  MAXIMUM_NAMED_RECORDS,
  RECORD_CLASSES,
  useImportRecords,
  useImportSummary,
  useSunoImport,
  type ImportChoice,
  type ImportFilter,
  type ImportRecord,
  type ImportSummary,
  type RecordSelection,
  type SunoImport,
} from '../api/sunoImports';
import { formatDateTime, useConfiguredTimeZone } from '../api/timeZone';
import { Notice } from '../components/Notice';
import { ChoiceEditor } from './ChoiceEditor';
import {
  CLASS_LABELS,
  choiceNote,
  choiceText,
  countText,
  durationText,
  excludedKindsText,
  filterFrom,
  groupRecords,
  isReviewable,
  reasonsText,
  recordCountText,
  stateText,
  UNKNOWN_KIND_FLAG,
} from './importReviewRules';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

const PAGE_CONTROL_LABELS: Record<'first' | 'previous' | 'next' | 'last', string> = {
  first: 'First page',
  previous: 'Previous page',
  next: 'Next page',
  last: 'Last page',
};

/** How often an export that is still being read or prepared is read again. */
const PREPARING_POLL_MS = 3000;

/**
 * Which records the next change names: some by Suno ID, or every record the review can change that
 * matches a filter (`label` says which), but those unticked since.
 */
type Selection =
  | { kind: 'ids'; ids: string[] }
  | { kind: 'filter'; filter: ImportFilter; label: string; except: string[] };

const NONE: Selection = { kind: 'ids', ids: [] };

/** Whether `record` matches `filter`, as the server's filter does (the title search ignoring case). */
function matches(record: ImportRecord, filter: ImportFilter): boolean {
  return (
    (filter.class === undefined || record.class === filter.class) &&
    (filter.workspace === undefined || record.workspaceId === filter.workspace) &&
    (filter.playlist === undefined || record.playlistIds.includes(filter.playlist)) &&
    (filter.q === undefined || (record.title ?? '').toLowerCase().includes(filter.q.toLowerCase()))
  );
}

function isSelected(selection: Selection, record: ImportRecord): boolean {
  if (!isReviewable(record)) {
    return false;
  }
  return selection.kind === 'ids'
    ? selection.ids.includes(record.sunoId)
    : matches(record, selection.filter) && !selection.except.includes(record.sunoId);
}

/** The data of a load once it has been ready, kept while it loads again, so the page does not flash. */
function useLastReady<T>(state: LoadState<T>): T | undefined {
  const [last, setLast] = useState<T>();
  // Derived state, kept during render (React's pattern for a value that follows a prop).
  if (state.phase === 'ready' && state.data !== last) {
    setLast(state.data);
  }
  return state.phase === 'ready' ? state.data : last;
}

/** An export that is not open for review: its state and nothing else. */
function ImportStateNotice({ state }: { state: SunoImport['state'] }) {
  const { title, text } = stateText(state);
  return (
    <Stack gap="sm" data-testid="import-state" data-state={state}>
      <Title order={3}>{title}</Title>
      <Text>{text}</Text>
      {(state === 'receiving' || state === 'classifying') && (
        <Loader size="sm" aria-label="Waiting for the import" />
      )}
    </Stack>
  );
}

/**
 * The review page of one Suno export (#139), at `/suno/imports/<id>`: everything the sync found, what
 * n8Tracks proposes for each record, and the choices to change before confirming. Nothing in the
 * catalog changes here: choices are saved on the export as they are made, and only Confirm (#140)
 * applies them. An export that is not ready shows its state only.
 */
export function ImportReviewPage() {
  const { id = '' } = useParams();
  const { state, reload } = useSunoImport(id);
  const exportState = state.phase === 'ready' ? state.data.state : undefined;

  // An export still being read or prepared is read again until it is ready (or ends).
  useEffect(() => {
    if (exportState !== 'receiving' && exportState !== 'classifying') {
      return;
    }
    const timer = window.setTimeout(reload, PREPARING_POLL_MS);
    return () => {
      window.clearTimeout(timer);
    };
  }, [exportState, reload]);

  return (
    <Stack gap="lg">
      <Title order={2}>Review Suno import</Title>
      {state.phase === 'loading' && <Loader aria-label="Loading the import" />}
      {state.phase === 'not-found' && (
        <Text data-testid="import-not-found">
          There is no such import. Open the{' '}
          <Anchor component={Link} to="/suno/imports" underline="always">
            Suno import
          </Anchor>{' '}
          page to see the one waiting, if any.
        </Text>
      )}
      {state.phase === 'error' && (
        <Notice title="The import could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <div>
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
          </div>
        </Notice>
      )}
      {state.phase === 'ready' && state.data.state !== 'ready' && (
        <ImportStateNotice state={state.data.state} />
      )}
      {state.phase === 'ready' && state.data.state === 'ready' && (
        <LoadedReview id={id} onEnded={reload} />
      )}
    </Stack>
  );
}

/** The review of a ready export: its summary, filters, records, selection, and choices. */
function LoadedReview({ id, onEnded }: { id: string; onEnded: () => void }) {
  const timeZone = useConfiguredTimeZone();
  const [searchParams, setSearchParams] = useSearchParams();
  const filter = filterFrom(searchParams);
  const page = Math.max(1, Number(searchParams.get('page') ?? '1') || 1);
  const summaryLoad = useImportSummary(id);
  const recordsLoad = useImportRecords(id, filter, page);
  const summary = useLastReady(summaryLoad.state);
  const records = useLastReady(recordsLoad.state);
  const [selection, setSelection] = useState<Selection>(NONE);
  const [busy, setBusy] = useState(false);
  const [refusals, setRefusals] = useState<Record<string, string[]>>({});
  const [message, setMessage] = useState<string>();
  const [conflict, setConflict] = useState(false);
  const [discarding, setDiscarding] = useState(false);
  const [discardMessage, setDiscardMessage] = useState<string>();
  // A new editor, its unsaved choice dropped, after each change and each reload.
  const [editorKey, setEditorKey] = useState(0);
  const ended =
    summaryLoad.state.phase === 'not-found' ||
    (summary !== undefined && summary.export.state !== 'ready');

  // The export ended meanwhile (discarded elsewhere, expired): the page shows its state instead.
  useEffect(() => {
    if (ended) {
      onEnded();
    }
  }, [ended, onEnded]);

  if (summary === undefined || records === undefined) {
    if (summaryLoad.state.phase === 'error' || recordsLoad.state.phase === 'error') {
      return (
        <Notice title="The import could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <div>
            <Button
              variant="default"
              size="xs"
              onClick={() => {
                summaryLoad.reload();
                recordsLoad.reload();
              }}
            >
              Try again
            </Button>
          </div>
        </Notice>
      );
    }
    return <Loader aria-label="Loading the records" />;
  }

  const workspaceNames = new Map(summary.workspaces.map((facet) => [facet.id, facet.name]));
  const workspaceLabel = (workspaceId: string | null) =>
    workspaceId === null ? '' : (workspaceNames.get(workspaceId) ?? workspaceId);
  const reviewable = records.items.filter(isReviewable);
  const selectedHere = reviewable.filter((record) => isSelected(selection, record));
  const selectionCount =
    selection.kind === 'ids'
      ? recordCountText(selection.ids.length)
      : selection.except.length === 0
        ? `every record ${selection.label}`
        : `every record ${selection.label} but ${String(selection.except.length)}`;
  const hasSelection = selection.kind === 'filter' || selection.ids.length > 0;
  const pages = Math.max(1, Math.ceil(records.total / records.pageSize));

  const reloadAll = () => {
    summaryLoad.reload();
    recordsLoad.reload();
  };

  const setQuery = (change: Partial<Record<'class' | 'workspace' | 'playlist' | 'q', string>>) => {
    const next = new URLSearchParams(searchParams);
    for (const [name, value] of Object.entries(change)) {
      if (value === '') {
        next.delete(name);
      } else {
        next.set(name, value);
      }
    }
    next.delete('page');
    setSearchParams(next, { replace: true });
  };

  const toggle = (record: ImportRecord, checked: boolean) => {
    setSelection((current) => {
      if (current.kind === 'filter') {
        return {
          ...current,
          except: checked
            ? current.except.filter((other) => other !== record.sunoId)
            : [...current.except, record.sunoId],
        };
      }
      return {
        kind: 'ids',
        ids: checked
          ? [...current.ids, record.sunoId]
          : current.ids.filter((other) => other !== record.sunoId),
      };
    });
  };

  const apply = async (choice: ImportChoice) => {
    const chosen: RecordSelection =
      selection.kind === 'ids'
        ? { kind: 'ids', sunoIds: selection.ids }
        : { kind: 'filter', filter: selection.filter, except: selection.except };
    if (chosen.kind === 'ids' && chosen.sunoIds.length > MAXIMUM_NAMED_RECORDS) {
      setMessage(
        `Choose at most ${String(MAXIMUM_NAMED_RECORDS)} records one by one, or select all that match instead.`,
      );
      return;
    }
    setBusy(true);
    setMessage(undefined);
    setRefusals({});
    const result = await changeImportChoices(id, summary.revision, chosen, choice);
    setBusy(false);
    switch (result.kind) {
      case 'changed':
        setSelection(NONE);
        setEditorKey((key) => key + 1);
        setMessage('The choice was saved.');
        reloadAll();
        return;
      case 'refused':
        setRefusals(result.records);
        setMessage(
          `Nothing was changed: ${recordCountText(Object.keys(result.records).length)} cannot take this choice. The reasons are on the rows.`,
        );
        return;
      case 'conflict':
        setConflict(true);
        return;
      case 'not-ready':
        onEnded();
        return;
      case 'none':
        setMessage('Nothing was changed: no record the review can change is selected.');
        return;
      default:
        setMessage(`Nothing was changed. ${FAILED_MESSAGE}`);
    }
  };

  const discard = async () => {
    setDiscardMessage(undefined);
    const result = await discardImport(id);
    if (result === 'discarded' || result === 'too-late') {
      setDiscarding(false);
      onEnded();
      return;
    }
    setDiscardMessage(FAILED_MESSAGE);
  };

  const exported = summary.export;
  const excluded = excludedKindsText(summary.libraryExcluded);

  return (
    <Stack gap="lg">
      <Stack gap={4}>
        <Text data-testid="import-captured">
          Read from Suno {formatDateTime(exported.capturedAt, timeZone)}:{' '}
          {recordCountText(exported.counts.total ?? 0)}.
          {exported.expiresAt !== null &&
            ` Review it by ${formatDateTime(exported.expiresAt, timeZone)}; after that it is thrown away.`}
        </Text>
        {excluded.length > 0 && (
          <Text size="sm" data-testid="library-excluded">
            Suno’s library filters left out {excluded.join(', ')}: they are not in this import.
          </Text>
        )}
        <Text size="sm">
          Nothing in your catalog changes until you confirm. Your choices are saved as you make
          them.
        </Text>
      </Stack>

      {conflict && (
        <Notice title="This import changed elsewhere">
          <Text>
            Its choices were changed in another window. Reload to see them; the choice you were
            making here was not saved.
          </Text>
          <div>
            <Button
              size="xs"
              onClick={() => {
                setConflict(false);
                setSelection(NONE);
                setEditorKey((key) => key + 1);
                reloadAll();
              }}
            >
              Reload
            </Button>
          </div>
        </Notice>
      )}

      <ImportSummaryPanel
        summary={summary}
        onDiscard={() => {
          setDiscarding(true);
        }}
      />

      <Group gap="sm" align="flex-end" role="search" aria-label="Filter records">
        <NativeSelect
          label="Class"
          value={filter.class ?? ''}
          data={[
            { value: '', label: 'All classes' },
            ...RECORD_CLASSES.map((recordClass) => ({
              value: recordClass,
              label: `${CLASS_LABELS[recordClass]} (${String(exported.counts[recordClass] ?? 0)})`,
            })),
          ]}
          onChange={(event) => {
            setQuery({ class: event.currentTarget.value });
          }}
        />
        <NativeSelect
          label="Workspace"
          value={filter.workspace ?? ''}
          data={[
            { value: '', label: 'All workspaces' },
            ...summary.workspaces.map((facet) => ({
              value: facet.id,
              label: `${facet.name ?? facet.id} (${String(facet.count)})`,
            })),
          ]}
          onChange={(event) => {
            setQuery({ workspace: event.currentTarget.value });
          }}
        />
        {summary.playlists.length > 0 && (
          <NativeSelect
            label="Playlist"
            value={filter.playlist ?? ''}
            data={[
              { value: '', label: 'All playlists' },
              ...summary.playlists.map((facet) => ({
                value: facet.id,
                label: `${facet.name ?? facet.id} (${String(facet.count)})`,
              })),
            ]}
            onChange={(event) => {
              setQuery({ playlist: event.currentTarget.value });
            }}
          />
        )}
        <TextInput
          label="Search titles"
          value={filter.q ?? ''}
          onChange={(event) => {
            setQuery({ q: event.currentTarget.value });
          }}
        />
      </Group>

      <Stack gap="sm" role="group" aria-label="Select records">
        <Group gap="sm" align="flex-end">
          <Button
            variant="default"
            disabled={records.total === 0}
            onClick={() => {
              setSelection({
                kind: 'filter',
                filter,
                label: 'that matches the filter',
                except: [],
              });
            }}
          >
            Select all that match
          </Button>
          <NativeSelect
            label="Select a workspace’s records"
            value=""
            data={[
              { value: '', label: 'Choose…' },
              ...summary.workspaces.map((facet) => ({
                value: facet.id,
                label: facet.name ?? facet.id,
              })),
            ]}
            onChange={(event) => {
              const workspace = event.currentTarget.value;
              if (workspace !== '') {
                setSelection({
                  kind: 'filter',
                  filter: { workspace },
                  label: `in workspace ${workspaceLabel(workspace)}`,
                  except: [],
                });
              }
            }}
          />
          {summary.playlists.length > 0 && (
            <NativeSelect
              label="Select a playlist’s records"
              value=""
              data={[
                { value: '', label: 'Choose…' },
                ...summary.playlists.map((facet) => ({
                  value: facet.id,
                  label: facet.name ?? facet.id,
                })),
              ]}
              onChange={(event) => {
                const playlist = event.currentTarget.value;
                if (playlist !== '') {
                  const name = summary.playlists.find((facet) => facet.id === playlist)?.name;
                  setSelection({
                    kind: 'filter',
                    filter: { playlist },
                    label: `in playlist ${name ?? playlist}`,
                    except: [],
                  });
                }
              }}
            />
          )}
          <Button
            variant="default"
            disabled={!hasSelection}
            onClick={() => {
              setSelection(NONE);
            }}
          >
            Clear the selection
          </Button>
        </Group>
        <Text size="sm" data-testid="selection">
          {hasSelection
            ? `Selected: ${selectionCount}. Records already in n8Tracks are never selected.`
            : 'No record is selected. Tick records, or select a whole workspace, playlist, or filter.'}
        </Text>
      </Stack>

      <div role="status">
        {message !== undefined && <Text data-testid="change-message">{message}</Text>}
      </div>

      {hasSelection && (
        <ChoiceEditor
          key={editorKey}
          exportId={id}
          sample={selectedHere[0] ?? reviewable[0]}
          nextKey={summary.nextKey}
          count={selectionCount}
          busy={busy}
          onApply={(choice) => {
            void apply(choice);
          }}
        />
      )}

      {records.total === 0 ? (
        <Text data-testid="no-records">No record matches these filters.</Text>
      ) : (
        <RecordTable
          records={records.items}
          selection={selection}
          summary={summary}
          refusals={refusals}
          workspaceLabel={workspaceLabel}
          timeZone={timeZone}
          onToggle={toggle}
          onTogglePage={(checked) => {
            setSelection(
              checked ? { kind: 'ids', ids: reviewable.map((record) => record.sunoId) } : NONE,
            );
          }}
        />
      )}

      {pages > 1 && (
        <Pagination
          total={pages}
          value={page}
          onChange={(next) => {
            const parameters = new URLSearchParams(searchParams);
            parameters.set('page', String(next));
            setSearchParams(parameters);
          }}
          getControlProps={(control) => ({ 'aria-label': PAGE_CONTROL_LABELS[control] })}
          getItemProps={(item) => ({ 'aria-label': `Page ${String(item)}` })}
        />
      )}

      <Modal
        opened={discarding}
        onClose={() => {
          setDiscarding(false);
        }}
        title="Discard this import?"
        centered
        closeButtonProps={{ 'aria-label': 'Close' }}
      >
        <Stack gap="md">
          <Text data-testid="discard-summary">
            Its {recordCountText(exported.counts.total ?? 0)} and your choices are thrown away.
            Nothing in your catalog changes, and nothing changes in Suno: sync again to review the
            records.
          </Text>
          {discardMessage !== undefined && (
            <Text size="sm" role="alert" c="var(--mantine-color-error)">
              {discardMessage}
            </Text>
          )}
          <Group gap="sm" justify="flex-end">
            <Button
              variant="default"
              data-autofocus
              onClick={() => {
                setDiscarding(false);
              }}
            >
              Keep reviewing
            </Button>
            <Button
              onClick={() => {
                void discard();
              }}
            >
              Discard
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}

/** What confirming will do, whether every choice is valid, the Confirm control (#140), and Discard. */
function ImportSummaryPanel({
  summary,
  onDiscard,
}: {
  summary: ImportSummary;
  onDiscard: () => void;
}) {
  const confirmNote = summary.nothingToDo
    ? 'There is nothing to do: no record is set to be imported or added to the ignore list.'
    : !summary.valid
      ? 'Change the marked choices first.'
      : 'Confirming arrives in a later update of n8Tracks; until then nothing changes in your catalog.';
  return (
    <Stack gap="xs" component="section" aria-labelledby="import-summary-title">
      <Title order={3} id="import-summary-title">
        What confirming will do
      </Title>
      <Text data-testid="summary-creates">
        Create {countText(summary.songs, 'Song')}, {countText(summary.versions, 'Version')}, and{' '}
        {countText(summary.generations, 'Generation')}
        {summary.reimports > 0 && ` (${String(summary.reimports)} of them reimported)`}.
      </Text>
      <Text data-testid="summary-ignored">
        Not copy {recordCountText(summary.ignored)} (Don’t copy: on the ignore list).
      </Text>
      <Text data-testid="summary-skipped">
        Leave {recordCountText(summary.skipped)} for a later sync (Skip this time).
      </Text>
      <Text data-testid="choices-valid" fw={600}>
        {summary.valid
          ? 'Every choice is valid.'
          : `${recordCountText(summary.invalidCount)} cannot be imported as chosen; the reasons are on the rows.`}
      </Text>
      <Group gap="sm">
        <Button disabled aria-describedby="confirm-note">
          Confirm import
        </Button>
        <Button variant="default" onClick={onDiscard}>
          Discard import
        </Button>
      </Group>
      <Text size="sm" id="confirm-note" data-testid="confirm-note">
        {confirmNote}
      </Text>
    </Stack>
  );
}

/** The records, grouped under a heading for each target, with a checkbox on each the review can change. */
function RecordTable({
  records,
  selection,
  summary,
  refusals,
  workspaceLabel,
  timeZone,
  onToggle,
  onTogglePage,
}: {
  records: ImportRecord[];
  selection: Selection;
  summary: ImportSummary;
  refusals: Record<string, string[]>;
  workspaceLabel: (id: string | null) => string;
  timeZone: string;
  onToggle: (record: ImportRecord, checked: boolean) => void;
  onTogglePage: (checked: boolean) => void;
}) {
  const reviewable = records.filter(isReviewable);
  const ticked = reviewable.filter((record) => isSelected(selection, record)).length;
  const groups = groupRecords(records);
  return (
    <Table.ScrollContainer minWidth={760}>
      <Table withTableBorder aria-label="Records in this import">
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col" w={40}>
              <Checkbox
                aria-label="Select every record on this page"
                disabled={reviewable.length === 0}
                checked={reviewable.length > 0 && ticked === reviewable.length}
                indeterminate={ticked > 0 && ticked < reviewable.length}
                onChange={(event) => {
                  onTogglePage(event.currentTarget.checked);
                }}
              />
            </Table.Th>
            <Table.Th scope="col">Title</Table.Th>
            <Table.Th scope="col">Workspace</Table.Th>
            <Table.Th scope="col">Created in Suno</Table.Th>
            <Table.Th scope="col">Length</Table.Th>
            <Table.Th scope="col">Class</Table.Th>
            <Table.Th scope="col">What happens</Table.Th>
          </Table.Tr>
        </Table.Thead>
        {groups.map((group) => (
          <Table.Tbody key={group.key} data-group={group.key}>
            {group.heading !== null && (
              <Table.Tr>
                <Table.Th scope="colgroup" colSpan={7} data-testid="group-heading">
                  {group.heading} ({recordCountText(group.records.length)})
                </Table.Th>
              </Table.Tr>
            )}
            {group.records.map((record) => {
              const title = record.title ?? 'Untitled';
              const invalid = summary.invalid[record.sunoId];
              const refused = refusals[record.sunoId];
              const note = choiceNote(record);
              return (
                <Table.Tr key={record.sunoId} data-record={record.sunoId}>
                  <Table.Td>
                    {isReviewable(record) && (
                      <Checkbox
                        aria-label={`Select ${title}`}
                        checked={isSelected(selection, record)}
                        onChange={(event) => {
                          onToggle(record, event.currentTarget.checked);
                        }}
                      />
                    )}
                  </Table.Td>
                  <Table.Th scope="row" fw="normal">
                    <Text size="sm">{title}</Text>
                    {record.flags.includes(UNKNOWN_KIND_FLAG) && (
                      <Text size="xs" fw={700} data-testid="unknown-kind">
                        Check this: its kind is unclear, so it is imported as a Song.
                      </Text>
                    )}
                  </Table.Th>
                  <Table.Td>{workspaceLabel(record.workspaceId)}</Table.Td>
                  <Table.Td>
                    {record.createdAt === null ? '' : formatDateTime(record.createdAt, timeZone)}
                  </Table.Td>
                  <Table.Td>{durationText(record.durationSeconds)}</Table.Td>
                  <Table.Td data-testid="record-class">
                    {record.class === null ? '' : CLASS_LABELS[record.class]}
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm" data-testid="record-choice">
                      {choiceText(record)}
                    </Text>
                    {record.generation !== null && (
                      <Anchor
                        component={Link}
                        size="sm"
                        underline="always"
                        to={`/songs/${record.generation.songShortcode}/generations/${record.generation.shortcode}`}
                      >
                        Generation {record.generation.shortcode}
                      </Anchor>
                    )}
                    {note !== undefined && (
                      <Text size="xs" data-testid="record-note">
                        {note}
                      </Text>
                    )}
                    {invalid !== undefined && (
                      <Text size="xs" fw={700} data-testid="record-invalid">
                        Cannot be imported as chosen: {reasonsText(invalid)}.
                      </Text>
                    )}
                    {refused !== undefined && (
                      <Text size="xs" fw={700} data-testid="record-refused">
                        Not changed: {reasonsText(refused)}.
                      </Text>
                    )}
                  </Table.Td>
                </Table.Tr>
              );
            })}
          </Table.Tbody>
        ))}
      </Table>
    </Table.ScrollContainer>
  );
}
