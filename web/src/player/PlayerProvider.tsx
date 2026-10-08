import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import type { UnmatchedFile } from '../api/audioFiles';
import { apiFetch } from '../api/client';
import { sunoSongUrl, type Generation } from '../api/generations';
import { fetchSession } from '../api/session';
import {
  readSongPlayback,
  songUnplayableText,
  type SongPlaybackAnswer,
  type SongPlaybackCandidate,
} from '../api/songPlayback';
import { body, isRecord } from '../api/songs';
import { carryTime, nextPair, NO_PAIR, switchFailedText, type ComparePair } from './compare';
import {
  PlayerContext,
  type PlayChosenOptions,
  type Player,
  type PlayerNotice,
  type PlayerState,
  type SwitchOptions,
} from './playerContext';
import {
  noStreamText,
  nowPlayingOfFile,
  nowPlayingOfGeneration,
  nowPlayingOfSong,
  nowPlayingOfStream,
  PLAYER_CHANNEL,
  readVolume,
  sourceOf,
  storeVolume,
  SUNO_START_TIMEOUT_MS,
  SUNO_STALL_TIMEOUT_MS,
  titleOf,
  type NowPlaying,
  type PlayableSong,
  type PlaybackFile,
  type SunoStreamSource,
} from './playerRules';

const NOT_ANSWERED = 'Playback could not start: n8Tracks did not answer as expected. Try again.';

function playbackFileOf(value: unknown): PlaybackFile | undefined {
  return isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.fileName === 'string' &&
    typeof value.format === 'string' &&
    typeof value.contentUrl === 'string'
    ? { id: value.id, fileName: value.fileName, format: value.format, contentUrl: value.contentUrl }
    : undefined;
}

/**
 * What the playback rule (#212, #221) names for a Generation: a local file, or its Suno stream; each
 * with the clip's Suno page (null without Suno data).
 */
type GenerationSource =
  | { kind: 'local'; file: PlaybackFile; sunoPageUrl: string | null }
  | { kind: 'suno'; stream: SunoStreamSource };

function textOrNull(value: unknown): string | null | undefined {
  return value === null || value === undefined
    ? null
    : typeof value === 'string'
      ? value
      : undefined;
}

/** A Play that cannot start, from the playback reason code: no local file, and why Suno cannot stream it. */
function nothingToPlayText(start: string, reason: unknown): string {
  return `${start} has no local audio file that can play, and ${noStreamText(typeof reason === 'string' ? reason : null)}.`;
}

/** Open in Suno for a notice, when there is a Suno page. */
function openInSuno(href: string | null): PlayerNotice['link'] {
  return href === null ? undefined : { label: 'Open in Suno', href };
}

/** A length the audio reports, or null while it does not know one (NaN) or has none (a stream). */
function durationOf(audio: HTMLAudioElement): number | null {
  return Number.isFinite(audio.duration) ? audio.duration : null;
}

/** A name for this tab on the player channel, so it ignores its own messages. */
function tabName(): string {
  return `${String(Date.now())}-${Math.random().toString(36).slice(2)}`;
}

function initialState(): PlayerState {
  const { volume, muted } = readVolume();
  return {
    current: null,
    status: 'paused',
    position: 0,
    duration: null,
    volume,
    muted,
    notice: null,
    noticeLink: null,
    starting: null,
    previous: null,
  };
}

/**
 * A switch on its way (#220): its ticket (a later switch replaces it), the source to go back to if it
 * fails and the time that source had reached (the time carried over), whether to play once there,
 * and whether it is itself the way back (which, failing, is an ordinary error).
 */
interface PendingSwitch {
  ticket: number;
  from: NowPlaying;
  at: number;
  play: boolean;
  back: NowPlaying | null;
}

/**
 * The signed-in app's player (#218): one `HTMLAudioElement`, created once and kept while the shell
 * is mounted, so moving between pages, editing, saving, and opening dialogs never touch it; and the
 * playing state every Play control and the bar read. There is no queue: playing something replaces
 * what is loaded, and when the audio ends the player stops, back at the start.
 *
 * A Generation's Play asks the server which file plays (`GET .../playback`, the one rule of #212) and
 * plays its `contentUrl` (#217, seeking by range requests); a file row plays that file. Volume and
 * mute are kept on this browser; what was playing is not restored after a reload. Starting playback
 * pauses the app's other tabs. The lock screen and media keys get the title and play/pause.
 *
 * When the audio cannot be loaded or decoded, the session is checked first: if it has ended, the
 * in-place sign-in prompt comes up (the bar stays as it was, paused, and Play carries on from there
 * once signed in); otherwise the bar names the file and offers Retry. Unmounting (signing out)
 * stops playback.
 *
 * With no local file available, a Generation streams from Suno (#221): the element plays Suno's
 * address directly, with no referrer (`referrerpolicy` `no-referrer`) and no credentials mode (no
 * `crossorigin`), so the request to Suno carries nothing of n8Tracks. A Suno stream that errors, has
 * not started 15 seconds after the play request, or stalls for 30 seconds while playing has failed:
 * the bar says so and offers Open in Suno, with no retry and no fall-over to another source.
 */
export function PlayerProvider({ children }: { children: ReactNode }) {
  const audioElement = useRef<HTMLAudioElement | null>(null);
  /**
   * The one audio element, made the first time it is needed and kept for the provider's life. It
   * sends no referrer and has no credentials mode, so a Suno stream's request carries nothing of
   * n8Tracks (#221).
   */
  const element = useCallback(() => {
    if (audioElement.current === null) {
      const audio = document.createElement('audio');
      audio.setAttribute('referrerpolicy', 'no-referrer');
      audio.removeAttribute('crossorigin');
      audioElement.current = audio;
    }
    return audioElement.current;
  }, []);
  const [state, setState] = useState<PlayerState>(initialState);
  const holder = useRef<HTMLSpanElement>(null);
  const current = useRef<NowPlaying | null>(null);
  const position = useRef(0);
  /** Set when the audio must be read again before it plays: where to carry on from. */
  const reloadAt = useRef<number | null>(null);
  const lookup = useRef(0);
  const unmounted = useRef(new AbortController());
  const channel = useRef<BroadcastChannel | null>(null);
  const [tab] = useState(tabName);
  const switching = useRef<PendingSwitch | null>(null);
  const switches = useRef(0);
  const pair = useRef<ComparePair>(NO_PAIR);
  /** The Suno stream's watchdog (#221): the timer that fails it when it does not start or stalls. */
  const watchdog = useRef<number | null>(null);
  /** Whether the Suno stream has played since the last play request. */
  const streamStarted = useRef(false);
  /** What a failed load does (set once defined below; the watchdog calls it). */
  const failure = useRef<() => void>(() => undefined);

  const disarm = useCallback(() => {
    if (watchdog.current !== null) {
      window.clearTimeout(watchdog.current);
      watchdog.current = null;
    }
  }, []);

  /** Fails what is loaded, if it is a Suno stream, unless it plays within `milliseconds`. */
  const arm = useCallback(
    (milliseconds: number) => {
      disarm();
      const loaded = current.current;
      if (loaded === null || sourceOf(loaded) !== 'suno') {
        return;
      }
      watchdog.current = window.setTimeout(() => {
        watchdog.current = null;
        if (current.current === loaded) {
          failure.current();
        }
      }, milliseconds);
    },
    [disarm],
  );

  /** `played` actually played (#220): the A/B pair moves on. */
  const settled = useCallback((played: NowPlaying) => {
    const next = nextPair(pair.current, played);
    pair.current = next;
    setState((previous) =>
      previous.previous === next.previous ? previous : { ...previous, previous: next.previous },
    );
  }, []);

  const playElement = useCallback(() => {
    // A Suno stream must start within 15 seconds of the play request (#221).
    streamStarted.current = false;
    arm(SUNO_START_TIMEOUT_MS);
    // Older browsers answer play() with nothing rather than a promise.
    Promise.resolve(element().play()).catch((error: unknown) => {
      // The browser refused to start without a gesture: it stays paused, ready for Play.
      if (error instanceof DOMException && error.name === 'NotAllowedError') {
        disarm();
        setState((previous) => ({ ...previous, status: 'paused' }));
      }
      // Anything else (a newer src, a pause) is answered by the element's own events.
    });
  }, [arm, disarm, element]);

  const load = useCallback(
    (next: NowPlaying, at: number) => {
      switching.current = null;
      disarm();
      current.current = next;
      reloadAt.current = null;
      position.current = at;
      element().src = next.src;
      if (at > 0) {
        const carryOn = () => {
          element().currentTime = at;
        };
        element().addEventListener('loadedmetadata', carryOn, { once: true });
      }
      setState((previous) => ({
        ...previous,
        current: next,
        status: 'loading',
        position: at,
        duration: null,
        notice: null,
        noticeLink: null,
      }));
      playElement();
    },
    [disarm, element, playElement],
  );

  const start = useCallback(
    (next: NowPlaying) => {
      // Whatever was being asked for is no longer wanted.
      lookup.current += 1;
      setState((previous) => ({ ...previous, starting: null }));
      load(next, 0);
    },
    [load],
  );

  /**
   * Starts asking for something to play: whatever was asked for before is no longer wanted. The
   * answer is applied with `settle` only while it is still the latest ask: a NowPlaying is loaded
   * (with `after`, a notice shown once it starts), a notice is shown and what was playing is left
   * alone, and undefined does nothing. It answers whether the ask was still the latest.
   */
  const ask = useCallback(
    (starting: string) => {
      lookup.current += 1;
      const ticket = lookup.current;
      setState((previous) => ({ ...previous, starting, notice: null, noticeLink: null }));
      return (next: NowPlaying | PlayerNotice | undefined, after?: string): boolean => {
        if (ticket !== lookup.current || unmounted.current.signal.aborted) {
          return false;
        }
        if (next === undefined) {
          setState((previous) => ({ ...previous, starting: null }));
        } else if ('text' in next) {
          // What was playing is left alone.
          setState((previous) => ({
            ...previous,
            starting: null,
            notice: next.text,
            noticeLink: next.link ?? null,
          }));
        } else {
          setState((previous) => ({ ...previous, starting: null }));
          load(next, 0);
          if (after !== undefined) {
            setState((previous) => ({ ...previous, notice: after }));
          }
        }
        return true;
      };
    },
    [load],
  );

  /**
   * What the playback rule (#212) names for a Generation: its local file or, with none available, its
   * Suno stream (#221); or a notice saying why there is none (with Open in Suno where there is a page).
   */
  const generationFile = useCallback(
    async (generation: {
      id: string;
      shortcode: string;
    }): Promise<GenerationSource | PlayerNotice> => {
      try {
        const response = await apiFetch(
          `api/v1/generations/${encodeURIComponent(generation.id)}/playback`,
          { signal: unmounted.current.signal },
        );
        const answer = await body(response);
        if (!response.ok || !isRecord(answer)) {
          return { text: NOT_ANSWERED };
        }
        const file = playbackFileOf(answer.audioFile);
        const sunoPageUrl = textOrNull(answer.sunoPageUrl);
        if (sunoPageUrl === undefined) {
          return { text: NOT_ANSWERED };
        }
        if (answer.source === 'local' && file !== undefined) {
          return { kind: 'local', file, sunoPageUrl };
        }
        if (answer.source === 'suno' && typeof answer.sunoAudioUrl === 'string') {
          return { kind: 'suno', stream: { url: answer.sunoAudioUrl, pageUrl: sunoPageUrl } };
        }
        return answer.source === 'none'
          ? {
              text: nothingToPlayText(
                `Playback could not start: ${generation.shortcode}`,
                answer.reason,
              ),
              link: openInSuno(sunoPageUrl),
            }
          : { text: NOT_ANSWERED };
      } catch {
        return { text: NOT_ANSWERED };
      }
    },
    [],
  );

  const playGeneration = useCallback(
    (generation: Generation, songTitle: string) => {
      const settle = ask(generation.id);
      void generationFile(generation).then((found) => {
        if ('text' in found) {
          settle(found);
        } else if (found.kind === 'suno') {
          settle(
            nowPlayingOfStream(
              { id: generation.song.id, shortcode: generation.song.shortcode, title: songTitle },
              generation,
              found.stream,
              null,
            ),
          );
        } else {
          settle(nowPlayingOfGeneration(generation, songTitle, found.file, found.sunoPageUrl));
        }
      });
    },
    [ask, generationFile],
  );

  const playSong = useCallback(
    (song: PlayableSong, onChoice: (answer: SongPlaybackAnswer) => void) => {
      const settle = ask(song.id);
      const read = async () => {
        let answer: SongPlaybackAnswer | undefined;
        try {
          answer = await readSongPlayback(song.id, unmounted.current.signal);
        } catch {
          answer = undefined;
        }
        if (answer === undefined) {
          settle({ text: NOT_ANSWERED });
          return;
        }
        const selected = answer.generation;
        if (answer.state === 'ready' && answer.audioFile !== null) {
          settle({
            ...nowPlayingOfSong(
              song,
              answer.audioFile,
              selected,
              selected === null ? 'song-preferred' : 'selected-generation',
            ),
            sunoPageUrl: answer.sunoPageUrl,
          });
        } else if (answer.state === 'ready' && answer.sunoAudioUrl !== null && selected !== null) {
          // "Selected one, from Suno": never another Generation's local file (#221).
          settle(
            nowPlayingOfStream(
              song,
              selected,
              { url: answer.sunoAudioUrl, pageUrl: answer.sunoPageUrl },
              'selected-generation',
            ),
          );
        } else if (answer.state === 'needs-choice') {
          // Only while this is still what was asked for: a later Play, or a close, wins.
          if (settle(undefined)) {
            onChoice(answer);
          }
        } else if (answer.state === 'selected-unplayable' && selected !== null) {
          settle({
            text: nothingToPlayText(
              `Nothing to play: ${song.title}’s Selected Generation, ${selected.shortcode},`,
              answer.reason,
            ),
            link: openInSuno(selected.sunoId === null ? null : sunoSongUrl(selected.sunoId)),
          });
        } else {
          settle({ text: songUnplayableText(answer.reason) });
        }
      };
      void read();
    },
    [ask],
  );

  const playChosen = useCallback(
    (song: PlayableSong, candidate: SongPlaybackCandidate, options?: PlayChosenOptions) => {
      const via = options?.selected === true ? 'selected-generation' : 'chosen';
      const settle = ask(song.id);
      if (candidate.kind === 'file') {
        settle(nowPlayingOfSong(song, candidate.audioFile, null, via), options?.notice);
        return;
      }
      void generationFile(candidate.generation).then((found) => {
        settle(
          'text' in found
            ? found
            : found.kind === 'suno'
              ? nowPlayingOfStream(song, candidate.generation, found.stream, via)
              : {
                  ...nowPlayingOfSong(song, found.file, candidate.generation, via),
                  sunoPageUrl: found.sunoPageUrl,
                },
          options?.notice,
        );
      });
    },
    [ask, generationFile],
  );

  const playFile = useCallback(
    (file: UnmatchedFile) => {
      start(nowPlayingOfFile(file));
    },
    [start],
  );

  /**
   * Loads `next` for a switch, seeks it to the pending switch's time once its length is known (its
   * beginning when it is shorter), and only then plays it (if the switch should) and lets it into the
   * A/B pair. Superseded meanwhile, it does nothing more.
   */
  const begin = useCallback(
    (next: NowPlaying, pending: PendingSwitch) => {
      switching.current = pending;
      disarm();
      current.current = next;
      reloadAt.current = null;
      const audio = element();
      audio.src = next.src;
      if (pending.play) {
        // A Suno stream must start within 15 seconds of the switch (#221).
        streamStarted.current = false;
        arm(SUNO_START_TIMEOUT_MS);
      }
      setState((previous) => ({
        ...previous,
        current: next,
        status: pending.play ? 'loading' : 'paused',
        position: pending.at,
        duration: null,
        notice: null,
        noticeLink: null,
        starting: null,
      }));
      const ready = () => {
        if (switching.current?.ticket !== pending.ticket) {
          return;
        }
        const target = carryTime(pending.at, durationOf(audio));
        const arrive = () => {
          if (switching.current?.ticket !== pending.ticket) {
            return;
          }
          switching.current = null;
          position.current = target;
          setState((previous) => ({ ...previous, position: target }));
          settled(next);
          if (pending.back !== null) {
            const failed = pending.back;
            setState((previous) => ({ ...previous, notice: switchFailedText(failed, next) }));
          }
          if (pending.play) {
            playElement();
          }
        };
        if (target > 0) {
          audio.addEventListener('seeked', arrive, { once: true });
          audio.currentTime = target;
        } else {
          arrive();
        }
      };
      audio.addEventListener('loadedmetadata', ready, { once: true });
    },
    [arm, disarm, element, playElement, settled],
  );

  const switchTo = useCallback(
    (next: NowPlaying, options: SwitchOptions) => {
      const pending = switching.current;
      // The time carried is that of the last source that actually played, never a pending one's.
      const from = pending?.from ?? current.current;
      if (from === null) {
        start(next);
        return;
      }
      if (pending === null && next.fileId === from.fileId) {
        return;
      }
      lookup.current += 1;
      switches.current += 1;
      const at = options.keepTime ? (pending?.at ?? position.current) : 0;
      const play = pending?.play ?? (!element().paused && reloadAt.current === null);
      begin(next, { ticket: switches.current, from, at, play, back: null });
    },
    [begin, element, start],
  );

  const pause = useCallback(() => {
    const pending = switching.current;
    if (pending !== null) {
      pending.play = false;
      disarm();
      setState((previous) => ({ ...previous, status: 'paused' }));
      return;
    }
    element().pause();
  }, [disarm, element]);

  const resume = useCallback(() => {
    const pending = switching.current;
    if (pending !== null) {
      pending.play = true;
      setState((previous) => ({ ...previous, status: 'loading' }));
      return;
    }
    const loaded = current.current;
    if (loaded === null) {
      return;
    }
    if (reloadAt.current !== null) {
      load(loaded, reloadAt.current);
      return;
    }
    setState((previous) => ({ ...previous, notice: null }));
    playElement();
  }, [load, playElement]);

  const toggle = useCallback(() => {
    const pending = switching.current;
    if (pending !== null ? !pending.play : element().paused || reloadAt.current !== null) {
      resume();
    } else {
      pause();
    }
  }, [element, pause, resume]);

  const seek = useCallback(
    (seconds: number) => {
      if (current.current === null || !Number.isFinite(seconds)) {
        return;
      }
      if (switching.current !== null) {
        switching.current.at = seconds;
      } else if (reloadAt.current !== null) {
        reloadAt.current = seconds;
      } else {
        element().currentTime = seconds;
      }
      position.current = seconds;
      setState((previous) => ({ ...previous, position: seconds }));
    },
    [element],
  );

  const setVolume = useCallback(
    (volume: number) => {
      const value = Math.min(1, Math.max(0, volume));
      element().volume = value;
      if (value > 0 && element().muted) {
        element().muted = false;
      }
      const muted = element().muted;
      storeVolume({ volume: value, muted });
      setState((previous) => ({ ...previous, volume: value, muted }));
    },
    [element],
  );

  const toggleMute = useCallback(() => {
    element().muted = !element().muted;
    const muted = element().muted;
    storeVolume({ volume: element().volume, muted });
    setState((previous) => ({ ...previous, muted }));
  }, [element]);

  const retry = useCallback(() => {
    const loaded = current.current;
    if (loaded !== null) {
      load(loaded, reloadAt.current ?? position.current);
    }
  }, [load]);

  const close = useCallback(() => {
    lookup.current += 1;
    disarm();
    switching.current = null;
    pair.current = NO_PAIR;
    current.current = null;
    reloadAt.current = null;
    position.current = 0;
    element().pause();
    element().removeAttribute('src');
    element().load();
    setState((previous) => ({
      ...previous,
      current: null,
      status: 'paused',
      position: 0,
      duration: null,
      notice: null,
      noticeLink: null,
      starting: null,
      previous: null,
    }));
  }, [disarm, element]);

  // The element lives in the page (so it can be found) for as long as the shell does; leaving the
  // signed-in app stops it.
  useEffect(() => {
    const audio = element();
    const signal = unmounted.current;
    const { volume, muted } = readVolume();
    audio.volume = volume;
    audio.muted = muted;
    audio.preload = 'auto';
    audio.dataset.testid = 'player-audio';
    holder.current?.appendChild(audio);
    return () => {
      signal.abort();
      unmounted.current = new AbortController();
      audio.pause();
      audio.removeAttribute('src');
      audio.load();
      audio.remove();
    };
  }, [element]);

  /**
   * What is loaded could not be played (the element's error, or a Suno stream that did not start or
   * stalled, #221). A switch goes back to the source before it (#220). A Suno stream stops asking
   * Suno, and the bar offers Open in Suno; a local file first checks the session.
   */
  const handleFailure = useCallback(() => {
    disarm();
    const failed = current.current;
    if (failed === null) {
      return;
    }
    const audio = element();
    const pending = switching.current;
    if (pending !== null && pending.back === null) {
      // The switch could not load or seek: back to the source before it, at its time (#220).
      switches.current += 1;
      begin(pending.from, { ...pending, ticket: switches.current, back: failed });
      return;
    }
    switching.current = null;
    const at = position.current;
    reloadAt.current = at;
    if (sourceOf(failed) === 'suno') {
      // Nothing of n8Tracks to check: say so, and stop the request to Suno. Play asks it again.
      setState((previous) => ({ ...previous, status: 'error', position: at }));
      audio.pause();
      audio.removeAttribute('src');
      audio.load();
      return;
    }
    const check = async () => {
      let ended = false;
      try {
        ended = (await fetchSession(unmounted.current.signal)) === null;
      } catch {
        ended = false;
      }
      if (current.current !== failed || unmounted.current.signal.aborted) {
        return;
      }
      setState((previous) => ({
        ...previous,
        status: ended ? 'paused' : 'error',
        position: at,
      }));
      if (ended) {
        // The shared fetch helper meets the 401 and brings up the in-place sign-in prompt.
        void apiFetch('api/v1/session', { signal: unmounted.current.signal }).catch(
          () => undefined,
        );
      }
    };
    void check();
  }, [begin, disarm, element]);

  useEffect(() => {
    failure.current = handleFailure;
  }, [handleFailure]);

  useEffect(() => disarm, [disarm]);

  // The element's own events are the truth about playback.
  useEffect(() => {
    const audio = element();
    const onPlay = () => {
      setState((previous) => ({ ...previous, status: 'playing' }));
      channel.current?.postMessage({ type: 'playing', tab });
    };
    const onPause = () => {
      // A switch's new source is not playing yet: the bar keeps what the switch will do.
      if (switching.current !== null) {
        return;
      }
      disarm();
      setState((previous) =>
        previous.status === 'error' ? previous : { ...previous, status: 'paused' },
      );
    };
    const onTime = () => {
      if (switching.current !== null || reloadAt.current !== null || current.current === null) {
        return;
      }
      position.current = audio.currentTime;
      setState((previous) => ({ ...previous, position: audio.currentTime }));
    };
    const onDuration = () => {
      setState((previous) => ({ ...previous, duration: durationOf(audio) }));
    };
    const onLoaded = () => {
      onDuration();
      // A source played any other way than by a switch joins the A/B pair once it loads.
      if (switching.current === null && current.current !== null) {
        settled(current.current);
      }
    };
    const onEnded = () => {
      disarm();
      // The player stops, back at the start: Play replays it. Nothing else starts.
      audio.currentTime = 0;
      position.current = 0;
      setState((previous) => ({ ...previous, status: 'paused', position: 0 }));
    };
    const onError = () => {
      if (audio.error !== null) {
        failure.current();
      }
    };
    const onPlaying = () => {
      // The Suno stream plays: it no longer needs to start (#221).
      streamStarted.current = true;
      disarm();
    };
    const onWaiting = () => {
      // A Suno stream that stops for want of data while playing has 30 seconds to carry on.
      if (streamStarted.current && !audio.paused) {
        arm(SUNO_STALL_TIMEOUT_MS);
      }
    };
    audio.addEventListener('play', onPlay);
    audio.addEventListener('playing', onPlaying);
    audio.addEventListener('waiting', onWaiting);
    audio.addEventListener('pause', onPause);
    audio.addEventListener('timeupdate', onTime);
    audio.addEventListener('durationchange', onDuration);
    audio.addEventListener('loadedmetadata', onLoaded);
    audio.addEventListener('ended', onEnded);
    audio.addEventListener('error', onError);
    return () => {
      audio.removeEventListener('play', onPlay);
      audio.removeEventListener('playing', onPlaying);
      audio.removeEventListener('waiting', onWaiting);
      audio.removeEventListener('pause', onPause);
      audio.removeEventListener('timeupdate', onTime);
      audio.removeEventListener('durationchange', onDuration);
      audio.removeEventListener('loadedmetadata', onLoaded);
      audio.removeEventListener('ended', onEnded);
      audio.removeEventListener('error', onError);
    };
  }, [element, tab, settled, arm, disarm]);

  // Starting playback in another tab of the app pauses this one; volume is not shared.
  useEffect(() => {
    if (typeof BroadcastChannel === 'undefined') {
      return undefined;
    }
    const shared = new BroadcastChannel(PLAYER_CHANNEL);
    shared.onmessage = (event: MessageEvent<unknown>) => {
      const message = event.data;
      if (isRecord(message) && message.type === 'playing' && message.tab !== tab) {
        element().pause();
      }
    };
    channel.current = shared;
    return () => {
      channel.current = null;
      shared.close();
    };
  }, [element, tab]);

  // Media keys and the lock screen: the title, and play and pause only.
  const title = state.current === null ? null : titleOf(state.current.label);
  useEffect(() => {
    if (!('mediaSession' in navigator)) {
      return undefined;
    }
    const session = navigator.mediaSession;
    if (title === null) {
      session.metadata = null;
      return undefined;
    }
    if (typeof MediaMetadata !== 'undefined') {
      session.metadata = new MediaMetadata({ title });
    }
    session.setActionHandler('play', resume);
    session.setActionHandler('pause', pause);
    return () => {
      session.setActionHandler('play', null);
      session.setActionHandler('pause', null);
    };
  }, [title, resume, pause]);

  const player = useMemo<Player>(
    () => ({
      state,
      playGeneration,
      playSong,
      playChosen,
      playFile,
      switchTo,
      pause,
      resume,
      toggle,
      seek,
      setVolume,
      toggleMute,
      retry,
      close,
    }),
    [
      state,
      playGeneration,
      playSong,
      playChosen,
      playFile,
      switchTo,
      pause,
      resume,
      toggle,
      seek,
      setVolume,
      toggleMute,
      retry,
      close,
    ],
  );

  return (
    <PlayerContext.Provider value={player}>
      <span ref={holder} hidden data-testid="player-audio-holder" />
      {children}
    </PlayerContext.Provider>
  );
}
