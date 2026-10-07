/**
 * Suno's clips as the Download view lists them (#215), read from the library feed the reader takes
 * (`libraryReader.ts`, the same way a sync reads them). Only what the view shows and the file plan
 * needs is kept; the raw clip stays in the page.
 */

/** Why a clip cannot be selected, as the view says it. */
export const UNAVAILABLE = {
  generating: 'Still generating in Suno',
  trashed: "In Suno's Trash",
  failed: 'Suno reported an error for this clip',
  noAudio: 'Suno has no audio for this clip',
} as const;

/** What an untitled clip is called (#216's file names too). */
export const UNTITLED = 'Untitled';

/** A clip as the Download view shows it. */
export interface DownloadClip {
  sunoId: string;
  /** Suno's title, or {@link UNTITLED}. */
  title: string;
  /** The Suno creator's `display_name`, or an empty string. */
  displayName: string;
  durationSeconds: number | null;
  /** Suno's `created_at`, as sent, or null. */
  createdAt: string | null;
  workspace: { id: string; name: string } | null;
  /** `is_download_unlocked`: WAV, MP3, and M4A cost no unlock. */
  unlocked: boolean;
  /** Hidden in Suno (`is_hidden`): still selectable. */
  hidden: boolean;
  /** Why it cannot be selected, or null when it can. */
  unavailable: string | null;
  /** Whether the playback stream (`media_urls[0]`) is in the clip data. */
  hasStream: boolean;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function text(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

/** Suno's statuses of a clip still being made (TS-001). */
const GENERATING: ReadonlySet<string> = new Set(['submitted', 'queued', 'streaming']);

function streamOf(clip: Record<string, unknown>): boolean {
  const first: unknown = Array.isArray(clip.media_urls) ? clip.media_urls[0] : undefined;
  return isRecord(first) && typeof first.url === 'string' && first.url !== '';
}

function unavailableOf(clip: Record<string, unknown>, hasStream: boolean): string | null {
  if (clip.is_trashed === true) {
    return UNAVAILABLE.trashed;
  }
  const status = text(clip.status);
  if (GENERATING.has(status)) {
    return UNAVAILABLE.generating;
  }
  if (status === 'error') {
    return UNAVAILABLE.failed;
  }
  if (!hasStream) {
    return UNAVAILABLE.noAudio;
  }
  return null;
}

/** A clip from the library feed, or null when the record has no ID. */
export function clipOf(raw: unknown): DownloadClip | null {
  if (!isRecord(raw) || typeof raw.id !== 'string' || raw.id === '') {
    return null;
  }
  const metadata = isRecord(raw.metadata) ? raw.metadata : {};
  const project = isRecord(raw.project) ? raw.project : null;
  const hasStream = streamOf(raw);
  const title = text(raw.title).trim();
  const duration = metadata.duration;
  return {
    sunoId: raw.id,
    title: title === '' ? UNTITLED : title,
    displayName: text(raw.display_name),
    durationSeconds:
      typeof duration === 'number' && Number.isFinite(duration) && duration >= 0 ? duration : null,
    createdAt: typeof raw.created_at === 'string' ? raw.created_at : null,
    workspace:
      project !== null && typeof project.id === 'string' && project.id !== ''
        ? { id: project.id, name: text(project.name) }
        : null,
    unlocked: raw.is_download_unlocked === true,
    hidden: raw.is_hidden === true,
    unavailable: unavailableOf(raw, hasStream),
    hasStream,
  };
}

/** A duration as the view shows it: `m:ss`, or an empty string when unknown. */
export function durationText(seconds: number | null): string {
  if (seconds === null) {
    return '';
  }
  const whole = Math.round(seconds);
  return `${String(Math.floor(whole / 60))}:${String(whole % 60).padStart(2, '0')}`;
}

/** A created time as the view shows it: the date, `YYYY-MM-DD`, or an empty string. */
export function createdText(createdAt: string | null): string {
  if (createdAt === null) {
    return '';
  }
  const time = new Date(createdAt);
  return Number.isNaN(time.getTime()) ? '' : time.toISOString().slice(0, 10);
}
