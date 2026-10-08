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
import {
  PlayerContext,
  type PlayChosenOptions,
  type Player,
  type PlayerNotice,
  type PlayerState,
} from './playerContext';
import {
  nowPlayingOfFile,
  nowPlayingOfGeneration,
  nowPlayingOfSong,
  PLAYER_CHANNEL,
  readVolume,
  storeVolume,
  titleOf,
  type NowPlaying,
  type PlayableSong,
  type PlaybackFile,
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
  };
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
 */
export function PlayerProvider({ children }: { children: ReactNode }) {
  const audioElement = useRef<HTMLAudioElement | null>(null);
  /** The one audio element, made the first time it is needed and kept for the provider's life. */
  const element = useCallback(() => {
    audioElement.current ??= document.createElement('audio');
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

  const playElement = useCallback(() => {
    // Older browsers answer play() with nothing rather than a promise.
    Promise.resolve(element().play()).catch((error: unknown) => {
      // The browser refused to start without a gesture: it stays paused, ready for Play.
      if (error instanceof DOMException && error.name === 'NotAllowedError') {
        setState((previous) => ({ ...previous, status: 'paused' }));
      }
      // Anything else (a newer src, a pause) is answered by the element's own events.
    });
  }, [element]);

  const load = useCallback(
    (next: NowPlaying, at: number) => {
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
    [element, playElement],
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

  /** The file the playback rule (#212) names for a Generation, or a notice saying why there is none. */
  const generationFile = useCallback(
    async (generation: { id: string; shortcode: string }): Promise<PlaybackFile | PlayerNotice> => {
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
        if (answer.source === 'local' && file !== undefined) {
          return file;
        }
        return answer.source === 'none'
          ? {
              text: `Playback could not start: ${generation.shortcode} has no local audio file that can play.`,
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
        settle('text' in found ? found : nowPlayingOfGeneration(generation, songTitle, found));
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
          settle(
            nowPlayingOfSong(
              song,
              answer.audioFile,
              selected,
              selected === null ? 'song-preferred' : 'selected-generation',
            ),
          );
        } else if (answer.state === 'needs-choice') {
          // Only while this is still what was asked for: a later Play, or a close, wins.
          if (settle(undefined)) {
            onChoice(answer);
          }
        } else if (answer.state === 'selected-unplayable' && selected !== null) {
          settle({
            text: `Nothing to play: ${song.title}’s Selected Generation, ${selected.shortcode}, has no local audio file that can play.`,
            link:
              selected.sunoId === null
                ? undefined
                : { label: 'Open in Suno', href: sunoSongUrl(selected.sunoId) },
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
          'text' in found ? found : nowPlayingOfSong(song, found, candidate.generation, via),
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

  const pause = useCallback(() => {
    element().pause();
  }, [element]);

  const resume = useCallback(() => {
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
    if (element().paused || reloadAt.current !== null) {
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
      if (reloadAt.current !== null) {
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
    }));
  }, [element]);

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

  // The element's own events are the truth about playback.
  useEffect(() => {
    const audio = element();
    const onPlay = () => {
      setState((previous) => ({ ...previous, status: 'playing' }));
      channel.current?.postMessage({ type: 'playing', tab });
    };
    const onPause = () => {
      setState((previous) =>
        previous.status === 'error' ? previous : { ...previous, status: 'paused' },
      );
    };
    const onTime = () => {
      if (reloadAt.current !== null || current.current === null) {
        return;
      }
      position.current = audio.currentTime;
      setState((previous) => ({ ...previous, position: audio.currentTime }));
    };
    const onDuration = () => {
      setState((previous) => ({ ...previous, duration: durationOf(audio) }));
    };
    const onEnded = () => {
      // The player stops, back at the start: Play replays it. Nothing else starts.
      audio.currentTime = 0;
      position.current = 0;
      setState((previous) => ({ ...previous, status: 'paused', position: 0 }));
    };
    const onError = () => {
      const failed = current.current;
      if (failed === null || audio.error === null) {
        return;
      }
      const at = position.current;
      reloadAt.current = at;
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
    };
    audio.addEventListener('play', onPlay);
    audio.addEventListener('pause', onPause);
    audio.addEventListener('timeupdate', onTime);
    audio.addEventListener('durationchange', onDuration);
    audio.addEventListener('loadedmetadata', onDuration);
    audio.addEventListener('ended', onEnded);
    audio.addEventListener('error', onError);
    return () => {
      audio.removeEventListener('play', onPlay);
      audio.removeEventListener('pause', onPause);
      audio.removeEventListener('timeupdate', onTime);
      audio.removeEventListener('durationchange', onDuration);
      audio.removeEventListener('loadedmetadata', onDuration);
      audio.removeEventListener('ended', onEnded);
      audio.removeEventListener('error', onError);
    };
  }, [element, tab]);

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
