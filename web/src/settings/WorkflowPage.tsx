import {
  Badge,
  Button,
  Group,
  Loader,
  Modal,
  NativeSelect,
  Paper,
  Radio,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useEffect, useRef, useState, type DragEvent, type SyntheticEvent } from 'react';
import {
  addState,
  deleteState,
  moved,
  reorderStates,
  stateNameError,
  STATE_NAME_MAXIMUM_LENGTH,
  updateState,
  useWorkflowList,
  type ManagedState,
  type StateEdit,
  type WorkflowList,
  type WorkflowResult,
} from '../api/workflow';
import { Notice } from '../components/Notice';
import { StateBadge } from '../songs/SongParts';
import { stateColours } from '../theme/palette';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

const CONFLICT_MESSAGE =
  'The workflow was changed somewhere else, so the list has been reloaded. Your change was not applied: check the list and try again.';

const LAST_VISIBLE_MESSAGE =
  'At least one state must stay visible, so the last visible state cannot be hidden or deleted.';

const COLOUR_NAMES = Object.keys(stateColours);

function colourLabel(colour: string): string {
  return colour.charAt(0).toUpperCase() + colour.slice(1);
}

function songCountText(count: number): string {
  if (count === 0) {
    return 'No Songs';
  }
  return count === 1 ? '1 Song' : `${String(count)} Songs`;
}

/** What the page tells the user after a change, with an Undo when the change was a hide. */
interface Message {
  text: string;
  tone: 'info' | 'problem';
  undoHide?: ManagedState;
}

function isLastVisible(states: readonly ManagedState[], state: ManagedState): boolean {
  return !state.hidden && states.every((other) => other.id === state.id || other.hidden);
}

/** The add form: a name, checked as the API checks it before it is sent. */
function AddStateForm({
  states,
  busy,
  onAdd,
}: {
  states: ManagedState[];
  busy: boolean;
  onAdd: (name: string) => Promise<WorkflowResult>;
}) {
  const [name, setName] = useState('');
  const [error, setError] = useState<string | undefined>();

  const submit = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    const problem = stateNameError(name, states);
    setError(problem);
    if (problem !== undefined || busy) {
      return;
    }
    const result = await onAdd(name);
    if (result.kind === 'saved') {
      setName('');
    } else if (result.kind === 'invalid') {
      setError(result.errors.name?.join(' ') ?? FAILED_MESSAGE);
    }
  };

  return (
    <form
      noValidate
      onSubmit={(event) => {
        void submit(event);
      }}
    >
      <Group align="end" gap="sm">
        <TextInput
          label="New state"
          description={`Up to ${String(STATE_NAME_MAXIMUM_LENGTH)} characters. It is added at the end.`}
          value={name}
          error={error}
          onChange={(event) => {
            setName(event.currentTarget.value);
            setError(undefined);
          }}
          style={{ flex: '1 1 16rem' }}
        />
        <Button type="submit" loading={busy}>
          Add state
        </Button>
      </Group>
    </form>
  );
}

/** Renames and recolours one state. Only the fields that changed are sent. */
function EditStateDialog({
  state,
  states,
  onClose,
  onSave,
}: {
  state: ManagedState | undefined;
  states: ManagedState[];
  onClose: () => void;
  onSave: (state: ManagedState, edit: StateEdit) => Promise<WorkflowResult>;
}) {
  const [name, setName] = useState(state?.name ?? '');
  const [colour, setColour] = useState(state?.colour ?? '');
  const [error, setError] = useState<string | undefined>();
  const [saving, setSaving] = useState(false);

  const submit = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (state === undefined) {
      return;
    }
    const problem = stateNameError(name, states, state.id);
    setError(problem);
    if (problem !== undefined) {
      return;
    }
    const edit: StateEdit = {};
    if (name.trim() !== state.name) {
      edit.name = name;
    }
    if (colour !== state.colour) {
      edit.colour = colour;
    }
    if (Object.keys(edit).length === 0) {
      onClose();
      return;
    }
    setSaving(true);
    const result = await onSave(state, edit);
    setSaving(false);
    if (result.kind === 'invalid') {
      setError(result.errors.name?.join(' ') ?? result.errors.colour?.join(' ') ?? FAILED_MESSAGE);
    } else if (result.kind !== 'failed') {
      onClose();
    }
  };

  return (
    <Modal
      opened={state !== undefined}
      onClose={onClose}
      title={state ? `Edit ${state.name}` : 'Edit state'}
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <form
        noValidate
        onSubmit={(event) => {
          void submit(event);
        }}
      >
        <Stack gap="md">
          <TextInput
            label="Name"
            value={name}
            error={error}
            data-autofocus
            onChange={(event) => {
              setName(event.currentTarget.value);
              setError(undefined);
            }}
          />
          <Radio.Group label="Colour" value={colour} onChange={setColour}>
            <Group gap="sm" mt={6}>
              {COLOUR_NAMES.map((option) => (
                <Radio
                  key={option}
                  value={option}
                  label={<StateBadge name={colourLabel(option)} colour={option} />}
                />
              ))}
            </Group>
          </Radio.Group>
          <Group justify="end">
            <Button variant="default" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={saving}>
              Save
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}

/** Deleting a state that has Songs: they all move to the replacement chosen here, in the same operation. */
function DeleteStateDialog({
  target,
  states,
  onClose,
  onDelete,
}: {
  target: { state: ManagedState; songCount: number } | undefined;
  states: ManagedState[];
  onClose: () => void;
  onDelete: (state: ManagedState, replacement: string) => Promise<WorkflowResult>;
}) {
  const others = states.filter((state) => state.id !== target?.state.id);
  const [replacement, setReplacement] = useState(
    others.find((state) => !state.hidden)?.id ?? others[0]?.id ?? '',
  );
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState<string | undefined>();

  const confirm = async () => {
    if (target === undefined) {
      return;
    }
    setDeleting(true);
    const result = await onDelete(target.state, replacement);
    setDeleting(false);
    if (result.kind === 'invalid') {
      setError(result.errors.replacement?.join(' ') ?? FAILED_MESSAGE);
    } else if (result.kind === 'failed') {
      setError(FAILED_MESSAGE);
    } else {
      onClose();
    }
  };

  const count = target?.songCount ?? 0;
  return (
    <Modal
      opened={target !== undefined}
      onClose={onClose}
      title={target ? `Delete ${target.state.name}` : 'Delete state'}
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <Stack gap="md">
        <Text>
          {count === 1 ? '1 Song is' : `${String(count)} Songs are`} in{' '}
          <strong>{target?.state.name}</strong>. Choose the state to move{' '}
          {count === 1 ? 'it' : 'them'} to; the state is then deleted.
        </Text>
        <NativeSelect
          label="Move its Songs to"
          value={replacement}
          error={error}
          onChange={(event) => {
            setReplacement(event.currentTarget.value);
            setError(undefined);
          }}
          data={others.map((state) => ({
            value: state.id,
            label: state.hidden ? `${state.name} (hidden)` : state.name,
          }))}
        />
        <Group justify="end">
          <Button variant="default" onClick={onClose} data-autofocus>
            Cancel
          </Button>
          <Button
            color="red"
            loading={deleting}
            onClick={() => {
              void confirm();
            }}
          >
            Move Songs and delete
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

type RowAction = 'up' | 'down';

/** One state: its colour and name, its Song count, and what can be done to it. */
function StateRow({
  state,
  index,
  count,
  lastVisible,
  dragOver,
  actions,
}: {
  state: ManagedState;
  index: number;
  count: number;
  lastVisible: boolean;
  dragOver: boolean;
  actions: {
    move: (index: number, to: number, action?: RowAction) => void;
    edit: () => void;
    setHidden: (hidden: boolean) => void;
    remove: () => void;
    dragStart: (index: number) => void;
    dragEnter: (index: number) => void;
    drop: (index: number) => void;
    dragEnd: () => void;
  };
}) {
  return (
    // eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions -- dragging is the pointer way to reorder; each row's Move up and Move down buttons are the keyboard way (AC)
    <li
      data-state-id={state.id}
      data-state-name={state.name}
      draggable
      onDragStart={(event: DragEvent<HTMLLIElement>) => {
        event.dataTransfer.effectAllowed = 'move';
        event.dataTransfer.setData('text/plain', state.id);
        actions.dragStart(index);
      }}
      onDragOver={(event) => {
        event.preventDefault();
        actions.dragEnter(index);
      }}
      onDrop={(event) => {
        event.preventDefault();
        actions.drop(index);
      }}
      onDragEnd={actions.dragEnd}
      style={{ listStyle: 'none' }}
    >
      <Paper
        withBorder
        p="xs"
        style={{
          cursor: 'grab',
          borderColor: dragOver ? 'var(--mantine-color-text)' : undefined,
          borderWidth: dragOver ? 2 : undefined,
        }}
      >
        <Group justify="space-between" wrap="wrap" gap="xs">
          <Group gap="sm" wrap="nowrap">
            <Text
              aria-hidden="true"
              c="var(--n8-color-secondary-text)"
              style={{ userSelect: 'none' }}
            >
              ⠿
            </Text>
            <StateBadge name={state.name} colour={state.colour} />
            {state.hidden && (
              <Badge variant="default" tt="none">
                Hidden
              </Badge>
            )}
            <Text size="sm">{songCountText(state.songCount)}</Text>
          </Group>
          <Group gap={6} wrap="wrap">
            <Button
              size="xs"
              variant="default"
              data-action="up"
              aria-label={`Move ${state.name} up`}
              disabled={index === 0}
              onClick={() => {
                actions.move(index, index - 1, 'up');
              }}
            >
              Move up
            </Button>
            <Button
              size="xs"
              variant="default"
              data-action="down"
              aria-label={`Move ${state.name} down`}
              disabled={index === count - 1}
              onClick={() => {
                actions.move(index, index + 1, 'down');
              }}
            >
              Move down
            </Button>
            <Button
              size="xs"
              variant="default"
              aria-label={`Edit ${state.name}`}
              onClick={actions.edit}
            >
              Edit
            </Button>
            <Button
              size="xs"
              variant="default"
              aria-label={`${state.hidden ? 'Show' : 'Hide'} ${state.name}`}
              disabled={lastVisible}
              onClick={() => {
                actions.setHidden(!state.hidden);
              }}
            >
              {state.hidden ? 'Show' : 'Hide'}
            </Button>
            <Button
              size="xs"
              variant="default"
              color="red"
              aria-label={`Delete ${state.name}`}
              disabled={lastVisible}
              onClick={actions.remove}
            >
              Delete
            </Button>
          </Group>
        </Group>
        {lastVisible && (
          <Text size="xs" c="var(--n8-color-secondary-text)" mt={4}>
            The only visible state: show another before hiding or deleting this one.
          </Text>
        )}
      </Paper>
    </li>
  );
}

/** The page once the list has loaded: it keeps its own copy, replaced by every answer. */
function WorkflowEditor({ initial, reload }: { initial: WorkflowList; reload: () => void }) {
  const [list, setList] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<Message | undefined>();
  const [editing, setEditing] = useState<ManagedState | undefined>();
  const [deleting, setDeleting] = useState<{ state: ManagedState; songCount: number }>();
  const [dragFrom, setDragFrom] = useState<number | undefined>();
  const [dragOver, setDragOver] = useState<number | undefined>();
  const [focusAfterMove, setFocusAfterMove] = useState<{ id: string; action: RowAction }>();
  const listRef = useRef<HTMLOListElement>(null);
  const busyRef = useRef(false);

  const states = list.items;

  // After a move by keyboard, the focus stays on the button that moved the state, or on the
  // other one when that is now disabled (the state reached the top or the bottom).
  useEffect(() => {
    if (focusAfterMove === undefined) {
      return;
    }
    const row = listRef.current?.querySelector(`[data-state-id="${focusAfterMove.id}"]`);
    const same = row?.querySelector<HTMLButtonElement>(`[data-action="${focusAfterMove.action}"]`);
    const other = row?.querySelector<HTMLButtonElement>(
      `[data-action="${focusAfterMove.action === 'up' ? 'down' : 'up'}"]`,
    );
    (same && !same.disabled ? same : other)?.focus();
  }, [focusAfterMove, list]);

  /** Runs one change at a time and applies its answer; answers what came back for the caller to finish. */
  const run = async (
    send: (revision: number) => Promise<WorkflowResult>,
    done: (saved: WorkflowList) => Message | undefined,
  ): Promise<WorkflowResult> => {
    if (busyRef.current) {
      return { kind: 'failed' };
    }
    busyRef.current = true;
    setBusy(true);
    const result = await send(list.revision);
    busyRef.current = false;
    setBusy(false);
    switch (result.kind) {
      case 'saved':
        setList(result.list);
        setMessage(done(result.list));
        break;
      case 'conflict':
        setList(result.current);
        setMessage({ text: CONFLICT_MESSAGE, tone: 'problem' });
        break;
      case 'last-visible':
        setMessage({ text: LAST_VISIBLE_MESSAGE, tone: 'problem' });
        break;
      case 'not-found':
        setMessage({ text: CONFLICT_MESSAGE, tone: 'problem' });
        reload();
        break;
      case 'failed':
        setMessage({ text: FAILED_MESSAGE, tone: 'problem' });
        break;
      default:
        // `invalid` and `in-use` are shown where the change was asked for.
        break;
    }
    return result;
  };

  const move = (from: number, to: number, action?: RowAction) => {
    const state = states[from];
    if (state === undefined || to < 0 || to >= states.length || from === to) {
      return;
    }
    const ids = moved(states, from, to).map((item) => item.id);
    void run(
      (revision) => reorderStates(revision, ids),
      () => ({
        text: `Moved ${state.name} to position ${String(to + 1)} of ${String(ids.length)}.`,
        tone: 'info',
      }),
    ).then((result) => {
      if (result.kind === 'saved' && action !== undefined) {
        setFocusAfterMove({ id: state.id, action });
      }
    });
  };

  const setHidden = (state: ManagedState, hidden: boolean) => {
    void run(
      (revision) => updateState(revision, state.id, { hidden }),
      () =>
        hidden
          ? { text: `${state.name} is hidden.`, tone: 'info', undoHide: state }
          : { text: `${state.name} is shown.`, tone: 'info' },
    );
  };

  const remove = async (state: ManagedState) => {
    if (state.songCount > 0) {
      setDeleting({ state, songCount: state.songCount });
      return;
    }
    const result = await run(
      (revision) => deleteState(revision, state.id),
      () => ({ text: `${state.name} is deleted.`, tone: 'info' }),
    );
    if (result.kind === 'in-use') {
      // Songs were put in it since the list was loaded.
      setDeleting({ state, songCount: result.songCount });
    }
  };

  return (
    <Stack gap="md">
      <AddStateForm
        states={states}
        busy={busy}
        onAdd={(name) =>
          run(
            (revision) => addState(revision, name),
            (saved) => ({
              text: `${saved.items.at(-1)?.name ?? name.trim()} is added at the end.`,
              tone: 'info',
            }),
          )
        }
      />

      <Group gap="xs" role="status" aria-live="polite" mih={28}>
        {message &&
          (message.tone === 'problem' ? (
            <Notice title="Not changed">
              <Text>{message.text}</Text>
            </Notice>
          ) : (
            <Text>{message.text}</Text>
          ))}
      </Group>
      {message?.undoHide && (
        <div>
          <Button
            size="xs"
            variant="default"
            onClick={() => {
              const state = message.undoHide;
              if (state) {
                setHidden(state, false);
              }
            }}
          >
            Undo
          </Button>
        </div>
      )}

      <Text size="sm" c="var(--n8-color-secondary-text)" id="workflow-states-help">
        Drag a state to move it, or use Move up and Move down. A hidden state is not offered for
        Songs, but Songs already in it keep it.
      </Text>
      <ol
        ref={listRef}
        aria-label="Workflow states"
        aria-describedby="workflow-states-help"
        style={{ padding: 0, margin: 0, display: 'flex', flexDirection: 'column', gap: 8 }}
      >
        {states.map((state, index) => (
          <StateRow
            key={state.id}
            state={state}
            index={index}
            count={states.length}
            lastVisible={isLastVisible(states, state)}
            dragOver={dragFrom !== undefined && dragOver === index && dragFrom !== index}
            actions={{
              move,
              edit: () => {
                setEditing(state);
              },
              setHidden: (hidden) => {
                setHidden(state, hidden);
              },
              remove: () => {
                void remove(state);
              },
              dragStart: setDragFrom,
              dragEnter: setDragOver,
              drop: (to) => {
                if (dragFrom !== undefined) {
                  move(dragFrom, to);
                }
                setDragFrom(undefined);
                setDragOver(undefined);
              },
              dragEnd: () => {
                setDragFrom(undefined);
                setDragOver(undefined);
              },
            }}
          />
        ))}
      </ol>

      <EditStateDialog
        key={`edit-${editing?.id ?? 'none'}`}
        state={editing}
        states={states}
        onClose={() => {
          setEditing(undefined);
        }}
        onSave={(state, edit) =>
          run(
            (revision) => updateState(revision, state.id, edit),
            (saved) => ({
              text: `${saved.items.find((item) => item.id === state.id)?.name ?? state.name} is saved.`,
              tone: 'info',
            }),
          )
        }
      />
      <DeleteStateDialog
        key={`delete-${deleting?.state.id ?? 'none'}`}
        target={deleting}
        states={states}
        onClose={() => {
          setDeleting(undefined);
        }}
        onDelete={(state, replacement) =>
          run(
            (revision) => deleteState(revision, state.id, replacement),
            (saved) => {
              const to = saved.items.find((item) => item.id === replacement);
              return {
                text: `${state.name} is deleted; its Songs are now in ${to?.name ?? 'the replacement'}.`,
                tone: 'info',
              };
            },
          )
        }
      />
    </Stack>
  );
}

/**
 * Settings → Workflow: the workflow states in order, each with its colour and Song count. The user
 * adds, renames, recolours, reorders (by drag, or Move up and Move down), hides, shows, and deletes
 * them. Each change is saved at once; deleting a state that has Songs first asks where to move
 * them. When the workflow was changed elsewhere, the list reloads and the change is not applied.
 */
export function WorkflowPage() {
  const { state, reload } = useWorkflowList();

  return (
    <Stack gap="lg">
      <Title order={2}>Workflow</Title>
      <Text>
        Every Song is in one workflow state. Shape the states to match how you work: a new Song
        starts in the first visible state.
      </Text>
      {state.phase === 'loading' && <Loader aria-label="Loading workflow states" />}
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Notice title="Workflow states could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <div>
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
          </div>
        </Notice>
      )}
      {state.phase === 'ready' && <WorkflowEditor initial={state.data} reload={reload} />}
    </Stack>
  );
}
