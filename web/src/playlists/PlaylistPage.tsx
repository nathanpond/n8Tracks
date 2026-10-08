import { Anchor, Badge, Button, Group, Loader, Paper, Stack, Text, Title } from '@mantine/core';
import { useCallback, useEffect, useRef, useState, type DragEvent } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router';
import {
  addPlaylistSong,
  deletePlaylist,
  normalisePlaylistTitle,
  PLAYLIST_DESCRIPTION_MAXIMUM_LENGTH,
  PLAYLIST_MAXIMUM_SONGS,
  PLAYLIST_TITLE_MAXIMUM_LENGTH,
  playlistTitleError,
  removePlaylistSong,
  reorderPlaylistSongs,
  updatePlaylist,
  usePlaylist,
  type Playlist,
  type PlaylistEdit,
  type PlaylistSong,
  type PlaylistSongsResult,
} from '../api/playlists';
import type { FieldValue, SaveResult } from '../api/saves';
import { descriptionError, normaliseAlbumText } from '../albums/albumRules';
import { ArtworkPicker } from '../common/ArtworkPicker';
import { artworkFields, artworkPatchOf, artworkValues } from '../common/artworkField';
import type { DeletedCollectionState } from '../common/collectionDeletion';
import { DeleteCollectionDialog } from '../common/DeleteCollection';
import { move as moved } from '../common/listMove';
import { SavedTextField, type Save } from '../common/SavedTextField';
import { SongSearch } from '../common/SongSearch';
import { useRevisionedSave, type SavedField } from '../common/useRevisionedSave';
import { Notice } from '../components/Notice';
import { StateBadge } from '../songs/SongParts';
import { SongPlayButton } from '../player/SongPlayButton';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

const CONFLICT_MESSAGE =
  'This Playlist was changed elsewhere, so your change was not applied. It now shows the Playlist as it is.';

function isFromPlaylists(value: unknown): value is { playlistsSearch: string } {
  return (
    typeof value === 'object' &&
    value !== null &&
    'playlistsSearch' in value &&
    typeof value.playlistsSearch === 'string'
  );
}

/** The Playlists list's page this one was opened from, when it was opened from one. */
function usePlaylistsSearch(): string {
  const location: { state: unknown } = useLocation();
  return isFromPlaylists(location.state) ? location.state.playlistsSearch : '';
}

/** Back to the Playlists list, on the page this one was opened from when it was opened from one. */
function BackToPlaylists() {
  const search = usePlaylistsSearch();
  return (
    <Anchor component={Link} to={`/playlists${search}`} size="sm">
      ← Playlists
    </Anchor>
  );
}

const none = (value: FieldValue) => value ?? 'None';

const FIELDS: readonly SavedField<Playlist>[] = [
  { key: 'title', label: 'Title', read: (playlist) => playlist.title, show: (value) => value },
  {
    key: 'description',
    label: 'Description',
    read: (playlist) => playlist.description,
    show: none,
  },
  ...artworkFields<Playlist>(),
];

/** The PATCH body for what the save helper holds. */
function playlistEditOf(edit: Readonly<Record<string, FieldValue>>): PlaylistEdit {
  const result: PlaylistEdit = artworkPatchOf(edit);
  for (const [key, value] of Object.entries(edit)) {
    if (key === 'title') {
      result.title = value ?? '';
    } else if (key === 'description') {
      result.description = value;
    }
  }
  return result;
}

/** What the Songs section says after a change: news, or a problem shown as a notice. */
interface Message {
  text: string;
  tone: 'info' | 'problem';
}

type RowAction = 'up' | 'down';

/** One Song on the Playlist: its place, shortcode, title, primary Artist, state, and what can be done to it. */
function SongRow({
  song,
  index,
  count,
  busy,
  dragOver,
  actions,
}: {
  song: PlaylistSong;
  index: number;
  count: number;
  busy: boolean;
  dragOver: boolean;
  actions: {
    move: (from: number, to: number, action?: RowAction) => void;
    remove: () => void;
    dragStart: (index: number) => void;
    dragEnter: (index: number) => void;
    drop: (index: number) => void;
    dragEnd: () => void;
  };
}) {
  const position = String(index + 1);
  return (
    // eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions -- dragging is the pointer way to reorder; each row's Move up and Move down buttons are the keyboard way (AC)
    <li
      data-song-id={song.id}
      data-song-title={song.title}
      draggable={!busy}
      onDragStart={(event: DragEvent<HTMLLIElement>) => {
        event.dataTransfer.effectAllowed = 'move';
        event.dataTransfer.setData('text/plain', song.id);
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
          cursor: busy ? undefined : 'grab',
          borderColor: dragOver ? 'var(--mantine-color-text)' : undefined,
          borderWidth: dragOver ? 2 : undefined,
        }}
      >
        <Group justify="space-between" wrap="wrap" gap="xs">
          <Group gap="sm" wrap="wrap">
            <Text
              aria-hidden="true"
              c="var(--n8-color-secondary-text)"
              style={{ userSelect: 'none' }}
            >
              ⠿
            </Text>
            <Text size="sm" fw={700} miw={28} ta="end" data-testid="playlist-position">
              {position}.
            </Text>
            <Anchor
              component={Link}
              to={`/songs/${song.shortcode}`}
              size="sm"
              ff="monospace"
              underline="always"
            >
              {song.shortcode}
            </Anchor>
            <Text size="sm" fw={500}>
              {song.title}
            </Text>
            <Text size="sm" c="var(--n8-color-secondary-text)">
              {song.primaryArtist?.name ?? 'No Artist'}
            </Text>
            <StateBadge name={song.state.name} colour={song.state.colour} />
            {!song.hasSelectedGeneration && (
              <Badge variant="default" tt="none" data-testid="no-selected-generation">
                No Selected Generation
              </Badge>
            )}
          </Group>
          <Group gap={6} wrap="wrap">
            <SongPlayButton song={song} size="xs" />
            <Button
              size="xs"
              variant="default"
              data-action="up"
              aria-label={`Move ${song.title} up`}
              disabled={busy || index === 0}
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
              aria-label={`Move ${song.title} down`}
              disabled={busy || index === count - 1}
              onClick={() => {
                actions.move(index, index + 1, 'down');
              }}
            >
              Move down
            </Button>
            <Button
              size="xs"
              variant="default"
              aria-label={`Remove ${song.title}`}
              disabled={busy}
              onClick={actions.remove}
            >
              Remove
            </Button>
          </Group>
        </Group>
      </Paper>
    </li>
  );
}

/**
 * The Playlist's Songs, in order: added with the shared Song search (at the end), reordered by
 * dragging or with Move up and Move down, and removed. Each change is saved at once under the
 * Playlist's revision; when the Playlist changed elsewhere it is shown as it is now, with a notice,
 * and the change is not applied.
 */
function PlaylistSongs({
  playlist,
  onPlaylist,
}: {
  playlist: Playlist;
  onPlaylist: (playlist: Playlist) => void;
}) {
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<Message>();
  const [dragFrom, setDragFrom] = useState<number | undefined>();
  const [dragOver, setDragOver] = useState<number | undefined>();
  const [focusAfterMove, setFocusAfterMove] = useState<{ id: string; action: RowAction }>();
  const listRef = useRef<HTMLOListElement>(null);
  const busyRef = useRef(false);
  const songs = playlist.songs;

  // After a move by keyboard, the focus stays on the button that moved the Song, or on the other
  // one when that is now disabled (the Song reached the top or the bottom).
  useEffect(() => {
    if (focusAfterMove === undefined) {
      return;
    }
    const row = listRef.current?.querySelector(`[data-song-id="${focusAfterMove.id}"]`);
    const same = row?.querySelector<HTMLButtonElement>(`[data-action="${focusAfterMove.action}"]`);
    const other = row?.querySelector<HTMLButtonElement>(
      `[data-action="${focusAfterMove.action === 'up' ? 'down' : 'up'}"]`,
    );
    (same && !same.disabled ? same : other)?.focus();
  }, [focusAfterMove, playlist]);

  /** Runs one change at a time and takes in the Playlist it answers with. */
  const run = async (
    send: () => Promise<PlaylistSongsResult>,
    done: (saved: Playlist) => string,
    refused: { duplicate?: string } = {},
  ): Promise<boolean> => {
    if (busyRef.current) {
      return false;
    }
    busyRef.current = true;
    setBusy(true);
    const result = await send();
    busyRef.current = false;
    setBusy(false);
    switch (result.kind) {
      case 'saved':
        onPlaylist(result.playlist);
        setMessage({ text: done(result.playlist), tone: 'info' });
        return true;
      case 'conflict':
        onPlaylist(result.current);
        setMessage({ text: CONFLICT_MESSAGE, tone: 'problem' });
        return false;
      case 'duplicate':
        onPlaylist(result.current);
        setMessage({
          text: refused.duplicate ?? 'That Song is on this Playlist already.',
          tone: 'problem',
        });
        return false;
      case 'full':
        onPlaylist(result.current);
        setMessage({
          text: `A Playlist holds at most ${PLAYLIST_MAXIMUM_SONGS.toLocaleString('en-US')} Songs. Remove one to add another.`,
          tone: 'problem',
        });
        return false;
      case 'not-found':
        setMessage({
          text: 'This Playlist, or that Song, is no longer there. Reload the page.',
          tone: 'problem',
        });
        return false;
      case 'failed':
        setMessage({ text: FAILED_MESSAGE, tone: 'problem' });
        return false;
    }
  };

  const move = (from: number, to: number, action?: RowAction) => {
    const song = songs[from];
    if (song === undefined || to < 0 || to >= songs.length || from === to) {
      return;
    }
    const ids = moved(songs, from, to).map((item) => item.id);
    void run(
      () => reorderPlaylistSongs(playlist, ids),
      () => `Moved ${song.title} to position ${String(to + 1)} of ${String(ids.length)}.`,
    ).then((saved) => {
      if (saved && action !== undefined) {
        setFocusAfterMove({ id: song.id, action });
      }
    });
  };

  const onPlaylistIds = songs.map((song) => song.id);
  const full = songs.length >= PLAYLIST_MAXIMUM_SONGS;

  return (
    <Stack gap="md">
      <Title order={3} size="h4" id="playlist-songs-heading">
        Songs
      </Title>
      <SongSearch
        label="Add a Song"
        description={
          full
            ? `This Playlist holds ${PLAYLIST_MAXIMUM_SONGS.toLocaleString('en-US')} Songs, the most it can.`
            : 'Search by title or shortcode. A new Song goes to the end; a Song is on a Playlist once.'
        }
        unavailable={onPlaylistIds}
        unavailableNote="on this Playlist"
        busy={busy}
        onChoose={(song) => {
          void run(
            () => addPlaylistSong(playlist, song.id),
            (saved) => `Added ${song.title} at position ${String(saved.songs.length)}.`,
            { duplicate: `${song.title} is on this Playlist already.` },
          );
        }}
      />

      <Group gap="xs" role="status" aria-live="polite" mih={28} data-testid="playlist-status">
        {message &&
          (message.tone === 'problem' ? (
            <Notice title="Not changed">
              <Text>{message.text}</Text>
            </Notice>
          ) : (
            <Text size="sm">{message.text}</Text>
          ))}
      </Group>

      {songs.length === 0 ? (
        <Text size="sm" c="var(--n8-color-secondary-text)">
          No Songs yet. Find one above to add it.
        </Text>
      ) : (
        <>
          <Text size="sm" c="var(--n8-color-secondary-text)" id="playlist-songs-help">
            Drag a Song to move it, or use Move up and Move down. Changing the Playlist never
            changes its Songs.
          </Text>
          <ol
            ref={listRef}
            aria-labelledby="playlist-songs-heading"
            aria-describedby="playlist-songs-help"
            style={{ padding: 0, margin: 0, display: 'flex', flexDirection: 'column', gap: 8 }}
          >
            {songs.map((song, index) => (
              <SongRow
                key={song.id}
                song={song}
                index={index}
                count={songs.length}
                busy={busy}
                dragOver={dragFrom !== undefined && dragOver === index && dragFrom !== index}
                actions={{
                  move,
                  remove: () => {
                    void run(
                      () => removePlaylistSong(playlist, song.id),
                      () => `Removed ${song.title}.`,
                    );
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
        </>
      )}
    </Stack>
  );
}

/** The Playlist's page once it has loaded: its artwork, title, and description, each saved on its own, and its Songs. */
function LoadedPlaylist({ initial }: { initial: Playlist }) {
  const [playlist, setPlaylist] = useState(initial);
  const [status, setStatus] = useState<'idle' | 'saving' | 'saved' | 'failed'>('idle');
  const [deleting, setDeleting] = useState(false);
  const playlistsSearch = usePlaylistsSearch();
  const navigate = useNavigate();

  const send = useCallback(
    (base: Playlist, edit: Readonly<Record<string, FieldValue>>): Promise<SaveResult<Playlist>> =>
      updatePlaylist(base, playlistEditOf(edit)),
    [],
  );
  const { save, saveFields, dialog } = useRevisionedSave({
    record: playlist,
    onRecord: setPlaylist,
    fields: FIELDS,
    send,
    subject: 'This Playlist',
  });

  const saveField: Save = useCallback(
    async (key, value) => {
      setStatus('saving');
      const outcome = await save(key, value);
      setStatus(outcome.kind === 'saved' ? 'saved' : outcome.kind === 'failed' ? 'failed' : 'idle');
      return outcome;
    },
    [save],
  );

  return (
    <Stack gap="lg">
      <BackToPlaylists />
      <Group justify="space-between" align="flex-start" wrap="nowrap" gap="sm">
        <Title order={2} style={{ overflowWrap: 'anywhere' }}>
          {playlist.title}
        </Title>
        <Button
          variant="default"
          size="compact-sm"
          onClick={() => {
            setDeleting(true);
          }}
        >
          Delete Playlist
        </Button>
      </Group>
      <div role="status" data-testid="playlist-save-status">
        {status === 'saving' && <Text size="sm">Saving…</Text>}
        {status === 'saved' && <Text size="sm">Saved.</Text>}
        {status === 'failed' && (
          <Notice title="Not saved">
            <Text>{FAILED_MESSAGE}</Text>
          </Notice>
        )}
      </div>

      <Stack gap="md" maw={720}>
        <ArtworkPicker
          title={playlist.title}
          noun="Playlist"
          artwork={playlist.artwork}
          save={(edit) => saveFields(artworkValues(edit))}
        />
        <SavedTextField
          key={`title ${playlist.title}`}
          field="title"
          label="Title"
          description={`Up to ${String(PLAYLIST_TITLE_MAXIMUM_LENGTH)} characters. Titles do not have to be unique.`}
          value={playlist.title}
          check={playlistTitleError}
          normalise={normalisePlaylistTitle}
          save={saveField}
          required
        />
        <SavedTextField
          key={`description ${playlist.description ?? ''}`}
          field="description"
          label="Description"
          description={`Plain text, up to ${PLAYLIST_DESCRIPTION_MAXIMUM_LENGTH.toLocaleString('en-US')} characters.`}
          value={playlist.description}
          check={descriptionError}
          normalise={normaliseAlbumText}
          save={saveField}
          multiline
        />
      </Stack>

      <PlaylistSongs playlist={playlist} onPlaylist={setPlaylist} />

      {dialog}
      <DeleteCollectionDialog
        noun="Playlist"
        record={playlist}
        opened={deleting}
        onClose={() => {
          setDeleting(false);
        }}
        remove={() => deletePlaylist(playlist)}
        onCurrent={setPlaylist}
        onDeleted={() => {
          const state: DeletedCollectionState = {
            deletedCollection: { noun: 'Playlist', title: playlist.title },
          };
          void navigate(`/playlists${playlistsSearch}`, { state });
        }}
      />
    </Stack>
  );
}

/**
 * A Playlist's page (`/playlists/<id>`): its own artwork (never borrowed from its Songs), title,
 * and description, each saved on its own under the Playlist's revision, and its Songs in order, added, reordered, and removed. A Song without a
 * Selected Generation stays on it, marked "No Selected Generation". "Delete Playlist" deletes it
 * after a confirmation that its Songs are not deleted; the Playlists list then opens with a notice.
 */
export function PlaylistPage() {
  const { id = '' } = useParams();
  const { state, reload } = usePlaylist(id);

  if (state.phase === 'loading') {
    return <Loader aria-label="Loading the Playlist" />;
  }
  if (state.phase === 'not-found') {
    return (
      <Stack gap="md">
        <BackToPlaylists />
        <Title order={2}>No such Playlist</Title>
        <Text>There is no Playlist at this address. It may have been typed wrongly.</Text>
      </Stack>
    );
  }
  if (state.phase === 'error') {
    return (
      <Stack gap="md">
        <BackToPlaylists />
        <Notice title="The Playlist could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <Group>
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
          </Group>
        </Notice>
      </Stack>
    );
  }
  return <LoadedPlaylist key={state.data.id} initial={state.data} />;
}
