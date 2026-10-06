import type { Generation } from '../api/generations';
import {
  INSPIRATION_MAXIMUM,
  isAudioAction,
  sourcesFor,
  type AudioAction,
  type FileInput,
  type FileKind,
  type InspirationPlaylist,
  type Lineage,
  type LineageSource,
  type Voice,
} from '../api/lineage';
import type { RelationshipType } from '../api/relationships';

/**
 * The Sources editor's rules (#125), as pure changes of a {@link Lineage}: each returns the lineage
 * the change leaves and, when it also removes something the user may not expect, what that is in
 * words, so the editor can ask first. The API checks the same rules again (#122).
 */

/** A change of the lineage, and what it removes besides what was asked (empty when nothing). */
export interface LineageChange {
  lineage: Lineage;
  removes: string[];
}

/** An audio action's relationship type: one whose Suno action is an audio action. */
export interface AudioType {
  id: string;
  name: string;
  action: AudioAction;
  /** How the action picker names it: a user's own type with the action it stands for (#126). */
  label: string;
}

/**
 * The relationship types a source's audio action can be chosen from, in the types' order: the
 * system types for the audio actions, then the user's own types mapped to one (#126).
 */
export function audioTypes(types: readonly RelationshipType[]): AudioType[] {
  return types.flatMap((type) =>
    isAudioAction(type.sunoAction)
      ? [
          {
            id: type.id,
            name: type.name,
            action: type.sunoAction,
            label: type.system ? type.name : `${type.name} (${actionName(type.sunoAction)})`,
          },
        ]
      : [],
  );
}

/** The audio action the sources stand for, from the first one with one; undefined for none. */
export function audioActionOf(lineage: Lineage): AudioAction | undefined {
  const action = lineage.sources.map((source) => source.sunoAction).find(isAudioAction);
  return action ?? undefined;
}

const ACTION_NAMES: Record<AudioAction, string> = {
  cover: 'Cover',
  extend: 'Extend',
  mashup: 'Mashup',
  sample: 'Sample This Song',
  reuse_prompt: 'Reuse Prompt',
};

/** An audio action in words, as Suno names it. */
export function actionName(action: AudioAction): string {
  return ACTION_NAMES[action];
}

function withoutInspiration(lineage: Lineage, removes: string[]): Lineage {
  if (lineage.inspiration === null) {
    return lineage;
  }
  removes.push('Inspiration (Suno does not take it with Cover)');
  return { ...lineage, inspiration: null };
}

function withoutAudioFile(lineage: Lineage, removes: string[]): Lineage {
  if (!lineage.fileInputs.some((file) => file.kind === 'audio')) {
    return lineage;
  }
  removes.push('the audio file note (Suno has one Audio slot)');
  return { ...lineage, fileInputs: lineage.fileInputs.filter((file) => file.kind !== 'audio') };
}

/**
 * Chooses the audio action `type` (undefined for none). The first source is kept, retyped; any the
 * new action cannot hold go, and so does Extend's position on another action. Choosing Cover removes
 * Inspiration, and any action removes an audio file note.
 */
export function chooseAudioAction(lineage: Lineage, type: AudioType | undefined): LineageChange {
  const removes: string[] = [];
  if (type === undefined) {
    if (lineage.sources.length > 0) {
      removes.push(lineage.sources.length === 1 ? 'the audio source' : 'both audio sources');
    }
    return { lineage: { ...lineage, sources: [] }, removes };
  }
  const kept = lineage.sources.slice(0, sourcesFor(type.action));
  if (kept.length < lineage.sources.length) {
    removes.push('the second source');
  }
  let next: Lineage = {
    ...lineage,
    sources: kept.map((source) => ({
      ...source,
      typeId: type.id,
      sunoAction: type.action,
      continueAtSeconds: type.action === 'extend' ? (source.continueAtSeconds ?? null) : null,
    })),
  };
  if (type.action === 'cover') {
    next = withoutInspiration(next, removes);
  }
  next = withoutAudioFile(next, removes);
  return { lineage: next, removes };
}

/** Puts `source` at `index` of the audio sources (at the end when `index` is past them), typed `type`. */
export function placeAudioSource(
  lineage: Lineage,
  type: AudioType,
  index: number,
  source: LineageSource,
): Lineage {
  const typed: LineageSource = {
    ...source,
    typeId: type.id,
    sunoAction: type.action,
    continueAtSeconds: type.action === 'extend' ? (source.continueAtSeconds ?? null) : null,
  };
  const sources = [...lineage.sources];
  if (index < sources.length) {
    sources[index] = typed;
  } else {
    sources.push(typed);
  }
  return { ...lineage, sources: sources.slice(0, sourcesFor(type.action)) };
}

/** Removes the audio source at `index`. */
export function removeAudioSource(lineage: Lineage, index: number): Lineage {
  return { ...lineage, sources: lineage.sources.filter((_, at) => at !== index) };
}

/** Sets Extend's position, in seconds, on the source at `index`. */
export function setContinueAt(lineage: Lineage, index: number, seconds: number | null): Lineage {
  return {
    ...lineage,
    sources: lineage.sources.map((source, at) =>
      at === index ? { ...source, continueAtSeconds: seconds } : source,
    ),
  };
}

/** The individual Inspiration sources, in order. */
export function inspirationSources(lineage: Lineage): LineageSource[] {
  return lineage.inspiration?.sources ?? [];
}

/** Whether another individual Inspiration source can be added: under four, and no playlist. */
export function canAddInspiration(lineage: Lineage): boolean {
  return !lineage.inspiration?.playlist && inspirationSources(lineage).length < INSPIRATION_MAXIMUM;
}

/** Puts `source` at `index` of the Inspiration sources (added at the end when past them); never a fifth. */
export function placeInspirationSource(
  lineage: Lineage,
  index: number,
  source: LineageSource,
): Lineage {
  const sources = [...inspirationSources(lineage)];
  const plain: LineageSource = {
    generation: source.generation,
    song: source.song,
    external: source.external,
    availability: source.availability,
  };
  if (index < sources.length) {
    sources[index] = plain;
  } else if (sources.length < INSPIRATION_MAXIMUM) {
    sources.push(plain);
  }
  return { ...lineage, inspiration: { sources } };
}

/** Removes the Inspiration source at `index`; Inspiration is none once the last goes. */
export function removeInspirationSource(lineage: Lineage, index: number): Lineage {
  const sources = inspirationSources(lineage).filter((_, at) => at !== index);
  return { ...lineage, inspiration: sources.length === 0 ? null : { sources } };
}

/** Moves the Inspiration source at `index` one place up (`-1`) or down (`1`). */
export function moveInspirationSource(lineage: Lineage, index: number, by: -1 | 1): Lineage {
  const sources = [...inspirationSources(lineage)];
  const to = index + by;
  const moving = sources[index];
  const other = sources[to];
  if (moving === undefined || other === undefined) {
    return lineage;
  }
  sources[index] = other;
  sources[to] = moving;
  return { ...lineage, inspiration: { sources } };
}

/** Uses `playlist` as the Inspiration (null for none); only when no individual source is chosen. */
export function choosePlaylist(lineage: Lineage, playlist: InspirationPlaylist | null): Lineage {
  return { ...lineage, inspiration: playlist === null ? null : { playlist } };
}

/** Sets the Voice (null for none). */
export function chooseVoice(lineage: Lineage, voice: Voice | null): Lineage {
  return { ...lineage, voice };
}

/** The file note of `kind`, if there is one. */
export function fileNote(lineage: Lineage, kind: FileKind): FileInput | undefined {
  return lineage.fileInputs.find((file) => file.kind === kind);
}

/** Adds or replaces the note of its kind. An audio file note removes the audio action. */
export function saveFileNote(lineage: Lineage, file: FileInput): LineageChange {
  const removes: string[] = [];
  let next: Lineage = {
    ...lineage,
    fileInputs: [...lineage.fileInputs.filter((other) => other.kind !== file.kind), file],
  };
  if (file.kind === 'audio' && next.sources.length > 0) {
    const action = audioActionOf(next);
    removes.push(`the audio action${action === undefined ? '' : ` (${actionName(action)})`}`);
    next = { ...next, sources: [] };
  }
  return { lineage: next, removes };
}

/** Removes the note of `kind`; removing the audio note leaves no audio action. */
export function removeFileNote(lineage: Lineage, kind: FileKind): Lineage {
  return { ...lineage, fileInputs: lineage.fileInputs.filter((file) => file.kind !== kind) };
}

/** A file note's description problem, or undefined when it can be saved. */
export function fileNoteError(description: string, maximum: number): string | undefined {
  if (description.trim() === '') {
    return 'Describe the file.';
  }
  return description.trim().length > maximum
    ? `Use at most ${maximum.toLocaleString('en-US')} characters.`
    : undefined;
}

/** A Generation chosen in the picker, as a source of it. */
export function generationSource(generation: Generation, songTitle: string): LineageSource {
  return {
    generation: {
      id: generation.id,
      shortcode: generation.shortcode,
      songId: generation.song.id,
      songShortcode: generation.song.shortcode,
      songTitle,
      title: generation.title,
      durationSeconds: generation.durationSeconds,
      missing: false,
    },
    availability:
      generation.remoteState === 'trashed'
        ? 'trashed'
        : generation.remoteState === 'missing'
          ? 'missing'
          : 'ok',
  };
}

/** A Suno clip known only by its pasted ID, as a source of it: Not imported until n8Tracks says otherwise. */
export function externalSource(sunoId: string): LineageSource {
  return {
    external: { sunoId, title: null, address: null, label: null },
    availability: 'not_imported',
  };
}

/** The Generation IDs a group's sources already name, so the picker does not offer them twice. */
export function chosenGenerationIds(sources: readonly LineageSource[]): string[] {
  return sources.flatMap((source) => (source.generation ? [source.generation.id] : []));
}

/** Seconds as the editor shows a position: minutes and seconds, seconds to at most two decimals. */
export function splitSeconds(total: number): { minutes: number; seconds: number } {
  const minutes = Math.floor(total / 60);
  return { minutes, seconds: Math.round((total - minutes * 60) * 100) / 100 };
}

/**
 * A position typed as minutes and seconds, in seconds, or an error: zero or more, to two decimals of
 * a second, and within the source's length when it is known (`duration`).
 */
export function continueAtOf(
  minutes: number | string,
  seconds: number | string,
  duration: number | null,
): { seconds: number } | { error: string } {
  const m = typeof minutes === 'number' ? minutes : minutes === '' ? 0 : Number(minutes);
  const s = typeof seconds === 'number' ? seconds : seconds === '' ? 0 : Number(seconds);
  if (!Number.isInteger(m) || m < 0 || !Number.isFinite(s) || s < 0 || s >= 60) {
    return { error: 'Give whole minutes and 0 to 59.99 seconds.' };
  }
  const total = Math.round((m * 60 + s) * 100) / 100;
  if (duration !== null && total > duration) {
    return { error: `The source is ${formatPosition(duration)} long: continue from within it.` };
  }
  return { seconds: total };
}

/** A position as `m:ss` (with hundredths when it has them). */
export function formatPosition(total: number): string {
  const { minutes, seconds } = splitSeconds(total);
  const whole = Math.floor(seconds);
  const hundredths = Math.round((seconds - whole) * 100);
  return `${String(minutes)}:${String(whole).padStart(2, '0')}${
    hundredths === 0 ? '' : `.${String(hundredths).padStart(2, '0')}`
  }`;
}
