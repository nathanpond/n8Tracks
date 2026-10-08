import type { AssociationOrigin, AudioFileStatus, UnmatchedFile } from '../api/audioFiles';
import { formatDuration, type GenerationAudioFiles } from '../api/generations';

/** The folder of a file on a Song's lists (#211), relative to the media folder: `/` for its root. */
export function songFolderText(file: Pick<UnmatchedFile, 'path' | 'fileName'>): string {
  const folder = file.path.slice(0, Math.max(0, file.path.length - file.fileName.length));
  const trimmed = folder.endsWith('/') ? folder.slice(0, -1) : folder;
  return trimmed === '' ? '/' : trimmed;
}

/** A duration as `m:ss` (`h:mm:ss` from an hour); a dash when the header gave none. */
export function fileDurationText(seconds: number | null): string {
  return seconds === null ? '—' : formatDuration(seconds);
}

const BYTES_PER_MB = 1024 * 1024;

/** A size in MB to one decimal (1 MB being 1,048,576 bytes). */
export function sizeMbText(bytes: number): string {
  return `${(bytes / BYTES_PER_MB).toLocaleString('en-US', {
    minimumFractionDigits: 1,
    maximumFractionDigits: 1,
  })} MB`;
}

/** A file's status as a word. */
export function statusText(status: AudioFileStatus): string {
  switch (status) {
    case 'available':
      return 'Available';
    case 'missing':
      return 'Missing';
    default:
      return 'Unavailable';
  }
}

/** How a file was associated, as the Song's lists say it. */
export function originLabel(origin: AssociationOrigin | null): string {
  return origin === 'suno-id' ? 'By Suno ID' : 'By you';
}

/** A format as the lists name it: upper case. */
export function formatLabel(format: string): string {
  return format.toUpperCase();
}

/**
 * A Generation's local audio files in a few words (#211): "2 files · WAV, MP3 · 1 missing", with
 * "n unavailable" while the media folder cannot be read; empty when it has none.
 */
export function tallyText(tally: GenerationAudioFiles): string {
  if (tally.count === 0) {
    return '';
  }
  const parts = [
    tally.count === 1 ? '1 file' : `${String(tally.count)} files`,
    tally.formats.map(formatLabel).join(', '),
  ];
  if (tally.missing > 0) {
    parts.push(`${String(tally.missing)} missing`);
  }
  if (tally.unavailable > 0) {
    parts.push(`${String(tally.unavailable)} unavailable`);
  }
  return parts.filter((part) => part !== '').join(' · ');
}

/**
 * Whether a file of a Song's list plays now (#212): a Generation's file when it is what plays for its
 * Generation, a Song-level file when it is what plays for the Song.
 */
export function playsNow(file: UnmatchedFile): boolean {
  return file.generation === null ? file.playsForSong === true : file.playsForGeneration === true;
}

/** Who a file's choice belongs to, in words: "Generation n8-1-v1-g1" or "the Song". */
export function preferenceOwnerText(file: Pick<UnmatchedFile, 'generation'>): string {
  return file.generation === null ? 'the Song' : `Generation ${file.generation.shortcode}`;
}

/**
 * Why the preferred file is not the one playing (#212), or undefined when it plays (or is not
 * preferred): it is Missing or Unavailable, and what plays instead, from the same Song's list. For a
 * Generation, its best available file; for the Song, its Selected Generation's file.
 */
export function preferenceNote(
  file: UnmatchedFile,
  files: readonly UnmatchedFile[],
): string | undefined {
  if (!file.isPreferred || playsNow(file)) {
    return undefined;
  }
  const away = file.status === 'available' ? 'not playable' : statusText(file.status);
  if (file.generation === null) {
    const instead = files.find((other) => other.playsForSong === true);
    return instead === undefined
      ? `Preferred for the Song, but ${away}: no local file plays for the Song.`
      : `Preferred for the Song, but ${away}: ${instead.fileName}${
          instead.generation === null ? '' : ` (${instead.generation.shortcode})`
        } plays instead, from the Selected Generation.`;
  }
  const generationId = file.generation.id;
  const instead = files.find(
    (other) => other.generation?.id === generationId && other.playsForGeneration === true,
  );
  return instead === undefined
    ? `Preferred, but ${away}: no other file of this Generation is available, so none plays.`
    : `Preferred, but ${away}: ${instead.fileName} plays instead.`;
}
