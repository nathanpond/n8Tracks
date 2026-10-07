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
import { useCreateFields, type CreateFields, type OptionValue } from '../api/createFields';
import {
  inputText,
  lineageOf,
  lineageSummary,
  lineageText,
  lineageValue,
  type Lineage,
} from '../api/lineage';
import type { FailureReason, FieldValue } from '../api/saves';
import { restoreSnapshot, type Snapshot } from '../api/snapshots';
import { useConfiguredTimeZone } from '../api/timeZone';
import {
  isLineageKey,
  isVersionDetail,
  KIND_OPTION,
  LINEAGE_KEYS,
  SONG_MODE_OPTION,
  OPTION_EDIT_PREFIX,
  optionFromText,
  optionText,
  sendVersionAsPageCloses,
  updateVersion,
  versionEditOf,
  useVersionDetail,
  VERSION_LYRICS_MAXIMUM_LENGTH,
  VERSION_NAME_MAXIMUM_LENGTH,
  VERSION_NOTES_MAXIMUM_LENGTH,
  VERSION_STYLES_MAXIMUM_LENGTH,
  type Version,
  type VersionDetail,
  type VersionOptions,
} from '../api/versions';
import { ConflictValue } from '../common/ConflictDialog';
import { ShortcodeBadge } from '../common/ShortcodeBadge';
import { useRevisionedSave, type SavedField } from '../common/useRevisionedSave';
import { AutosaveIndicator } from '../editor/AutosaveIndicator';
import { FrozenNotice } from '../editor/FrozenNotice';
import { HistoryPanel, type RestoreResult } from '../editor/HistoryPanel';
import { ImportedNotice } from '../editor/ImportedNotice';
import { LeaveGuard } from '../editor/LeaveGuard';
import { useAutosave, type AutosaveStatus, type Edit } from '../editor/useAutosave';
import { useSnapshots, type EditorText } from '../editor/useSnapshots';
import { VersionInputs } from '../editor/VersionInputs';
import { GenerateOnSunoButton, GenerateOnSunoStatus } from '../versions/GenerateOnSuno';
import { useGenerateOnSuno, type GenerateOnSunoController } from '../versions/useGenerateOnSuno';
import { SourcesSection } from '../versions/SourcesSection';
import { OptionsPanel } from './inputs/OptionsPanel';
import { choiceLabel } from './inputs/optionFormat';
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

/** The Version's editable fields as the user has them on screen: its text, and its options. */
interface Drafts {
  name: string;
  notes: string;
  lyrics: string;
  styles: string;
  inputs: VersionOptions;
}

/** The text fields of {@link Drafts}. */
type TextKey = Exclude<keyof Drafts, 'inputs'>;

function draftsOf(version: VersionDetail): Drafts {
  return {
    name: version.name ?? '',
    notes: version.notes ?? '',
    lyrics: version.lyrics,
    styles: version.styles,
    inputs: version.inputs,
  };
}

/** Every option key either side holds: the lineage keys (#122) are not options. */
function optionKeys(...sides: VersionOptions[]): string[] {
  return [...new Set(sides.flatMap((side) => Object.keys(side)))].filter(
    (key) => !isLineageKey(key),
  );
}

/**
 * Every key of `inputs` either side holds, the lineage keys always among them: a lineage key left
 * out is an empty one, the same as one held empty.
 */
function inputKeys(...sides: VersionOptions[]): string[] {
  return [...new Set([...sides.flatMap((side) => Object.keys(side)), ...LINEAGE_KEYS])];
}

/** How the conflict view names each lineage key. */
const LINEAGE_LABELS: Readonly<Record<string, string>> = {
  sources: 'Sources',
  inspiration: 'Inspiration',
  voice: 'Voice',
  fileInputs: 'Files to attach in Suno',
};

/** Each editable field: how its stored value is read, and how a draft of it is saved. */
const DRAFTED: readonly {
  key: TextKey;
  read: (version: VersionDetail) => FieldValue;
  normalise: (draft: string) => FieldValue;
}[] = [
  { key: 'name', read: (version) => version.name, normalise: normaliseName },
  { key: 'notes', read: (version) => version.notes, normalise: normaliseNotes },
  // Lyrics and styles are stored exactly as typed.
  { key: 'lyrics', read: (version) => version.lyrics, normalise: (draft) => draft },
  { key: 'styles', read: (version) => version.styles, normalise: (draft) => draft },
];

/** The creation inputs: the fields a frozen Version can no longer take (its options too). */
const INPUTS: readonly TextKey[] = ['lyrics', 'styles'];

/**
 * The fields whose drafts differ from `stored`, with the values they would be saved as; each option
 * that differs is `inputs.<key>`, holding its JSON. A frozen Version's lyrics, styles, and options
 * are never sent.
 */
function editOf(drafts: Drafts, stored: VersionDetail): Edit {
  const edit: Record<string, FieldValue> = {};
  for (const field of DRAFTED) {
    if (stored.isFrozen && INPUTS.includes(field.key)) {
      continue;
    }
    const value = field.normalise(drafts[field.key]);
    if (value !== field.read(stored)) {
      edit[field.key] = value;
    }
  }
  if (!stored.isFrozen) {
    for (const key of inputKeys(drafts.inputs)) {
      const value = inputText(key, drafts.inputs[key]);
      if (value !== inputText(key, stored.inputs[key])) {
        edit[OPTION_EDIT_PREFIX + key] = value;
      }
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
  for (const key of inputKeys(before.inputs, after.inputs)) {
    const was = inputText(key, before.inputs[key]);
    if (
      !Object.hasOwn(sending, OPTION_EDIT_PREFIX + key) &&
      inputText(key, after.inputs[key]) !== was &&
      inputText(key, next.inputs[key]) === was
    ) {
      next = { ...next, inputs: { ...next.inputs, [key]: after.inputs[key] ?? null } };
    }
  }
  return next;
}

/** Whether `drafts` hold lyrics or styles that `stored` does not. */
function textDiffers(drafts: Drafts, stored: VersionDetail): boolean {
  return drafts.lyrics !== stored.lyrics || drafts.styles !== stored.styles;
}

/** Whether `drafts` hold an option `stored` does not. */
function optionsDiffer(drafts: Drafts, stored: VersionDetail): boolean {
  return inputKeys(drafts.inputs, stored.inputs).some(
    (key) => inputText(key, drafts.inputs[key]) !== inputText(key, stored.inputs[key]),
  );
}

/** Whether `drafts` hold creation inputs (lyrics, styles, options) that `stored` does not. */
function inputsDiffer(drafts: Drafts, stored: VersionDetail): boolean {
  return textDiffers(drafts, stored) || optionsDiffer(drafts, stored);
}

/**
 * Why `current` cannot take `edit` at all: it is frozen and the edit changes its lyrics, styles, or
 * an option. A stale save meeting that is not a conflict to resolve; the text goes to a new Version
 * instead.
 */
function refusesInputs(
  current: VersionDetail,
  edit: Readonly<Record<string, FieldValue>>,
): FailureReason | undefined {
  const changes = (key: 'lyrics' | 'styles') =>
    Object.hasOwn(edit, key) && edit[key] !== current[key];
  const changesOption = Object.entries(edit).some(
    ([key, value]) =>
      key.startsWith(OPTION_EDIT_PREFIX) &&
      value !==
        inputText(
          key.slice(OPTION_EDIT_PREFIX.length),
          current.inputs[key.slice(OPTION_EDIT_PREFIX.length)],
        ),
  );
  return current.isFrozen && (changes('lyrics') || changes('styles') || changesOption)
    ? 'frozen'
    : undefined;
}

/** Why the drafts cannot be saved as they are, or undefined when they can. */
function problemOf(drafts: Drafts, createFields: CreateFields | undefined): string | undefined {
  const optionProblems = (createFields?.fields ?? []).map((field) => {
    const value = field.option === null ? undefined : drafts.inputs[field.option];
    return (
      typeof value === 'string' &&
      field.maxLength !== undefined &&
      value.length > field.maxLength &&
      `${field.label} is over its limit.`
    );
  });
  const problems = [
    nameError(singleLine(drafts.name)) && 'The name is over its limit.',
    notesError(drafts.notes) && 'The notes are over their limit.',
    drafts.lyrics.length > VERSION_LYRICS_MAXIMUM_LENGTH && 'The lyrics are over their limit.',
    drafts.styles.length > VERSION_STYLES_MAXIMUM_LENGTH && 'The styles are over their limit.',
    ...optionProblems,
  ].filter((problem): problem is string => typeof problem === 'string');
  return problems.length === 0 ? undefined : `${problems.join(' ')} Shorten the text to save.`;
}

/** The lyrics and styles of the drafts, or undefined when they are over a limit (the API refuses them). */
function snapshotTextOf(drafts: Drafts): EditorText | undefined {
  return drafts.lyrics.length > VERSION_LYRICS_MAXIMUM_LENGTH ||
    drafts.styles.length > VERSION_STYLES_MAXIMUM_LENGTH
    ? undefined
    : { lyrics: drafts.lyrics, styles: drafts.styles };
}

/** Why Restore is unavailable while the editor's work is in `status`, or undefined when it is available. */
function restoreBlockedBy(status: AutosaveStatus): string | undefined {
  switch (status.kind) {
    case 'saved':
      return undefined;
    case 'conflict':
      return 'Restore is unavailable while this Version has a conflict to resolve. Choose Reload or Reapply my change first.';
    default:
      return 'Restore is unavailable while your changes are not saved. It is available again once they are.';
  }
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
  /**
   * The Version turned out to be deleted (elsewhere) on a save or a load: `text` is the unsaved
   * lyrics and styles, if any, to offer as a new Version's content.
   */
  onDeletedElsewhere: (version: Version, text: EditorText | undefined) => void;
}

/** The heading, marks, and action buttons of the selected Version. */
function VersionHeader({
  version,
  actions,
  busy,
  generate,
}: Omit<DetailsProps, 'onVersion' | 'onDeletedElsewhere'> & {
  /** Generate on Suno for this Version: held by the panel, so it outlives the switch from loading to loaded. */
  generate: GenerateOnSunoController;
}) {
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
        <GenerateOnSunoButton controller={generate} />
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
        <Button
          variant="default"
          color="red"
          disabled={busy}
          onClick={() => {
            actions.onDelete(version);
          }}
        >
          Delete
        </Button>
      </Group>
      <ShortcodeBadge shortcode={version.shortcode} testId="version-shortcode" />
      <GenerateOnSunoStatus controller={generate} />
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
  const { state: fieldsState, reload: reloadFields } = useCreateFields();

  // A Version no longer there when it is loaded may have been deleted elsewhere: the page reads
  // its Versions again, and says so if it was.
  const { version, onDeletedElsewhere } = props;
  const missing = state.phase === 'not-found';
  useEffect(() => {
    if (missing) {
      onDeletedElsewhere(version, undefined);
    }
  }, [missing, onDeletedElsewhere, version]);

  const generate = useGenerateOnSuno(props.version.id);

  if (state.phase === 'ready' && fieldsState.phase === 'ready') {
    return (
      <LoadedVersionDetails
        {...props}
        loaded={state.data}
        createFields={fieldsState.data}
        generate={generate}
      />
    );
  }

  return (
    <Paper p="md" withBorder component="section" aria-labelledby="version-heading">
      <Stack gap="sm">
        <VersionHeader
          version={props.version}
          actions={props.actions}
          busy={props.busy}
          generate={generate}
        />
        {state.phase === 'loading' ||
        (state.phase === 'ready' && fieldsState.phase === 'loading') ? (
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
            <Button
              variant="default"
              size="compact-sm"
              onClick={() => {
                if (state.phase !== 'ready') {
                  reload();
                }
                if (fieldsState.phase !== 'ready') {
                  reloadFields();
                }
              }}
            >
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
  onDeletedElsewhere,
  actions,
  busy,
  loaded,
  createFields,
  generate,
}: DetailsProps & {
  loaded: VersionDetail;
  createFields: CreateFields;
  generate: GenerateOnSunoController;
}) {
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
        : {
            ...record,
            name: version.name,
            notes: version.notes,
            archived: version.archived,
            isFrozen: version.isFrozen,
          };
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

  const optionNames = optionKeys(loaded.inputs).join(',');
  const fields = useMemo((): SavedField<VersionDetail>[] => {
    const show = (value: FieldValue) => <ConflictValue value={value} />;
    // An option is compared by its JSON and shown as the page writes its value.
    const showOption = (value: FieldValue) => {
      const option = optionFromText(value);
      return (
        <ConflictValue
          value={
            option === null
              ? null
              : typeof option === 'string'
                ? choiceLabel(option)
                : typeof option === 'boolean'
                  ? option
                    ? 'On'
                    : 'Off'
                  : String(option)
          }
        />
      );
    };
    // Speech and Sound options share labels with a Song's (Vocal Gender, Variety), so say whose.
    const labelOf = (key: string) => {
      const field = createFields.fields.find((candidate) => candidate.option === key);
      if (field === undefined) {
        return choiceLabel(key);
      }
      return field.tab === 'speech'
        ? `Speech: ${field.label}`
        : field.tab === 'sounds'
          ? `Sound: ${field.label}`
          : field.label;
    };
    return [
      { key: 'name', label: 'Name', read: (current) => current.name, show },
      { key: 'notes', label: 'Notes', read: (current) => current.notes, show },
      { key: 'lyrics', label: 'Lyrics', read: (current) => current.lyrics, show },
      { key: 'styles', label: 'Styles', read: (current) => current.styles, show },
      ...optionNames
        .split(',')
        .filter((key) => key !== '')
        .map((key): SavedField<VersionDetail> => ({
          key: OPTION_EDIT_PREFIX + key,
          label: labelOf(key),
          read: (current) => optionText(current.inputs[key]),
          show: showOption,
        })),
      ...LINEAGE_KEYS.map((key): SavedField<VersionDetail> => ({
        key: OPTION_EDIT_PREFIX + key,
        label: LINEAGE_LABELS[key] ?? key,
        read: (current) => lineageText(key, current.inputs[key]),
        show: (value) => <ConflictValue value={lineageSummary(key, value)} />,
      })),
    ];
  }, [createFields, optionNames]);

  const send = useCallback(
    (base: VersionDetail, edit: Readonly<Record<string, FieldValue>>) =>
      updateVersion(base, versionEditOf(edit)),
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

  // History: snapshots of the lyrics and styles on screen, taken on a pause and on leaving.
  const [historyKey, setHistoryKey] = useState(0);
  const snapshots = useSnapshots({
    versionId: loaded.id,
    read: useCallback(() => snapshotTextOf(latestDrafts.current), []),
    onStored: useCallback(() => {
      setHistoryKey((key) => key + 1);
    }, []),
  });
  const { capture, rebase } = snapshots;

  const { saveFields, dialog } = useRevisionedSave({
    record,
    onRecord,
    fields,
    send,
    subject: 'This Version',
    refuses: refusesInputs,
  });

  // A freeze met with unsaved lyrics or styles: that text cannot go into this Version, so it is
  // kept (`carried`, and a snapshot in History) to start a new one, and the editor goes back to
  // the stored text, read only. Unsaved options go back to the stored ones (a new Version copies
  // those). The name and notes are unaffected and go on saving.
  const [carried, setCarried] = useState<EditorText | undefined>();
  const takeFrozen = useCallback(
    (stored: VersionDetail) => {
      const now = latestDrafts.current;
      if (!inputsDiffer(now, stored)) {
        return;
      }
      if (textDiffers(now, stored)) {
        capture();
        setCarried({ lyrics: now.lyrics, styles: now.styles });
        rebase({ lyrics: stored.lyrics, styles: stored.styles }, false);
      }
      setDrafts({ ...now, lyrics: stored.lyrics, styles: stored.styles, inputs: stored.inputs });
    },
    [capture, rebase, setDrafts],
  );

  /**
   * After a save, each lineage part it sent is taken as the API stored it (with what it looked up: a
   * pasted Suno ID it has as a Generation, titles, availability), unless it was changed again since.
   */
  const adoptSaved = useCallback(
    (edit: Edit) => {
      const now = latestDrafts.current;
      let inputs = now.inputs;
      for (const key of LINEAGE_KEYS) {
        const sent = edit[OPTION_EDIT_PREFIX + key];
        if (sent !== undefined && inputText(key, now.inputs[key]) === sent) {
          inputs = { ...inputs, [key]: latest.current.inputs[key] ?? null };
        }
      }
      if (inputs !== now.inputs) {
        setDrafts({ ...now, inputs });
      }
    },
    [setDrafts],
  );

  /** A 409 `version_frozen` on a Version the page did not know was frozen: it is now. */
  const markFrozen = useCallback(() => {
    if (!latest.current.isFrozen) {
      const frozen = { ...latest.current, isFrozen: true };
      latest.current = frozen;
      setRecord(frozen);
      onVersion(frozen);
    }
  }, [onVersion]);

  const autosave = useAutosave({
    pending: useCallback(() => editOf(latestDrafts.current, latest.current), []),
    problem: useCallback(() => problemOf(latestDrafts.current, createFields), [createFields]),
    send: useCallback(
      async (edit: Edit) => {
        sending.current = edit;
        try {
          const outcome = await saveFields(edit);
          if (outcome.kind === 'saved') {
            adoptSaved(edit);
          }
          if (outcome.kind === 'failed' && outcome.reason === 'deleted') {
            // Deleted elsewhere: the unsaved lyrics and styles go to the page, to start a new Version.
            const now = latestDrafts.current;
            onDeletedElsewhere(
              latest.current,
              textDiffers(now, latest.current)
                ? { lyrics: now.lyrics, styles: now.styles }
                : undefined,
            );
            return outcome;
          }
          if (outcome.kind !== 'failed' || outcome.reason !== 'frozen') {
            return outcome;
          }
          // The Version froze: its lyrics and styles are carried to a new Version, and the rest of
          // the edit (name, notes) is saved as usual, so the frozen notice, not a failure, shows.
          markFrozen();
          takeFrozen(latest.current);
          const rest = editOf(latestDrafts.current, latest.current);
          if (Object.keys(rest).length === 0) {
            return { kind: 'saved' } as const;
          }
          sending.current = rest;
          const saved = await saveFields(rest);
          if (saved.kind === 'saved') {
            adoptSaved(rest);
          }
          return saved;
        } finally {
          sending.current = null;
        }
      },
      [adoptSaved, markFrozen, onDeletedElsewhere, saveFields, takeFrozen],
    ),
    onReloaded: useCallback(() => {
      // The text being discarded goes into history first; the text taken in may be new to it.
      capture();
      setDrafts(draftsOf(latest.current));
      rebase({ lyrics: latest.current.lyrics, styles: latest.current.styles }, true);
    }, [capture, rebase, setDrafts]),
  });
  const { changed, flush } = autosave;

  // A freeze learnt any other way (the Version read again from the tree) carries the text too.
  useEffect(() => {
    if (record.isFrozen && inputsDiffer(latestDrafts.current, record)) {
      takeFrozen(record);
      changed();
    }
  }, [record, takeFrozen, changed]);

  const snapshotChanged = snapshots.changed;
  const change = useCallback(
    (key: TextKey, value: string) => {
      setDrafts({ ...latestDrafts.current, [key]: value });
      changed();
      if (key === 'lyrics' || key === 'styles') {
        snapshotChanged();
      }
    },
    [changed, setDrafts, snapshotChanged],
  );

  const changeOption = useCallback(
    (key: string, value: OptionValue) => {
      const now = latestDrafts.current;
      setDrafts({ ...now, inputs: { ...now.inputs, [key]: value } });
      changed();
    },
    [changed, setDrafts],
  );

  const changeLineage = useCallback(
    (lineage: Lineage) => {
      const now = latestDrafts.current;
      const inputs: Record<string, OptionValue> = { ...now.inputs };
      for (const key of ['sources', 'inspiration', 'voice', 'fileInputs'] as const) {
        inputs[key] = lineageValue(lineage, key);
      }
      setDrafts({ ...now, inputs });
      changed();
    },
    [changed, setDrafts],
  );

  const restore = useCallback(
    async (snapshot: Snapshot): Promise<RestoreResult> => {
      const result = await restoreSnapshot(latest.current, snapshot.id);
      if (result.kind === 'saved') {
        const restored = result.record;
        onRecord(restored);
        setDrafts({ ...latestDrafts.current, lyrics: restored.lyrics, styles: restored.styles });
        // The restored text is the snapshot's, and the text it replaced the API has kept.
        rebase({ lyrics: restored.lyrics, styles: restored.styles }, false);
        setHistoryKey((key) => key + 1);
        return 'restored';
      }
      if (result.kind === 'conflict') {
        onRecord(result.current);
        return 'changed-elsewhere';
      }
      if (result.kind === 'failed' && result.reason === 'frozen') {
        markFrozen();
      }
      return 'failed';
    },
    [markFrozen, onRecord, rebase, setDrafts],
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

  const snapshotAsPageCloses = snapshots.onPageHide;
  const onPageHide = useCallback(() => {
    // The save goes first: the browser limits how much may still be sent as a page closes.
    const edit = editOf(latestDrafts.current, latest.current);
    if (
      Object.keys(edit).length > 0 &&
      problemOf(latestDrafts.current, createFields) === undefined
    ) {
      sendVersionAsPageCloses(latest.current, versionEditOf(edit));
    }
    snapshotAsPageCloses();
  }, [createFields, snapshotAsPageCloses]);

  const shown: Version = { ...record, current: version.current };
  const frozen = record.isFrozen;
  // The editing history covers a Song's lyrics and styles; a Speech or Sound has none in V1.
  const isSong = (drafts.inputs[KIND_OPTION] ?? 'song') === 'song';
  // On a frozen Version, branching from it carries any text it could not take.
  const paneActions: VersionActions = useMemo(
    () => ({
      ...actions,
      onCreateFrom: (target, content) => {
        actions.onCreateFrom(target, content ?? (target.id === record.id ? carried : undefined));
      },
    }),
    [actions, carried, record.id],
  );

  return (
    <Paper p="md" withBorder component="section" aria-labelledby="version-heading" ref={panel}>
      <Stack gap="sm">
        <VersionHeader version={shown} actions={paneActions} busy={busy} generate={generate} />
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
        <div role="status">
          {frozen && (
            <FrozenNotice
              number={record.number}
              carried={carried !== undefined}
              onCreate={() => {
                paneActions.onCreateFrom(shown);
              }}
            />
          )}
        </div>
        <ImportedNotice imported={record.imported} fields={createFields.fields} />
        <OptionsPanel
          fields={createFields}
          options={drafts.inputs}
          onOption={changeOption}
          readOnly={frozen}
          text={(sections) => (
            <VersionInputs
              readOnly={frozen}
              lyrics={drafts.lyrics}
              styles={drafts.styles}
              showLyrics={sections.lyrics}
              showStyles={sections.styles}
              onLyrics={(value) => {
                change('lyrics', value);
              }}
              onStyles={(value) => {
                change('styles', value);
              }}
            />
          )}
        />
        {isSong && <Divider />}
        {isSong && (
          <SourcesSection
            lineage={lineageOf(drafts.inputs)}
            songMode={drafts.inputs[SONG_MODE_OPTION] === 'simple' ? 'simple' : 'advanced'}
            versionId={record.id}
            readOnly={frozen}
            onChange={changeLineage}
          />
        )}
        {isSong && <Divider />}
        {isSong && (
          <HistoryPanel
            versionId={record.id}
            current={{ lyrics: drafts.lyrics, styles: drafts.styles }}
            timeZone={timeZone}
            refreshKey={historyKey}
            restoreBlocked={restoreBlockedBy(autosave.status)}
            onRestore={restore}
            onRestoreIntoNew={
              frozen
                ? (snapshot) => {
                    actions.onCreateFrom(shown, {
                      lyrics: snapshot.lyrics,
                      styles: snapshot.styles,
                    });
                  }
                : undefined
            }
          />
        )}
      </Stack>
      {dialog}
      <LeaveGuard status={autosave.status} flush={flush} onPageHide={onPageHide} />
    </Paper>
  );
}
