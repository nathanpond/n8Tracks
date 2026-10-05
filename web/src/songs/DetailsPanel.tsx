import {
  Button,
  CloseButton,
  Drawer,
  Group,
  Paper,
  Stack,
  Text,
  Textarea,
  Title,
} from '@mantine/core';
import { useState, type KeyboardEvent, type ReactNode } from 'react';
import { createGenre, genreNameError, useGenres, type Genre } from '../api/genres';
import type { FieldValue } from '../api/saves';
import { readSong, SONG_NOTES_MAXIMUM_LENGTH, type Song, type SongGenre } from '../api/songs';
import { TokenPicker } from '../common/TokenPicker';
import {
  focusOnMount,
  SAVE_FAILED_MESSAGE,
  saveError,
  useInPlaceEdit,
} from '../common/useInPlaceEdit';
import type { SaveOutcome } from '../common/useRevisionedSave';
import { DETAILS_PANEL_ID, DETAILS_PANEL_WIDTH } from './detailsPanelState';
import { alphabetical, GENRES_KEY, genresValue } from './genreField';
import { normaliseNotes, songNotesError } from './songRules';

type SaveFields = (edit: Readonly<Record<string, FieldValue>>) => Promise<SaveOutcome>;

/**
 * The Song's Genres: chosen from the user's list with the {@link TokenPicker}, or created on the
 * spot. Each change saves the Song's whole new set of Genres under its revision; a Genre that no
 * longer exists is refused, and the Song's Genres and the list are read again.
 */
function GenresSection({
  song,
  saveFields,
  onSong,
}: {
  song: Song;
  saveFields: SaveFields;
  onSong: (song: Song) => void;
}) {
  const { state, reload } = useGenres();
  const [created, setCreated] = useState<Genre[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const listed = state.phase === 'ready' ? state.data : undefined;
  const options =
    listed === undefined
      ? undefined
      : [...listed, ...created.filter((genre) => !listed.some((known) => known.id === genre.id))];

  const change = async (next: SongGenre[]) => {
    setBusy(true);
    setError(undefined);
    const outcome = await saveFields({ [GENRES_KEY]: genresValue(next) });
    const refused = outcome.kind === 'invalid' ? outcome.errors[GENRES_KEY] : undefined;
    if (refused !== undefined) {
      const current = await readSong(song.id);
      if (current !== undefined) {
        onSong(current);
      }
      setCreated([]);
      reload();
      setError(`${refused.join(' ')} The Genres have been read again.`);
    } else {
      setError(saveError(outcome, GENRES_KEY));
    }
    setBusy(false);
  };

  const create = async (name: string) => {
    setBusy(true);
    setError(undefined);
    const result = await createGenre(name);
    if (result.kind !== 'created') {
      setBusy(false);
      setError(
        result.kind === 'invalid'
          ? (result.errors.name?.join(' ') ?? SAVE_FAILED_MESSAGE)
          : SAVE_FAILED_MESSAGE,
      );
      return;
    }
    const genre = result.genre;
    setCreated((previous) => [...previous, genre]);
    if (song.genres.some((chosen) => chosen.id === genre.id)) {
      setBusy(false);
      return;
    }
    await change([...song.genres, { id: genre.id, name: genre.name }]);
  };

  return (
    <Stack gap={4}>
      <TokenPicker
        label="Genres"
        noun="Genre"
        chosen={alphabetical(song.genres)}
        options={options}
        busy={busy}
        error={error}
        nameError={genreNameError}
        onAdd={(genre) => {
          void change([...song.genres, { id: genre.id, name: genre.name }]);
        }}
        onCreate={(name) => {
          void create(name);
        }}
        onRemove={(genre) => {
          void change(song.genres.filter((chosen) => chosen.id !== genre.id));
        }}
      />
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Group gap="xs">
          <Text size="sm" c="var(--mantine-color-error)">
            The Genre list could not be loaded.
          </Text>
          <Button variant="default" size="compact-xs" onClick={reload}>
            Try again
          </Button>
        </Group>
      )}
    </Stack>
  );
}

/** The Song's notes: free-form plain text, edited in place. Blur or Ctrl/Cmd+Enter saves; Escape cancels. */
function NotesSection({
  song,
  save,
}: {
  song: Song;
  save: (key: string, value: FieldValue) => Promise<SaveOutcome>;
}) {
  const edit = useInPlaceEdit({
    field: 'notes',
    current: song.notes,
    normalise: normaliseNotes,
    check: songNotesError,
    save,
  });

  const keys = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      void edit.commit();
    } else if (event.key === 'Escape') {
      // Escape cancels the edit; it does not also close an overlaid panel.
      event.preventDefault();
      event.stopPropagation();
      edit.cancel();
    }
  };

  return (
    <Stack gap={4}>
      <Group gap="sm" align="center">
        <Text fw={500} size="sm" id="song-notes">
          Notes
        </Text>
        {!edit.editing && (
          <Button variant="subtle" size="compact-sm" onClick={edit.start} aria-label="Edit notes">
            Edit
          </Button>
        )}
      </Group>
      {edit.editing ? (
        <Textarea
          aria-labelledby="song-notes"
          description={`Up to ${SONG_NOTES_MAXIMUM_LENGTH.toLocaleString('en-US')} characters. Ctrl+Enter or Cmd+Enter saves, Escape cancels; leave it empty to clear it.`}
          rows={8}
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
      ) : song.notes === null ? (
        <Text size="sm" c="var(--n8-color-secondary-text)">
          No notes yet.
        </Text>
      ) : (
        <Text size="sm" style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>
          {song.notes}
        </Text>
      )}
    </Stack>
  );
}

/** What the Details panel holds for a Song: its Genres and its notes. Later stories add sections. */
export function SongDetails({
  song,
  saveFields,
  onSong,
}: {
  song: Song;
  saveFields: SaveFields;
  onSong: (song: Song) => void;
}) {
  return (
    <Stack gap="lg">
      <GenresSection song={song} saveFields={saveFields} onSong={onSong} />
      <NotesSection song={song} save={(key, value) => saveFields({ [key]: value })} />
    </Stack>
  );
}

/**
 * The Song page's Details panel. Beside the editor (`narrow` false) it is a 340-pixel column on
 * the right with its own close control; on a narrow window it overlays the editor as a drawer,
 * which traps focus, closes with Escape or its close control, and gives focus back to the control
 * that opened it. Nothing is drawn while it is closed beside the editor.
 */
export function DetailsPanel({
  open,
  narrow,
  onClose,
  children,
}: {
  open: boolean;
  narrow: boolean;
  onClose: () => void;
  children: ReactNode;
}) {
  if (narrow) {
    return (
      <Drawer
        opened={open}
        onClose={onClose}
        position="right"
        size={DETAILS_PANEL_WIDTH}
        title="Details"
        id={DETAILS_PANEL_ID}
        closeButtonProps={{ 'aria-label': 'Close details' }}
      >
        {children}
      </Drawer>
    );
  }

  if (!open) {
    return null;
  }

  return (
    <Paper
      component="aside"
      id={DETAILS_PANEL_ID}
      aria-labelledby="song-details-heading"
      withBorder
      p="md"
      style={{ flex: `0 0 ${String(DETAILS_PANEL_WIDTH)}px`, width: DETAILS_PANEL_WIDTH }}
    >
      <Stack gap="md">
        <Group justify="space-between" align="center" wrap="nowrap">
          <Title order={3} size="h4" id="song-details-heading">
            Details
          </Title>
          <CloseButton aria-label="Close details" onClick={onClose} />
        </Group>
        {children}
      </Stack>
    </Paper>
  );
}
