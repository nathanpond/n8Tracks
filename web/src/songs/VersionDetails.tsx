import {
  Badge,
  Button,
  Divider,
  Group,
  Loader,
  Paper,
  Stack,
  Text,
  TextInput,
  Textarea,
  Title,
} from '@mantine/core';
import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import type { FieldValue } from '../api/saves';
import { useConfiguredTimeZone } from '../api/timeZone';
import {
  isVersionDetail,
  updateVersion,
  useVersionDetail,
  VERSION_NAME_MAXIMUM_LENGTH,
  VERSION_NOTES_MAXIMUM_LENGTH,
  type Version,
  type VersionDetail,
} from '../api/versions';
import { ConflictValue } from '../common/ConflictDialog';
import { focusOnMount, useInPlaceEdit } from '../common/useInPlaceEdit';
import { useRevisionedSave, type SaveOutcome, type SavedField } from '../common/useRevisionedSave';
import { VersionInputs } from '../editor/VersionInputs';
import { RelativeTime } from './SongParts';
import { nameError, notesError, singleLine } from './songRules';
import type { VersionActions } from './VersionTree';

/** A name as it is saved: one line, trimmed, and null when nothing is left. */
function normaliseName(draft: string): FieldValue {
  const trimmed = singleLine(draft).trim();
  return trimmed === '' ? null : trimmed;
}

/** Notes as they are saved: line endings as `\n`, trimmed, and null when nothing is left. */
function normaliseNotes(draft: string): FieldValue {
  const normalised = draft.replace(/\r\n|\r/g, '\n').trim();
  return normalised === '' ? null : normalised;
}

type Save = (key: string, value: FieldValue) => Promise<SaveOutcome>;

/** The name, edited in place on one line. Enter or blur saves; Escape cancels. */
function NameField({ version, save }: { version: Version; save: Save }) {
  const edit = useInPlaceEdit({
    field: 'name',
    current: version.name,
    normalise: normaliseName,
    check: (draft) => nameError(singleLine(draft)),
    save,
  });

  if (!edit.editing) {
    return (
      <Group gap="sm" align="center" wrap="nowrap">
        <Text fw={700} style={{ overflowWrap: 'anywhere' }}>
          {version.name ?? (
            <Text span fw={400} c="var(--n8-color-secondary-text)">
              No name.
            </Text>
          )}
        </Text>
        <Button variant="subtle" size="compact-sm" onClick={edit.start} aria-label="Edit name">
          Edit
        </Button>
      </Group>
    );
  }

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
      label="Name"
      description={`Up to ${VERSION_NAME_MAXIMUM_LENGTH.toLocaleString('en-US')} characters. Enter saves, Escape cancels; leave it empty to clear it.`}
      value={edit.draft}
      onChange={(event) => {
        edit.setDraft(singleLine(event.currentTarget.value));
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
  );
}

/** The notes: several lines, edited in place. Blur or Ctrl/Cmd+Enter saves; Escape cancels. */
function NotesField({ version, save }: { version: Version; save: Save }) {
  const edit = useInPlaceEdit({
    field: 'notes',
    current: version.notes,
    normalise: normaliseNotes,
    check: notesError,
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
    <Stack gap={4}>
      <Group gap="sm" align="center">
        <Title order={4} size="h6" id="version-notes">
          Notes
        </Title>
        {!edit.editing && (
          <Button variant="subtle" size="compact-sm" onClick={edit.start} aria-label="Edit notes">
            Edit
          </Button>
        )}
      </Group>
      {edit.editing ? (
        <Textarea
          aria-labelledby="version-notes"
          description={`Up to ${VERSION_NOTES_MAXIMUM_LENGTH.toLocaleString('en-US')} characters. Ctrl+Enter or Cmd+Enter saves, Escape cancels; leave it empty to clear them.`}
          rows={5}
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
      ) : version.notes === null ? (
        <Text c="var(--n8-color-secondary-text)">No notes.</Text>
      ) : (
        <Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{version.notes}</Text>
      )}
    </Stack>
  );
}

/** What the Version's heading, actions, and annotations need, shared by every state of the panel. */
interface DetailsProps {
  version: Version;
  onVersion: (version: Version) => void;
  actions: VersionActions;
  /** An action on a Version is in flight: the action buttons wait for it. */
  busy: boolean;
}

/** The heading, marks, and action buttons of the selected Version. */
function VersionHeader({ version, actions, busy }: Omit<DetailsProps, 'onVersion'>) {
  return (
    <>
      <Group gap="sm" align="center" wrap="wrap">
        <Title order={3} size="h4" id="version-heading">
          Version {version.number}
        </Title>
        {version.current && (
          <Badge variant="filled" radius="sm" tt="none">
            Current working Version
          </Badge>
        )}
        {version.archived && (
          <Badge variant="outline" color="gray" radius="sm" tt="none">
            Archived
          </Badge>
        )}
      </Group>
      <Group gap="sm">
        <Button
          onClick={() => {
            actions.onCreateFrom(version);
          }}
        >
          Create New Version From {version.number}
        </Button>
        {!version.current && (
          <Button
            variant="default"
            disabled={busy}
            onClick={() => {
              actions.onMakeCurrent(version);
            }}
          >
            Make current
          </Button>
        )}
        <Button
          variant="default"
          disabled={busy}
          onClick={() => {
            actions.onSetArchived(version, !version.archived);
          }}
        >
          {version.archived ? 'Unarchive' : 'Archive'}
        </Button>
      </Group>
      <Text ff="monospace" size="sm" c="var(--n8-color-secondary-text)">
        {version.shortcode}
      </Text>
    </>
  );
}

/**
 * The selected Version: its number and marks, the actions the tree's menu offers (repeated here),
 * its name and notes, each edited in place at any time (they are annotations, not creation
 * inputs), and its lyrics and styles in the editor. The Version is read with its lyrics and styles
 * first. Every save goes through one {@link useRevisionedSave}, so one based on an old revision is
 * refused and offered for comparison; `onVersion` gets every newer Version.
 */
export function VersionDetails(props: DetailsProps) {
  const { state, reload } = useVersionDetail(props.version.id);

  if (state.phase === 'ready') {
    return <LoadedVersionDetails {...props} loaded={state.data} />;
  }

  return (
    <Paper p="md" withBorder component="section" aria-labelledby="version-heading">
      <Stack gap="sm">
        <VersionHeader version={props.version} actions={props.actions} busy={props.busy} />
        {state.phase === 'loading' ? (
          <Group gap="sm">
            <Loader size="sm" aria-hidden="true" />
            <Text role="status">Loading the lyrics and styles…</Text>
          </Group>
        ) : (
          <Group gap="sm">
            <Text role="alert">
              {state.phase === 'not-found'
                ? 'This Version is no longer there.'
                : 'The lyrics and styles could not be loaded.'}
            </Text>
            <Button variant="default" size="compact-sm" onClick={reload}>
              Try again
            </Button>
          </Group>
        )}
      </Stack>
    </Paper>
  );
}

function LoadedVersionDetails({
  version,
  onVersion,
  actions,
  busy,
  loaded,
}: DetailsProps & { loaded: VersionDetail }) {
  const timeZone = useConfiguredTimeZone();
  const [record, setRecord] = useState(loaded);
  const latest = useRef(loaded);
  const [seen, setSeen] = useState(version);

  // A newer revision of this Version from the page (archived or unarchived from the tree) replaces
  // the record; one without lyrics and styles brings its annotations only.
  if (version !== seen) {
    setSeen(version);
    if (version.revision > record.revision) {
      const next = isVersionDetail(version)
        ? version
        : { ...record, name: version.name, notes: version.notes, archived: version.archived };
      setRecord(next);
    }
  }
  useEffect(() => {
    latest.current = record;
  }, [record]);

  const fields = useMemo((): SavedField<VersionDetail>[] => {
    const show = (value: FieldValue) => <ConflictValue value={value} />;
    return [
      { key: 'name', label: 'Name', read: (current) => current.name, show },
      { key: 'notes', label: 'Notes', read: (current) => current.notes, show },
      { key: 'lyrics', label: 'Lyrics', read: (current) => current.lyrics, show },
      { key: 'styles', label: 'Styles', read: (current) => current.styles, show },
    ];
  }, []);

  const send = useCallback(
    (base: VersionDetail, edit: Readonly<Record<string, FieldValue>>) => updateVersion(base, edit),
    [],
  );

  const onRecord = useCallback(
    (next: VersionDetail) => {
      latest.current = next;
      setRecord(next);
      onVersion(next);
    },
    [onVersion],
  );

  const { save, saveFields, dialog } = useRevisionedSave({
    record,
    onRecord,
    fields,
    send,
    subject: 'This Version',
  });

  const shown: Version = { ...record, current: version.current };

  return (
    <Paper p="md" withBorder component="section" aria-labelledby="version-heading">
      <Stack gap="sm">
        <VersionHeader version={shown} actions={actions} busy={busy} />
        <NameField version={shown} save={save} />
        <NotesField version={shown} save={save} />
        <Text size="sm" c="var(--n8-color-secondary-text)">
          Created <RelativeTime utc={record.createdAt} timeZone={timeZone} />
        </Text>
        <Divider />
        <VersionInputs record={record} saveFields={saveFields} latest={() => latest.current} />
      </Stack>
      {dialog}
    </Paper>
  );
}
