import {
  Button,
  Group,
  Menu,
  Paper,
  Stack,
  Text,
  TextInput,
  Textarea,
  Title,
  VisuallyHidden,
} from '@mantine/core';
import {
  useCallback,
  useMemo,
  useRef,
  useState,
  type ClipboardEvent,
  type KeyboardEvent,
} from 'react';
import type { FieldValue } from '../api/saves';
import {
  CONCEPT_MAXIMUM_LENGTH,
  updateSong,
  useWorkflowStates,
  type Song,
  type WorkflowState,
} from '../api/songs';
import { ConflictValue } from '../common/ConflictDialog';
import { useRevisionedSave, type SaveOutcome, type SavedField } from '../common/useRevisionedSave';
import { StateBadge } from './SongParts';
import { conceptError, singleLine, titleError } from './songRules';

const FAILED_MESSAGE =
  'Not saved: n8Tracks did not answer as expected. Check that it is running and try again.';

/** The title as it is saved: one line, trimmed. */
function normaliseTitle(draft: string): string {
  return singleLine(draft).trim();
}

/** The concept as it is saved: line endings as `\n`, trimmed, and null when nothing is left. */
function normaliseConcept(draft: string): FieldValue {
  const normalised = draft.replace(/\r\n|\r/g, '\n').trim();
  return normalised === '' ? null : normalised;
}

/** The message a save that did not go through leaves on its field. */
function saveError(outcome: SaveOutcome, field: string): string | undefined {
  switch (outcome.kind) {
    case 'invalid':
      return outcome.errors[field]?.join(' ') ?? FAILED_MESSAGE;
    case 'failed':
      return FAILED_MESSAGE;
    default:
      return undefined;
  }
}

/** Focuses a field as it opens: the user just asked to edit it, so their typing goes there. */
function focusOnMount(element: HTMLElement | null) {
  element?.focus();
}

/**
 * Editing one text field in place. `start` opens it with the current value; `commit` (Enter or
 * blur) checks the draft, sends it unless it changes nothing, and closes it once saved or once the
 * user took the current record instead; `cancel` (Escape) closes it unsaved. A refused or failed
 * save keeps it open with the draft and an error. A blur while a save is in flight (the conflict
 * dialog taking focus) is ignored.
 */
function useInPlaceEdit({
  field,
  current,
  normalise,
  check,
  save,
}: {
  field: string;
  current: FieldValue;
  normalise: (draft: string) => FieldValue;
  check: (draft: string) => string | undefined;
  save: (key: string, value: FieldValue) => Promise<SaveOutcome>;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');
  const [error, setError] = useState<string | undefined>();
  const [saving, setSaving] = useState(false);
  const open = useRef(false);
  const busy = useRef(false);

  const start = () => {
    open.current = true;
    setDraft(current ?? '');
    setError(undefined);
    setEditing(true);
  };

  const close = () => {
    open.current = false;
    setEditing(false);
    setError(undefined);
  };

  const commit = async () => {
    if (!open.current || busy.current) {
      return;
    }
    const problem = check(draft);
    if (problem !== undefined) {
      setError(problem);
      return;
    }
    const value = normalise(draft);
    if (value === current) {
      close();
      return;
    }

    busy.current = true;
    setSaving(true);
    setError(undefined);
    const outcome = await save(field, value);
    busy.current = false;
    setSaving(false);
    if (outcome.kind === 'saved' || outcome.kind === 'reloaded') {
      close();
    } else {
      setError(saveError(outcome, field));
    }
  };

  const cancel = () => {
    if (!busy.current) {
      close();
    }
  };

  return { editing, draft, setDraft, error, saving, start, commit, cancel };
}

/** The title: the page's h2, edited in place on one line. Enter or blur saves; Escape cancels. */
function TitleField({
  song,
  save,
}: {
  song: Song;
  save: (key: string, value: FieldValue) => Promise<SaveOutcome>;
}) {
  const edit = useInPlaceEdit({
    field: 'title',
    current: song.title,
    normalise: normaliseTitle,
    check: titleError,
    save,
  });

  if (!edit.editing) {
    return (
      <Group gap="sm" align="center">
        <Title order={2} style={{ overflowWrap: 'anywhere' }}>
          {song.title}
        </Title>
        <Button variant="subtle" size="compact-sm" onClick={edit.start} aria-label="Edit title">
          Edit
        </Button>
      </Group>
    );
  }

  const paste = (event: ClipboardEvent<HTMLInputElement>) => {
    const pasted = event.clipboardData.getData('text');
    if (!/[\r\n]/.test(pasted)) {
      return;
    }
    // A text field would drop the line breaks, running the words together; spaces keep them apart.
    event.preventDefault();
    const input = event.currentTarget;
    const start = input.selectionStart ?? input.value.length;
    const end = input.selectionEnd ?? start;
    edit.setDraft(input.value.slice(0, start) + singleLine(pasted) + input.value.slice(end));
  };

  const keys = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      void edit.commit();
    } else if (event.key === 'Escape') {
      event.preventDefault();
      edit.cancel();
    }
  };

  return (
    <TextInput
      label="Title"
      description="Enter saves, Escape cancels."
      size="md"
      value={edit.draft}
      onChange={(event) => {
        edit.setDraft(singleLine(event.currentTarget.value));
      }}
      onPaste={paste}
      onKeyDown={keys}
      onBlur={() => {
        void edit.commit();
      }}
      error={edit.error}
      aria-invalid={edit.error !== undefined}
      readOnly={edit.saving}
      ref={focusOnMount}
    />
  );
}

/** The concept: several lines, edited in place. Blur or Ctrl/Cmd+Enter saves; Escape cancels. */
function ConceptField({
  song,
  save,
}: {
  song: Song;
  save: (key: string, value: FieldValue) => Promise<SaveOutcome>;
}) {
  const edit = useInPlaceEdit({
    field: 'concept',
    current: song.concept,
    normalise: normaliseConcept,
    check: conceptError,
    save,
  });

  const keys = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      void edit.commit();
    } else if (event.key === 'Escape') {
      event.preventDefault();
      edit.cancel();
    }
  };

  return (
    <Paper p="md" withBorder component="section" aria-labelledby="song-concept">
      <Stack gap={4}>
        <Group gap="sm" align="center">
          <Title order={3} size="h5" id="song-concept">
            Concept
          </Title>
          {!edit.editing && (
            <Button
              variant="subtle"
              size="compact-sm"
              onClick={edit.start}
              aria-label="Edit concept"
            >
              Edit
            </Button>
          )}
        </Group>
        {edit.editing ? (
          <Textarea
            aria-labelledby="song-concept"
            description={`Up to ${CONCEPT_MAXIMUM_LENGTH.toLocaleString('en-US')} characters. Ctrl+Enter or Cmd+Enter saves, Escape cancels; leave it empty to clear it.`}
            rows={6}
            resize="vertical"
            value={edit.draft}
            onChange={(event) => {
              edit.setDraft(event.currentTarget.value);
            }}
            onKeyDown={keys}
            onBlur={() => {
              void edit.commit();
            }}
            error={edit.error}
            aria-invalid={edit.error !== undefined}
            readOnly={edit.saving}
            ref={focusOnMount}
          />
        ) : song.concept === null ? (
          <Text c="var(--n8-color-secondary-text)">No concept yet.</Text>
        ) : (
          <Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{song.concept}</Text>
        )}
      </Stack>
    </Paper>
  );
}

/**
 * The states the menu offers: every visible state and, while the Song is in a hidden one, that
 * one too, in workflow order.
 */
function offeredStates(states: WorkflowState[], currentId: string): WorkflowState[] {
  return states.filter((state) => !state.hidden || state.id === currentId);
}

/** The workflow state, changed from a menu. A Song may move from any state to any other. */
function StateField({
  song,
  states,
  save,
}: {
  song: Song;
  states: WorkflowState[] | undefined;
  save: (key: string, value: FieldValue) => Promise<SaveOutcome>;
}) {
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const offered = offeredStates(states ?? [], song.state.id);

  const choose = async (id: string) => {
    if (id === song.state.id) {
      return;
    }
    setSaving(true);
    setError(undefined);
    const outcome = await save('stateId', id);
    setSaving(false);
    setError(saveError(outcome, 'stateId'));
  };

  return (
    <Stack gap={2}>
      {/* No focus placeholder: Mantine's is a focusable element with role="presentation" inside
          the menu, which axe reports as a child a menu may not have (aria-required-children).
          Not hidden when the button scrolls out of view: the menu is short-lived, and the check
          closes it at once where nothing is laid out (jsdom). */}
      <Menu
        position="bottom-start"
        withinPortal
        withInitialFocusPlaceholder={false}
        hideDetached={false}
      >
        <Menu.Target>
          <Button
            variant="default"
            size="compact-sm"
            loading={saving}
            disabled={states === undefined}
            rightSection={<span aria-hidden="true">▾</span>}
          >
            <VisuallyHidden>State: </VisuallyHidden>
            <StateBadge name={song.state.name} colour={song.state.colour} />
          </Button>
        </Menu.Target>
        <Menu.Dropdown>
          <Menu.Label>Move to</Menu.Label>
          {offered.map((state) => (
            <Menu.Item
              key={state.id}
              onClick={() => {
                void choose(state.id);
              }}
              rightSection={state.id === song.state.id ? '✓' : undefined}
            >
              <StateBadge name={state.name} colour={state.colour} />
              {state.id === song.state.id && <VisuallyHidden> (current)</VisuallyHidden>}
              {state.hidden && <VisuallyHidden> (hidden)</VisuallyHidden>}
            </Menu.Item>
          ))}
        </Menu.Dropdown>
      </Menu>
      {error !== undefined && (
        <Text size="sm" c="var(--mantine-color-error)" role="alert">
          {error}
        </Text>
      )}
    </Stack>
  );
}

/**
 * The Song page's header: shortcode, title, workflow state, and concept, each edited where it is
 * shown. Every save goes through {@link useRevisionedSave}, so a save based on an old revision is
 * refused and offered for comparison and reapplying instead of overwriting. `onSong` gets every
 * newer Song: one just saved, or the current one a refused save brought back.
 */
export function SongHeader({ song, onSong }: { song: Song; onSong: (song: Song) => void }) {
  const { state: statesState } = useWorkflowStates();
  const states = statesState.phase === 'ready' ? statesState.data : undefined;

  const fields = useMemo((): SavedField<Song>[] => {
    const showState = (id: FieldValue) => {
      const state = states?.find((candidate) => candidate.id === id);
      return state ? <StateBadge name={state.name} colour={state.colour} /> : (id ?? '');
    };
    const showText = (value: FieldValue) => <ConflictValue value={value} />;
    return [
      { key: 'title', label: 'Title', read: (record) => record.title, show: showText },
      { key: 'concept', label: 'Concept', read: (record) => record.concept, show: showText },
      { key: 'stateId', label: 'State', read: (record) => record.state.id, show: showState },
    ];
  }, [states]);

  const send = useCallback(
    (base: Song, key: string, value: FieldValue) => updateSong(base, { [key]: value }),
    [],
  );

  const { save, dialog } = useRevisionedSave({
    record: song,
    onRecord: onSong,
    fields,
    send,
    subject: 'This Song',
  });

  return (
    <>
      <Stack gap={4}>
        <Text ff="monospace" size="sm" c="var(--n8-color-secondary-text)" data-testid="shortcode">
          {song.shortcode}
        </Text>
        <Group gap="md" align="flex-start" wrap="wrap">
          <div style={{ flex: '1 1 20rem', minWidth: 0 }}>
            <TitleField song={song} save={save} />
          </div>
          <StateField song={song} states={states} save={save} />
        </Group>
      </Stack>
      <ConceptField song={song} save={save} />
      {dialog}
    </>
  );
}
