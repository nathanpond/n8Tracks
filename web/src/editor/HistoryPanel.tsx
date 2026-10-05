import { Box, Button, Group, Loader, Modal, Stack, Text, Title } from '@mantine/core';
import { useEffect, useId, useRef, useState } from 'react';
import { formatDateTime } from '../api/timeZone';
import {
  fetchSnapshot,
  useSnapshotList,
  type Snapshot,
  type SnapshotReadResult,
  type SnapshotSummary,
} from '../api/snapshots';
import { hasChanges, lineDiff, type DiffLine } from './lineDiff';
import { SNAPSHOT_IDLE_MS, type EditorText } from './useSnapshots';

/** How a restore asked for from the History panel ended. */
export type RestoreResult = 'restored' | 'changed-elsewhere' | 'failed';

const LINE_LABELS: Record<Exclude<DiffLine['kind'], 'same'>, { marker: string; label: string }> = {
  added: { marker: '+', label: 'Added' },
  removed: { marker: '−', label: 'Removed' },
};

/**
 * One field's comparison, line by line, from the snapshot to the text in the editor now. Each
 * added or removed line says so in words (and a + or − marker), not by colour alone.
 */
function Comparison({ label, before, after }: { label: string; before: string; after: string }) {
  const diff = lineDiff(before, after);
  return (
    <Stack gap={4}>
      <Text fw={700} size="sm">
        {label}
      </Text>
      {!hasChanges(diff) ? (
        <Text size="sm">The same as in the editor now.</Text>
      ) : (
        <Box
          component="ol"
          aria-label={`${label}: changes from the snapshot to the editor now`}
          m={0}
          p={0}
          style={{ listStyle: 'none', fontFamily: 'var(--mantine-font-family-monospace)' }}
        >
          {diff.map((line, index) => {
            const mark = line.kind === 'same' ? undefined : LINE_LABELS[line.kind];
            return (
              <Box
                component="li"
                // Lines repeat, so the position is part of the key.
                key={`${String(index)}-${line.kind}`}
                data-kind={line.kind}
                px={6}
                style={{
                  whiteSpace: 'pre-wrap',
                  overflowWrap: 'anywhere',
                  background:
                    line.kind === 'added'
                      ? 'var(--mantine-color-green-light)'
                      : line.kind === 'removed'
                        ? 'var(--mantine-color-red-light)'
                        : undefined,
                }}
              >
                {mark ? (
                  <Text span size="sm" fw={700}>
                    <span aria-hidden="true">{mark.marker} </span>
                    {mark.label}:{' '}
                  </Text>
                ) : (
                  <Text span size="sm" aria-hidden="true">
                    {'  '}
                  </Text>
                )}
                <Text span size="sm">
                  {line.text === '' ? '(blank line)' : line.text}
                </Text>
              </Box>
            );
          })}
        </Box>
      )}
    </Stack>
  );
}

/** The selected snapshot: its time, the comparison with the editor's text, and Restore. */
function SnapshotView({
  summary,
  versionId,
  current,
  timeZone,
  restoreBlocked,
  onRestore,
}: {
  summary: SnapshotSummary;
  versionId: string;
  current: EditorText;
  timeZone: string;
  restoreBlocked: string | undefined;
  onRestore: (snapshot: Snapshot) => Promise<RestoreResult>;
}) {
  const [read, setRead] = useState<{ id: string; result: SnapshotReadResult } | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [restoring, setRestoring] = useState(false);
  const [message, setMessage] = useState<string | undefined>();
  const reasonId = useId();
  const when = formatDateTime(summary.createdAt, timeZone);

  useEffect(() => {
    const controller = new AbortController();
    void fetchSnapshot(versionId, summary.id, controller.signal).then((result) => {
      if (!controller.signal.aborted) {
        setRead({ id: summary.id, result });
      }
    });
    return () => {
      controller.abort();
    };
  }, [versionId, summary.id]);

  const result = read?.id === summary.id ? read.result : undefined;
  if (result === undefined) {
    return (
      <Group gap="sm">
        <Loader size="xs" aria-hidden="true" />
        <Text size="sm">Loading the snapshot…</Text>
      </Group>
    );
  }
  if (result.kind !== 'found') {
    return (
      <Text size="sm" role="alert">
        {result.kind === 'gone'
          ? 'This snapshot is no longer kept.'
          : 'The snapshot could not be loaded. Choose it again to retry.'}
      </Text>
    );
  }

  const snapshot = result.snapshot;
  const restore = async () => {
    setConfirming(false);
    setRestoring(true);
    setMessage(undefined);
    const outcome = await onRestore(snapshot);
    setRestoring(false);
    setMessage(
      outcome === 'restored'
        ? `Restored the snapshot from ${when}. The text it replaced is kept in History.`
        : outcome === 'changed-elsewhere'
          ? 'Not restored: this Version was changed elsewhere. The editor now shows its current text; choose Restore again to replace it.'
          : 'Not restored: n8Tracks could not be reached or refused it. Try again.',
    );
  };

  return (
    <Stack gap="sm" data-testid="snapshot">
      <Title order={5} size="h6">
        Snapshot from {when}
      </Title>
      <Text size="sm" c="var(--n8-color-secondary-text)">
        Compared with the text in the editor now.
      </Text>
      <Comparison label="Lyrics" before={snapshot.lyrics} after={current.lyrics} />
      <Comparison label="Styles" before={snapshot.styles} after={current.styles} />
      <Group gap="sm" align="center" wrap="wrap">
        <Button
          disabled={restoreBlocked !== undefined || restoring}
          loading={restoring}
          aria-describedby={restoreBlocked === undefined ? undefined : reasonId}
          onClick={() => {
            setConfirming(true);
          }}
        >
          Restore this snapshot
        </Button>
        {restoreBlocked !== undefined && (
          <Text size="sm" id={reasonId} style={{ flex: '1 1 16rem' }}>
            {restoreBlocked}
          </Text>
        )}
      </Group>
      {message !== undefined && (
        <Text size="sm" role="status">
          {message}
        </Text>
      )}
      <Modal
        opened={confirming}
        onClose={() => {
          setConfirming(false);
        }}
        title="Restore this snapshot?"
        centered
        closeButtonProps={{ 'aria-label': 'Close' }}
      >
        <Stack gap="md">
          <Text>
            The lyrics and styles in the editor are replaced with the snapshot from {when}. The text
            they hold now is kept in History first, so you can restore it again.
          </Text>
          <Group gap="sm" justify="flex-end">
            <Button
              variant="default"
              onClick={() => {
                setConfirming(false);
              }}
            >
              Cancel
            </Button>
            <Button onClick={() => void restore()}>Restore</Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}

/** The open panel: the list of snapshots, newest first, and the selected one. */
function HistoryContent({
  versionId,
  current,
  timeZone,
  refreshKey,
  restoreBlocked,
  onRestore,
}: Omit<HistoryPanelProps, 'versionId'> & { versionId: string }) {
  const { state, reload } = useSnapshotList(versionId);
  const [selected, setSelected] = useState<string | undefined>();
  const seenKey = useRef(refreshKey);

  // A new snapshot (or a restore) reads the list again.
  useEffect(() => {
    if (seenKey.current !== refreshKey) {
      seenKey.current = refreshKey;
      reload();
    }
  }, [refreshKey, reload]);

  // While a reload runs, the last list stays on screen.
  const [shown, setShown] = useState<SnapshotSummary[] | undefined>();
  if (state.phase === 'ready' && state.data !== shown) {
    setShown(state.data);
  }

  if (shown === undefined) {
    if (state.phase === 'loading') {
      return (
        <Group gap="sm">
          <Loader size="xs" aria-hidden="true" />
          <Text size="sm">Loading the history…</Text>
        </Group>
      );
    }
    return (
      <Group gap="sm">
        <Text size="sm" role="alert">
          The history could not be loaded.
        </Text>
        <Button variant="default" size="compact-sm" onClick={reload}>
          Try again
        </Button>
      </Group>
    );
  }

  if (shown.length === 0) {
    return (
      <Text size="sm">
        No snapshots yet. A snapshot of the lyrics and styles is kept when you pause for{' '}
        {String(SNAPSHOT_IDLE_MS / 1000)} seconds after a change, and when you leave this Version.
      </Text>
    );
  }

  const choice = shown.find((snapshot) => snapshot.id === selected);
  return (
    <Stack gap="sm">
      <Text size="sm" c="var(--n8-color-secondary-text)">
        {shown.length === 1 ? '1 snapshot' : `${String(shown.length)} snapshots`}, newest first.
        Choose one to compare it with the editor.
      </Text>
      <Box component="ul" aria-label="Snapshots" m={0} p={0} style={{ listStyle: 'none' }}>
        {shown.map((snapshot) => (
          <li key={snapshot.id}>
            <Button
              variant={snapshot.id === selected ? 'light' : 'subtle'}
              size="compact-sm"
              aria-pressed={snapshot.id === selected}
              onClick={() => {
                setSelected(snapshot.id);
              }}
            >
              {formatDateTime(snapshot.createdAt, timeZone)}
            </Button>
          </li>
        ))}
      </Box>
      {choice !== undefined && (
        <SnapshotView
          key={choice.id}
          summary={choice}
          versionId={versionId}
          current={current}
          timeZone={timeZone}
          restoreBlocked={restoreBlocked}
          onRestore={onRestore}
        />
      )}
    </Stack>
  );
}

export interface HistoryPanelProps {
  versionId: string;
  /** The lyrics and styles in the editor now, which a snapshot is compared with. */
  current: EditorText;
  timeZone: string;
  /** Changes when a snapshot of this Version has been stored, so the list is read again. */
  refreshKey: number;
  /** Why Restore is unavailable now (unsaved or conflicted changes), or undefined. */
  restoreBlocked: string | undefined;
  /** Restores the snapshot (after the user confirmed it). */
  onRestore: (snapshot: Snapshot) => Promise<RestoreResult>;
}

/**
 * A Version's History: shown on request, it lists the snapshots of its lyrics and styles, newest
 * first, with their time. Choosing one shows it compared, line by line, with the text in the
 * editor now, and offers Restore (confirmed first), which is unavailable while the editor has
 * unsaved or conflicted changes and says why.
 */
export function HistoryPanel(props: HistoryPanelProps) {
  const [open, setOpen] = useState(false);
  const panelId = useId();

  return (
    <Stack gap="sm" component="section" aria-labelledby={`${panelId}-heading`}>
      <Group gap="sm" align="center">
        <Title order={4} size="h6" id={`${panelId}-heading`}>
          History
        </Title>
        <Button
          variant="default"
          size="compact-sm"
          aria-expanded={open}
          aria-controls={panelId}
          onClick={() => {
            setOpen(!open);
          }}
        >
          {open ? 'Hide history' : 'Show history'}
        </Button>
      </Group>
      <div id={panelId} hidden={!open}>
        {open && <HistoryContent {...props} />}
      </div>
    </Stack>
  );
}
