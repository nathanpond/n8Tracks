import { resolveAppUrl } from './baseUrl';
import { noticeMaintenance } from './maintenance';
import { ANTIFORGERY_HEADER } from './session';

/** Where artwork is uploaded, relative to the app's base. */
export const ARTWORK_PATH = 'api/v1/artwork';

/** The largest image n8Tracks accepts: 25 MB (25 × 1,024 × 1,024 bytes), as the API counts it. */
export const ARTWORK_MAXIMUM_BYTES = 25 * 1024 * 1024;

/** What the file picker offers; the API decides by the file's content, whatever it is called. */
export const ARTWORK_ACCEPT = 'image/jpeg,image/png,image/webp,.jpg,.jpeg,.png,.webp';

/** How long an upload may take before it is given up: an image can be 25 MB. */
export const ARTWORK_UPLOAD_TIMEOUT_MS = 120_000;

/** The URLs an asset is fetched from: the original, and each thumbnail size in pixels on the long side. */
export interface ArtworkUrls {
  original: string;
  '96': string;
  '320': string;
  '1024': string;
}

/** A square crop in pixels of the original: left, top, and side. */
export interface ArtworkCrop {
  x: number;
  y: number;
  size: number;
}

/** Where the artwork is fetched as a square, at each thumbnail size. */
export interface ArtworkSquareUrls {
  '96': string;
  '320': string;
  '1024': string;
}

/** An owner's artwork, as a Song, Album, Playlist, or Artist carries it. */
export interface Artwork {
  assetId: string;
  /** The original's width in pixels, its orientation applied: what a crop is measured in. */
  width: number;
  /** The original's height in pixels, its orientation applied. */
  height: number;
  /** The whole image: the original, and thumbnails by their long side. */
  urls: ArtworkUrls;
  /** The crop the owner set, or null for the centred square. */
  crop: ArtworkCrop | null;
  /**
   * The artwork as a square: the crop's own square thumbnails (their URLs change with the crop),
   * or, with no crop, the whole-image thumbnails, to be shown cut to their centred square.
   */
  squareUrls: ArtworkSquareUrls;
}

/**
 * Where the artwork a Song shows comes from (#121): its own (uploaded, or picked from a Generation
 * and so copied), or its Selected Generation's image, shown while it has none of its own.
 */
export type SongArtworkSource = 'own' | 'selectedGeneration';

/** The artwork a Song shows, with where it comes from. A Selected Generation's image has no crop. */
export interface SongArtwork extends Artwork {
  source: SongArtworkSource;
}

/** An uploaded image, as the upload answers it. */
export interface UploadedArtwork {
  id: string;
  width: number;
  height: number;
  urls: ArtworkUrls;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isArtworkUrls(value: unknown): value is ArtworkUrls {
  return (
    isRecord(value) &&
    typeof value.original === 'string' &&
    typeof value['96'] === 'string' &&
    typeof value['320'] === 'string' &&
    typeof value['1024'] === 'string'
  );
}

function isArtworkSquareUrls(value: unknown): value is ArtworkSquareUrls {
  return (
    isRecord(value) &&
    typeof value['96'] === 'string' &&
    typeof value['320'] === 'string' &&
    typeof value['1024'] === 'string'
  );
}

function isArtworkCrop(value: unknown): value is ArtworkCrop {
  return (
    isRecord(value) &&
    typeof value.x === 'number' &&
    typeof value.y === 'number' &&
    typeof value.size === 'number'
  );
}

/** The text alternative of an owner's artwork. */
export function artworkAlt(title: string): string {
  return `Artwork for ${title}`;
}

export function isArtwork(value: unknown): value is Artwork {
  return (
    isRecord(value) &&
    typeof value.assetId === 'string' &&
    typeof value.width === 'number' &&
    typeof value.height === 'number' &&
    isArtworkUrls(value.urls) &&
    (value.crop === null || isArtworkCrop(value.crop)) &&
    isArtworkSquareUrls(value.squareUrls)
  );
}

export function isSongArtwork(value: unknown): value is SongArtwork {
  return (
    isArtwork(value) &&
    isRecord(value) &&
    (value.source === 'own' || value.source === 'selectedGeneration')
  );
}

/** A Song's own artwork: what it shows when that is its own, null when it shows none or its Selected Generation's. */
export function ownArtwork(artwork: SongArtwork | null): SongArtwork | null {
  return artwork?.source === 'own' ? artwork : null;
}

function isUploadedArtwork(value: unknown): value is UploadedArtwork {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.width === 'number' &&
    typeof value.height === 'number' &&
    isArtworkUrls(value.urls)
  );
}

/** How an upload ended. A refusal carries the API's message, which says why. Never a rejection. */
export type ArtworkUploadResult =
  | { kind: 'uploaded'; artwork: UploadedArtwork }
  | { kind: 'refused'; message: string }
  | { kind: 'failed' };

/** What the user is told when n8Tracks gives no usable answer. */
export const UPLOAD_FAILED_MESSAGE =
  'The image could not be uploaded. Check that n8Tracks is running and try again.';

/**
 * Uploads `file` as artwork. n8Tracks judges it by its content: a file that is not really a JPEG,
 * PNG, or WebP image, or is damaged, too large, or too many pixels, is refused with a message saying
 * so, and nothing is stored. A file over the size limit is refused here, without sending it.
 * Identical bytes uploaded again answer the same asset.
 */
export async function uploadArtwork(file: File): Promise<ArtworkUploadResult> {
  if (file.size > ARTWORK_MAXIMUM_BYTES) {
    return { kind: 'refused', message: 'The file is larger than 25 MB, the most artwork can be.' };
  }
  const form = new FormData();
  form.append('file', file, file.name);
  const controller = new AbortController();
  const timeout = setTimeout(() => {
    controller.abort();
  }, ARTWORK_UPLOAD_TIMEOUT_MS);
  try {
    const response = await fetch(resolveAppUrl(ARTWORK_PATH), {
      method: 'POST',
      headers: { Accept: 'application/json', [ANTIFORGERY_HEADER]: '1' },
      body: form,
      signal: controller.signal,
    });
    await noticeMaintenance(response);
    let answer: unknown;
    try {
      answer = await response.json();
    } catch {
      answer = undefined;
    }
    if (response.ok) {
      return isUploadedArtwork(answer) ? { kind: 'uploaded', artwork: answer } : { kind: 'failed' };
    }
    if (
      [413, 415, 422].includes(response.status) &&
      isRecord(answer) &&
      typeof answer.title === 'string'
    ) {
      return { kind: 'refused', message: answer.title };
    }
    return { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  } finally {
    clearTimeout(timeout);
  }
}
