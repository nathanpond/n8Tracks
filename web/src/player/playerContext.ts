import { createContext, useContext } from 'react';
import type { UnmatchedFile } from '../api/audioFiles';
import type { Generation } from '../api/generations';
import type { NowPlaying } from './playerRules';

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
  /** The Generation whose file is being asked for, or null. */
  starting: string | null;
}

/** The app's one player (#218): its state, and what the bar and the Play controls do with it. */
export interface Player {
  state: PlayerState;
  /** Asks which file plays for the Generation (#212) and plays it from the start. */
  playGeneration: (generation: Generation, songTitle: string) => void;
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
