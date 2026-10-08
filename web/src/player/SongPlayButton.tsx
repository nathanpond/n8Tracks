import { Button, Tooltip, VisuallyHidden, type ButtonProps } from '@mantine/core';
import { useId, useState } from 'react';
import { selectGeneration } from '../api/generations';
import { songUnplayableText, type SongPlaybackCandidate } from '../api/songPlayback';
import { readSong, type Song } from '../api/songs';
import { ChooseGenerationDialog, type ChosenCandidate } from './ChooseGenerationDialog';
import { usePlayer } from './playerContext';
import type { PlayableSong } from './playerRules';

/**
 * The Play control of a Song (#219): on the Song page's header, each row of the Songs table, and each
 * Song row of an Album or a Playlist. It never picks a Generation: the server's playback answer
 * (`GET /songs/{reference}/playback`) decides. The Song's choice (its Song-level preferred file, or
 * its Selected Generation's file) plays at once; with nothing selected the chooser opens and the pick
 * plays for that one listen, made the Selected Generation only when that option is ticked (with
 * `revision`, the Song's as the page has it, or as read just before when the row has none; a
 * conflict is reported in the bar and playback still starts). While this Song's Play is what is
 * playing it pauses and resumes, and never asks again. A Song with nothing to offer (`playback`
 * `none`) shows Play disabled, with the reason. Only that Song plays: nothing follows it. Outside the
 * signed-in app (no player) it renders nothing.
 */
export function SongPlayButton({
  song,
  revision,
  onSelected,
  size = 'compact-xs',
}: {
  song: PlayableSong;
  /** The Song's revision, where the page has the Song; a row without one reads it when needed. */
  revision?: number;
  /** Called with the Song as it is now after a pick was made its Selected Generation. */
  onSelected?: (song: Song) => void;
  size?: ButtonProps['size'];
}) {
  const player = usePlayer();
  const reasonId = useId();
  const [candidates, setCandidates] = useState<SongPlaybackCandidate[] | null>(null);
  if (player === null) {
    return null;
  }
  const { state } = player;
  const isCurrent = state.current?.songId === song.id && state.current.via !== null;
  const going = isCurrent && (state.status === 'playing' || state.status === 'loading');
  const starting = state.starting === song.id;
  const reason =
    !isCurrent && song.playback?.state === 'none'
      ? songUnplayableText(song.playback.reason)
      : undefined;

  const pick = async ({ candidate, makeSelected }: ChosenCandidate) => {
    setCandidates(null);
    if (!makeSelected || candidate.kind !== 'generation') {
      player.playChosen(song, candidate);
      return;
    }
    const shortcode = candidate.generation.shortcode;
    const base = revision ?? (await readSong(song.id))?.revision;
    const result =
      base === undefined
        ? undefined
        : await selectGeneration(song.id, candidate.generation.id, base);
    if (result?.kind === 'saved') {
      onSelected?.(result.record);
      player.playChosen(song, candidate, { selected: true });
    } else if (result?.kind === 'conflict') {
      player.playChosen(song, candidate, {
        notice: `${shortcode} was not made the Selected Generation: the Song was changed elsewhere. Reload it to choose again. Playing it for this listen.`,
      });
    } else {
      player.playChosen(song, candidate, {
        notice: `${shortcode} could not be made the Selected Generation. Playing it for this listen.`,
      });
    }
  };

  if (reason !== undefined) {
    return (
      <>
        <Tooltip
          label={reason}
          events={{ hover: true, focus: true, touch: true }}
          multiline
          maw={320}
        >
          <Button
            size={size}
            variant="default"
            data-disabled
            aria-disabled="true"
            aria-label={`Play ${song.title}`}
            aria-describedby={reasonId}
            data-testid="song-play-button"
            data-playing="false"
            onClick={(event) => {
              event.preventDefault();
            }}
          >
            Play
          </Button>
        </Tooltip>
        <VisuallyHidden id={reasonId}>{reason}</VisuallyHidden>
      </>
    );
  }

  return (
    <>
      <Button
        size={size}
        variant={isCurrent ? 'filled' : 'default'}
        aria-label={`${going ? 'Pause' : 'Play'} ${song.title}`}
        aria-busy={starting || undefined}
        data-testid="song-play-button"
        data-playing={String(going)}
        onClick={() => {
          if (isCurrent) {
            if (going) {
              player.pause();
            } else {
              player.resume();
            }
          } else {
            player.playSong(song, (answer) => {
              setCandidates(answer.candidates);
            });
          }
        }}
      >
        {going ? 'Pause' : 'Play'}
      </Button>
      <ChooseGenerationDialog
        song={song}
        candidates={candidates}
        onClose={() => {
          setCandidates(null);
        }}
        onPick={(chosen) => {
          void pick(chosen);
        }}
      />
    </>
  );
}
