import type { ArtworkCrop } from '../api/artwork';

/** The edit field every owner takes its artwork's crop in, as JSON (`{x, y, size}`), or null for the centred square. */
export const ARTWORK_CROP_KEY = 'artworkCrop';

/** An edit of an owner's artwork: the asset (null removes it) and the crop (null for the centred square), each only when given. */
export interface ArtworkEdit {
  assetId?: string | null;
  crop?: ArtworkCrop | null;
}

/** The crop as the save helper holds it: JSON, or null for the centred square. */
export function cropValue(crop: ArtworkCrop | null): string | null {
  return crop === null ? null : JSON.stringify({ x: crop.x, y: crop.y, size: crop.size });
}

/** The crop a saved value holds (see {@link cropValue}). */
export function cropOf(value: string | null): ArtworkCrop | null {
  return value === null ? null : (JSON.parse(value) as ArtworkCrop);
}
