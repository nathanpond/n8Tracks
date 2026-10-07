import {
  Anchor,
  Badge,
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
  Title,
} from '@mantine/core';
import { useEffect, useRef, useState } from 'react';
import { Link, useParams } from 'react-router';
import type { Song } from '../api/songs';
import {
  moveWorkspaceSongs,
  SUNO_WORKSPACES_PATH,
  useSunoWorkspaces,
  useWorkspaceSongs,
  workspaceName,
  workspacePath,
  type MoveSongsResult,
  type SunoWorkspace,
  type WorkspaceSongSelection,
} from '../api/sunoWorkspaces';
import { formatDate, useConfiguredTimeZone } from '../api/timeZone';
import { Notice } from '../components/Notice';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

const PAGE_CONTROL_LABELS: Record<'first' | 'previous' | 'next' | 'last', string> = {
  first: 'First page',
  previous: 'Previous page',
  next: 'Next page',
  last: 'Last page',
};

/** "1 Song", "3 Songs". */
function songCountText(count: number): string {
  return `${count.toLocaleString()} ${count === 1 ? 'Song' : 'Songs'}`;
}

/** A workspace's state as the pages show it: Available, or Unavailable with the date it was last seen. */
function WorkspaceState({ workspace, timeZone }: { workspace: SunoWorkspace; timeZone: string }) {
  return workspace.state === 'available' ? (
    <Text size="sm" span>
      Available
    </Text>
  ) : (
    <Group gap="xs" wrap="nowrap" data-testid="workspace-state-unavailable">
      <Badge size="sm" variant="default" radius="sm" tt="none">
        Unavailable
      </Badge>
      <Text size="sm" span>
        last seen {formatDate(workspace.lastSeen, timeZone)}
      </Text>
    </Group>
  );
}

/** What to show while the workspace list loads or fails; null once it is ready. */
function ListStatus({
  state,
  reload,
}: {
  state: ReturnType<typeof useSunoWorkspaces>['state'];
  reload: () => void;
}) {
  if (state.phase === 'loading') {
    return <Loader aria-label="Loading the Suno workspaces" />;
  }
  if (state.phase === 'error' || state.phase === 'not-found') {
    return (
      <Notice title="The Suno workspaces could not be loaded">
        <Text>{FAILED_MESSAGE}</Text>
        <div>
          <Button variant="default" size="xs" onClick={reload}>
            Try again
          </Button>
        </div>
      </Notice>
    );
  }
  return null;
}

/**
 * Settings → Suno workspaces (#151): every Suno workspace n8Tracks knows, by name, with its state
 * (an Unavailable one with the date it was last seen) and how many Songs are in it. Each opens its
 * own page, where its Songs are listed and moved.
 */
export function SunoWorkspacesPage() {
  const { state, reload } = useSunoWorkspaces();
  const timeZone = useConfiguredTimeZone();
  return (
    <Stack gap="lg">
      <Title order={2}>Suno workspaces</Title>
      <Text>
        The workspaces in Suno that the browser extension has reported. A Song lives in one;
        Generate on Suno saves its results there. Open a workspace to move its Songs to another.
      </Text>
      <ListStatus state={state} reload={reload} />
      {state.phase === 'ready' && state.data.length === 0 && (
        <Text data-testid="no-workspaces">
          No Suno workspaces are known yet: they appear once the browser extension reports them.
        </Text>
      )}
      {state.phase === 'ready' && state.data.length > 0 && (
        <Table.ScrollContainer minWidth={480}>
          <Table withTableBorder aria-label="Suno workspaces">
            <Table.Thead>
              <Table.Tr>
                <Table.Th scope="col">Workspace</Table.Th>
                <Table.Th scope="col">State</Table.Th>
                <Table.Th scope="col" ta="end">
                  Songs
                </Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {state.data.map((workspace) => (
                <Table.Tr key={workspace.id} data-testid="workspace-row">
                  <Table.Th scope="row" fw="normal">
                    <Stack gap={2}>
                      <Anchor component={Link} to={workspacePath(workspace.id)}>
                        {workspaceName(workspace)}
                      </Anchor>
                      {workspace.description.trim() !== '' && (
                        <Text size="xs" c="var(--n8-color-secondary-text)">
                          {workspace.description}
                        </Text>
                      )}
                    </Stack>
                  </Table.Th>
                  <Table.Td>
                    <WorkspaceState workspace={workspace} timeZone={timeZone} />
                  </Table.Td>
                  <Table.Td ta="end">{workspace.songCount.toLocaleString()}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
    </Stack>
  );
}

/** Which Songs are chosen: every one in the workspace, or these (by ID). */
type Selection = { kind: 'all' } | { kind: 'some'; ids: string[] };

const NONE: Selection = { kind: 'some', ids: [] };

/** The message for a move that was refused or failed: nothing moved. */
function refusal(result: Exclude<MoveSongsResult, { kind: 'moved' }>): string {
  switch (result.kind) {
    case 'invalid':
      return `Nothing moved: ${Object.values(result.errors).flat().join(' ') || FAILED_MESSAGE}`;
    case 'not-in-workspace':
      return 'Nothing moved: some of the selected Songs are no longer in this workspace. The list has been read again; select the Songs and move them again.';
    case 'too-many':
      return `Nothing moved: one move takes at most ${result.limit.toLocaleString()} Songs. Select fewer and move them in parts.`;
    case 'gone':
      return 'Nothing moved: n8Tracks no longer knows this workspace.';
    case 'failed':
      return `Nothing moved: ${FAILED_MESSAGE}`;
  }
}

/**
 * The confirmation for a bulk move: how many Songs will move, from which workspace to which. The
 * move is one command, all or nothing. A refusal is said inside the dialog and takes keyboard focus
 * there (#347): the Move button waits for the answer disabled, so focus would otherwise fall out of
 * the modal.
 */
function MoveDialog({
  opened,
  count,
  from,
  to,
  moving,
  message,
  onClose,
  onConfirm,
}: {
  opened: boolean;
  count: number;
  from: SunoWorkspace;
  to: SunoWorkspace | undefined;
  moving: boolean;
  message: string | undefined;
  onClose: () => void;
  onConfirm: () => void;
}) {
  const refusal = useRef<HTMLParagraphElement>(null);
  useEffect(() => {
    if (message !== undefined) {
      refusal.current?.focus();
    }
  }, [message]);
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={`Move ${songCountText(count)}?`}
      centered
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <Stack gap="md">
        <Text data-testid="move-count">
          {songCountText(count)} will move from {workspaceName(from)} to{' '}
          {to === undefined ? 'the workspace chosen' : workspaceName(to)}. Each Song’s workspace
          changes; nothing else about it does. Either every one moves or none does.
        </Text>
        {message !== undefined && (
          <Text
            ref={refusal}
            tabIndex={-1}
            size="sm"
            role="alert"
            c="var(--mantine-color-error)"
            data-testid="move-refusal"
          >
            {message}
          </Text>
        )}
        <Group gap="sm" justify="flex-end">
          <Button variant="default" data-autofocus onClick={onClose}>
            Cancel
          </Button>
          <Button loading={moving} onClick={onConfirm}>
            Move {songCountText(count)}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** The table of a workspace's Songs on one page, each with a checkbox, and one to select them all. */
function SongTable({
  songs,
  total,
  selection,
  onSelect,
}: {
  songs: Song[];
  total: number;
  selection: Selection;
  onSelect: (selection: Selection) => void;
}) {
  const all = selection.kind === 'all';
  const chosen = selection.kind === 'some' ? selection.ids : [];
  const toggle = (id: string, checked: boolean) => {
    // From "all", unticking one leaves the others listed here ticked.
    const current = all ? songs.map((song) => song.id) : chosen;
    onSelect({
      kind: 'some',
      ids: checked ? [...current, id] : current.filter((other) => other !== id),
    });
  };
  return (
    <Table.ScrollContainer minWidth={420}>
      <Table withTableBorder aria-label="Songs in this workspace">
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col" w={40}>
              <Checkbox
                aria-label={`Select all ${songCountText(total)} in this workspace`}
                checked={all}
                indeterminate={!all && chosen.length > 0}
                onChange={(event) => {
                  onSelect(event.currentTarget.checked ? { kind: 'all' } : NONE);
                }}
              />
            </Table.Th>
            <Table.Th scope="col">Title</Table.Th>
            <Table.Th scope="col">Shortcode</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {songs.map((song) => (
            <Table.Tr key={song.id} data-testid="workspace-song">
              <Table.Td>
                <Checkbox
                  aria-label={`Select ${song.title}`}
                  checked={all || chosen.includes(song.id)}
                  onChange={(event) => {
                    toggle(song.id, event.currentTarget.checked);
                  }}
                />
              </Table.Td>
              <Table.Th scope="row" fw="normal">
                <Anchor component={Link} to={`/songs/${song.shortcode}`}>
                  {song.title}
                </Anchor>
              </Table.Th>
              <Table.Td>{song.shortcode}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

/**
 * One workspace's Songs and the bulk move: select some or all of them, choose an Available other
 * workspace, and confirm the count. The Songs can leave an Unavailable workspace the same way.
 */
function WorkspaceSongs({
  workspace,
  workspaces,
  onMoved,
}: {
  workspace: SunoWorkspace;
  workspaces: SunoWorkspace[];
  onMoved: () => void;
}) {
  const [page, setPage] = useState(1);
  const { state, reload } = useWorkspaceSongs(workspace.id, page);
  const [selection, setSelection] = useState<Selection>(NONE);
  const [target, setTarget] = useState('');
  const [confirming, setConfirming] = useState(false);
  // The count the confirmation states, fixed when it opens so it stays while the dialog closes.
  const [confirmCount, setConfirmCount] = useState(0);
  const [moving, setMoving] = useState(false);
  const [message, setMessage] = useState<string>();
  const [moved, setMoved] = useState<{ count: number; to: SunoWorkspace }>();
  // After a move the dialog and its trigger are gone or disabled: focus goes to what it says (#347).
  const movedNote = useRef<HTMLParagraphElement>(null);
  useEffect(() => {
    if (moved !== undefined) {
      movedNote.current?.focus();
    }
  }, [moved]);

  const targets = workspaces.filter(
    (candidate) => candidate.state === 'available' && candidate.id !== workspace.id,
  );
  const to = targets.find((candidate) => candidate.id === target);
  const songs = state.phase === 'ready' ? state.data : undefined;
  const total = songs?.total ?? 0;
  const count = selection.kind === 'all' ? total : selection.ids.length;
  const pages = songs === undefined ? 1 : Math.max(1, Math.ceil(songs.total / songs.pageSize));

  const close = () => {
    setConfirming(false);
    setMessage(undefined);
  };

  const confirm = async () => {
    if (to === undefined) {
      return;
    }
    setMoving(true);
    setMessage(undefined);
    const chosen: WorkspaceSongSelection =
      selection.kind === 'all' ? { all: true } : { songIds: selection.ids };
    const result = await moveWorkspaceSongs(workspace.id, chosen, to.id);
    setMoving(false);
    if (result.kind === 'moved') {
      setConfirming(false);
      setSelection(NONE);
      setTarget('');
      setPage(1);
      setMoved({ count: result.moved, to });
      reload();
      onMoved();
      return;
    }
    setMessage(refusal(result));
    if (result.kind === 'not-in-workspace' || result.kind === 'invalid') {
      // The Songs or the workspaces changed meanwhile: read them again.
      setSelection(NONE);
      reload();
      onMoved();
    }
  };

  return (
    <Stack gap="md">
      <div role="status">
        {moved !== undefined && (
          <Text ref={movedNote} tabIndex={-1} data-testid="songs-moved">
            Moved {songCountText(moved.count)} to{' '}
            <Anchor component={Link} to={workspacePath(moved.to.id)} underline="always">
              {workspaceName(moved.to)}
            </Anchor>
            .
          </Text>
        )}
      </div>
      {state.phase === 'loading' && <Loader aria-label="Loading the workspace’s Songs" />}
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Notice title="The workspace’s Songs could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <div>
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
          </div>
        </Notice>
      )}
      {songs?.total === 0 && (
        <Text data-testid="no-workspace-songs">No Songs are in this workspace.</Text>
      )}
      {songs !== undefined && songs.items.length > 0 && (
        <>
          <Group align="flex-end" gap="sm" role="group" aria-label="Move Songs">
            <NativeSelect
              label="Move to"
              data={[
                { value: '', label: targets.length === 0 ? 'No other workspace' : 'Choose…' },
                ...targets.map((candidate) => ({
                  value: candidate.id,
                  label: workspaceName(candidate),
                })),
              ]}
              value={target}
              disabled={targets.length === 0}
              onChange={(event) => {
                setTarget(event.currentTarget.value);
              }}
            />
            <Button
              disabled={count === 0 || to === undefined}
              onClick={() => {
                setMoved(undefined);
                setConfirmCount(count);
                setConfirming(true);
              }}
            >
              {count === 0 ? 'Move selected Songs' : `Move ${songCountText(count)}`}
            </Button>
          </Group>
          {targets.length === 0 && (
            <Text size="sm" c="var(--n8-color-secondary-text)" data-testid="no-move-target">
              There is no other Available workspace to move these Songs to.
            </Text>
          )}
          <SongTable
            songs={songs.items}
            total={songs.total}
            selection={selection}
            onSelect={setSelection}
          />
          <Group justify="space-between">
            <Text size="sm" data-testid="selected-count">
              {count === 0 ? 'None selected' : `${songCountText(count)} selected`} of{' '}
              {songCountText(songs.total)}
            </Text>
            {pages > 1 && (
              <Group component="nav" aria-label="Pages">
                <Pagination
                  total={pages}
                  value={songs.page}
                  onChange={setPage}
                  withEdges
                  getItemProps={(item) => ({ 'aria-label': `Page ${String(item)}` })}
                  getControlProps={(control) => ({ 'aria-label': PAGE_CONTROL_LABELS[control] })}
                />
              </Group>
            )}
          </Group>
        </>
      )}
      <MoveDialog
        opened={confirming}
        count={confirmCount}
        from={workspace}
        to={to}
        moving={moving}
        message={message}
        onClose={close}
        onConfirm={() => {
          void confirm();
        }}
      />
    </Stack>
  );
}

/** Settings → Suno workspaces → one workspace (#151): its state, its Songs, and the bulk move. */
export function SunoWorkspacePage() {
  const { id = '' } = useParams();
  const { state, reload } = useSunoWorkspaces();
  const timeZone = useConfiguredTimeZone();
  // The list as last read: it stays shown while a move reads it again, so the page keeps its place.
  const [known, setKnown] = useState<SunoWorkspace[]>();
  if (state.phase === 'ready' && state.data !== known) {
    setKnown(state.data);
  }
  const workspace = known?.find((candidate) => candidate.id === id);
  return (
    <Stack gap="lg">
      <Anchor component={Link} to={SUNO_WORKSPACES_PATH} size="sm">
        All Suno workspaces
      </Anchor>
      {(known === undefined || state.phase === 'error' || state.phase === 'not-found') && (
        <ListStatus state={state} reload={reload} />
      )}
      {known !== undefined && workspace === undefined && (
        <>
          <Title order={2}>Workspace not found</Title>
          <Text>n8Tracks knows no Suno workspace with this ID.</Text>
        </>
      )}
      {known !== undefined && workspace !== undefined && (
        <>
          <Stack gap={4}>
            <Title order={2}>{workspaceName(workspace)}</Title>
            {workspace.description.trim() !== '' && <Text>{workspace.description}</Text>}
            <Group gap="md" data-testid="workspace-summary">
              <WorkspaceState workspace={workspace} timeZone={timeZone} />
              <Text size="sm">{songCountText(workspace.songCount)}</Text>
            </Group>
          </Stack>
          {workspace.state === 'unavailable' && (
            <Notice title="Suno no longer offers this workspace">
              <Text size="sm">
                Suno no longer lists it, or it is in Suno’s Trash. Its Songs keep it until you move
                them to an Available workspace.
              </Text>
            </Notice>
          )}
          <WorkspaceSongs
            key={workspace.id}
            workspace={workspace}
            workspaces={known}
            onMoved={reload}
          />
        </>
      )}
    </Stack>
  );
}
