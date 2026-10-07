import type { MatchReason, UnmatchedFile, UnmatchedReason } from '../api/audioFiles';

/** The folder a file is in, relative to the media folder; the top level when it is in no folder. */
export function folderText(file: Pick<UnmatchedFile, 'path' | 'fileName'>): string {
  const folder = file.path.slice(0, Math.max(0, file.path.length - file.fileName.length));
  const trimmed = folder.endsWith('/') ? folder.slice(0, -1) : folder;
  return trimmed === '' ? 'Top level' : trimmed;
}

/** A duration as `m:ss`; unknown when the header could not be read. */
export function clockText(seconds: number | null): string {
  if (seconds === null) {
    return 'Unknown';
  }
  const whole = Math.round(seconds);
  const minutes = Math.floor(whole / 60);
  return `${String(minutes)}:${String(whole % 60).padStart(2, '0')}`;
}

/** Why a file is unmatched, as a sentence, or null when there is nothing in particular to say. */
export function unmatchedReasonText(reason: UnmatchedReason | null): string | null {
  switch (reason) {
    case 'generation_deleted':
      return 'Its Generation was deleted.';
    case 'song_deleted':
      return 'Its Song was deleted.';
    case 'multiple_suno_ids':
      return 'Its name carries the Suno IDs of more than one Generation.';
    case 'unassociated_by_user':
      return 'You removed its association.';
    default:
      return null;
  }
}

const ARTIST_SOURCES: Record<string, string> = {
  file_name: 'is in the file name',
  folder: 'is in a folder name',
  embedded_artist: 'is the embedded artist',
};

/** One piece of evidence as the page says it. A code this build does not know is shown as written. */
export function reasonText(reason: MatchReason): string {
  const generation = reason.generation?.shortcode;
  switch (reason.code) {
    case 'title_equals_file_name':
      return 'Title matches the file name';
    case 'embedded_title_equals_title':
      return 'Title matches the embedded title';
    case 'generation_title_equals_file_name':
      return generation === undefined
        ? 'Suno title of more than one of its Generations matches the file name'
        : `Suno title of Generation ${generation} matches the file name`;
    case 'title_in_file_name':
      return 'Title is in the file name';
    case 'folder_equals_title':
      return `Folder “${reason.folder ?? ''}” matches the title`;
    case 'artist_present':
      return `Artist ${reason.artist ?? ''} ${ARTIST_SOURCES[reason.artistSource ?? ''] ?? 'is named'}`;
    case 'duration_close': {
      const apart =
        reason.differenceSeconds === undefined
          ? ''
          : ` (${reason.differenceSeconds.toLocaleString('en-US', { maximumFractionDigits: 1 })} s apart)`;
      return generation === undefined
        ? `Duration within 2 seconds of two of its Generations, equally${apart}`
        : `Duration within 2 seconds of Generation ${generation}${apart}`;
    }
    default:
      return reason.code;
  }
}
