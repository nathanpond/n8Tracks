import { Anchor, Button, Checkbox, Loader, Pagination, Stack, Table, Text } from '@mantine/core';
import { useState } from 'react';
import { Link } from 'react-router';
import type { LoadState } from '../api/songs';
import {
  remoteChangeNote,
  remoteChangeText,
  setRemoteStates,
  useRemoteStates,
  type RemoteStatePage,
} from '../api/sunoRemoteStates';
import { Notice } from '../components/Notice';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

/** The data of a load once it has been ready, kept while it loads again, so the list does not flash. */
function useLastReady<T>(state: LoadState<T>): T | undefined {
  const [last, setLast] = useState<T>();
  if (state.phase === 'ready' && state.data !== last) {
    setLast(state.data);
  }
  return state.phase === 'ready' ? state.data : last;
}

/**
 * The review's "Suno state changes" (#142): each clip already in n8Tracks that the sync found in Suno's
 * Trash ("In Suno Trash: will be archived"), listed again ("Restored in Suno: will be reactivated"), or,
 * after a whole-library sync, listed nowhere (Remote Missing). Each is ticked to apply at Confirm and can
 * be unticked to skip it. Nothing is deleted, and nothing changes in Suno. `revision` is the export's, as
 * the review read it; `onChanged` reads the review again after a change, `onConflict` when the export
 * changed elsewhere, and `onEnded` when it is no longer open for review.
 */
export function RemoteStateChanges({
  exportId,
  revision,
  q,
  onChanged,
  onConflict,
  onEnded,
}: {
  exportId: string;
  revision: number;
  q: string | undefined;
  onChanged: () => void;
  onConflict: () => void;
  onEnded: () => void;
}) {
  const [page, setPage] = useState(1);
  const load = useRemoteStates(exportId, q, page);
  const rows = useLastReady(load.state);
  const [busy, setBusy] = useState<string>();
  const [message, setMessage] = useState<string>();
  // The revision this list last saved at, so a second change does not wait for the review to reload.
  const [saved, setSaved] = useState(revision);

  if (rows === undefined) {
    if (load.state.phase === 'error' || load.state.phase === 'not-found') {
      return (
        <Notice title="The Suno state changes could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <div>
            <Button variant="default" size="xs" onClick={load.reload}>
              Try again
            </Button>
          </div>
        </Notice>
      );
    }
    return <Loader aria-label="Loading the Suno state changes" />;
  }

  const toggle = async (sunoId: string, apply: boolean) => {
    setBusy(sunoId);
    setMessage(undefined);
    const result = await setRemoteStates(exportId, Math.max(saved, revision), [sunoId], apply);
    setBusy(undefined);
    switch (result.kind) {
      case 'changed':
        setSaved(result.revision);
        setMessage(apply ? 'It will be applied at Confirm.' : 'It is skipped this time.');
        load.reload();
        onChanged();
        return;
      case 'conflict':
        onConflict();
        return;
      case 'not-ready':
        onEnded();
        return;
      default:
        setMessage(`Nothing was changed. ${FAILED_MESSAGE}`);
    }
  };

  const pages = Math.max(1, Math.ceil(rows.total / rows.pageSize));
  return (
    <Stack gap="sm" component="section" aria-labelledby="remote-states-title">
      <Text fw={600} id="remote-states-title">
        Suno state changes
      </Text>
      <Text size="sm" data-testid="remote-states-summary">
        {summaryText(rows)}
      </Text>
      <div role="status">
        {message !== undefined && <Text data-testid="remote-state-message">{message}</Text>}
      </div>
      {rows.total === 0 ? (
        <Text data-testid="no-remote-states">
          {q === undefined
            ? 'Nothing changed in Suno for the clips already in n8Tracks.'
            : 'No Suno state change matches this search.'}
        </Text>
      ) : (
        <Table.ScrollContainer minWidth={640}>
          <Table withTableBorder aria-label="Suno state changes">
            <Table.Thead>
              <Table.Tr>
                <Table.Th scope="col" w={90}>
                  Apply
                </Table.Th>
                <Table.Th scope="col">Title</Table.Th>
                <Table.Th scope="col">Generation</Table.Th>
                <Table.Th scope="col">What happens</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rows.items.map((row) => {
                const title = row.title ?? 'Untitled';
                const note = remoteChangeNote(row);
                return (
                  <Table.Tr
                    key={row.sunoId}
                    data-remote-state={row.sunoId}
                    data-change={row.change}
                  >
                    <Table.Td>
                      <Checkbox
                        aria-label={`Apply: ${title}`}
                        checked={row.apply}
                        disabled={busy !== undefined}
                        onChange={(event) => {
                          void toggle(row.sunoId, event.currentTarget.checked);
                        }}
                      />
                    </Table.Td>
                    <Table.Th scope="row" fw="normal">
                      <Text size="sm">{title}</Text>
                    </Table.Th>
                    <Table.Td>
                      <Anchor
                        component={Link}
                        size="sm"
                        underline="always"
                        to={`/songs/${row.generation.songShortcode}/generations/${row.generation.shortcode}`}
                      >
                        {row.generation.shortcode}
                      </Anchor>
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm" data-testid="remote-state-change">
                        {row.apply ? remoteChangeText(row) : `Skipped: ${remoteChangeText(row)}`}
                      </Text>
                      {note !== undefined && (
                        <Text size="xs" data-testid="remote-state-note">
                          {note}
                        </Text>
                      )}
                    </Table.Td>
                  </Table.Tr>
                );
              })}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
      {pages > 1 && (
        <Pagination
          total={pages}
          value={page}
          onChange={setPage}
          getControlProps={(control) => ({
            'aria-label': {
              first: 'First page',
              previous: 'Previous page',
              next: 'Next page',
              last: 'Last page',
            }[control],
          })}
          getItemProps={(item) => ({ 'aria-label': `Page ${String(item)}` })}
        />
      )}
    </Stack>
  );
}

/** How many changes of each kind, how many are skipped, and whether the sync could find clips missing. */
function summaryText(rows: RemoteStatePage): string {
  const { trashed, restored, missing, skipped } = rows.counts;
  const parts = [
    `${String(trashed)} in Suno Trash`,
    `${String(restored)} restored in Suno`,
    `${String(missing)} Remote Missing`,
  ];
  const skippedText = skipped === 0 ? '' : ` ${String(skipped)} skipped this time.`;
  const missingText = rows.missingChecked
    ? ''
    : ' This sync did not read your whole library and Trash to the end, so no clip is marked Remote Missing.';
  return `${parts.join(', ')}.${skippedText} Nothing is deleted, and nothing changes in Suno.${missingText}`;
}
