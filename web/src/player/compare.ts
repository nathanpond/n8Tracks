import type { PlaybackSourceGeneration, PlaybackSources } from '../api/playbackSources';
import type { SongPlaybackFile } from '../api/songPlayback';
import {
  formatText,
  nowPlayingOfSong,
  nowPlayingOfStream,
  sourceOf,
  streamKeyOf,
  type NowPlaying,
  type PlaySource,
} from './playerRules';

/**
 * One source of a Song the player can switch to while comparing (#220): a file (its ID is the key),
 * the Generation it belongs to (null for a Song-level file), whether Previous and Next stop at it
 * (each Song-level file, and each Generation's playback file), and its name in the menu and the bar.
 * A Generation with no file available is one entry for its Suno stream (#221): `source` `suno`, its
 * key `suno:<Generation ID>`, and its `file` standing for the stream (Suno's address as `contentUrl`).
 */
export interface CompareEntry {
  key: string;
  file: SongPlaybackFile;
  generation: PlaybackSourceGeneration | null;
  stop: boolean;
  name: string;
  source: PlaySource;
}

/** What a Suno stream is called in the menu, the bar and notices: "n8-12-v1.1-g3 · Suno stream". */
export function streamName(shortcode: string): string {
  return `${shortcode} · Suno stream`;
}

/** The two sources A/B switches between: what is playing now and what played before it, from this Song. */
export interface ComparePair {
  current: NowPlaying | null;
  previous: NowPlaying | null;
}

export const NO_PAIR: ComparePair = { current: null, previous: null };

/** The keys the shortcuts answer to, by physical key (`KeyboardEvent.code`), whatever the layout. */
export const COMPARE_KEYS = {
  previous: 'BracketLeft',
  next: 'BracketRight',
  ab: 'Backslash',
} as const;

export type CompareAction = keyof typeof COMPARE_KEYS;

/** What each shortcut is called in the controls' descriptions. */
export const COMPARE_SHORTCUT_TEXT: Record<CompareAction, string> = {
  previous: 'Shortcut: [ (left bracket).',
  next: 'Shortcut: ] (right bracket).',
  ab: 'Shortcut: \\ (backslash).',
};

/**
 * Every source of the Song as one list in comparison order, as the server sends it: the Song-level
 * files, then each Generation's files (its playback file first), or its Suno stream when it has no
 * file (#221). A Generation's file is named by the Generation's shortcode and the format, with the
 * file name when the Generation has two files of that format; a stream by the shortcode and "Suno
 * stream"; a Song-level file by its file name.
 */
export function entriesOf(sources: PlaybackSources): CompareEntry[] {
  const songLevel = sources.songFiles.map<CompareEntry>((source) => ({
    key: source.audioFile.id,
    file: source.audioFile,
    generation: null,
    stop: true,
    name: source.audioFile.fileName,
    source: 'local',
  }));
  const ofGenerations = sources.generations.flatMap((generation) => {
    const stream = generation.sunoAudioUrl;
    if (generation.files.length === 0 && stream !== null) {
      const key = streamKeyOf(generation.generation.id);
      const name = streamName(generation.generation.shortcode);
      return [
        {
          key,
          file: {
            id: key,
            fileName: name,
            format: '',
            durationSeconds: generation.durationSeconds,
            contentUrl: stream,
          },
          generation,
          stop: true,
          name,
          source: 'suno',
        } satisfies CompareEntry,
      ];
    }
    return generation.files.map<CompareEntry>((source, index) => {
      const format = source.audioFile.format.toLowerCase();
      const twin =
        generation.files.filter((other) => other.audioFile.format.toLowerCase() === format).length >
        1;
      const name = `${generation.generation.shortcode} · ${formatText(source.audioFile.format)}`;
      return {
        key: source.audioFile.id,
        file: source.audioFile,
        generation,
        stop: index === 0,
        name: twin ? `${name} · ${source.audioFile.fileName}` : name,
        source: 'local',
      };
    });
  });
  return [...songLevel, ...ofGenerations];
}

/**
 * Where Previous (`-1`) or Next (`1`) goes from the file `current`: the stop before or after it,
 * wrapping at the ends. From a Generation's other file it moves relative to that Generation's stop;
 * from a file not in the list, to the first stop. Undefined when there is nowhere else to go.
 */
export function stepFrom(
  entries: readonly CompareEntry[],
  current: string | null,
  direction: 1 | -1,
): CompareEntry | undefined {
  const stops = entries.filter((entry) => entry.stop);
  if (stops.length === 0) {
    return undefined;
  }
  let at = stops.findIndex((entry) => entry.key === current);
  if (at < 0) {
    const owner = entries.find((entry) => entry.key === current)?.generation;
    at =
      owner === undefined || owner === null
        ? -1
        : stops.findIndex((entry) => entry.generation?.generation.id === owner.generation.id);
  }
  if (at < 0) {
    return stops[0]?.key === current ? undefined : stops[0];
  }
  const target = stops[(at + direction + stops.length) % stops.length];
  return target === undefined || target.key === current ? undefined : target;
}

/**
 * Where a switch starts the new source: the time carried over, or its beginning when the new audio is
 * shorter than that time (its length is known only once it loads; null while unknown).
 */
export function carryTime(at: number, duration: number | null): number {
  if (!Number.isFinite(at) || at <= 0) {
    return 0;
  }
  return duration !== null && at >= duration ? 0 : at;
}

/**
 * The A/B pair once `played` has actually played (loaded and, for a switch, reached its time): it
 * becomes the current source, and the one before it is kept when both are from the same Song; a
 * different Song forgets the pair. Playing the current source again changes nothing.
 */
export function nextPair(pair: ComparePair, played: NowPlaying): ComparePair {
  const { current } = pair;
  if (current !== null && current.fileId === played.fileId) {
    return { current: played, previous: pair.previous };
  }
  const sameSong = current !== null && played.songId !== null && current.songId === played.songId;
  return { current: played, previous: sameSong ? current : null };
}

/**
 * What switching to `entry` loads: its file, labelled by its Generation (or as the Song's Song-level
 * file). After a Song's Play the bar says it was chosen for this listen; after a Generation's or a
 * file's own Play, nothing is said about a rule.
 */
export function nowPlayingOfEntry(
  song: PlaybackSources['song'],
  entry: CompareEntry,
  from: NowPlaying | null,
): NowPlaying {
  const generation =
    entry.generation === null
      ? null
      : { id: entry.generation.generation.id, shortcode: entry.generation.generation.shortcode };
  const via = (from?.via ?? null) === null ? null : 'chosen';
  if (entry.source === 'suno' && generation !== null) {
    return nowPlayingOfStream(
      song,
      generation,
      { url: entry.file.contentUrl, pageUrl: entry.generation?.sunoPageUrl ?? null },
      via,
    );
  }
  const next = nowPlayingOfSong(song, entry.file, generation, 'chosen');
  return { ...next, via, sunoPageUrl: entry.generation?.sunoPageUrl ?? null };
}

/**
 * What a loaded source is called in a notice: "n8-12-v1.1-g3 · WAV", "n8-12-v1.1-g3 · Suno stream"
 * (#221), or a Song-level file's name.
 */
export function sourceNameOf(playing: NowPlaying): string {
  const { label } = playing;
  if (label.kind !== 'generation') {
    return label.fileName;
  }
  return sourceOf(playing) === 'suno'
    ? streamName(label.shortcode)
    : `${label.shortcode} · ${formatText(label.format)}`;
}

/** What the bar says when a switch could not load or seek, and the player went back. */
export function switchFailedText(failed: NowPlaying, back: NowPlaying): string {
  return `${sourceNameOf(failed)} could not be played, so the player went back to ${sourceNameOf(back)}.`;
}

/** A Generation as the menu groups its files: "Version 1.1 · n8-12-v1.1-g3 · 4 stars" (or "not rated"). */
export function generationHeading(generation: PlaybackSourceGeneration): string {
  const rating =
    generation.rating === null
      ? 'not rated'
      : generation.rating === 1
        ? '1 star'
        : `${String(generation.rating)} stars`;
  return `Version ${generation.versionNumber} · ${generation.generation.shortcode} · ${rating}`;
}

/** Which shortcut a key press is, or undefined: only the bare key, never auto-repeat or with a modifier held. */
export function shortcutOf(event: {
  code: string;
  repeat: boolean;
  altKey: boolean;
  ctrlKey: boolean;
  metaKey: boolean;
  shiftKey: boolean;
}): CompareAction | undefined {
  if (event.repeat || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) {
    return undefined;
  }
  const found = (Object.keys(COMPARE_KEYS) as CompareAction[]).find(
    (action) => COMPARE_KEYS[action] === event.code,
  );
  return found;
}

const TEXT_INPUT_TYPES = new Set([
  'text',
  'search',
  'email',
  'url',
  'tel',
  'password',
  'number',
  'date',
  'datetime-local',
  'month',
  'time',
  'week',
]);

/** Whether focus is where typing goes (a text field, a text area, a list box, or the editor), so a shortcut must not fire. */
export function isTypingTarget(target: EventTarget | null): boolean {
  if (!(target instanceof Element)) {
    return false;
  }
  if (target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement) {
    return true;
  }
  if (target instanceof HTMLInputElement) {
    return TEXT_INPUT_TYPES.has(target.type.toLowerCase());
  }
  if (target instanceof HTMLElement && target.isContentEditable) {
    return true;
  }
  return (
    target.closest(
      '[contenteditable="true"], [contenteditable=""], [role="textbox"], .cm-editor',
    ) !== null
  );
}
