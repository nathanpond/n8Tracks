import { createContext, useContext } from 'react';
import type { UnmatchedFile } from '../api/audioFiles';
import type { Generation } from '../api/generations';
import type { SongPlaybackAnswer, SongPlaybackCandidate } from '../api/songPlayback';
import type { NowPlaying, PlayableSong } from './playerRules';

/**
 * Where the player is: loading what was asked for (it is going to play), playing, paused (also once
 * the audio has ended, back at the start), or stopped by an error the bar names.
 */
export type PlayerStatus = 'loading' | 'playing' | 'paused' | 'error';

/** What the player bar and every Play control show. */
export interface PlayerState {
  /** What is loaded, or null before anything was played and after the bar is closed. */
  current: NowPlaying | null;
  status: PlayerStatus;
  /** Where playback is, in seconds. */
  position: number;
  /** The length in seconds, or null until the audio says. */
  duration: number | null;
  /** 0 to 1. */
  volume: number;
  muted: boolean;
  /** Why the last Play could not start (what was playing is left alone), or null. */
  notice: string | null;
  /** A link the notice offers (Open in Suno for a Selected Generation with nothing to play, #219), or null. */
  noticeLink: { label: string; href: string } | null;
  /** The Generation (or, for a Song's Play, the Song) whose file is being asked for, or null. */
  starting: string | null;
}

/** How a chooser pick is played: whether it was also made the Selected Generation, and a notice to show. */
export interface PlayChosenOptions {
  selected?: boolean;
  notice?: string;
}

/** A notice the bar shows when a Play cannot start, with a link it may offer. */
export interface PlayerNotice {
  text: string;
  link?: { label: string; href: string };
}

/** The app's one player (#218): its state, and what the bar and the Play controls do with it. */
export interface Player {
  state: PlayerState;
  /** Asks which file plays for the Generation (#212) and plays it from the start. */
  playGeneration: (generation: Generation, songTitle: string) => void;
  /**
   * Asks what Play on the Song does (#219, `GET /songs/{reference}/playback`, the one rule) and plays
   * the Song's choice: its Song-level preferred file, or its Selected Generation's file. When nothing
   * is selected it plays nothing and hands the answer (with the chooser's candidates) to `onChoice`;
   * when the Selected Generation has nothing to play the bar says so, with Open in Suno.
   */
  playSong: (song: PlayableSong, onChoice: (answer: SongPlaybackAnswer) => void) => void;
  /**
   * Plays what was picked in the chooser (a Generation, by its own playback rule, or a Song-level
   * file), labelled "Chosen for this listen", or as the Selected Generation when the pick was also
   * made the selection (`selected`); `notice`, if given, is shown once it starts.
   */
  playChosen: (
    song: PlayableSong,
    candidate: SongPlaybackCandidate,
    options?: PlayChosenOptions,
  ) => void;
  /** Plays this file from the start. */
  playFile: (file: UnmatchedFile) => void;
  pause: () => void;
  /** Plays what is loaded from where it is (reading it again if it had to stop). */
  resume: () => void;
  toggle: () => void;
  seek: (seconds: number) => void;
  /** 0 to 1; raising it from 0 unmutes. */
  setVolume: (volume: number) => void;
  toggleMute: () => void;
  /** Reads the file again after an error and plays on from where it stopped. */
  retry: () => void;
  /** Stops playback and hides the bar. */
  close: () => void;
}

export const PlayerContext = createContext<Player | null>(null);

/** The signed-in app's player, or null outside it (nothing there can play). */
export function usePlayer(): Player | null {
  return useContext(PlayerContext);
}
