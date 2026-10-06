import type { Artwork, ArtworkCrop } from '../api/artwork';
import type { FieldValue } from '../api/saves';
import { cropText } from './cropRules';
import type { SavedField } from './useRevisionedSave';

/** The edit field every owner takes its artwork in: an uploaded asset's ID, or null for none. */
export const ARTWORK_KEY = 'artworkAssetId';

/** The edit field every owner takes its artwork's crop in, as JSON (`{x, y, size}`), or null for the centred square. */
export const ARTWORK_CROP_KEY = 'artworkCrop';

/** An edit of an owner's artwork: the asset (null removes it) and the crop (null for the centred square), each only when given. */
export interface ArtworkEdit {
  assetId?: string | null;
  crop?: ArtworkCrop | null;
}

/** The artwork fields of an owner's PATCH, each only when sent. */
export interface ArtworkPatch {
  artworkAssetId?: string | null;
  artworkCrop?: ArtworkCrop | null;
}

/** The crop as the save helper holds it: JSON, or null for the centred square. */
export function cropValue(crop: ArtworkCrop | null): string | null {
  return crop === null ? null : JSON.stringify({ x: crop.x, y: crop.y, size: crop.size });
}

/** The crop a saved value holds (see {@link cropValue}). */
export function cropOf(value: string | null): ArtworkCrop | null {
  return value === null ? null : (JSON.parse(value) as ArtworkCrop);
}

/**
 * An owner's two artwork fields for the shared save helper (its asset and its crop), named for the
 * conflict dialog. An Album, Playlist, or Artist adds them to its own fields.
 */
export function artworkFields<T extends { artwork: Artwork | null }>(): SavedField<T>[] {
  return [
    {
      key: ARTWORK_KEY,
      label: 'Artwork',
      read: (record) => record.artwork?.assetId ?? null,
      show: (value) => (value === null ? 'None' : 'An uploaded image'),
    },
    {
      key: ARTWORK_CROP_KEY,
      label: 'Artwork crop',
      read: (record) => cropValue(record.artwork?.crop ?? null),
      show: (value) => {
        const crop = cropOf(value);
        return crop === null ? 'Centred' : cropText(crop);
      },
    },
  ];
}

/** What the artwork picker saves, as the shared save helper's field values. */
export function artworkValues(edit: ArtworkEdit): Record<string, FieldValue> {
  return {
    ...(edit.assetId === undefined ? {} : { [ARTWORK_KEY]: edit.assetId }),
    ...(edit.crop === undefined ? {} : { [ARTWORK_CROP_KEY]: cropValue(edit.crop) }),
  };
}

/** The artwork fields of an owner's PATCH, from what the shared save helper holds. */
export function artworkPatchOf(edit: Readonly<Record<string, FieldValue>>): ArtworkPatch {
  const asset = edit[ARTWORK_KEY];
  const crop = edit[ARTWORK_CROP_KEY];
  return {
    ...(asset === undefined ? {} : { artworkAssetId: asset }),
    ...(crop === undefined ? {} : { artworkCrop: cropOf(crop) }),
  };
}
