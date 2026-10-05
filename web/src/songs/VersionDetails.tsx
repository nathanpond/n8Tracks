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
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { FieldValue } from '../api/saves';
import { useConfiguredTimeZone } from '../api/timeZone';
import {
  isVersionDetail,
  sendVersionAsPageCloses,
  updateVersion,
  useVersionDetail,
  VERSION_LYRICS_MAXIMUM_LENGTH,
  VERSION_NAME_MAXIMUM_LENGTH,
  VERSION_NOTES_MAXIMUM_LENGTH,
  VERSION_STYLES_MAXIMUM_LENGTH,
  type Version,
  type VersionDetail,
} from '../api/versions';
import { ConflictValue } from '../common/ConflictDialog';
import { useRevisionedSave, type SavedField } from '../common/useRevisionedSave';
import { AutosaveIndicator } from '../editor/AutosaveIndicator';
import { LeaveGuard } from '../editor/LeaveGuard';
import { useAutosave, type Edit } from '../editor/useAutosave';
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

/** The text in the Version's editable fields, as the user has it on screen. */
interface Drafts {
  name: string;
  notes: string;
  lyrics: string;
  styles: string;
}

function draftsOf(version: VersionDetail): Drafts {
  return {
    name: version.name ?? '',
    notes: version.notes ?? '',
    lyrics: version.lyrics,
    styles: version.styles,
  };
}

/** Each editable field: how its stored value is read, and how a draft of it is saved. */
const DRAFTED: readonly {
  key: keyof Drafts;
  read: (version: VersionDetail) => FieldValue;
  normalise: (draft: string) => FieldValue;
}[] = [
  { key: 'name', read: (version) => version.name, normalise: normaliseName },
  { key: 'notes', read: (version) => version.notes, normalise: normaliseNotes },
  // Lyrics and styles are stored exactly as typed.
  { key: 'lyrics', read: (version) => version.lyrics, normalise: (draft) => draft },
  { key: 'styles', read: (version) => version.styles, normalise: (draft) => draft },
];

/** The fields whose drafts differ from `stored`, with the values they would be saved as. */
function editOf(drafts: Drafts, stored: VersionDetail): Edit {
  const edit: Record<string, FieldValue> = {};
  for (const field of DRAFTED) {
    const value = field.normalise(drafts[field.key]);
    if (value !== field.read(stored)) {
      edit[field.key] = value;
    }
  }
  return edit;
}

/**
 * The drafts once the stored Version moves from `before` to `after`: a field the user has not
 * changed (its draft is what `before` holds) takes `after`'s value when that differs; one they
 * have changed keeps their text, and so does every field of the save in flight (`sending`), whose
 * draft may have been typed back to the old value meanwhile. So a change made elsewhere and taken
 * in (reapplying over it, archiving from the tree) is shown, and never sent back over itself.
 */
function follow(
  drafts: Drafts,
  before: VersionDetail,
  after: VersionDetail,
  sending: Edit = {},
): Drafts {
  let next = drafts;
  for (const field of DRAFTED) {
    if (
      !Object.hasOwn(sending, field.key) &&
      field.read(after) !== field.read(before) &&
      field.normalise(drafts[field.key]) === field.read(before)
    ) {
      next = { ...next, [field.key]: draftsOf(after)[field.key] };
    }
  }
  return next;
}

/** Why the drafts cannot be saved as they are, or undefined when they can. */
function problemOf(drafts: Drafts): string | undefined {
  const problems = [
    nameError(singleLine(drafts.name)) && 'The name is over its limit.',
    notesError(drafts.notes) && 'The notes are over their limit.',
    drafts.lyrics.length > VERSION_LYRICS_MAXIMUM_LENGTH && 'The lyrics are over their limit.',
    drafts.styles.length > VERSION_STYLES_MAXIMUM_LENGTH && 'The styles are over their limit.',
  ].filter((problem): problem is string => typeof problem === 'string');
  return problems.length === 0 ? undefined : `${problems.join(' ')} Shorten the text to save.`;
}

/** The name, on one line, always editable; saved automatically as the user types. */
function NameField({ draft, onChange }: { draft: string; onChange: (name: string) => void }) {
  const error = nameError(singleLine(draft));
  return (
    <TextInput
      label="Name"
      description={`Up to ${VERSION_NAME_MAXIMUM_LENGTH.toLocaleString('en-US')} characters; leave it empty for no name.`}
      value={draft}
      onChange={(event) => {
        onChange(singleLine(event.currentTarget.value));
      }}
      error={error}
      aria-invalid={error !== undefined}
    />
  );
}

/** The notes: several lines, always editable; saved automatically as the user types. */
function NotesField({ draft, onChange }: { draft: string; onChange: (notes: string) => void }) {
  const error = notesError(draft);
  return (
    <Textarea
      label="Notes"
      description={`Up to ${VERSION_NOTES_MAXIMUM_LENGTH.toLocaleString('en-US')} characters.`}
      rows={3}
      resize="vertical"
      value={draft}
      onChange={(event) => {
        onChange(event.currentTarget.value);
      }}
      error={error}
      aria-invalid={error !== undefined}
    />
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
 * its name and notes (annotations, editable at any time), and its lyrics and styles in the editor.
 * The Version is read with its lyrics and styles first. Every field saves automatically
 * ({@link useAutosave}): one combined save on one queue for the whole Version, through one
 * {@link useRevisionedSave}, so one based on an old revision is refused and offered for
 * comparison; the indicator says whether the work is stored, and leaving with work not stored is
 * held ({@link LeaveGuard}). `onVersion` gets every newer Version.
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
  const [drafts, setDraftsState] = useState(() => draftsOf(loaded));
  const latestDrafts = useRef(drafts);
  /** The edit of the save in flight, if any. */
  const sending = useRef<Edit | null>(null);
  const [seen, setSeen] = useState(version);

  // A newer revision of this Version from the page (archived or unarchived from the tree) replaces
  // the record; one without lyrics and styles brings its annotations only. Unchanged drafts follow.
  if (version !== seen) {
    setSeen(version);
    if (version.revision > record.revision) {
      const next = isVersionDetail(version)
        ? version
        : { ...record, name: version.name, notes: version.notes, archived: version.archived };
      setRecord(next);
      setDraftsState(follow(drafts, record, next));
    }
  }
  useEffect(() => {
    latest.current = record;
  }, [record]);
  useEffect(() => {
    latestDrafts.current = drafts;
  }, [drafts]);

  const setDrafts = useCallback((next: Drafts) => {
    latestDrafts.current = next;
    setDraftsState(next);
  }, []);

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
      const before = latest.current;
      latest.current = next;
      setRecord(next);
      const followed = follow(latestDrafts.current, before, next, sending.current ?? undefined);
      if (followed !== latestDrafts.current) {
        setDrafts(followed);
      }
      onVersion(next);
    },
    [onVersion, setDrafts],
  );

  const { saveFields, dialog } = useRevisionedSave({
    record,
    onRecord,
    fields,
    send,
    subject: 'This Version',
  });

  const autosave = useAutosave({
    pending: useCallback(() => editOf(latestDrafts.current, latest.current), []),
    problem: useCallback(() => problemOf(latestDrafts.current), []),
    send: useCallback(
      async (edit: Edit) => {
        sending.current = edit;
        try {
          return await saveFields(edit);
        } finally {
          sending.current = null;
        }
      },
      [saveFields],
    ),
    onReloaded: useCallback(() => {
      setDrafts(draftsOf(latest.current));
    }, [setDrafts]),
  });
  const { changed, flush } = autosave;

  const change = useCallback(
    (key: keyof Drafts, value: string) => {
      setDrafts({ ...latestDrafts.current, [key]: value });
      changed();
    },
    [changed, setDrafts],
  );

  // Ctrl/Cmd+S anywhere in the pane (the lyrics editor included) saves now, not after the pause.
  const panel = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const element = panel.current;
    const keys = (event: globalThis.KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
        event.preventDefault();
        void flush();
      }
    };
    element?.addEventListener('keydown', keys);
    return () => {
      element?.removeEventListener('keydown', keys);
    };
  }, [flush]);

  const onPageHide = useCallback(() => {
    const edit = editOf(latestDrafts.current, latest.current);
    if (Object.keys(edit).length > 0 && problemOf(latestDrafts.current) === undefined) {
      sendVersionAsPageCloses(latest.current, edit);
    }
  }, []);

  const shown: Version = { ...record, current: version.current };

  return (
    <Paper p="md" withBorder component="section" aria-labelledby="version-heading" ref={panel}>
      <Stack gap="sm">
        <VersionHeader version={shown} actions={actions} busy={busy} />
        <AutosaveIndicator
          status={autosave.status}
          onRetry={autosave.retry}
          onReload={autosave.reload}
          onReapply={autosave.reapply}
        />
        <NameField
          draft={drafts.name}
          onChange={(value) => {
            change('name', value);
          }}
        />
        <NotesField
          draft={drafts.notes}
          onChange={(value) => {
            change('notes', value);
          }}
        />
        <Text size="sm" c="var(--n8-color-secondary-text)">
          Created <RelativeTime utc={record.createdAt} timeZone={timeZone} />
        </Text>
        <Divider />
        <VersionInputs
          lyrics={drafts.lyrics}
          styles={drafts.styles}
          onLyrics={(value) => {
            change('lyrics', value);
          }}
          onStyles={(value) => {
            change('styles', value);
          }}
        />
      </Stack>
      {dialog}
      <LeaveGuard status={autosave.status} flush={flush} onPageHide={onPageHide} />
    </Paper>
  );
}
