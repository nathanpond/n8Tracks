import type { AudioFileStatus, UnmatchedFile } from '../api/audioFiles';
import { resolveAppUrl } from '../api/baseUrl';
import type { Generation } from '../api/generations';
import type { SongPlaybackFile } from '../api/songPlayback';
import type { SongPlayability } from '../api/songs';

/** Where this browser remembers the player's volume and mute (#218); what was playing is not kept. */
export const VOLUME_STORAGE_KEY = 'n8tracks.player.volume';

/** How far the seek bar's arrow keys move (seconds). */
export const SEEK_STEP_SECONDS = 5;

/** How far the seek bar's Page Up and Page Down keys move (seconds). */
export const SEEK_PAGE_SECONDS = 30;

/** The volume slider's step, in percent. */
export const VOLUME_STEP_PERCENT = 5;

/** The channel the app's open tabs share, so starting playback in one pauses the others. */
export const PLAYER_CHANNEL = 'n8tracks-player';

/** The volume (0 to 1) and mute state the player starts with, and keeps on this browser. */
export interface VolumeSetting {
  volume: number;
  muted: boolean;
}

export const DEFAULT_VOLUME: VolumeSetting = { volume: 1, muted: false };

const clampVolume = (value: number) => Math.min(1, Math.max(0, value));

/** The remembered volume, or the default when none is kept (or storage cannot be read). */
export function readVolume(): VolumeSetting {
  try {
    const text = window.localStorage.getItem(VOLUME_STORAGE_KEY);
    if (text === null) {
      return DEFAULT_VOLUME;
    }
    const value: unknown = JSON.parse(text);
    if (
      typeof value === 'object' &&
      value !== null &&
      'volume' in value &&
      'muted' in value &&
      typeof value.volume === 'number' &&
      Number.isFinite(value.volume) &&
      typeof value.muted === 'boolean'
    ) {
      return { volume: clampVolume(value.volume), muted: value.muted };
    }
    return DEFAULT_VOLUME;
  } catch {
    return DEFAULT_VOLUME;
  }
}

/** Keeps the volume on this browser; without storage (private mode) it is simply not remembered. */
export function storeVolume(setting: VolumeSetting): void {
  try {
    window.localStorage.setItem(
      VOLUME_STORAGE_KEY,
      JSON.stringify({ volume: clampVolume(setting.volume), muted: setting.muted }),
    );
  } catch {
    // Storage may be unavailable; the volume then starts at the default next time.
  }
}

/**
 * What the bar says is playing: a Generation's file (its Song, Version number, shortcode, and format),
 * a Song-level file (its Song and name), or a file with no Song (its name only).
 */
export type PlayLabel =
  | {
      kind: 'generation';
      song: { shortcode: string; title: string };
      versionNumber: string;
      shortcode: string;
      format: string;
      fileName: string;
    }
  | {
      kind: 'song-level';
      song: { shortcode: string; title: string };
      format: string;
      fileName: string;
    }
  | { kind: 'unmatched'; format: string; fileName: string };

/**
 * How a Song's Play (#219) came to play what it does: the Song's own choice, by its Song-level
 * preferred file or by its Selected Generation, or a Generation or Song-level file picked in the
 * chooser for this one listen.
 */
export type PlayVia = 'song-preferred' | 'selected-generation' | 'chosen';

/**
 * What is loaded in the player: the file, its address, the Generation and Song it belongs to (or
 * null), its label, and, when a Song's Play started it, how (`via`; null for a Generation's or a
 * file's own Play).
 */
export interface NowPlaying {
  fileId: string;
  src: string;
  generationId: string | null;
  songId: string | null;
  via: PlayVia | null;
  label: PlayLabel;
}

/** A Song as its Play control (#219) needs it: wherever Songs are listed, and on the Song page. */
export interface PlayableSong {
  id: string;
  shortcode: string;
  title: string;
  /** What Play does, as the list says; absent, Play asks the server when pressed. */
  playback?: SongPlayability;
}

/** What the bar says about how a Song's Play chose what plays. */
export function viaText(via: PlayVia): string {
  switch (via) {
    case 'song-preferred':
      return 'The Song’s choice: its Song-level file';
    case 'selected-generation':
      return 'The Song’s choice: its Selected Generation';
    case 'chosen':
      return 'Chosen for this listen';
  }
}

/**
 * What a Song's Play plays: the file the playback rule (or the chooser) named, labelled by the
 * Generation it belongs to, or as the Song's Song-level file when `generation` is null.
 */
export function nowPlayingOfSong(
  song: PlayableSong,
  file: Pick<SongPlaybackFile, 'id' | 'fileName' | 'format' | 'contentUrl'>,
  generation: { id: string; shortcode: string } | null,
  via: PlayVia,
): NowPlaying {
  const base = {
    fileId: file.id,
    src: resolveAppUrl(file.contentUrl).toString(),
    songId: song.id,
    via,
  };
  const owner = { shortcode: song.shortcode, title: song.title };
  return generation === null
    ? {
        ...base,
        generationId: null,
        label: { kind: 'song-level', song: owner, format: file.format, fileName: file.fileName },
      }
    : {
        ...base,
        generationId: generation.id,
        label: {
          kind: 'generation',
          song: owner,
          versionNumber: versionNumberOf(generation.shortcode),
          shortcode: generation.shortcode,
          format: file.format,
          fileName: file.fileName,
        },
      };
}

/**
 * The Version number a Version or Generation shortcode carries (`n8-12-v1.1-g3` → `1.1`). A Version
 * number never holds a `-`, so it runs from the last `-v` to the next `-`.
 */
export function versionNumberOf(shortcode: string): string {
  const start = shortcode.toLowerCase().lastIndexOf('-v');
  if (start < 0) {
    return '';
  }
  const rest = shortcode.slice(start + 2);
  const end = rest.indexOf('-');
  return end < 0 ? rest : rest.slice(0, end);
}

/** The address of a file's audio (#217), resolved against the app's base. */
export function contentUrlOf(fileId: string): string {
  return resolveAppUrl(`api/v1/audio-files/${encodeURIComponent(fileId)}/content`).toString();
}

/** What a file row plays: the file itself, labelled by what it is associated with. */
export function nowPlayingOfFile(file: UnmatchedFile): NowPlaying {
  const base = {
    fileId: file.id,
    src: contentUrlOf(file.id),
    songId: file.song?.id ?? null,
    via: null,
  };
  if (file.song !== null && file.generation !== null) {
    return {
      ...base,
      generationId: file.generation.id,
      label: {
        kind: 'generation',
        song: { shortcode: file.song.shortcode, title: file.song.title },
        versionNumber: versionNumberOf(file.generation.shortcode),
        shortcode: file.generation.shortcode,
        format: file.format,
        fileName: file.fileName,
      },
    };
  }
  if (file.song !== null) {
    return {
      ...base,
      generationId: null,
      label: {
        kind: 'song-level',
        song: { shortcode: file.song.shortcode, title: file.song.title },
        format: file.format,
        fileName: file.fileName,
      },
    };
  }
  return {
    ...base,
    generationId: null,
    label: { kind: 'unmatched', format: file.format, fileName: file.fileName },
  };
}

/** The file the playback read (#212) named for a Generation. */
export interface PlaybackFile {
  id: string;
  fileName: string;
  format: string;
  contentUrl: string;
}

/** What a Generation's Play plays: the file the playback rule named, labelled by the Generation. */
export function nowPlayingOfGeneration(
  generation: Generation,
  songTitle: string,
  file: PlaybackFile,
): NowPlaying {
  return {
    fileId: file.id,
    src: resolveAppUrl(file.contentUrl).toString(),
    generationId: generation.id,
    songId: generation.song.id,
    via: null,
    label: {
      kind: 'generation',
      song: { shortcode: generation.song.shortcode, title: songTitle },
      versionNumber: versionNumberOf(generation.shortcode),
      shortcode: generation.shortcode,
      format: file.format,
      fileName: file.fileName,
    },
  };
}

/** A format as the bar shows it: upper case (`WAV`, `M4A`). */
export function formatText(format: string): string {
  return format.toUpperCase();
}

/** The title the bar and the lock screen give what is playing: its Song's title, or the file's name. */
export function titleOf(label: PlayLabel): string {
  return label.kind === 'unmatched' ? label.fileName : label.song.title;
}

/** The line under the title: "Version 1.1 · n8-12-v1.1-g3 · WAV", "Song-level file · name", or the format. */
export function detailOf(label: PlayLabel): string {
  switch (label.kind) {
    case 'generation':
      return `Version ${label.versionNumber} · ${label.shortcode} · ${formatText(label.format)}`;
    case 'song-level':
      return `Song-level file · ${label.fileName}`;
    case 'unmatched':
      return formatText(label.format);
  }
}

/** A position or length as the bar shows it: `1:05`, or `1:02:05` past an hour; whole seconds, rounded down. */
export function clockText(seconds: number | null): string {
  if (seconds === null || !Number.isFinite(seconds)) {
    return '–:––';
  }
  const whole = Math.max(0, Math.floor(seconds));
  const hours = Math.floor(whole / 3600);
  const minutes = Math.floor((whole % 3600) / 60);
  const rest = String(whole % 60).padStart(2, '0');
  return hours > 0
    ? `${String(hours)}:${String(minutes).padStart(2, '0')}:${rest}`
    : `${String(minutes)}:${rest}`;
}

/** The seek bar's value in words: "1:05 of 3:07" (or "of unknown length" before the length is known). */
export function positionText(position: number, duration: number | null): string {
  return duration === null
    ? `${clockText(position)} of unknown length`
    : `${clockText(position)} of ${clockText(duration)}`;
}

/** Where a seek key moves the position from `position`, kept within the track; undefined for any other key. */
export function seekTarget(
  key: string,
  position: number,
  duration: number | null,
): number | undefined {
  const end = duration ?? position;
  const within = (value: number) => Math.min(end, Math.max(0, value));
  switch (key) {
    case 'ArrowRight':
    case 'ArrowUp':
      return within(position + SEEK_STEP_SECONDS);
    case 'ArrowLeft':
    case 'ArrowDown':
      return within(position - SEEK_STEP_SECONDS);
    case 'PageUp':
      return within(position + SEEK_PAGE_SECONDS);
    case 'PageDown':
      return within(position - SEEK_PAGE_SECONDS);
    case 'Home':
      return 0;
    case 'End':
      return end;
    default:
      return undefined;
  }
}

/**
 * Why a Generation's Play is disabled, in words, or undefined when it can play. Until Suno streaming
 * (#221) only local files play, so the reason names what is wrong with them.
 */
export function unplayableReason(generation: Generation): string | undefined {
  if (generation.playback.playable) {
    return undefined;
  }
  const files = generation.audioFiles;
  if (files.count === 0) {
    return 'Nothing to play: this Generation has no local audio file.';
  }
  if (files.unavailable > 0) {
    return 'Nothing to play: the media folder cannot be read.';
  }
  if (files.missing === files.count) {
    return files.count === 1
      ? 'Nothing to play: its audio file is Missing.'
      : 'Nothing to play: its audio files are Missing.';
  }
  return 'Nothing to play: none of its audio files can be played.';
}

/** Why a file row's Play is disabled, in words, or undefined when the file can play. */
export function unplayableFileReason(status: AudioFileStatus): string | undefined {
  switch (status) {
    case 'available':
      return undefined;
    case 'missing':
      return 'Cannot play: the file is Missing from the media folder.';
    case 'unavailable':
      return 'Cannot play: the media folder cannot be read.';
  }
}
