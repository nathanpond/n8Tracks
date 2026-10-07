import {
  Anchor,
  Badge,
  Button,
  Group,
  List,
  Loader,
  Modal,
  NativeSelect,
  NumberInput,
  Stack,
  Text,
  Textarea,
  Title,
} from '@mantine/core';
import { useState, type ReactNode } from 'react';
import { Link } from 'react-router';
import {
  availabilityLabel,
  FILE_DESCRIPTION_MAXIMUM_LENGTH,
  INSPIRATION_MAXIMUM,
  sourceDuration,
  sourceShortcode,
  sourcesFor,
  sourceTitle,
  useSunoPersonas,
  useSunoPlaylists,
  type FileKind,
  type Lineage,
  type LineageSource,
} from '../api/lineage';
import { useRelationshipTypes } from '../api/relationships';
import { ignoreSource } from '../api/sunoIgnored';
import { SourcePicker } from './SourcePicker';
import {
  actionName,
  audioActionOf,
  audioTypes,
  canAddInspiration,
  chooseAudioAction,
  choosePlaylist,
  chooseVoice,
  chosenGenerationIds,
  continueAtOf,
  fileNote,
  fileNoteError,
  formatPosition,
  inspirationSources,
  moveInspirationSource,
  placeAudioSource,
  placeInspirationSource,
  removeAudioSource,
  removeFileNote,
  removeInspirationSource,
  saveFileNote,
  setContinueAt,
  splitSeconds,
  type AudioType,
  type LineageChange,
} from './sourcesRules';

const FILE_LABELS: Record<FileKind, string> = {
  audio: 'Audio file',
  image: 'Image',
  video: 'Video',
};

const ADD_NOTE_LABELS: Record<FileKind, string> = {
  audio: 'Add an audio file note',
  image: 'Add an image note',
  video: 'Add a video note',
};

/** Where the picker puts what is chosen: an audio source's place, or an Inspiration source's. */
type PickTarget = { group: 'audio'; index: number } | { group: 'inspiration'; index: number };

/** A label Suno marks an action with (TS-002: Inspiration and Voice are Pro); never a reason to block. */
function ProBadge() {
  return (
    <Badge size="sm" variant="outline" radius="sm" tt="none" data-testid="pro-label">
      Pro
    </Badge>
  );
}

function PartHeading({
  id,
  children,
  pro = false,
}: {
  id: string;
  children: ReactNode;
  pro?: boolean;
}) {
  return (
    <Group gap="xs">
      <Title order={5} size="sm" id={id}>
        {children}
      </Title>
      {pro && <ProBadge />}
    </Group>
  );
}

/** What putting a Not imported source on the ignore list said, by result (#153). */
const IGNORE_MESSAGES = {
  added: 'Added to the ignore list. It stays a source of this Version.',
  'already-listed': 'It is on the ignore list already.',
  imported: 'It is imported now: reload the page to see it as a Generation.',
  deleted: 'It was deleted in n8Tracks, so it is not put on the ignore list.',
  failed: 'It could not be added to the ignore list. Try again.',
} as const;

/**
 * One source: its title (a link to its Generation when it is one), its shortcode when it is in
 * n8Tracks, and a label when it cannot be used as it is. A Not imported source (#153) can have its
 * Suno address copied or be put on the ignore list, on a frozen Version too: the source stays, as long
 * as the Version does.
 */
function SourceLine({ source }: { source: LineageSource }) {
  const shortcode = sourceShortcode(source);
  const label = availabilityLabel(source);
  const title = sourceTitle(source);
  const [message, setMessage] = useState<string>();
  const [busy, setBusy] = useState(false);
  const generation = source.generation;
  const external = source.external;
  const notImported =
    external !== undefined && (source.availability ?? 'not_imported') === 'not_imported';
  const address = external?.address ?? null;

  const copyAddress = async (text: string) => {
    try {
      // The Clipboard API is missing outside a secure context; the cast lets the check say so.
      const clipboard = navigator.clipboard as Clipboard | undefined;
      if (clipboard === undefined) {
        throw new Error('No clipboard.');
      }
      await clipboard.writeText(text);
      setMessage('Suno address copied.');
    } catch {
      setMessage(`Copying is not available here. The Suno address is ${text}`);
    }
  };

  const ignore = async (sunoId: string) => {
    setBusy(true);
    const result = await ignoreSource(sunoId);
    setBusy(false);
    setMessage(IGNORE_MESSAGES[result.kind]);
  };

  return (
    <Stack gap={4}>
      <Group gap="xs" wrap="wrap">
        {generation?.shortcode && generation.songShortcode ? (
          <Anchor
            component={Link}
            size="sm"
            fw={500}
            underline="always"
            to={`/songs/${generation.songShortcode}/generations/${generation.shortcode}`}
            data-testid="source-title"
          >
            {title}
          </Anchor>
        ) : (
          <Text size="sm" fw={500} data-testid="source-title">
            {title}
          </Text>
        )}
        {shortcode !== null && (
          <Text size="sm" ff="monospace" data-testid="source-shortcode">
            {shortcode}
          </Text>
        )}
        {label !== null && (
          <Badge
            size="sm"
            variant="default"
            radius="sm"
            tt="none"
            data-testid="source-availability"
          >
            {label}
          </Badge>
        )}
      </Group>
      {notImported && (
        <Group gap="xs">
          {address !== null && (
            <Button
              variant="default"
              size="compact-sm"
              aria-label={`Copy Suno address of ${title}`}
              onClick={() => {
                void copyAddress(address);
              }}
            >
              Copy Suno address
            </Button>
          )}
          <Button
            variant="default"
            size="compact-sm"
            disabled={busy}
            aria-label={`Add to the ignore list: ${title}`}
            onClick={() => {
              void ignore(external.sunoId);
            }}
          >
            Add to the ignore list
          </Button>
        </Group>
      )}
      {notImported && (
        <Text size="xs" role="status" data-testid="source-message">
          {message}
        </Text>
      )}
    </Stack>
  );
}

/**
 * Extend's position, as minutes and seconds within the source's length when it is known. A value
 * is saved once it is valid; until then the field says what is wrong.
 */
function ContinueAt({
  seconds,
  duration,
  readOnly,
  onChange,
}: {
  seconds: number | null;
  duration: number | null;
  readOnly: boolean;
  onChange: (seconds: number) => void;
}) {
  const shown = seconds === null ? undefined : splitSeconds(seconds);
  const [minutesDraft, setMinutes] = useState<number | string>(shown?.minutes ?? '');
  const [secondsDraft, setSeconds] = useState<number | string>(shown?.seconds ?? '');
  const [touched, setTouched] = useState(false);
  const result = continueAtOf(minutesDraft, secondsDraft, duration);
  const error = touched && 'error' in result ? result.error : undefined;
  if (readOnly) {
    return (
      <Text size="sm" data-testid="continue-at">
        {seconds === null ? 'Continues from: not set' : `Continues from ${formatPosition(seconds)}`}
      </Text>
    );
  }
  const update = (minutes: number | string, secondsPart: number | string) => {
    setTouched(true);
    const next = continueAtOf(minutes, secondsPart, duration);
    if ('seconds' in next && next.seconds !== seconds) {
      onChange(next.seconds);
    }
  };
  return (
    <Stack gap={4} component="fieldset" style={{ border: 0, padding: 0, margin: 0 }}>
      <Text component="legend" size="sm" fw={500}>
        Continue from
      </Text>
      <Group gap="xs" align="flex-start">
        <NumberInput
          label="Minutes"
          min={0}
          allowDecimal={false}
          allowNegative={false}
          w={110}
          value={minutesDraft}
          onChange={(value) => {
            setMinutes(value);
            update(value, secondsDraft);
          }}
        />
        <NumberInput
          label="Seconds"
          min={0}
          max={59.99}
          decimalScale={2}
          allowNegative={false}
          w={110}
          value={secondsDraft}
          onChange={(value) => {
            setSeconds(value);
            update(minutesDraft, value);
          }}
        />
      </Group>
      <Text size="xs" c="var(--n8-color-secondary-text)">
        {duration === null
          ? 'The source’s length is not known: any position from 0:00 is accepted.'
          : `Within the source’s ${formatPosition(duration)}.`}
      </Text>
      {error !== undefined && (
        <Text size="sm" role="alert" c="var(--n8-notice-text)" data-testid="continue-at-error">
          {error}
        </Text>
      )}
    </Stack>
  );
}

/** Asks before a change that removes something besides what was asked. */
function ConfirmChange({
  pending,
  onConfirm,
  onCancel,
}: {
  pending: { what: string; change: LineageChange } | undefined;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  return (
    <Modal
      opened={pending !== undefined}
      onClose={onCancel}
      title="Change the sources?"
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      {pending !== undefined && (
        <Stack gap="sm">
          <Text data-testid="sources-confirmation">
            {pending.what} This also removes {pending.change.removes.join(' and ')}.
          </Text>
          <Group justify="flex-end" gap="sm">
            <Button variant="default" onClick={onCancel}>
              Keep as it is
            </Button>
            <Button onClick={onConfirm}>Change</Button>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}

/** Adds or edits a note of a file to attach by hand in Suno: one per kind, with a description. */
function FileNoteDialog({
  editing,
  lineage,
  audioAction,
  onSave,
  onClose,
}: {
  editing: FileKind | undefined;
  lineage: Lineage;
  /** The audio action chosen, by name; an audio file replaces it. */
  audioAction: string | undefined;
  onSave: (kind: FileKind, description: string) => void;
  onClose: () => void;
}) {
  return (
    <Modal
      opened={editing !== undefined}
      onClose={onClose}
      title={editing === undefined ? '' : `${FILE_LABELS[editing]} note`}
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      {editing !== undefined && (
        <FileNoteForm
          key={editing}
          kind={editing}
          lineage={lineage}
          audioAction={audioAction}
          onSave={(description) => {
            onSave(editing, description);
          }}
          onClose={onClose}
        />
      )}
    </Modal>
  );
}

function FileNoteForm({
  kind,
  lineage,
  audioAction,
  onSave,
  onClose,
}: {
  kind: FileKind;
  lineage: Lineage;
  audioAction: string | undefined;
  onSave: (description: string) => void;
  onClose: () => void;
}) {
  const [description, setDescription] = useState(fileNote(lineage, kind)?.description ?? '');
  const [tried, setTried] = useState(false);
  const error = fileNoteError(description, FILE_DESCRIPTION_MAXIMUM_LENGTH);
  const replaces = kind === 'audio' && audioAction !== undefined;
  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        setTried(true);
        if (error === undefined) {
          onSave(description.trim());
        }
      }}
    >
      <Stack gap="sm">
        <Textarea
          label="Description"
          description={`What to attach, in up to ${FILE_DESCRIPTION_MAXIMUM_LENGTH.toLocaleString('en-US')} characters. n8Tracks keeps only this note: you attach the file by hand in Suno.`}
          rows={3}
          resize="vertical"
          value={description}
          data-autofocus
          error={tried ? error : undefined}
          onChange={(event) => {
            setDescription(event.currentTarget.value);
          }}
        />
        {replaces && (
          <Text data-testid="audio-note-replaces">
            Suno’s form has one Audio slot: adding an audio file replaces this Version’s audio
            action ({audioAction}) and its sources.
          </Text>
        )}
        <Group justify="flex-end" gap="sm">
          <Button variant="default" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit">{replaces ? 'Replace the audio action' : 'Save note'}</Button>
        </Group>
      </Stack>
    </form>
  );
}

/**
 * A Version's sources (#125), below its options, for a Song Version: its audio action (Cover,
 * Extend, Mashup, Sample This Song, or Reuse Prompt) with its source or, for Mashup, two, and for
 * Extend the position to continue from; Inspiration as up to four songs in order or one Suno
 * playlist (hidden while Cover is chosen); a Voice; and notes of files to attach by hand in Suno.
 * Each change goes to `onChange` and is saved with the rest of the editor; a change that also
 * removes something is confirmed first. On a frozen Version (`readOnly`) everything is shown and
 * nothing can change: Create New Version From carries it into a new Version.
 */
export function SourcesSection({
  lineage,
  songMode,
  versionId,
  readOnly,
  onChange,
}: {
  lineage: Lineage;
  songMode: 'simple' | 'advanced';
  /** The Version itself, whose own Generations are never offered. */
  versionId: string;
  readOnly: boolean;
  onChange: (lineage: Lineage) => void;
}) {
  const { state: typesState, reload: reloadTypes } = useRelationshipTypes();
  const types = typesState.phase === 'ready' ? audioTypes(typesState.data) : [];
  const [chosenTypeId, setChosenTypeId] = useState<string | undefined>();
  const [picking, setPicking] = useState<PickTarget | undefined>();
  const [pending, setPending] = useState<
    { what: string; change: LineageChange; typeId: string | undefined } | undefined
  >();
  const [noteKind, setNoteKind] = useState<FileKind | undefined>();

  // The audio action is the sources' own; before a source is added it is the one chosen here.
  const heldTypeId = lineage.sources[0]?.typeId;
  const typeId = heldTypeId ?? chosenTypeId;
  const heldAction = audioActionOf(lineage);
  // Until the types load (or for a type no longer listed), a held source still names its action.
  const type: AudioType | undefined =
    types.find((candidate) => candidate.id === typeId) ??
    (heldTypeId !== undefined && heldAction !== undefined
      ? {
          id: heldTypeId,
          name: actionName(heldAction),
          action: heldAction,
          label: actionName(heldAction),
        }
      : undefined);
  const action = type?.action;

  const chooseType = (id: string) => {
    const next = types.find((candidate) => candidate.id === id);
    const change = chooseAudioAction(lineage, next);
    if (change.removes.length === 0) {
      setChosenTypeId(next?.id);
      onChange(change.lineage);
    } else {
      setPending({
        what:
          next === undefined
            ? 'The Version will have no audio action.'
            : `The audio action becomes ${next.name}.`,
        change,
        typeId: next?.id,
      });
    }
  };

  const individual = inspirationSources(lineage);
  const playlist = lineage.inspiration?.playlist ?? null;
  const advancedOnlyNote =
    songMode === 'simple' && individual.length > 0
      ? 'Suno takes individual Inspiration songs in Advanced mode; in Simple mode only a playlist is sent. They are kept.'
      : undefined;

  const pickedGroup =
    picking?.group === 'inspiration'
      ? individual
      : picking?.group === 'audio'
        ? lineage.sources
        : [];

  return (
    <Stack
      gap="md"
      component="section"
      aria-labelledby="sources-heading"
      data-testid="sources-section"
    >
      <Title order={4} size="h6" id="sources-heading">
        Sources
      </Title>
      {readOnly && (
        <Text size="sm" data-testid="sources-frozen">
          The sources are frozen with this Version. Create New Version From carries them into a new
          Version, where they can be changed.
        </Text>
      )}

      {/* The audio action and its sources. */}
      <Stack gap="xs" role="group" aria-labelledby="audio-action-heading">
        <PartHeading id="audio-action-heading">Audio action</PartHeading>
        {typesState.phase === 'error' && (
          <Group gap="sm">
            <Text role="alert">The source types could not be loaded.</Text>
            <Button variant="default" size="compact-sm" onClick={reloadTypes}>
              Try again
            </Button>
          </Group>
        )}
        {readOnly ? (
          <Text size="sm" data-testid="audio-action">
            {type === undefined ? 'None' : type.label}
          </Text>
        ) : (
          <NativeSelect
            label="Action"
            data-testid="audio-action-select"
            value={type?.id ?? ''}
            disabled={typesState.phase !== 'ready'}
            data={[
              { value: '', label: 'None' },
              ...types.map((candidate) => ({ value: candidate.id, label: candidate.label })),
            ]}
            onChange={(event) => {
              chooseType(event.currentTarget.value);
            }}
          />
        )}
        {lineage.sources.length > 0 && (
          <List listStyleType="none" spacing="xs" aria-label="Audio sources" withPadding={false}>
            {lineage.sources.map((source, index) => (
              <List.Item key={`${String(index)}-${sourceTitle(source)}`} data-source-index={index}>
                <Stack gap={4}>
                  <SourceLine source={source} />
                  {!readOnly && type !== undefined && (
                    <Group gap="xs">
                      <Button
                        variant="default"
                        size="compact-sm"
                        aria-label={`Replace ${sourceTitle(source)}`}
                        onClick={() => {
                          setPicking({ group: 'audio', index });
                        }}
                      >
                        Replace
                      </Button>
                      <Button
                        variant="default"
                        size="compact-sm"
                        aria-label={`Remove ${sourceTitle(source)}`}
                        onClick={() => {
                          onChange(removeAudioSource(lineage, index));
                        }}
                      >
                        Remove
                      </Button>
                    </Group>
                  )}
                  {action === 'extend' && (
                    <ContinueAt
                      key={sourceTitle(source)}
                      seconds={source.continueAtSeconds ?? null}
                      duration={sourceDuration(source)}
                      readOnly={readOnly}
                      onChange={(seconds) => {
                        onChange(setContinueAt(lineage, index, seconds));
                      }}
                    />
                  )}
                </Stack>
              </List.Item>
            ))}
          </List>
        )}
        {!readOnly && type !== undefined && lineage.sources.length < sourcesFor(type.action) && (
          <Group gap="sm">
            <Button
              variant="default"
              onClick={() => {
                setPicking({ group: 'audio', index: lineage.sources.length });
              }}
            >
              {lineage.sources.length === 0
                ? `Choose the ${type.name} source`
                : 'Add the second source'}
            </Button>
            {action === 'mashup' && lineage.sources.length === 1 && (
              <Text size="sm" data-testid="mashup-needs-second">
                A Mashup needs a second source.
              </Text>
            )}
          </Group>
        )}
      </Stack>

      {/* Inspiration: individual songs or one playlist, never both, and never with Cover. */}
      {action !== 'cover' && (
        <Stack gap="xs" role="group" aria-labelledby="inspiration-heading">
          <PartHeading id="inspiration-heading" pro>
            Inspiration
          </PartHeading>
          {playlist !== null ? (
            <Group gap="xs" data-testid="inspiration-playlist">
              <Text size="sm" fw={500}>
                Playlist: {playlist.name === '' ? playlist.sunoPlaylistId : playlist.name}
              </Text>
              <Text size="sm">
                {playlist.clipIds.length === 1
                  ? '1 clip'
                  : `${String(playlist.clipIds.length)} clips`}
              </Text>
              {!readOnly && (
                <Button
                  variant="default"
                  size="compact-sm"
                  onClick={() => {
                    onChange(choosePlaylist(lineage, null));
                  }}
                >
                  Remove the playlist
                </Button>
              )}
            </Group>
          ) : (
            individual.length > 0 && (
              <List
                type="ordered"
                spacing="xs"
                aria-label="Inspiration songs"
                withPadding
                data-testid="inspiration-sources"
              >
                {individual.map((source, index) => (
                  <List.Item
                    key={`${String(index)}-${sourceTitle(source)}`}
                    data-inspiration-index={index}
                  >
                    <Stack gap={4}>
                      <SourceLine source={source} />
                      {!readOnly && (
                        <Group gap="xs">
                          <Button
                            variant="default"
                            size="compact-sm"
                            disabled={index === 0}
                            aria-label={`Move ${sourceTitle(source)} up`}
                            onClick={() => {
                              onChange(moveInspirationSource(lineage, index, -1));
                            }}
                          >
                            Move up
                          </Button>
                          <Button
                            variant="default"
                            size="compact-sm"
                            disabled={index === individual.length - 1}
                            aria-label={`Move ${sourceTitle(source)} down`}
                            onClick={() => {
                              onChange(moveInspirationSource(lineage, index, 1));
                            }}
                          >
                            Move down
                          </Button>
                          <Button
                            variant="default"
                            size="compact-sm"
                            aria-label={`Replace ${sourceTitle(source)}`}
                            onClick={() => {
                              setPicking({ group: 'inspiration', index });
                            }}
                          >
                            Replace
                          </Button>
                          <Button
                            variant="default"
                            size="compact-sm"
                            aria-label={`Remove ${sourceTitle(source)}`}
                            onClick={() => {
                              onChange(removeInspirationSource(lineage, index));
                            }}
                          >
                            Remove
                          </Button>
                        </Group>
                      )}
                    </Stack>
                  </List.Item>
                ))}
              </List>
            )
          )}
          {advancedOnlyNote !== undefined && <Text size="sm">{advancedOnlyNote}</Text>}
          {!readOnly && playlist === null && (
            <InspirationChoices
              lineage={lineage}
              onAddSong={() => {
                setPicking({ group: 'inspiration', index: individual.length });
              }}
              onPlaylist={(chosen) => {
                onChange(choosePlaylist(lineage, chosen));
              }}
            />
          )}
          {readOnly && playlist === null && individual.length === 0 && <Text size="sm">None</Text>}
        </Stack>
      )}

      <VoicePart lineage={lineage} readOnly={readOnly} onChange={onChange} />

      {/* Files Suno takes that n8Tracks only notes. */}
      <Stack gap="xs" role="group" aria-labelledby="file-notes-heading">
        <PartHeading id="file-notes-heading">Files to attach in Suno</PartHeading>
        <Text size="sm">
          n8Tracks keeps only a note of each file: you attach it by hand in Suno.
        </Text>
        {lineage.fileInputs
          .filter((file) => file.kind === 'audio' || songMode === 'simple')
          .map((file) => (
            <Group gap="xs" key={file.kind} data-file-note={file.kind} wrap="wrap">
              <Text size="sm" fw={500}>
                {FILE_LABELS[file.kind]}:
              </Text>
              <Text size="sm" data-testid="file-note-description">
                {file.description}
              </Text>
              {!readOnly && (
                <>
                  <Button
                    variant="default"
                    size="compact-sm"
                    aria-label={`Edit the ${FILE_LABELS[file.kind].toLowerCase()} note`}
                    onClick={() => {
                      setNoteKind(file.kind);
                    }}
                  >
                    Edit
                  </Button>
                  <Button
                    variant="default"
                    size="compact-sm"
                    aria-label={`Remove the ${FILE_LABELS[file.kind].toLowerCase()} note`}
                    onClick={() => {
                      onChange(removeFileNote(lineage, file.kind));
                    }}
                  >
                    Remove
                  </Button>
                </>
              )}
            </Group>
          ))}
        {!readOnly && (
          <Group gap="sm">
            {(songMode === 'simple' ? (['audio', 'image', 'video'] as const) : (['audio'] as const))
              .filter((kind) => fileNote(lineage, kind) === undefined)
              .map((kind) => (
                <Button
                  key={kind}
                  variant="default"
                  onClick={() => {
                    setNoteKind(kind);
                  }}
                >
                  {ADD_NOTE_LABELS[kind]}
                </Button>
              ))}
          </Group>
        )}
        {readOnly && lineage.fileInputs.length === 0 && <Text size="sm">None</Text>}
      </Stack>

      <SourcePicker
        target={picking}
        versionId={versionId}
        chosen={chosenGenerationIds(pickedGroup).filter((_, at) => at !== picking?.index)}
        title={
          picking?.group === 'inspiration'
            ? 'Choose an Inspiration song'
            : type === undefined
              ? 'Choose a source'
              : `Choose the ${type.name} source`
        }
        onClose={() => {
          setPicking(undefined);
        }}
        onPick={(source) => {
          if (picking?.group === 'inspiration') {
            onChange(placeInspirationSource(lineage, picking.index, source));
          } else if (picking !== undefined && type !== undefined) {
            onChange(placeAudioSource(lineage, type, picking.index, source));
          }
          setPicking(undefined);
        }}
      />
      <ConfirmChange
        pending={pending}
        onCancel={() => {
          setPending(undefined);
        }}
        onConfirm={() => {
          if (pending !== undefined) {
            setChosenTypeId(pending.typeId);
            onChange(pending.change.lineage);
          }
          setPending(undefined);
        }}
      />
      <FileNoteDialog
        editing={noteKind}
        lineage={lineage}
        audioAction={type?.name}
        onClose={() => {
          setNoteKind(undefined);
        }}
        onSave={(kind, description) => {
          const change = saveFileNote(lineage, { kind, description });
          if (kind === 'audio') {
            setChosenTypeId(undefined);
          }
          // The dialog itself said what an audio note replaces, so saving it is the confirmation.
          onChange(change.lineage);
          setNoteKind(undefined);
        }}
      />
    </Stack>
  );
}

/** The ways to add Inspiration while none of a playlist is chosen: a song (up to four), or one playlist. */
function InspirationChoices({
  lineage,
  onAddSong,
  onPlaylist,
}: {
  lineage: Lineage;
  onAddSong: () => void;
  onPlaylist: (playlist: { sunoPlaylistId: string; name: string; clipIds: string[] }) => void;
}) {
  const { state, reload } = useSunoPlaylists();
  const songs = inspirationSources(lineage);
  return (
    <Stack gap="xs">
      {canAddInspiration(lineage) ? (
        <Group>
          <Button variant="default" onClick={onAddSong}>
            Add an Inspiration song
          </Button>
        </Group>
      ) : (
        <Text size="sm" data-testid="inspiration-full">
          Inspiration holds up to {INSPIRATION_MAXIMUM} songs.
        </Text>
      )}
      {songs.length === 0 &&
        (state.phase === 'loading' ? (
          <Group gap="xs">
            <Loader size="xs" aria-hidden="true" />
            <Text size="sm">Loading the Suno playlists…</Text>
          </Group>
        ) : state.phase !== 'ready' ? (
          <Group gap="sm">
            <Text role="alert">The Suno playlists could not be loaded.</Text>
            <Button variant="default" size="compact-sm" onClick={reload}>
              Try again
            </Button>
          </Group>
        ) : state.data.length === 0 ? (
          <Text size="sm" data-testid="no-playlists">
            Or use a Suno playlist: none yet. n8Tracks lists the playlists it has seen once an
            import has run.
          </Text>
        ) : (
          <NativeSelect
            label="Or use a Suno playlist"
            value=""
            data={[
              { value: '', label: 'Choose a playlist' },
              ...state.data.map((candidate) => ({
                value: candidate.id,
                label: `${candidate.name === '' ? candidate.id : candidate.name} (${String(candidate.memberCount)})`,
              })),
            ]}
            onChange={(event) => {
              const chosen = state.data.find(
                (candidate) => candidate.id === event.currentTarget.value,
              );
              if (chosen !== undefined) {
                onPlaylist({
                  sunoPlaylistId: chosen.id,
                  name: chosen.name,
                  clipIds: chosen.clipIds,
                });
              }
            }}
          />
        ))}
      {songs.length > 0 && (
        <Text size="sm">
          Inspiration is either songs or one playlist: remove the songs to use a playlist.
        </Text>
      )}
    </Stack>
  );
}

/** The Voice: one of the personas n8Tracks has seen in imported clips, or none. */
function VoicePart({
  lineage,
  readOnly,
  onChange,
}: {
  lineage: Lineage;
  readOnly: boolean;
  onChange: (lineage: Lineage) => void;
}) {
  const { state, reload } = useSunoPersonas();
  const voice = lineage.voice;
  const personas = state.phase === 'ready' ? state.data : [];
  const known = voice === null || personas.some((persona) => persona.id === voice.personaId);
  const nameOf = (id: string, name: string) => (name === '' ? id : name);
  return (
    <Stack gap="xs" role="group" aria-labelledby="voice-heading">
      <PartHeading id="voice-heading" pro>
        Voice
      </PartHeading>
      {readOnly ? (
        <Text size="sm" data-testid="voice">
          {voice === null ? 'None' : nameOf(voice.personaId, voice.name)}
        </Text>
      ) : state.phase === 'error' || state.phase === 'not-found' ? (
        <Group gap="sm">
          <Text role="alert">The Suno voices could not be loaded.</Text>
          <Button variant="default" size="compact-sm" onClick={reload}>
            Try again
          </Button>
        </Group>
      ) : (
        <>
          <NativeSelect
            label="Voice"
            value={voice?.personaId ?? ''}
            disabled={state.phase === 'loading'}
            data={[
              { value: '', label: 'None' },
              ...(known
                ? []
                : [{ value: voice.personaId, label: nameOf(voice.personaId, voice.name) }]),
              ...personas.map((persona) => ({
                value: persona.id,
                label: nameOf(persona.id, persona.name),
              })),
            ]}
            onChange={(event) => {
              const id = event.currentTarget.value;
              const persona = personas.find((candidate) => candidate.id === id);
              onChange(
                chooseVoice(
                  lineage,
                  id === ''
                    ? null
                    : persona !== undefined
                      ? { personaId: persona.id, name: persona.name }
                      : voice,
                ),
              );
            }}
          />
          {state.phase === 'ready' && personas.length === 0 && (
            <Text size="sm" data-testid="no-personas">
              No Suno voices yet: n8Tracks lists the voices it has seen in imported clips once an
              import has run.
            </Text>
          )}
        </>
      )}
    </Stack>
  );
}
