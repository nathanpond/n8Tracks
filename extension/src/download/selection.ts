import type { DownloadClip } from './clips.ts';

/**
 * The Download view's selection (#215): which clips, in which formats, and the file plan handed to
 * the downloader (#216), one entry per clip and format. Pure: no DOM, no browser, no network.
 *
 * Selections are kept by Suno ID, so they survive filter changes and a Refresh (for the clips still
 * there). A clip that cannot be selected (still generating, in Suno's Trash, failed, or without
 * audio) is never selected. Select all takes only the clips shown, and only once the list was read
 * to its end.
 */

/** The formats (TS-004): `m4a-stream` is the playback stream, which needs no Suno unlock. */
export type DownloadFormat = 'wav' | 'mp3' | 'm4a' | 'm4a-stream';

export interface FormatChoice {
  format: DownloadFormat;
  label: string;
  /** Whether a clip not yet unlocked spends one of the plan's downloads for it. */
  unlock: boolean;
  /** Said beside the choice. */
  note: string | null;
}

/** The formats offered, in the order shown. */
export const FORMATS: readonly FormatChoice[] = [
  { format: 'wav', label: 'WAV', unlock: true, note: null },
  { format: 'mp3', label: 'MP3', unlock: true, note: null },
  { format: 'm4a', label: 'M4A', unlock: true, note: null },
  {
    format: 'm4a-stream',
    label: 'M4A (streaming quality)',
    unlock: false,
    note: 'Lower quality; needs no Suno unlock.',
  },
];

export function isDownloadFormat(value: unknown): value is DownloadFormat {
  return FORMATS.some((choice) => choice.format === value);
}

/** One file to download: a clip in a format, as the downloader (#216) takes it. */
export interface PlanEntry {
  sunoId: string;
  /** Suno's title, or "Untitled". */
  title: string;
  /** The Suno creator's `display_name`. */
  displayName: string;
  /** The name of the Song's primary Artist in n8Tracks, or null. */
  artist: string | null;
  format: DownloadFormat;
  /** Whether the clip is already unlocked for download on Suno. */
  unlocked: boolean;
  /** The playback stream's address, which `m4a-stream` is downloaded from; null when unknown. */
  streamAddress: string | null;
}

/** What the list is narrowed to: a workspace (null for every one) and text in the title. */
export interface ListFilter {
  workspaceId: string | null;
  text: string;
}

export const NO_FILTER: ListFilter = { workspaceId: null, text: '' };

function matches(clip: DownloadClip, filter: ListFilter): boolean {
  if (filter.workspaceId !== null && clip.workspace?.id !== filter.workspaceId) {
    return false;
  }
  const wanted = filter.text.trim().toLocaleLowerCase();
  return wanted === '' || clip.title.toLocaleLowerCase().includes(wanted);
}

export class DownloadSelection {
  private clips: DownloadClip[] = [];
  private readonly byId = new Map<string, DownloadClip>();
  private readonly chosen = new Set<string>();
  private readonly formats = new Set<DownloadFormat>();
  private readComplete = false;

  /** Whether the list was read to its end, which Select all needs. */
  get complete(): boolean {
    return this.readComplete;
  }

  /** The clips read so far, in the order Suno listed them. */
  all(): readonly DownloadClip[] {
    return this.clips;
  }

  find(sunoId: string): DownloadClip | undefined {
    return this.byId.get(sunoId);
  }

  /**
   * Starts a new read: the list empties, and the selection is kept (or set to `selected`, the Suno
   * IDs carried over a Refresh) until the clips are read again.
   */
  restart(selected?: Iterable<string>): void {
    this.clips = [];
    this.byId.clear();
    this.readComplete = false;
    if (selected !== undefined) {
      this.chosen.clear();
      for (const id of selected) {
        this.chosen.add(id);
      }
    }
  }

  /** Adds clips as they are read; a clip read again replaces the earlier one. */
  add(clips: readonly DownloadClip[]): void {
    for (const clip of clips) {
      if (this.byId.has(clip.sunoId)) {
        this.clips = this.clips.map((other) => (other.sunoId === clip.sunoId ? clip : other));
      } else {
        this.clips.push(clip);
      }
      this.byId.set(clip.sunoId, clip);
      if (clip.unavailable !== null) {
        this.chosen.delete(clip.sunoId);
      }
    }
  }

  /**
   * The read ended: `complete` when the list was read to its end. Only then is a selected clip that
   * is no longer listed let go; after a partial read it may still be further down.
   */
  finish(complete: boolean): void {
    this.readComplete = complete;
    if (complete) {
      for (const id of [...this.chosen]) {
        if (!this.byId.has(id)) {
          this.chosen.delete(id);
        }
      }
    }
  }

  /** The clips the filter shows, in list order. */
  shown(filter: ListFilter): DownloadClip[] {
    return this.clips.filter((clip) => matches(clip, filter));
  }

  /** The workspaces the clips are in, by name, then ID. */
  workspaces(): { id: string; name: string }[] {
    const found = new Map<string, string>();
    for (const clip of this.clips) {
      if (clip.workspace !== null) {
        found.set(clip.workspace.id, clip.workspace.name);
      }
    }
    return [...found]
      .map(([id, name]) => ({ id, name }))
      .sort((a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id));
  }

  isSelected(sunoId: string): boolean {
    return this.chosen.has(sunoId) && this.byId.get(sunoId)?.unavailable === null;
  }

  /** Selects or lets go of one clip; false when it cannot be selected. */
  toggle(sunoId: string, selected: boolean): boolean {
    if (!selected) {
      this.chosen.delete(sunoId);
      return true;
    }
    if (this.byId.get(sunoId)?.unavailable !== null) {
      return false;
    }
    this.chosen.add(sunoId);
    return true;
  }

  /** Selects every selectable clip shown; nothing until the list was read to its end. */
  selectAllShown(filter: ListFilter): number {
    if (!this.readComplete) {
      return 0;
    }
    let added = 0;
    for (const clip of this.shown(filter)) {
      if (clip.unavailable === null && !this.chosen.has(clip.sunoId)) {
        this.chosen.add(clip.sunoId);
        added += 1;
      }
    }
    return added;
  }

  clear(): void {
    this.chosen.clear();
  }

  /** The selected clips that are listed, in list order. */
  selected(): DownloadClip[] {
    return this.clips.filter((clip) => this.isSelected(clip.sunoId));
  }

  /** Every selected Suno ID, listed yet or not: what a Refresh carries over. */
  selectedIds(): string[] {
    return [...this.chosen];
  }

  /** How many selected clips the filter hides. */
  hiddenByFilter(filter: ListFilter): number {
    return this.selected().filter((clip) => !matches(clip, filter)).length;
  }

  setFormat(format: DownloadFormat, chosen: boolean): void {
    if (chosen) {
      this.formats.add(format);
    } else {
      this.formats.delete(format);
    }
  }

  setFormats(formats: readonly DownloadFormat[]): void {
    this.formats.clear();
    for (const format of formats) {
      this.formats.add(format);
    }
  }

  /** The formats chosen, in the order offered. */
  chosenFormats(): DownloadFormat[] {
    return FORMATS.map((choice) => choice.format).filter((format) => this.formats.has(format));
  }

  /**
   * The chosen formats a clip can be had in. The playback stream needs the address in the clip
   * data; WAV, MP3, and M4A are offered for every finished clip, and one that fails is reported by
   * file (#216), since that cannot be known before trying (TS-004).
   */
  formatsFor(clip: DownloadClip): DownloadFormat[] {
    return this.chosenFormats().filter((format) => format !== 'm4a-stream' || clip.hasStream);
  }

  /** The files: each selected clip in each chosen format it can be had in. */
  plan(artistOf: (sunoId: string) => string | null = () => null): PlanEntry[] {
    return this.selected().flatMap((clip) =>
      this.formatsFor(clip).map((format) => ({
        sunoId: clip.sunoId,
        title: clip.title,
        displayName: clip.displayName,
        artist: artistOf(clip.sunoId),
        format,
        unlocked: clip.unlocked,
        streamAddress: clip.streamAddress ?? null,
      })),
    );
  }

  /**
   * How many Suno download unlocks the run uses: one per selected clip not yet unlocked when WAV,
   * MP3, or M4A is chosen (one unlock covers all three), none for the stream alone.
   */
  unlocksNeeded(): number {
    const paid = this.chosenFormats().some(
      (format) => FORMATS.find((choice) => choice.format === format)?.unlock === true,
    );
    return paid ? this.selected().filter((clip) => !clip.unlocked).length : 0;
  }
}
