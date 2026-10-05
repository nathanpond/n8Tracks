import {
  Badge,
  Button,
  Group,
  Loader,
  Modal,
  Paper,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useEffect, useRef, useState, type SyntheticEvent } from 'react';
import {
  addModel,
  deleteModel,
  isLastOffered,
  MODEL_NAME_MAXIMUM_LENGTH,
  MODEL_NOTE_MAXIMUM_LENGTH,
  modelNameError,
  modelNoteError,
  reorderModels,
  updateModel,
  useSunoModels,
  type ModelEdit,
  type ModelResult,
  type SunoModel,
  type SunoModelList,
} from '../api/sunoModels';
import { moved } from '../api/workflow';
import { Notice } from '../components/Notice';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

const CONFLICT_MESSAGE =
  'The model list was changed somewhere else, so it has been reloaded. Your change was not applied: check the list and try again.';

const LAST_OFFERED_MESSAGE =
  'At least one model must stay offered, so the last model that is not retired cannot be retired or deleted.';

function versionCountText(count: number): string {
  if (count === 0) {
    return 'No Versions';
  }
  return count === 1 ? '1 Version' : `${String(count)} Versions`;
}

function inUseMessage(name: string, count: number): string {
  return `${versionCountText(count)} ${count === 1 ? 'names' : 'name'} ${name}, so it cannot be renamed or deleted. Retire it instead to stop offering it.`;
}

/** What the section tells the user after a change. */
interface Message {
  text: string;
  tone: 'info' | 'problem';
}

/** The add form: a name and an optional note, checked as the API checks them before they are sent. */
function AddModelForm({
  models,
  busy,
  onAdd,
}: {
  models: SunoModel[];
  busy: boolean;
  onAdd: (name: string, note: string) => Promise<ModelResult>;
}) {
  const [name, setName] = useState('');
  const [note, setNote] = useState('');
  const [nameError, setNameError] = useState<string | undefined>();
  const [noteError, setNoteError] = useState<string | undefined>();

  const submit = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    const nameProblem = modelNameError(name, models);
    const noteProblem = modelNoteError(note);
    setNameError(nameProblem);
    setNoteError(noteProblem);
    if (nameProblem !== undefined || noteProblem !== undefined || busy) {
      return;
    }
    const result = await onAdd(name, note);
    if (result.kind === 'saved') {
      setName('');
      setNote('');
    } else if (result.kind === 'invalid') {
      setNameError(result.errors.name?.join(' '));
      setNoteError(result.errors.note?.join(' '));
    }
  };

  return (
    <form
      noValidate
      onSubmit={(event) => {
        void submit(event);
      }}
    >
      <Group align="start" gap="sm">
        <TextInput
          label="New model"
          description={`Its name as Suno shows it, up to ${String(MODEL_NAME_MAXIMUM_LENGTH)} characters. It is added at the end.`}
          value={name}
          error={nameError}
          onChange={(event) => {
            setName(event.currentTarget.value);
            setNameError(undefined);
          }}
          style={{ flex: '1 1 14rem' }}
        />
        <TextInput
          label="Note"
          description="Optional, such as which plan it needs."
          value={note}
          error={noteError}
          onChange={(event) => {
            setNote(event.currentTarget.value);
            setNoteError(undefined);
          }}
          style={{ flex: '1 1 14rem' }}
        />
        <Button type="submit" loading={busy} mt={44}>
          Add model
        </Button>
      </Group>
    </form>
  );
}

/** Renames and annotates one model. A model Versions name keeps its name. Only what changed is sent. */
function EditModelDialog({
  model,
  models,
  onClose,
  onSave,
}: {
  model: SunoModel | undefined;
  models: SunoModel[];
  onClose: () => void;
  onSave: (model: SunoModel, edit: ModelEdit) => Promise<ModelResult>;
}) {
  const [name, setName] = useState(model?.name ?? '');
  const [note, setNote] = useState(model?.note ?? '');
  const [nameError, setNameError] = useState<string | undefined>();
  const [noteError, setNoteError] = useState<string | undefined>();
  const [saving, setSaving] = useState(false);
  const used = (model?.versionCount ?? 0) > 0;

  const submit = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (model === undefined) {
      return;
    }
    const nameProblem = used ? undefined : modelNameError(name, models, model.id);
    const noteProblem = modelNoteError(note);
    setNameError(nameProblem);
    setNoteError(noteProblem);
    if (nameProblem !== undefined || noteProblem !== undefined) {
      return;
    }
    const edit: ModelEdit = {};
    if (!used && name.trim() !== model.name) {
      edit.name = name;
    }
    if (note.trim() !== (model.note ?? '')) {
      edit.note = note;
    }
    if (Object.keys(edit).length === 0) {
      onClose();
      return;
    }
    setSaving(true);
    const result = await onSave(model, edit);
    setSaving(false);
    if (result.kind === 'invalid') {
      setNameError(result.errors.name?.join(' '));
      setNoteError(result.errors.note?.join(' '));
    } else if (result.kind === 'in-use') {
      setNameError(inUseMessage(model.name, result.versionCount));
    } else if (result.kind !== 'failed') {
      onClose();
    }
  };

  return (
    <Modal
      opened={model !== undefined}
      onClose={onClose}
      title={model ? `Edit ${model.name}` : 'Edit model'}
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
            error={nameError}
            disabled={used}
            description={
              used
                ? `${versionCountText(model?.versionCount ?? 0)} ${model?.versionCount === 1 ? 'names' : 'name'} this model, so its name stays.`
                : undefined
            }
            data-autofocus={used ? undefined : true}
            onChange={(event) => {
              setName(event.currentTarget.value);
              setNameError(undefined);
            }}
          />
          <TextInput
            label="Note"
            description={`Optional, up to ${String(MODEL_NOTE_MAXIMUM_LENGTH)} characters.`}
            value={note}
            error={noteError}
            data-autofocus={used ? true : undefined}
            onChange={(event) => {
              setNote(event.currentTarget.value);
              setNoteError(undefined);
            }}
          />
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

type RowAction = 'up' | 'down';

/** One model: its name, note, and use, and what can be done to it. */
function ModelRow({
  model,
  index,
  count,
  lastOffered,
  actions,
}: {
  model: SunoModel;
  index: number;
  count: number;
  lastOffered: boolean;
  actions: {
    move: (index: number, to: number, action: RowAction) => void;
    edit: () => void;
    setRetired: (retired: boolean) => void;
    remove: () => void;
  };
}) {
  const used = model.versionCount > 0;
  return (
    <li data-model-id={model.id} data-model-name={model.name} style={{ listStyle: 'none' }}>
      <Paper withBorder p="xs">
        <Group justify="space-between" wrap="wrap" gap="xs">
          <Group gap="sm" wrap="wrap">
            <Text fw={600}>{model.name}</Text>
            {model.retired && (
              <Badge variant="default" tt="none">
                Retired
              </Badge>
            )}
            {model.discovered && (
              <Badge variant="default" tt="none">
                Discovered
              </Badge>
            )}
            <Text size="sm">{versionCountText(model.versionCount)}</Text>
          </Group>
          <Group gap={6} wrap="wrap">
            <Button
              size="xs"
              variant="default"
              data-action="up"
              aria-label={`Move ${model.name} up`}
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
              aria-label={`Move ${model.name} down`}
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
              aria-label={`Edit ${model.name}`}
              onClick={actions.edit}
            >
              Edit
            </Button>
            <Button
              size="xs"
              variant="default"
              aria-label={`${model.retired ? 'Restore' : 'Retire'} ${model.name}`}
              disabled={lastOffered}
              onClick={() => {
                actions.setRetired(!model.retired);
              }}
            >
              {model.retired ? 'Restore' : 'Retire'}
            </Button>
            <Button
              size="xs"
              variant="default"
              color="red"
              aria-label={`Delete ${model.name}`}
              disabled={lastOffered || used}
              onClick={actions.remove}
            >
              Delete
            </Button>
          </Group>
        </Group>
        {model.note !== null && (
          <Text size="sm" mt={4}>
            {model.note}
          </Text>
        )}
        {lastOffered && (
          <Text size="xs" c="var(--n8-color-secondary-text)" mt={4}>
            The only model offered: restore or add another before retiring or deleting this one.
          </Text>
        )}
        {used && !lastOffered && (
          <Text size="xs" c="var(--n8-color-secondary-text)" mt={4}>
            Versions name this model, so it cannot be deleted or renamed.
          </Text>
        )}
      </Paper>
    </li>
  );
}

/** The list once it has loaded: the section keeps its own copy, replaced by every answer. */
function ModelsEditor({
  initial,
  reload,
  onChanged,
}: {
  initial: SunoModelList;
  reload: () => void;
  onChanged?: () => void;
}) {
  const [list, setList] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<Message | undefined>();
  const [editing, setEditing] = useState<SunoModel | undefined>();
  const [focusAfterMove, setFocusAfterMove] = useState<{ id: string; action: RowAction }>();
  const listRef = useRef<HTMLOListElement>(null);
  const busyRef = useRef(false);

  const models = list.items;

  // After a move, the focus stays on the button that moved the model, or on the other one when
  // that is now disabled (the model reached the top or the bottom).
  useEffect(() => {
    if (focusAfterMove === undefined) {
      return;
    }
    const row = listRef.current?.querySelector(`[data-model-id="${focusAfterMove.id}"]`);
    const same = row?.querySelector<HTMLButtonElement>(`[data-action="${focusAfterMove.action}"]`);
    const other = row?.querySelector<HTMLButtonElement>(
      `[data-action="${focusAfterMove.action === 'up' ? 'down' : 'up'}"]`,
    );
    (same && !same.disabled ? same : other)?.focus();
  }, [focusAfterMove, list]);

  /** Runs one change at a time and applies its answer; answers what came back for the caller to finish. */
  const run = async (
    send: (revision: number) => Promise<ModelResult>,
    done: (saved: SunoModelList) => Message | undefined,
    model?: SunoModel,
  ): Promise<ModelResult> => {
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
        onChanged?.();
        break;
      case 'conflict':
        setList(result.current);
        setMessage({ text: CONFLICT_MESSAGE, tone: 'problem' });
        onChanged?.();
        break;
      case 'last-offered':
        setMessage({ text: LAST_OFFERED_MESSAGE, tone: 'problem' });
        break;
      case 'in-use':
        setMessage({
          text: inUseMessage(model?.name ?? 'this model', result.versionCount),
          tone: 'problem',
        });
        break;
      case 'not-found':
        setMessage({ text: CONFLICT_MESSAGE, tone: 'problem' });
        reload();
        break;
      case 'failed':
        setMessage({ text: FAILED_MESSAGE, tone: 'problem' });
        break;
      default:
        // `invalid` is shown where the change was asked for.
        break;
    }
    return result;
  };

  const move = (from: number, to: number, action: RowAction) => {
    const model = models[from];
    if (model === undefined || to < 0 || to >= models.length || from === to) {
      return;
    }
    const ids = moved(models, from, to).map((item) => item.id);
    void run(
      (revision) => reorderModels(revision, ids),
      () => ({
        text: `Moved ${model.name} to position ${String(to + 1)} of ${String(ids.length)}.`,
        tone: 'info',
      }),
    ).then((result) => {
      if (result.kind === 'saved') {
        setFocusAfterMove({ id: model.id, action });
      }
    });
  };

  const setRetired = (model: SunoModel, retired: boolean) => {
    void run(
      (revision) => updateModel(revision, model.id, { retired }),
      () =>
        retired
          ? {
              text: `${model.name} is retired: it is no longer offered, and Versions that name it keep it.`,
              tone: 'info',
            }
          : { text: `${model.name} is restored and offered again.`, tone: 'info' },
      model,
    );
  };

  const remove = (model: SunoModel) => {
    void run(
      (revision) => deleteModel(revision, model.id),
      () => ({ text: `${model.name} is deleted.`, tone: 'info' }),
      model,
    );
  };

  return (
    <Stack gap="md">
      <AddModelForm
        models={models}
        busy={busy}
        onAdd={(name, note) =>
          run(
            (revision) => addModel(revision, name, note),
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

      <Text size="sm" c="var(--n8-color-secondary-text)" id="suno-models-help">
        The model pickers offer the models that are not retired, in this order. Retire a model Suno
        no longer offers: Versions that name it keep it.
      </Text>
      <ol
        ref={listRef}
        aria-label="Suno models"
        aria-describedby="suno-models-help"
        style={{ padding: 0, margin: 0, display: 'flex', flexDirection: 'column', gap: 8 }}
      >
        {models.map((model, index) => (
          <ModelRow
            key={model.id}
            model={model}
            index={index}
            count={models.length}
            lastOffered={isLastOffered(models, model)}
            actions={{
              move,
              edit: () => {
                setEditing(model);
              },
              setRetired: (retired) => {
                setRetired(model, retired);
              },
              remove: () => {
                remove(model);
              },
            }}
          />
        ))}
      </ol>

      <EditModelDialog
        key={`edit-${editing?.id ?? 'none'}`}
        model={editing}
        models={models}
        onClose={() => {
          setEditing(undefined);
        }}
        onSave={(model, edit) =>
          run(
            (revision) => updateModel(revision, model.id, edit),
            (saved) => ({
              text: `${saved.items.find((item) => item.id === model.id)?.name ?? model.name} is saved.`,
              tone: 'info',
            }),
            model,
          )
        }
      />
    </Stack>
  );
}

/**
 * The Suno model list: every model in order, each with its note and how many Versions name it. The
 * user adds, renames, annotates, reorders (Move up and Move down), retires, restores, and deletes
 * models. Each change is saved at once. When the list was changed elsewhere, it reloads and the
 * change is not applied. `onChanged` is told after each change, or a reload from elsewhere.
 */
export function SunoModelsSection({ onChanged }: { onChanged?: () => void } = {}) {
  const { state, reload } = useSunoModels();

  return (
    <Stack gap="md" component="section" aria-labelledby="suno-models-heading">
      <Title order={3} id="suno-models-heading">
        Models
      </Title>
      <Text>
        Suno adds and retires models faster than n8Tracks is released, so keep this list current:
        add a model as soon as Suno offers it.
      </Text>
      {state.phase === 'loading' && <Loader aria-label="Loading Suno models" />}
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Notice title="Suno models could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <div>
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
          </div>
        </Notice>
      )}
      {state.phase === 'ready' && (
        <ModelsEditor initial={state.data} reload={reload} onChanged={onChanged} />
      )}
    </Stack>
  );
}
