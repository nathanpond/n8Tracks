import type { ArtworkCrop } from '../api/artwork';

/**
 * The square crop rules, as n8Tracks applies them (`ArtworkCropRules` in the API), in pixels of the
 * original with its orientation applied. A crop lies wholly inside the image and is at least
 * {@link MINIMUM_CROP} pixels on a side; an image under that on its shorter side can only be cropped
 * to its full shorter side. With no crop set, the centred square of the shorter side is shown.
 */

/** The smallest crop, in pixels of the original on a side. */
export const MINIMUM_CROP = 64;

/** The smallest side a crop of a `width` by `height` image may have. */
export function smallestCrop(width: number, height: number): number {
  return Math.min(MINIMUM_CROP, width, height);
}

/** The centred square of the shorter side: what is shown while no crop is set. */
export function centredCrop(width: number, height: number): ArtworkCrop {
  const size = Math.min(width, height);
  return { x: Math.floor((width - size) / 2), y: Math.floor((height - size) / 2), size };
}

/** Whether `crop` may be set on a `width` by `height` image. */
export function cropFits(crop: ArtworkCrop, width: number, height: number): boolean {
  const shorter = Math.min(width, height);
  return (
    (shorter >= MINIMUM_CROP || crop.size === shorter) &&
    crop.size >= smallestCrop(width, height) &&
    crop.x >= 0 &&
    crop.y >= 0 &&
    crop.x + crop.size <= width &&
    crop.y + crop.size <= height
  );
}

/**
 * A crop carried over to a new image ("Keep crop" when the artwork is replaced): the same crop when
 * it fits the new image, otherwise null, the centred default.
 */
export function keptCrop(
  crop: ArtworkCrop | null,
  width: number,
  height: number,
): ArtworkCrop | null {
  return crop !== null && cropFits(crop, width, height) ? crop : null;
}

/** `crop` made to fit: its side between the smallest and the shorter side, then moved inside the image. */
export function clampCrop(crop: ArtworkCrop, width: number, height: number): ArtworkCrop {
  const size = Math.round(
    Math.min(Math.max(crop.size, smallestCrop(width, height)), Math.min(width, height)),
  );
  return {
    x: Math.round(Math.min(Math.max(crop.x, 0), width - size)),
    y: Math.round(Math.min(Math.max(crop.y, 0), height - size)),
    size,
  };
}

/** One keyboard step: 1 percent of the shorter side, or 10 percent when `large`; at least a pixel. */
export function cropStep(width: number, height: number, large: boolean): number {
  return Math.max(1, Math.round((Math.min(width, height) * (large ? 10 : 1)) / 100));
}

/** `crop` moved by `dx` and `dy` pixels, stopping at the edges. */
export function moveCrop(
  crop: ArtworkCrop,
  dx: number,
  dy: number,
  width: number,
  height: number,
): ArtworkCrop {
  return clampCrop({ ...crop, x: crop.x + dx, y: crop.y + dy }, width, height);
}

/** `crop` grown (or, for a negative `delta`, shrunk) by `delta` pixels about its centre, within the limits and the edges. */
export function resizeCrop(
  crop: ArtworkCrop,
  delta: number,
  width: number,
  height: number,
): ArtworkCrop {
  const size = Math.min(
    Math.max(crop.size + delta, smallestCrop(width, height)),
    Math.min(width, height),
  );
  const shift = (size - crop.size) / 2;
  return clampCrop(
    { x: Math.round(crop.x - shift), y: Math.round(crop.y - shift), size },
    width,
    height,
  );
}

/** The selection in words, as the crop control states it. */
export function cropText(crop: ArtworkCrop): string {
  return `Left ${String(crop.x)} px, top ${String(crop.y)} px, size ${String(crop.size)} px.`;
}

/** Whether two crops are the same. */
export function sameCrop(a: ArtworkCrop | null, b: ArtworkCrop | null): boolean {
  return a === b || (a !== null && b !== null && a.x === b.x && a.y === b.y && a.size === b.size);
}
