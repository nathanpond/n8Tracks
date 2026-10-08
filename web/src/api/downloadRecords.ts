import { isRecord, useResource } from './songs';

/**
 * Download records (#222): the files the extension downloaded from Suno for a Generation's clip,
 * each with whether a scanned audio file of its name is in the media folder. A record says a file was
 * fetched to the user's computer, not that it is in the media folder.
 */

/** The formats a download is recorded in; `m4a-stream` is the playback stream. */
export type DownloadFormat = 'wav' | 'mp3' | 'm4a' | 'm4a-stream';

/** How a record's file name compares with the scanned audio files. */
export type DownloadMatch = 'attached' | 'elsewhere' | 'not-found';

/** The status a scanned file reports. */
export type ScannedStatus = 'available' | 'missing' | 'unavailable';

export interface GenerationDownload {
  id: string;
  sunoId: string;
  format: DownloadFormat;
  /** The base name the browser saved the file under. */
  fileName: string;
  completedAt: string;
  receivedAt: string;
  sizeBytes: number | null;
  /** Whether the run spent a Suno download unlock on the clip. */
  spentUnlock: boolean;
  mediaFolder: {
    match: DownloadMatch;
    audioFile: { id: string; status: ScannedStatus } | null;
  };
}

const FORMATS: readonly DownloadFormat[] = ['wav', 'mp3', 'm4a', 'm4a-stream'];
const MATCHES: readonly DownloadMatch[] = ['attached', 'elsewhere', 'not-found'];
const STATUSES: readonly ScannedStatus[] = ['available', 'missing', 'unavailable'];

function isGenerationDownload(value: unknown): value is GenerationDownload {
  if (!isRecord(value) || !isRecord(value.mediaFolder)) {
    return false;
  }
  const { audioFile, match } = value.mediaFolder;
  return (
    typeof value.id === 'string' &&
    typeof value.sunoId === 'string' &&
    FORMATS.includes(value.format as DownloadFormat) &&
    typeof value.fileName === 'string' &&
    typeof value.completedAt === 'string' &&
    typeof value.receivedAt === 'string' &&
    (value.sizeBytes === null || typeof value.sizeBytes === 'number') &&
    typeof value.spentUnlock === 'boolean' &&
    MATCHES.includes(match as DownloadMatch) &&
    (audioFile === null ||
      (isRecord(audioFile) &&
        typeof audioFile.id === 'string' &&
        STATUSES.includes(audioFile.status as ScannedStatus)))
  );
}

const acceptDownloads = (answer: unknown) =>
  isRecord(answer) && Array.isArray(answer.items) && answer.items.every(isGenerationDownload)
    ? answer.items
    : undefined;

/** A Generation's download records, newest first (at most 100). */
export function useGenerationDownloads(generationId: string) {
  return useResource(
    `api/v1/generations/${encodeURIComponent(generationId)}/downloads`,
    acceptDownloads,
  );
}

const FORMAT_LABELS: Record<DownloadFormat, string> = {
  wav: 'WAV',
  mp3: 'MP3',
  m4a: 'M4A',
  'm4a-stream': 'M4A (streaming quality)',
};

export function downloadFormatLabel(format: DownloadFormat): string {
  return FORMAT_LABELS[format];
}

/** Whether a scanned file of the record's name is in the media folder, in plain words. */
export function mediaFolderText(download: GenerationDownload): string {
  const { match, audioFile } = download.mediaFolder;
  const state =
    audioFile?.status === 'missing'
      ? ' (Missing: not found by the last scan)'
      : audioFile?.status === 'unavailable'
        ? ' (the media folder is unavailable)'
        : '';
  switch (match) {
    case 'attached':
      return `In media folder${state}`;
    case 'elsewhere':
      return `Found, not attached to this Generation${state}`;
    case 'not-found':
      return 'Not found in media folder';
  }
}
