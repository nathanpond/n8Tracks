import type { Artwork, SongArtwork } from '../api/artwork';
import { jsonResponse } from './helpers';

/** The ID the fake artwork store gives its `n`th upload, from 1. */
export function testAssetId(n: number): string {
  return `01a20000-0000-7000-8000-${String(n).padStart(12, '0')}`;
}

/** The dimensions the fake artwork store gives every upload unless told otherwise. */
export const TEST_ARTWORK_SIZE = { width: 1200, height: 600 };

/**
 * Artwork of the asset `assetId`, as a Song carries it: 1,200 × 600 with no crop unless `change`
 * says otherwise. A crop's square URLs name it, as the API's short hash does.
 */
export function testArtwork(
  assetId: string,
  change: Partial<Pick<SongArtwork, 'width' | 'height' | 'crop' | 'source'>> = {},
): SongArtwork {
  const original = `/api/v1/artwork/${assetId}`;
  const crop = change.crop ?? null;
  const square =
    crop === null
      ? original
      : `${original}/crops/${String(crop.x)}-${String(crop.y)}-${String(crop.size)}`;
  return {
    assetId,
    width: change.width ?? TEST_ARTWORK_SIZE.width,
    height: change.height ?? TEST_ARTWORK_SIZE.height,
    urls: {
      original,
      '96': `${original}/96`,
      '320': `${original}/320`,
      '1024': `${original}/1024`,
    },
    crop,
    squareUrls: { '96': `${square}/96`, '320': `${square}/320`, '1024': `${square}/1024` },
    // A Song's own unless the test says it shows its Selected Generation's (#121); other owners ignore it.
    source: change.source ?? 'own',
  };
}

/**
 * The fake artwork store an Album, Playlist, or Artist fake shares with the Song fake's rules: an
 * upload (`POST /api/v1/artwork`) is given {@link testAssetId} of its number, at
 * {@link TEST_ARTWORK_SIZE}; an owner's PATCH may name any asset uploaded so far, and a crop must
 * lie inside the artwork it applies to (422 otherwise).
 */
export function artworkFake() {
  const fake = {
    uploads: [] as File[],
    /** When set, answers the next upload (once) instead of the fake store. */
    nextUpload: undefined as (() => Response) | undefined,

    /** The answer to an upload request. */
    upload(init: RequestInit | undefined): Response {
      const file = init?.body instanceof FormData ? init.body.get('file') : null;
      if (file instanceof File) {
        fake.uploads.push(file);
      }
      const next = fake.nextUpload;
      if (next !== undefined) {
        fake.nextUpload = undefined;
        return next();
      }
      const id = testAssetId(fake.uploads.length);
      const { width, height, urls } = testArtwork(id, TEST_ARTWORK_SIZE);
      return jsonResponse(201, { id, width, height, urls });
    },

    /**
     * The owner's artwork after the `artworkAssetId` and `artworkCrop` in `edit` (each only when
     * sent), or the errors the API would answer with.
     */
    apply(
      current: Artwork | null,
      edit: Record<string, unknown>,
    ): { artwork: Artwork | null } | { errors: Record<string, string[]> } {
      let artwork = current;
      if (Object.hasOwn(edit, 'artworkAssetId')) {
        const assetId = edit.artworkAssetId;
        const known = Array.from({ length: fake.uploads.length }, (_, index) =>
          testAssetId(index + 1),
        );
        if (assetId === null) {
          artwork = null;
        } else if (typeof assetId === 'string' && known.includes(assetId)) {
          artwork = assetId === current?.assetId ? current : testArtwork(assetId);
        } else {
          return {
            errors: {
              artworkAssetId: [
                'There is no such artwork, or it was removed. Upload the image again.',
              ],
            },
          };
        }
      }
      if (Object.hasOwn(edit, 'artworkCrop')) {
        const crop = edit.artworkCrop as Artwork['crop'];
        const fits =
          crop === null ||
          (artwork !== null &&
            crop.x >= 0 &&
            crop.y >= 0 &&
            crop.x + crop.size <= artwork.width &&
            crop.y + crop.size <= artwork.height);
        if (!fits) {
          return { errors: { artworkCrop: ['The crop must lie inside the image.'] } };
        }
        if (artwork !== null) {
          artwork = testArtwork(artwork.assetId, { ...artwork, crop });
        }
      }
      return { artwork };
    },
  };
  return fake;
}

/** `edit` without the artwork fields, which {@link artworkFake}'s `apply` handles. */
export function withoutArtwork(edit: Record<string, unknown>): Record<string, unknown> {
  const rest = { ...edit };
  delete rest.artworkAssetId;
  delete rest.artworkCrop;
  return rest;
}
