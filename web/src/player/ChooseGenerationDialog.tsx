import { Button, Checkbox, Group, Modal, Radio, Stack, Text } from '@mantine/core';
import { useState } from 'react';
import { formatDuration, ratingText } from '../api/generations';
import type { SongPlaybackCandidate } from '../api/songPlayback';
import { SelectedGenerationBadges } from '../generations/GenerationParts';
import { formatText, noStreamText, type PlayableSong } from './playerRules';

/** Why a candidate cannot play, in words: no local file, and why Suno cannot stream it (#221). */
function nothingToPlay(candidate: SongPlaybackCandidate): string {
  return `Nothing to play: it has no local audio file that can play, and ${noStreamText(candidate.reason)}.`;
}

/** What a candidate's line ends with: whether it streams from Suno (#221), or why it cannot play. */
function playText(candidate: SongPlaybackCandidate): string {
  if (!candidate.playable) {
    return ` · ${nothingToPlay(candidate)}`;
  }
  return candidate.reason === 'suno_stream' ? ' · Streams from Suno' : '';
}

function keyOf(candidate: SongPlaybackCandidate): string {
  return candidate.kind === 'file'
    ? `file:${candidate.audioFile.id}`
    : `generation:${candidate.generation.id}`;
}

function labelOf(candidate: SongPlaybackCandidate): string {
  if (candidate.kind === 'file') {
    return `Song-level file ${candidate.audioFile.fileName}`;
  }
  return `Version ${candidate.versionNumber} · ${candidate.generation.shortcode}`;
}

function detailOf(candidate: SongPlaybackCandidate): string {
  if (candidate.kind === 'file') {
    const length =
      candidate.audioFile.durationSeconds === null
        ? 'length unknown'
        : formatDuration(candidate.audioFile.durationSeconds);
    return `${formatText(candidate.audioFile.format)} · ${length}`;
  }
  const length =
    candidate.durationSeconds === null
      ? 'length unknown'
      : formatDuration(candidate.durationSeconds);
  return `${ratingText(candidate.rating)} · ${length}`;
}

/** What the user picked: the candidate, and whether to make it the Song's Selected Generation. */
export interface ChosenCandidate {
  candidate: SongPlaybackCandidate;
  makeSelected: boolean;
}

/**
 * The chooser (#219), shown when Play is pressed on a Song with no Selected Generation and no
 * available Song-level preferred file: n8Tracks never picks one by itself. It lists what the server's
 * playback answer offers, in its order: the Song's available Song-level files, then its Active
 * Generations, then Archived ones and those in Suno's Trash or no longer in Suno (each with a badge),
 * by Version and shortcode with the rating and duration; one with nothing to play is listed disabled,
 * with the reason. The pick plays for this one listen; "Make this the Selected Generation" is a
 * separate option, unticked, and offered for a Generation only. The dialog traps focus and gives it
 * back to the Play control when it closes.
 */
export function ChooseGenerationDialog({
  song,
  candidates,
  onClose,
  onPick,
}: {
  song: PlayableSong;
  /** What to offer, in order; null while the chooser is closed. */
  candidates: SongPlaybackCandidate[] | null;
  onClose: () => void;
  onPick: (chosen: ChosenCandidate) => void;
}) {
  // Always mounted, so the dialog knows what had focus when it opened and gives it back.
  return (
    <Modal
      opened={candidates !== null}
      onClose={onClose}
      title={`Play ${song.title}`}
      closeButtonProps={{ 'aria-label': 'Close' }}
      size="lg"
    >
      {candidates !== null && <Choices candidates={candidates} onClose={onClose} onPick={onPick} />}
    </Modal>
  );
}

/** The chooser's list and buttons; a fresh pick (nothing ticked) each time it opens. */
function Choices({
  candidates,
  onClose,
  onPick,
}: {
  candidates: SongPlaybackCandidate[];
  onClose: () => void;
  onPick: (chosen: ChosenCandidate) => void;
}) {
  const [picked, setPicked] = useState<string | null>(null);
  const [makeSelected, setMakeSelected] = useState(false);
  const candidate = candidates.find((each) => keyOf(each) === picked);

  return (
    <Stack gap="sm" data-testid="choose-generation">
      <Text size="sm">
        This Song has no Selected Generation. Choose what to play for this listen; the next Play
        asks again.
      </Text>
      <Radio.Group
        value={picked}
        onChange={(value) => {
          setPicked(value);
          setMakeSelected(false);
        }}
        label="What to play"
      >
        <Stack gap="xs" mt="xs">
          {candidates.map((each) => (
            <Radio
              key={keyOf(each)}
              value={keyOf(each)}
              disabled={!each.playable}
              data-testid="play-candidate"
              data-kind={each.kind}
              data-playable={String(each.playable)}
              label={
                <Group gap={6} wrap="wrap" component="span">
                  <span>{labelOf(each)}</span>
                  {each.kind === 'generation' && (
                    <SelectedGenerationBadges
                      selected={{
                        id: each.generation.id,
                        shortcode: each.generation.shortcode,
                        state: each.state,
                        remoteState: each.remoteState,
                      }}
                    />
                  )}
                </Group>
              }
              description={`${detailOf(each)}${playText(each)}`}
            />
          ))}
        </Stack>
      </Radio.Group>
      {candidate?.kind === 'generation' && (
        <Checkbox
          label="Make this the Selected Generation"
          checked={makeSelected}
          onChange={(event) => {
            setMakeSelected(event.currentTarget.checked);
          }}
          data-testid="make-selected"
        />
      )}
      <Group justify="flex-end" gap="sm">
        <Button variant="default" onClick={onClose}>
          Cancel
        </Button>
        <Button
          disabled={candidate === undefined}
          onClick={() => {
            if (candidate !== undefined) {
              onPick({
                candidate,
                makeSelected: candidate.kind === 'generation' && makeSelected,
              });
            }
          }}
          data-testid="play-chosen"
        >
          Play
        </Button>
      </Group>
    </Stack>
  );
}
