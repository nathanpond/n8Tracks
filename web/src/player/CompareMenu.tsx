import { Button, Group, Menu, Text, VisuallyHidden } from '@mantine/core';
import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { rateGeneration } from '../api/generations';
import {
  readPlaybackSources,
  type PlaybackSourceGeneration,
  type PlaybackSources,
} from '../api/playbackSources';
import { StarRating } from '../generations/StarRating';
import {
  announceRated,
  ratedGenerationOf,
  GENERATION_RATED_EVENT,
} from '../generations/ratingEvents';
import {
  COMPARE_KEYS,
  COMPARE_SHORTCUT_TEXT,
  entriesOf,
  generationHeading,
  isTypingTarget,
  nowPlayingOfEntry,
  shortcutOf,
  sourceNameOf,
  stepFrom,
  type CompareAction,
  type CompareEntry,
} from './compare';
import type { Player } from './playerContext';

/** The sources as last read for one Song, or that reading them failed. */
type SourcesState =
  | { songId: string; kind: 'loaded'; sources: PlaybackSources }
  | { songId: string; kind: 'failed' }
  | null;

const KEY_NAMES: Record<CompareAction, string> = {
  previous: '[',
  next: ']',
  ab: '\\',
};

/**
 * Compare (#220): while a Song's Generation or file is loaded, the player bar offers the Song's
 * other sources. The Compare menu lists the Song-level files, then each playable Generation (by
 * Version, shortcode and rating) with its files by format, or, with no file available, its Suno
 * stream, named "Suno stream" (#221); choosing one switches to it at the same
 * moment. Previous and Next step through the stops (each Song-level file, each Generation's playback
 * file), wrapping; A/B switches between what plays now and what played before it from this Song.
 * Shortcuts: `[` previous, `]` next, `\` A/B (by physical key, without modifiers or auto-repeat, and
 * never while typing), listed in each control's description. The list is read when the Song changes
 * and each time the menu opens; switching writes nothing. A compact rating control rates the playing
 * Generation without stopping playback; it is absent while a Song-level file plays.
 */
export function CompareMenu({ player }: { player: Player }) {
  const { state } = player;
  const { current, previous } = state;
  const songId = current?.songId ?? null;
  const [loaded, setLoaded] = useState<SourcesState>(null);
  const [ratingProblem, setRatingProblem] = useState<string | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const reading = useRef<AbortController | null>(null);
  const descriptions = useId();

  const read = useCallback((id: string) => {
    reading.current?.abort();
    const controller = new AbortController();
    reading.current = controller;
    void readPlaybackSources(id, controller.signal)
      .catch(() => undefined)
      .then((sources) => {
        if (controller.signal.aborted) {
          return;
        }
        setLoaded(
          sources === undefined
            ? { songId: id, kind: 'failed' }
            : { songId: id, kind: 'loaded', sources },
        );
      });
  }, []);

  // Read again whenever the Song changes; nothing is read for a file with no Song.
  useEffect(() => {
    if (songId === null) {
      reading.current?.abort();
      return undefined;
    }
    read(songId);
    return () => {
      reading.current?.abort();
    };
  }, [songId, read]);

  const sources =
    loaded !== null && loaded.songId === songId && loaded.kind === 'loaded' ? loaded.sources : null;
  const entries = sources === null ? [] : entriesOf(sources);
  const fileId = current?.fileId ?? null;
  const playing = entries.find((entry) => entry.key === fileId);
  const previousTarget = stepFrom(entries, fileId, -1);
  const nextTarget = stepFrom(entries, fileId, 1);
  const abTarget = previous !== null && previous.songId === songId ? previous : null;

  // A rating saved elsewhere (the Song page) keeps the bar's copy and revision current.
  useEffect(() => {
    const onRated = (event: Event) => {
      const rated = ratedGenerationOf(event);
      if (rated === undefined) {
        return;
      }
      setLoaded((state) =>
        state?.kind !== 'loaded'
          ? state
          : {
              ...state,
              sources: {
                ...state.sources,
                generations: state.sources.generations.map((generation) =>
                  generation.generation.id === rated.id && generation.revision < rated.revision
                    ? { ...generation, rating: rated.rating, revision: rated.revision }
                    : generation,
                ),
              },
            },
      );
    };
    window.addEventListener(GENERATION_RATED_EVENT, onRated);
    return () => {
      window.removeEventListener(GENERATION_RATED_EVENT, onRated);
    };
  }, []);

  const switchToEntry = (entry: CompareEntry) => {
    if (sources !== null) {
      player.switchTo(nowPlayingOfEntry(sources.song, entry, current), { keepTime: true });
    }
  };

  const act = (action: CompareAction) => {
    if (action === 'ab') {
      if (abTarget !== null) {
        player.switchTo(abTarget, { keepTime: true });
      }
      return;
    }
    const target = action === 'next' ? nextTarget : previousTarget;
    if (target !== undefined) {
      switchToEntry(target);
    }
  };

  // The shortcuts, anywhere in the app but where typing goes.
  const latest = useRef(act);
  useEffect(() => {
    latest.current = act;
  });
  useEffect(() => {
    if (songId === null) {
      return undefined;
    }
    const onKey = (event: KeyboardEvent) => {
      const action = shortcutOf(event);
      if (action === undefined || event.defaultPrevented || isTypingTarget(event.target)) {
        return;
      }
      event.preventDefault();
      latest.current(action);
    };
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('keydown', onKey);
    };
  }, [songId]);

  // Say what plays now after each switch within the Song.
  const said = useRef<{ songId: string | null; fileId: string | null }>({ songId, fileId });
  useEffect(() => {
    const before = said.current;
    said.current = { songId, fileId };
    if (
      current !== null &&
      songId !== null &&
      before.songId === songId &&
      before.fileId !== fileId
    ) {
      setAnnouncement(`Now playing ${sourceNameOf(current)}.`);
    } else if (before.songId !== songId) {
      setAnnouncement('');
    }
  }, [current, songId, fileId]);

  const rate = (generation: PlaybackSourceGeneration, rating: number | null) => {
    setRatingProblem(null);
    const id = generation.generation.id;
    const apply = (rated: number | null, revision: number) => {
      setLoaded((state) =>
        state?.kind !== 'loaded'
          ? state
          : {
              ...state,
              sources: {
                ...state.sources,
                generations: state.sources.generations.map((other) =>
                  other.generation.id === id ? { ...other, rating: rated, revision } : other,
                ),
              },
            },
      );
    };
    apply(rating, generation.revision);
    const send = async (revision: number, retried: boolean): Promise<void> => {
      const result = await rateGeneration(id, rating, revision);
      if (result.kind === 'saved') {
        apply(result.record.rating, result.record.revision);
        announceRated(result.record);
        return;
      }
      if (result.kind === 'conflict' && !retried) {
        await send(result.current.revision, true);
        return;
      }
      if (result.kind === 'conflict') {
        apply(result.current.rating, result.current.revision);
      } else {
        apply(generation.rating, generation.revision);
      }
      setRatingProblem(`The rating of ${generation.generation.shortcode} could not be saved.`);
    };
    void send(generation.revision, false).catch(() => {
      apply(generation.rating, generation.revision);
      setRatingProblem(`The rating of ${generation.generation.shortcode} could not be saved.`);
    });
  };

  if (current === null || songId === null) {
    return null;
  }

  const describedBy = (action: CompareAction) => `${descriptions}-${action}`;
  const failed = loaded !== null && loaded.songId === songId && loaded.kind === 'failed';
  const ratedGeneration = playing?.generation ?? null;
  const groups = sources === null ? [] : sources.generations;

  return (
    <Group gap="xs" wrap="wrap" align="center" data-testid="player-compare">
      <Menu
        position="top-start"
        withinPortal
        withInitialFocusPlaceholder={false}
        hideDetached={false}
        onOpen={() => {
          read(songId);
        }}
      >
        <Menu.Target>
          <Button
            variant="default"
            size="compact-sm"
            aria-label={`Compare: playing ${playing?.name ?? sourceNameOf(current)}`}
            data-testid="compare-button"
          >
            Compare
          </Button>
        </Menu.Target>
        <Menu.Dropdown data-testid="compare-menu" mah={360} style={{ overflowY: 'auto' }}>
          {sources === null && (
            <Menu.Label>{failed ? 'The sources could not be read.' : 'Reading…'}</Menu.Label>
          )}
          {sources !== null && sources.songFiles.length > 0 && (
            <Menu.Label>Song-level files</Menu.Label>
          )}
          {entries
            .filter((entry) => entry.generation === null)
            .map((entry) => (
              <CompareItem
                key={entry.key}
                entry={entry}
                label={entry.name}
                playing={entry.key === fileId}
                onChoose={switchToEntry}
              />
            ))}
          {groups.map((generation) => (
            <div key={generation.generation.id} data-testid="compare-generation">
              <Menu.Label>{generationHeading(generation)}</Menu.Label>
              {entries
                .filter((entry) => entry.generation?.generation.id === generation.generation.id)
                .map((entry) => (
                  <CompareItem
                    key={entry.key}
                    entry={entry}
                    label={entry.name}
                    playing={entry.key === fileId}
                    onChoose={switchToEntry}
                  />
                ))}
            </div>
          ))}
        </Menu.Dropdown>
      </Menu>
      {(['previous', 'next', 'ab'] as const).map((action) => {
        const disabled =
          action === 'ab'
            ? abTarget === null
            : (action === 'next' ? nextTarget : previousTarget) === undefined;
        return (
          <Button
            key={action}
            variant="default"
            size="compact-sm"
            disabled={disabled}
            aria-describedby={describedBy(action)}
            aria-keyshortcuts={KEY_NAMES[action]}
            onClick={() => {
              act(action);
            }}
            data-testid={`compare-${action}`}
            data-key={COMPARE_KEYS[action]}
          >
            {action === 'previous' ? 'Previous' : action === 'next' ? 'Next' : 'A/B'}
          </Button>
        );
      })}
      <VisuallyHidden id={describedBy('previous')}>
        Previous source of this Song. {COMPARE_SHORTCUT_TEXT.previous}
      </VisuallyHidden>
      <VisuallyHidden id={describedBy('next')}>
        Next source of this Song. {COMPARE_SHORTCUT_TEXT.next}
      </VisuallyHidden>
      <VisuallyHidden id={describedBy('ab')}>
        {abTarget === null
          ? 'Switches between the last two sources played from this Song, once two have played.'
          : `Switches to ${sourceNameOf(abTarget)}, at the same moment.`}{' '}
        {COMPARE_SHORTCUT_TEXT.ab}
      </VisuallyHidden>
      {playing?.generation != null && playing.name.endsWith(` · ${playing.file.fileName}`) && (
        // Two files of one format: the bar names the file too.
        <Text size="xs" data-testid="player-source-file" style={{ overflowWrap: 'anywhere' }}>
          File: {playing.file.fileName}
        </Text>
      )}
      {ratedGeneration !== null && (
        <Group gap={4} wrap="nowrap" data-testid="player-rating">
          <Text size="xs" aria-hidden="true">
            Rating
          </Text>
          <StarRating
            size="sm"
            value={ratedGeneration.rating}
            label={`Rating of ${ratedGeneration.generation.shortcode}`}
            onChange={(rating) => {
              rate(ratedGeneration, rating);
            }}
          />
        </Group>
      )}
      {ratingProblem !== null && (
        <Text size="xs" role="alert" data-testid="player-rating-problem">
          {ratingProblem}
        </Text>
      )}
      <VisuallyHidden role="status" data-testid="compare-announcement">
        {announcement}
      </VisuallyHidden>
    </Group>
  );
}

function CompareItem({
  entry,
  label,
  playing,
  onChoose,
}: {
  entry: CompareEntry;
  label: string;
  playing: boolean;
  onChoose: (entry: CompareEntry) => void;
}) {
  return (
    <Menu.Item
      onClick={() => {
        onChoose(entry);
      }}
      aria-current={playing ? 'true' : undefined}
      leftSection={<span aria-hidden="true">{playing ? '▶' : ''}</span>}
      data-testid="compare-entry"
      data-file={entry.key}
      data-source={entry.source}
      data-playing={playing}
    >
      {label}
      {playing && <VisuallyHidden> (playing now)</VisuallyHidden>}
    </Menu.Item>
  );
}
