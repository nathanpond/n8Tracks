import { isSunoImageAddress } from './addresses.ts';

/**
 * The cover-image read (#152): the extension's one request to Suno that is not observation, and
 * the single file besides n8Tracks' own client and the page observer that the invariant 4 guard
 * lets send a request. It is a plain GET with no cookies, no authorization, and no headers of its
 * own, sent only to the hosts `addresses.ts` lists as Suno image hosts; it never follows a
 * redirect (which could leave those hosts). Nothing here logs, and the image is never read for
 * anything but its bytes.
 */

/**
 * Whether Suno's cover images can be read this way. Spike TS-003 found that they can (no cookies
 * or credentials, `Access-Control-Allow-Origin: *`); if that ever stops being true, this is set to
 * false and the extension skips images and says so in the panel.
 */
export const IMAGES_READABLE = true;

/** How long one image read may take. */
export const IMAGE_TIMEOUT_MS = 20_000;

/** The largest image read: n8Tracks takes no artwork over 25 MB. */
export const MAXIMUM_IMAGE_BYTES = 25 * 1024 * 1024;

/** A clip's cover: its Suno ID and the image address it reports. */
export interface Cover {
  sunoId: string;
  address: string;
}

/** Why an image could not be read. */
export type ImageFailure =
  /** The address is not on a listed Suno image host: nothing was requested. */
  | 'not-listed'
  /** No answer: the network, a block, a redirect, or the timeout. */
  | 'unreachable'
  /** The host answered with an error. */
  | 'refused'
  /** The answer was not an image, or was larger than n8Tracks takes. */
  | 'not-an-image';

export type ImageRead = { ok: true; image: Blob } | { ok: false; failure: ImageFailure };

export type ImageFetch = (input: string, init: RequestInit) => Promise<Response>;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** `address` as a listed Suno image address, or null. */
function listed(address: string): URL | null {
  try {
    const url = new URL(address);
    return isSunoImageAddress(url) ? url : null;
  } catch {
    return null;
  }
}

/**
 * A clip's cover, as the clip reports it: the large image when there is one, else the small one.
 * Null for a clip with no Suno ID or no image on a listed host.
 */
export function coverOf(clip: unknown): Cover | null {
  if (!isRecord(clip) || typeof clip.id !== 'string' || clip.id === '') {
    return null;
  }
  for (const field of ['image_large_url', 'image_url']) {
    const address = clip[field];
    if (typeof address === 'string' && listed(address) !== null) {
      return { sunoId: clip.id, address };
    }
  }
  return null;
}

/**
 * Reads the image at `address`: refused before any request unless it is on a listed Suno image
 * host, and sent with `credentials: 'omit'`, no headers, no referrer, and no redirects.
 */
export async function readImage(
  address: string,
  fetchImpl: ImageFetch = (input, init) => fetch(input, init),
): Promise<ImageRead> {
  const url = listed(address);
  if (url === null) {
    return { ok: false, failure: 'not-listed' };
  }
  let response: Response;
  try {
    response = await fetchImpl(url.href, {
      method: 'GET',
      mode: 'cors',
      credentials: 'omit',
      redirect: 'error',
      referrerPolicy: 'no-referrer',
      signal: AbortSignal.timeout(IMAGE_TIMEOUT_MS),
    });
  } catch {
    return { ok: false, failure: 'unreachable' };
  }
  if (!response.ok) {
    return { ok: false, failure: 'refused' };
  }
  const type = response.headers.get('Content-Type') ?? '';
  const length = Number(response.headers.get('Content-Length') ?? '0');
  if (!type.toLowerCase().startsWith('image/') || length > MAXIMUM_IMAGE_BYTES) {
    return { ok: false, failure: 'not-an-image' };
  }
  let image: Blob;
  try {
    image = await response.blob();
  } catch {
    return { ok: false, failure: 'unreachable' };
  }
  return image.size === 0 || image.size > MAXIMUM_IMAGE_BYTES
    ? { ok: false, failure: 'not-an-image' }
    : { ok: true, image };
}
