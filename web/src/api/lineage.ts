import type { OptionValue } from './createFields';
import { isRecord, useResource } from './songs';
import { isLineageKey, type VersionOptions } from './versions';

/**
 * A Version's lineage (#122) as the API spells it under the four lineage keys of `inputs`, and the
 * Suno playlists and personas the Sources editor (#125) offers. The editor keeps each key's value as
 * the API reads it (with what a read adds: shortcodes, titles, `availability`), so a read can be sent
 * back as it is; two values are the same edit when their {@link lineageText} is, which leaves out
 * everything the API ignores when it is sent.
 */

/** Whether a source can still be used, as the API computes it. */
export type Availability = 'ok' | 'not_imported' | 'deleted' | 'trashed' | 'missing';

/** The audio actions a Version may have, by the Suno action key their relationship type stands for. */
export const AUDIO_ACTIONS = ['cover', 'extend', 'mashup', 'sample', 'reuse_prompt'] as const;

export type AudioAction = (typeof AUDIO_ACTIONS)[number];

export function isAudioAction(value: unknown): value is AudioAction {
  return typeof value === 'string' && (AUDIO_ACTIONS as readonly string[]).includes(value);
}

/** How many sources an audio action takes: two for Mashup, one for the rest. */
export function sourcesFor(action: AudioAction): number {
  return action === 'mashup' ? 2 : 1;
}

/** Individual Inspiration takes up to four sources. */
export const INSPIRATION_MAXIMUM = 4;

/** A file note's description: 1 to 500 characters. */
export const FILE_DESCRIPTION_MAXIMUM_LENGTH = 500;

/** A Generation a source points at, as a read shows it. */
export interface GenerationTarget {
  id: string;
  shortcode: string | null;
  songId: string | null;
  songShortcode: string | null;
  songTitle: string | null;
  title?: string | null;
  durationSeconds?: number | null;
  missing?: boolean;
}

/** A Song a source points at, as a read shows it. */
export interface SongTarget {
  id: string;
  shortcode: string | null;
  title: string | null;
  missing?: boolean;
}

/** A Suno clip known only by its Suno ID. */
export interface ExternalTarget {
  sunoId: string;
  title: string | null;
  address: string | null;
  label?: string | null;
}

/**
 * One source as the API reads and takes it: an audio source has its relationship type (`typeId`,
 * with the Suno action it stands for), an Inspiration source has none; exactly one target; Extend's
 * position in seconds; any secondary identifiers Suno supplied.
 */
export interface LineageSource {
  typeId?: string;
  sunoAction?: string | null;
  generation?: GenerationTarget;
  song?: SongTarget;
  external?: ExternalTarget;
  availability?: Availability;
  continueAtSeconds?: number | null;
  secondaryIds?: Record<string, string> | null;
}

/** A Suno playlist used as Inspiration, with the clip IDs it held when chosen. */
export interface InspirationPlaylist {
  sunoPlaylistId: string;
  name: string;
  clipIds: string[];
}

/** Inspiration: up to four sources, or one playlist; never both. */
export interface Inspiration {
  sources?: LineageSource[];
  playlist?: InspirationPlaylist | null;
}

/** The Suno persona used as the Voice. */
export interface Voice {
  personaId: string;
  name: string;
}

export type FileKind = 'audio' | 'image' | 'video';

/** A file to attach by hand in Suno: n8Tracks keeps only this note. */
export interface FileInput {
  kind: FileKind;
  description: string;
}

/** A Version's whole lineage, each part as the API reads it, empty parts as empty. */
export interface Lineage {
  sources: LineageSource[];
  inspiration: Inspiration | null;
  voice: Voice | null;
  fileInputs: FileInput[];
}

export const NO_LINEAGE: Lineage = { sources: [], inspiration: null, voice: null, fileInputs: [] };

const FILE_ORDER: readonly FileKind[] = ['audio', 'image', 'video'];

function isFileKind(value: unknown): value is FileKind {
  return value === 'audio' || value === 'image' || value === 'video';
}

function sourcesOf(value: unknown): LineageSource[] {
  return Array.isArray(value)
    ? value.filter(
        (source): source is LineageSource =>
          isRecord(source) &&
          (isRecord(source.generation) || isRecord(source.song) || isRecord(source.external)),
      )
    : [];
}

function playlistOf(value: unknown): InspirationPlaylist | null {
  return isRecord(value) && typeof value.sunoPlaylistId === 'string'
    ? {
        sunoPlaylistId: value.sunoPlaylistId,
        name: typeof value.name === 'string' ? value.name : '',
        clipIds: Array.isArray(value.clipIds)
          ? value.clipIds.filter((clip): clip is string => typeof clip === 'string')
          : [],
      }
    : null;
}

/** The lineage a Version's `inputs` hold; a key left out (an older fixture, say) is empty. */
export function lineageOf(inputs: VersionOptions): Lineage {
  const values = inputs as Readonly<Record<string, unknown>>;
  const inspiration = values.inspiration;
  const playlist = isRecord(inspiration) ? playlistOf(inspiration.playlist) : null;
  const individual = isRecord(inspiration) ? sourcesOf(inspiration.sources) : [];
  const voice = values.voice;
  return {
    sources: sourcesOf(values.sources),
    inspiration:
      playlist !== null ? { playlist } : individual.length > 0 ? { sources: individual } : null,
    voice:
      isRecord(voice) && typeof voice.personaId === 'string'
        ? { personaId: voice.personaId, name: typeof voice.name === 'string' ? voice.name : '' }
        : null,
    fileInputs: Array.isArray(values.fileInputs)
      ? values.fileInputs.filter(
          (file): file is FileInput =>
            isRecord(file) && isFileKind(file.kind) && typeof file.description === 'string',
        )
      : [],
  };
}

/** The value of one lineage key of `lineage`, as `inputs` holds it. */
export function lineageValue(lineage: Lineage, key: keyof Lineage): OptionValue {
  // Lineage values are objects and arrays among the options' plain values: `inputs` holds both.
  return lineage[key] as unknown as OptionValue;
}

/** What a source is sent as: its type, its target by ID, and the rest the API stores. */
function sourceWritten(source: LineageSource, withType: boolean): Record<string, unknown> {
  const target = source.generation
    ? { generation: source.generation.id }
    : source.song
      ? { song: source.song.id }
      : {
          external: {
            sunoId: source.external?.sunoId ?? '',
            title: source.external?.title ?? null,
            address: source.external?.address ?? null,
          },
        };
  if (!withType) {
    return target;
  }
  const secondary = source.secondaryIds ?? null;
  return {
    typeId: source.typeId ?? null,
    ...target,
    continueAtSeconds: source.continueAtSeconds ?? null,
    secondaryIds:
      secondary === null
        ? null
        : Object.fromEntries(
            Object.entries(secondary).sort(([left], [right]) => (left < right ? -1 : 1)),
          ),
  };
}

/**
 * A lineage key's value as the API stores it, in one canonical text: what a read adds is left out
 * and an empty part is one form. Two values with the same text are no change to each other.
 */
export function lineageText(key: string, value: unknown): string {
  const lineage = lineageOf({ [key]: value } as unknown as VersionOptions);
  switch (key) {
    case 'sources':
      return JSON.stringify(lineage.sources.map((source) => sourceWritten(source, true)));
    case 'inspiration': {
      const inspiration = lineage.inspiration;
      return JSON.stringify(
        inspiration?.playlist
          ? { playlist: inspiration.playlist }
          : inspiration?.sources && inspiration.sources.length > 0
            ? { sources: inspiration.sources.map((source) => sourceWritten(source, false)) }
            : null,
      );
    }
    case 'voice':
      return JSON.stringify(lineage.voice);
    default:
      return JSON.stringify(
        [...lineage.fileInputs]
          .sort((left, right) => FILE_ORDER.indexOf(left.kind) - FILE_ORDER.indexOf(right.kind))
          .map((file) => ({ kind: file.kind, description: file.description })),
      );
  }
}

/** An option's or a lineage key's value as an edit holds it and conflicts compare it. */
export function inputText(key: string, value: OptionValue | undefined): string {
  return isLineageKey(key) ? lineageText(key, value) : JSON.stringify(value ?? null);
}

/** The Pro actions Suno marks (TS-002): labelled, never blocked. */
export const PRO_PARTS: ReadonlySet<'inspiration' | 'voice'> = new Set(['inspiration', 'voice']);

/** What a source points at, in words: its title when known, its Song's title, or the Suno clip. */
export function sourceTitle(source: LineageSource): string {
  if (source.generation) {
    return (
      source.generation.title ??
      source.generation.songTitle ??
      source.generation.shortcode ??
      'A Generation'
    );
  }
  if (source.song) {
    return source.song.title ?? source.song.shortcode ?? 'A Song';
  }
  const sunoId = source.external?.sunoId ?? '';
  return source.external?.title ?? `Suno clip ${sunoId.slice(0, 8)}`;
}

/** A source's shortcode in n8Tracks, or null when it is not in n8Tracks. */
export function sourceShortcode(source: LineageSource): string | null {
  return source.generation?.shortcode ?? source.song?.shortcode ?? null;
}

/** The label for a source that cannot be used as it is, or null when it can. */
export function availabilityLabel(source: LineageSource): string | null {
  switch (source.availability ?? (source.external ? 'not_imported' : 'ok')) {
    case 'not_imported':
      return 'Not imported';
    case 'deleted':
      return 'Deleted';
    case 'trashed':
      return 'In Suno Trash';
    case 'missing':
      return 'Remote Missing';
    default:
      return null;
  }
}

/** The length of the Generation a source points at, in seconds, when known. */
export function sourceDuration(source: LineageSource | undefined): number | null {
  return source?.generation?.durationSeconds ?? null;
}

/**
 * A Suno song address (`https://suno.com/song/<id>`) or a bare clip ID, as pasted: the clip's Suno
 * ID, or undefined when the text is neither.
 */
export function pastedSunoId(text: string): string | undefined {
  const trimmed = text.trim();
  const address = /^https?:\/\/(?:www\.)?suno\.com\/song\/([^/?#\s]+)\/?(?:[?#].*)?$/i.exec(
    trimmed,
  );
  const id = address ? decodeURIComponent(address[1] ?? '') : trimmed;
  return id !== '' && id.length <= 100 && !/[\s\p{Cc}/]/u.test(id) ? id : undefined;
}

/** A Suno playlist n8Tracks has seen in an import. */
export interface SunoPlaylist {
  id: string;
  name: string;
  memberCount: number;
  clipIds: string[];
  lastSeen: string;
}

/** A Suno persona (a Voice) n8Tracks has seen in an imported clip. */
export interface SunoPersona {
  id: string;
  name: string;
}

function isSunoPlaylist(value: unknown): value is SunoPlaylist {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.name === 'string' &&
    typeof value.memberCount === 'number' &&
    Array.isArray(value.clipIds) &&
    value.clipIds.every((clip) => typeof clip === 'string') &&
    typeof value.lastSeen === 'string'
  );
}

function isSunoPersona(value: unknown): value is SunoPersona {
  return isRecord(value) && typeof value.id === 'string' && typeof value.name === 'string';
}

const acceptPlaylists = (answer: unknown) =>
  isRecord(answer) && Array.isArray(answer.items) && answer.items.every(isSunoPlaylist)
    ? answer.items
    : undefined;

const acceptPersonas = (answer: unknown) =>
  isRecord(answer) && Array.isArray(answer.items) && answer.items.every(isSunoPersona)
    ? answer.items
    : undefined;

/** The Suno playlists seen in imports, by name; empty until an import has run. */
export function useSunoPlaylists() {
  return useResource('api/v1/suno/playlists', acceptPlaylists);
}

/** The Suno personas seen in imported clips, by name; empty until an import has run. */
export function useSunoPersonas() {
  return useResource('api/v1/suno/personas', acceptPersonas);
}

/** A lineage key's value as {@link lineageText} writes it, in a few words, for the conflict view. */
export function lineageSummary(key: string, text: string | null): string {
  const value: unknown = text === null ? null : JSON.parse(text);
  const lineage = lineageOf({ [key]: value as OptionValue });
  const count = (n: number, noun: string) => `${String(n)} ${noun}${n === 1 ? '' : 's'}`;
  switch (key) {
    case 'sources':
      return lineage.sources.length === 0 ? 'None' : count(lineage.sources.length, 'source');
    case 'inspiration': {
      const playlist = lineage.inspiration?.playlist;
      if (playlist) {
        return `Playlist ${playlist.name === '' ? playlist.sunoPlaylistId : playlist.name}`;
      }
      const songs = lineage.inspiration?.sources?.length ?? 0;
      return songs === 0 ? 'None' : count(songs, 'song');
    }
    case 'voice':
      return lineage.voice === null
        ? 'None'
        : lineage.voice.name === ''
          ? lineage.voice.personaId
          : lineage.voice.name;
    default:
      return lineage.fileInputs.length === 0
        ? 'None'
        : lineage.fileInputs.map((file) => `${file.kind}: ${file.description}`).join('; ');
  }
}
