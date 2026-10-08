import { Button, Tooltip, VisuallyHidden, type ButtonProps } from '@mantine/core';
import { useId } from 'react';
import type { UnmatchedFile } from '../api/audioFiles';
import type { Generation } from '../api/generations';
import { usePlayer } from './playerContext';
import { unplayableFileReason, unplayableReason } from './playerRules';

/** What a Play control plays: a Generation (its file, as the server's rule decides) or one file. */
export type PlayTarget =
  | { kind: 'generation'; generation: Generation; songTitle: string }
  | { kind: 'file'; file: UnmatchedFile };

/**
 * The Play control of a Generation row, the Generation panel, and each audio file row (#218). It
 * never chooses a file: a Generation's asks the server's playback rule (#212), a file row plays that
 * file. It shows Pause while what it refers to is playing (for a Generation, any of its files), and
 * pausing there pauses the bar; Play on the paused current item resumes it, and on anything else
 * starts that from the beginning. With nothing playable it stays focusable but disabled
 * (`aria-disabled`), with the reason in a tooltip and as its accessible description. Outside the
 * signed-in app (no player) it renders nothing.
 */
export function PlayButton({
  target,
  size = 'compact-xs',
}: {
  target: PlayTarget;
  size?: ButtonProps['size'];
}) {
  const player = usePlayer();
  const reasonId = useId();
  if (player === null) {
    return null;
  }
  const { state } = player;
  const isCurrent =
    target.kind === 'generation'
      ? state.current?.generationId === target.generation.id
      : state.current?.fileId === target.file.id;
  const going = isCurrent && (state.status === 'playing' || state.status === 'loading');
  const starting = target.kind === 'generation' && state.starting === target.generation.id;
  const what = target.kind === 'generation' ? target.generation.shortcode : target.file.fileName;
  // What is loaded keeps playing even when its file has since gone Missing.
  const reason = isCurrent
    ? undefined
    : target.kind === 'generation'
      ? unplayableReason(target.generation)
      : unplayableFileReason(target.file.status);

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
            aria-label={`Play ${what}`}
            aria-describedby={reasonId}
            data-testid="play-button"
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
    <Button
      size={size}
      variant={isCurrent ? 'filled' : 'default'}
      aria-label={`${going ? 'Pause' : 'Play'} ${what}`}
      aria-busy={starting || undefined}
      data-testid="play-button"
      data-playing={String(going)}
      onClick={() => {
        if (isCurrent) {
          if (going) {
            player.pause();
          } else {
            player.resume();
          }
        } else if (target.kind === 'generation') {
          player.playGeneration(target.generation, target.songTitle);
        } else {
          player.playFile(target.file);
        }
      }}
    >
      {going ? 'Pause' : 'Play'}
    </Button>
  );
}
