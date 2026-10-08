import {
  Anchor,
  Badge,
  Button,
  Group,
  Menu,
  Paper,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useEffect, useRef, useState, type DragEvent } from 'react';
import { Link } from 'react-router';
import {
  addAlbumTrack,
  ALBUM_TRACK_MAXIMUM_NUMBER,
  removeAlbumTrack,
  setAlbumTracks,
  type Album,
  type AlbumTrack,
  type AlbumTrackPlace,
  type AlbumTracksResult,
} from '../api/albums';
import { SongSearch } from '../common/SongSearch';
import { Notice } from '../components/Notice';
import { StateBadge } from '../songs/SongParts';
import {
  discsOf,
  isNumberedInOrder,
  moveToDisc,
  moveWithinDisc,
  placesOf,
  renumber,
  withTrackNumber,
} from './trackOrder';
import { SongPlayButton } from '../player/SongPlayButton';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

const CONFLICT_MESSAGE =
  'This Album was changed elsewhere, so your change was not applied. It now shows the Album as it is.';

const NUMBER_ERROR = `Use a whole number from 1 to ${String(ALBUM_TRACK_MAXIMUM_NUMBER)}.`;

/** Why a track cannot be moved to the end of `disc`: it already has the last track number. */
function discFullMessage(disc: number, title: string): string {
  const number = String(disc);
  return `Disc ${number} already has track ${String(ALBUM_TRACK_MAXIMUM_NUMBER)}, so ${title} cannot go at its end. Choose another disc or a new one, or renumber disc ${number} if it has gaps.`;
}

/** What the Tracks section says after a change: news, or a problem shown as a notice. */
interface Message {
  text: string;
  tone: 'info' | 'problem';
}

type RowAction = 'up' | 'down';

/** A typed track number, as a whole number from 1 to 999, or undefined. */
function trackNumberOf(text: string): number | undefined {
  const trimmed = text.trim();
  if (!/^[0-9]+$/.test(trimmed)) {
    return undefined;
  }
  const number = Number(trimmed);
  return number >= 1 && number <= ALBUM_TRACK_MAXIMUM_NUMBER ? number : undefined;
}

/**
 * A track's number, edited in place: saved when it loses the focus or on Enter. A number another
 * track on the disc holds is refused by the API, and the field goes back to the saved number.
 */
function TrackNumberField({
  track,
  busy,
  onCommit,
}: {
  track: AlbumTrack;
  busy: boolean;
  onCommit: (track: number) => Promise<boolean>;
}) {
  const [draft, setDraft] = useState(String(track.track));
  const [error, setError] = useState<string>();

  const commit = async () => {
    const number = trackNumberOf(draft);
    if (number === undefined) {
      setError(NUMBER_ERROR);
      return;
    }
    setError(undefined);
    if (number === track.track) {
      setDraft(String(number));
      return;
    }
    if (!(await onCommit(number))) {
      setDraft(String(track.track));
    }
  };

  return (
    <TextInput
      aria-label={`Track number of ${track.title}`}
      inputMode="numeric"
      size="xs"
      w={64}
      value={draft}
      readOnly={busy}
      error={error}
      aria-invalid={error !== undefined}
      onChange={(event) => {
        setDraft(event.currentTarget.value);
      }}
      onBlur={() => {
        void commit();
      }}
      onKeyDown={(event) => {
        if (event.key === 'Enter') {
          event.preventDefault();
          void commit();
        }
      }}
    />
  );
}

/** One track: its number, the Song's shortcode, title, primary Artist, and state, and what can be done to it. */
function TrackRow({
  track,
  index,
  count,
  discs,
  busy,
  dragOver,
  actions,
}: {
  track: AlbumTrack;
  index: number;
  count: number;
  /** The disc numbers the Album has, in order. */
  discs: number[];
  busy: boolean;
  dragOver: boolean;
  actions: {
    move: (to: number, action?: RowAction) => void;
    moveToDisc: (disc: number) => void;
    number: (number: number) => Promise<boolean>;
    remove: () => void;
    dragStart: () => void;
    dragEnter: () => void;
    drop: () => void;
    dragEnd: () => void;
  };
}) {
  const lastDisc = discs.at(-1) ?? 1;
  const otherDiscs = discs.filter((disc) => disc !== track.disc);
  // A new disc after the last one, unless the track is already alone on the last disc.
  const offersNewDisc = !(track.disc === lastDisc && count === 1);
  const newDisc = lastDisc + 1;

  return (
    // eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions -- dragging is the pointer way to reorder; each row's Move up and Move down buttons are the keyboard way (AC)
    <li
      data-song-id={track.songId}
      data-song-title={track.title}
      data-disc={track.disc}
      data-track={track.track}
      draggable={!busy}
      onDragStart={(event: DragEvent<HTMLLIElement>) => {
        event.dataTransfer.effectAllowed = 'move';
        event.dataTransfer.setData('text/plain', track.songId);
        actions.dragStart();
      }}
      onDragOver={(event) => {
        event.preventDefault();
        actions.dragEnter();
      }}
      onDrop={(event) => {
        event.preventDefault();
        actions.drop();
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
            <TrackNumberField
              key={`${track.songId} ${String(track.track)}`}
              track={track}
              busy={busy}
              onCommit={actions.number}
            />
            <Anchor
              component={Link}
              to={`/songs/${track.shortcode}`}
              size="sm"
              ff="monospace"
              underline="always"
            >
              {track.shortcode}
            </Anchor>
            <Text size="sm" fw={500}>
              {track.title}
            </Text>
            <Text size="sm" c="var(--n8-color-secondary-text)">
              {track.primaryArtist?.name ?? 'No Artist'}
            </Text>
            <StateBadge name={track.state.name} colour={track.state.colour} />
            {!track.hasSelectedGeneration && (
              <Badge variant="default" tt="none" data-testid="track-incomplete">
                Incomplete: no Selected Generation
              </Badge>
            )}
          </Group>
          <Group gap={6} wrap="wrap">
            <SongPlayButton
              song={{
                id: track.songId,
                shortcode: track.shortcode,
                title: track.title,
                playback: track.playback,
              }}
              size="xs"
            />
            <Button
              size="xs"
              variant="default"
              data-action="up"
              aria-label={`Move ${track.title} up`}
              disabled={busy || index === 0}
              onClick={() => {
                actions.move(index - 1, 'up');
              }}
            >
              Move up
            </Button>
            <Button
              size="xs"
              variant="default"
              data-action="down"
              aria-label={`Move ${track.title} down`}
              disabled={busy || index === count - 1}
              onClick={() => {
                actions.move(index + 1, 'down');
              }}
            >
              Move down
            </Button>
            {/* No focus placeholder: see the state menu in SongHeader (aria-required-children). */}
            <Menu
              position="bottom-end"
              withinPortal
              withInitialFocusPlaceholder={false}
              hideDetached={false}
            >
              <Menu.Target>
                <Button
                  size="xs"
                  variant="default"
                  aria-label={`Move ${track.title} to disc`}
                  disabled={busy || (otherDiscs.length === 0 && !offersNewDisc)}
                  rightSection={<span aria-hidden="true">▾</span>}
                >
                  Move to disc
                </Button>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Label>Move to the end of</Menu.Label>
                {otherDiscs.map((disc) => (
                  <Menu.Item
                    key={disc}
                    onClick={() => {
                      actions.moveToDisc(disc);
                    }}
                  >
                    Disc {disc}
                  </Menu.Item>
                ))}
                {offersNewDisc && (
                  <Menu.Item
                    onClick={() => {
                      actions.moveToDisc(newDisc);
                    }}
                  >
                    New disc {newDisc}
                  </Menu.Item>
                )}
              </Menu.Dropdown>
            </Menu>
            <Button
              size="xs"
              variant="default"
              aria-label={`Remove ${track.title}`}
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
 * The Album's tracks, disc by disc, each disc in track-number order: Songs added with the shared Song
 * search (at the end of the last disc), reordered within a disc by dragging or with Move up and Move
 * down (which renumber the disc 1, 2, 3…), numbered directly (a gap is allowed; Renumber closes
 * gaps), moved to another disc, and removed. Each change is saved at once under the Album's
 * revision; when the Album changed elsewhere it is shown as it is now, with a notice, and the change
 * is not applied. Changing an Album's tracks never changes its Songs.
 */
export function TrackList({ album, onAlbum }: { album: Album; onAlbum: (album: Album) => void }) {
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<Message>();
  const [dragFrom, setDragFrom] = useState<AlbumTrack | undefined>();
  const [dragOver, setDragOver] = useState<string | undefined>();
  const [focusAfterMove, setFocusAfterMove] = useState<{ id: string; action: RowAction }>();
  const sectionRef = useRef<HTMLDivElement>(null);
  const busyRef = useRef(false);
  const tracks = album.tracks;
  const discs = discsOf(tracks);
  const discNumbers = discs.map((disc) => disc.disc);

  // After a move by keyboard, the focus stays on the button that moved the track, or on the other
  // one when that is now disabled (the track reached the top or the bottom of its disc).
  useEffect(() => {
    if (focusAfterMove === undefined) {
      return;
    }
    const row = sectionRef.current?.querySelector(`[data-song-id="${focusAfterMove.id}"]`);
    const same = row?.querySelector<HTMLButtonElement>(`[data-action="${focusAfterMove.action}"]`);
    const other = row?.querySelector<HTMLButtonElement>(
      `[data-action="${focusAfterMove.action === 'up' ? 'down' : 'up'}"]`,
    );
    (same && !same.disabled ? same : other)?.focus();
  }, [focusAfterMove, album]);

  /** Runs one change at a time and takes in the Album it answers with. */
  const run = async (
    send: () => Promise<AlbumTracksResult>,
    done: (saved: Album) => string,
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
        onAlbum(result.album);
        setMessage({ text: done(result.album), tone: 'info' });
        return true;
      case 'conflict':
        onAlbum(result.current);
        setMessage({ text: CONFLICT_MESSAGE, tone: 'problem' });
        return false;
      case 'duplicate':
        onAlbum(result.current);
        setMessage({
          text: refused.duplicate ?? 'That Song is on this Album already.',
          tone: 'problem',
        });
        return false;
      case 'disc-full':
      case 'taken':
        onAlbum(result.current);
        setMessage({ text: result.message, tone: 'problem' });
        return false;
      case 'invalid':
        setMessage({ text: result.message || NUMBER_ERROR, tone: 'problem' });
        return false;
      case 'not-found':
        setMessage({
          text: 'This Album, or that Song, is no longer there. Reload the page.',
          tone: 'problem',
        });
        return false;
      case 'failed':
        setMessage({ text: FAILED_MESSAGE, tone: 'problem' });
        return false;
    }
  };

  const placeOf = (saved: Album, songId: string) =>
    saved.tracks.find((track) => track.songId === songId);

  const send = (next: AlbumTrackPlace[]) => () => setAlbumTracks(album, next);

  const moveWithin = (track: AlbumTrack, to: number, action?: RowAction) => {
    const disc = discs.find((group) => group.disc === track.disc);
    const from = disc?.tracks.findIndex((item) => item.songId === track.songId) ?? -1;
    if (disc === undefined || to < 0 || to >= disc.tracks.length || from === to) {
      return;
    }
    void run(send(moveWithinDisc(tracks, track.songId, to)), (saved) => {
      const place = placeOf(saved, track.songId);
      return `Moved ${track.title} to track ${String(place?.track ?? to + 1)} of ${String(disc.tracks.length)} on disc ${String(place?.disc ?? track.disc)}.`;
    }).then((saved) => {
      if (saved && action !== undefined) {
        setFocusAfterMove({ id: track.songId, action });
      }
    });
  };

  return (
    <Stack gap="md" ref={sectionRef}>
      <Title order={3} id="album-tracks-heading">
        Tracks
      </Title>
      <SongSearch
        label="Add a Song"
        description="Search by title or shortcode. A new Song goes to the end of the last disc; a Song is on an Album once."
        unavailable={tracks.map((track) => track.songId)}
        unavailableNote="on this Album"
        busy={busy}
        onChoose={(song) => {
          void run(
            () => addAlbumTrack(album, song.id),
            (saved) => {
              const place = placeOf(saved, song.id);
              return `Added ${song.title} as track ${String(place?.track ?? '')} on disc ${String(place?.disc ?? '')}.`;
            },
            { duplicate: `${song.title} is on this Album already.` },
          );
        }}
      />

      <Group gap="xs" role="status" aria-live="polite" mih={28} data-testid="album-track-status">
        {message &&
          (message.tone === 'problem' ? (
            <Notice title="Not changed">
              <Text>{message.text}</Text>
            </Notice>
          ) : (
            <Text size="sm">{message.text}</Text>
          ))}
      </Group>

      {tracks.length === 0 ? (
        <Text size="sm" c="var(--n8-color-secondary-text)">
          No tracks yet. Find a Song above to add it.
        </Text>
      ) : (
        <>
          <Group justify="space-between" wrap="wrap" gap="xs">
            <Text size="sm" c="var(--n8-color-secondary-text)" id="album-tracks-help" maw={560}>
              Drag a track within its disc, or use Move up and Move down; the disc is renumbered 1,
              2, 3…. Type a number to leave a gap. Changing the Album never changes its Songs.
            </Text>
            <Button
              size="xs"
              variant="default"
              disabled={busy || isNumberedInOrder(tracks)}
              onClick={() => {
                void run(send(renumber(tracks)), () => 'Renumbered every disc 1, 2, 3….');
              }}
            >
              Renumber
            </Button>
          </Group>
          {discs.map((disc) => (
            <Stack key={disc.disc} gap="xs">
              <Title order={4} size="h5" id={`album-disc-${String(disc.disc)}`}>
                Disc {disc.disc}
              </Title>
              <ol
                aria-labelledby={`album-disc-${String(disc.disc)}`}
                aria-describedby="album-tracks-help"
                style={{ padding: 0, margin: 0, display: 'flex', flexDirection: 'column', gap: 8 }}
              >
                {disc.tracks.map((track, index) => (
                  <TrackRow
                    key={track.songId}
                    track={track}
                    index={index}
                    count={disc.tracks.length}
                    discs={discNumbers}
                    busy={busy}
                    dragOver={
                      dragFrom?.disc === track.disc &&
                      dragOver === track.songId &&
                      dragFrom.songId !== track.songId
                    }
                    actions={{
                      move: (to, action) => {
                        moveWithin(track, to, action);
                      },
                      moveToDisc: (target) => {
                        // A track lands after the target disc's last one, so a disc that ends at
                        // 999 has no room: say so instead of sending a number the API refuses.
                        const last = discs.find((group) => group.disc === target)?.tracks.at(-1);
                        if (last !== undefined && last.track >= ALBUM_TRACK_MAXIMUM_NUMBER) {
                          setMessage({
                            text: discFullMessage(target, track.title),
                            tone: 'problem',
                          });
                          return;
                        }
                        void run(send(moveToDisc(tracks, track.songId, target)), (saved) => {
                          const place = placeOf(saved, track.songId);
                          return `Moved ${track.title} to disc ${String(place?.disc ?? target)}, track ${String(place?.track ?? '')}.`;
                        });
                      },
                      number: (number) =>
                        run(
                          send(withTrackNumber(placesOf(tracks), track.songId, number)),
                          () =>
                            `${track.title} is now track ${String(number)} on disc ${String(track.disc)}.`,
                        ),
                      remove: () => {
                        void run(
                          () => removeAlbumTrack(album, track.songId),
                          () => `Removed ${track.title}.`,
                        );
                      },
                      dragStart: () => {
                        setDragFrom(track);
                      },
                      dragEnter: () => {
                        setDragOver(track.songId);
                      },
                      drop: () => {
                        if (dragFrom?.disc === track.disc) {
                          moveWithin(dragFrom, index);
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
            </Stack>
          ))}
        </>
      )}
    </Stack>
  );
}
