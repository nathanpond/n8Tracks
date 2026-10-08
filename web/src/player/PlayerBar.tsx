import { Anchor, Button, Group, Stack, Text } from '@mantine/core';
import type { KeyboardEvent } from 'react';
import { Link } from 'react-router';
import { CompareMenu } from './CompareMenu';
import type { Player } from './playerContext';
import {
  clockText,
  detailOf,
  positionText,
  seekTarget,
  titleOf,
  viaText,
  VOLUME_STEP_PERCENT,
} from './playerRules';

const RANGE_STYLE = { accentColor: 'var(--mantine-primary-color-filled)', minWidth: 0 };

/**
 * The player bar (#218), at the bottom of every signed-in page once something has been played: what
 * is playing (its Song's title, which links to the Song, and its Version number, Generation shortcode,
 * and format; for a Song-level file, "Song-level file" and its name; for a file with no Song, its
 * name), Play/Pause, a seek bar with the elapsed and total time, Mute and volume, and Close, which
 * stops playback. Everything works by mouse and keyboard: the seek bar's arrow keys move 5 seconds,
 * Page Up and Page Down 30, Home and End to either end, and the space bar on the seek bar plays or
 * pauses, as it does on the Play button. When the audio cannot be played the bar says so, names the
 * file, and offers Retry; when a Play could not start it says why, and what was playing goes on.
 * When a Song's Play (#219) started it, the bar also says which rule chose it: the Song's Song-level
 * file, its Selected Generation, or "Chosen for this listen"; when the Selected Generation has nothing
 * to play, the notice offers Open in Suno where there is a link. While a Song's source is loaded, the
 * Compare row (#220) switches between the Song's Generations and files and rates the playing one.
 */
export function PlayerBar({ player }: { player: Player }) {
  const { state } = player;
  const { current } = state;
  const going = state.status === 'playing' || state.status === 'loading';
  const volumePercent = Math.round(state.volume * 100);

  const onSeekKey = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === ' ') {
      event.preventDefault();
      player.toggle();
      return;
    }
    const target = seekTarget(event.key, state.position, state.duration);
    if (target !== undefined) {
      event.preventDefault();
      player.seek(target);
    }
  };

  return (
    <Stack gap={4} h="100%" justify="center" data-testid="player-bar" data-status={state.status}>
      {current !== null && (
        <Group gap="sm" wrap="wrap" align="center">
          <Button
            variant="filled"
            size="sm"
            miw={84}
            onClick={player.toggle}
            data-testid="player-toggle"
          >
            {going ? 'Pause' : 'Play'}
          </Button>
          <Stack gap={0} miw={0} style={{ flex: '1 1 180px' }} data-testid="player-now-playing">
            {current.label.kind === 'unmatched' ? (
              <Text size="sm" fw={600} truncate="end" data-testid="player-title">
                {titleOf(current.label)}
              </Text>
            ) : (
              <Anchor
                component={Link}
                to={`/songs/${current.label.song.shortcode}`}
                size="sm"
                fw={600}
                truncate="end"
                underline="always"
                data-testid="player-title"
              >
                {titleOf(current.label)}
              </Anchor>
            )}
            <Text size="xs" truncate="end" data-testid="player-detail">
              {detailOf(current.label)}
            </Text>
            {current.via !== null && (
              <Text size="xs" truncate="end" data-testid="player-via" data-via={current.via}>
                {viaText(current.via)}
              </Text>
            )}
          </Stack>
          <Group gap={6} wrap="nowrap" style={{ flex: '3 1 240px' }}>
            <Text size="xs" ff="monospace" data-testid="player-elapsed">
              {clockText(state.position)}
            </Text>
            <input
              type="range"
              aria-label="Seek"
              aria-valuetext={positionText(state.position, state.duration)}
              min={0}
              max={state.duration ?? 0}
              step="any"
              value={Math.min(state.position, state.duration ?? state.position)}
              onChange={(event) => {
                player.seek(Number(event.currentTarget.value));
              }}
              onKeyDown={onSeekKey}
              style={{ ...RANGE_STYLE, flex: 1 }}
              data-testid="player-seek"
            />
            <Text size="xs" ff="monospace" data-testid="player-duration">
              {clockText(state.duration)}
            </Text>
          </Group>
          <Group gap={6} wrap="nowrap">
            <Button
              variant={state.muted ? 'filled' : 'default'}
              size="compact-sm"
              aria-pressed={state.muted}
              onClick={player.toggleMute}
              data-testid="player-mute"
            >
              Mute
            </Button>
            <input
              type="range"
              aria-label="Volume"
              aria-valuetext={`${String(volumePercent)}%${state.muted ? ', muted' : ''}`}
              min={0}
              max={100}
              step={VOLUME_STEP_PERCENT}
              value={volumePercent}
              onChange={(event) => {
                player.setVolume(Number(event.currentTarget.value) / 100);
              }}
              style={{ ...RANGE_STYLE, width: 96 }}
              data-testid="player-volume"
            />
          </Group>
          <Button
            variant="default"
            size="compact-sm"
            aria-label="Close the player"
            onClick={player.close}
          >
            Close
          </Button>
        </Group>
      )}
      {typeof current?.songId === 'string' && <CompareMenu player={player} />}
      {current !== null && state.status === 'error' && (
        <Group gap="sm" wrap="wrap">
          <Text
            size="sm"
            role="alert"
            data-testid="player-error"
            style={{ overflowWrap: 'anywhere' }}
          >
            {current.label.fileName} could not be played: the audio could not be loaded or decoded.
          </Text>
          <Button variant="default" size="compact-sm" onClick={player.retry}>
            Retry
          </Button>
        </Group>
      )}
      <Group gap="sm" wrap="wrap">
        <Text size="sm" role="status" data-testid="player-notice">
          {state.notice ?? ''}
        </Text>
        {state.notice !== null && state.noticeLink !== null && (
          <Anchor
            href={state.noticeLink.href}
            target="_blank"
            rel="noopener noreferrer"
            size="sm"
            underline="always"
            aria-label={`${state.noticeLink.label} (opens a new tab)`}
            data-testid="player-notice-link"
          >
            {state.noticeLink.label}
          </Anchor>
        )}
        {current === null && state.notice !== null && (
          <Button
            variant="default"
            size="compact-sm"
            aria-label="Close the player"
            onClick={player.close}
          >
            Close
          </Button>
        )}
      </Group>
    </Stack>
  );
}
