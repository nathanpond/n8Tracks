import { describe, expect, it, vi } from 'vitest';
import { sunoObject } from '../testing/sunoResponses.ts';
import { isSunoImageAddress, SUNO_IMAGE_HOSTS } from './addresses.ts';
import {
  coverOf,
  IMAGES_READABLE,
  MAXIMUM_IMAGE_BYTES,
  readImage,
  type ImageFetch,
} from './imageReader.ts';

const LARGE = 'https://cdn2.suno.ai/image_large_00000000-0000-4000-8000-000000000001.jpeg';
const SMALL = 'https://cdn2.suno.ai/image_00000000-0000-4000-8000-000000000001.jpeg';

function image(type = 'image/jpeg', bytes = 16, headers: Record<string, string> = {}): Response {
  return new Response(new Uint8Array(bytes), {
    status: 200,
    headers: { 'Content-Type': type, ...headers },
  });
}

/** Every clip in the TS-003 library and Trash fixtures. */
function fixtureClips(): Record<string, unknown>[] {
  return [
    'feed-v3.library-page-1.response',
    'feed-v3.library-page-2.response',
    'clips-trashed-v2.response',
  ].flatMap((name) => sunoObject(name).clips as Record<string, unknown>[]);
}

describe('the Suno image hosts', () => {
  it('are the hosts of every image address in the TS-003 fixtures', () => {
    const hosts = new Set(
      fixtureClips().flatMap((clip) =>
        ['image_url', 'image_large_url']
          .map((field) => clip[field])
          .filter((value) => typeof value === 'string')
          .map((value) => new URL(value).hostname),
      ),
    );

    expect(hosts.size).toBeGreaterThan(0);
    expect([...hosts]).toEqual([...SUNO_IMAGE_HOSTS]);
  });

  it.each([
    [LARGE, true],
    ['http://cdn2.suno.ai/image_1.jpeg', false],
    ['https://cdn2.suno.ai:8443/image_1.jpeg', false],
    ['https://user:secret@cdn2.suno.ai/image_1.jpeg', false],
    ['https://cdn1.suno.ai/image_1.jpeg', false],
    ['https://suno.com/image_1.jpeg', false],
    ['https://cdn2.suno.ai.example.com/image_1.jpeg', false],
    ['https://example.com/cdn2.suno.ai/image_1.jpeg', false],
  ])('%s is a listed image address: %s', (address, listed) => {
    expect(isSunoImageAddress(new URL(address))).toBe(listed);
  });

  it('spike TS-003 found that the images can be read without credentials', () => {
    expect(IMAGES_READABLE).toBe(true);
  });
});

describe("a clip's cover", () => {
  it('is the large image when there is one, else the small one', () => {
    expect(coverOf({ id: 'c1', image_url: SMALL, image_large_url: LARGE })).toEqual({
      sunoId: 'c1',
      address: LARGE,
    });
    expect(coverOf({ id: 'c1', image_url: SMALL, image_large_url: null })).toEqual({
      sunoId: 'c1',
      address: SMALL,
    });
    expect(
      coverOf({ id: 'c1', image_url: SMALL, image_large_url: 'https://example.com/x.jpeg' }),
    ).toEqual({ sunoId: 'c1', address: SMALL });
  });

  it('is none for a clip with no image, no Suno ID, or an image on another host', () => {
    expect(coverOf({ id: 'c1' })).toBeNull();
    expect(coverOf({ image_url: SMALL })).toBeNull();
    expect(coverOf({ id: '', image_url: SMALL })).toBeNull();
    expect(coverOf({ id: 'c1', image_url: 'https://example.com/image.jpeg' })).toBeNull();
    expect(coverOf({ id: 'c1', image_url: 'not an address' })).toBeNull();
    expect(coverOf(null)).toBeNull();
  });

  it('is found for every fixture clip that has an image', () => {
    const clips = fixtureClips();
    const covers = clips.map(coverOf);

    expect(covers.every((cover) => cover !== null)).toBe(true);
    expect(covers.map((cover) => cover?.sunoId)).toEqual(clips.map((clip) => clip.id));
  });
});

describe('reading an image', () => {
  it('sends a plain GET with no credentials, no headers, no referrer, and no redirects', async () => {
    const fetchImpl = vi.fn<ImageFetch>(() => Promise.resolve(image()));

    const read = await readImage(LARGE, fetchImpl);

    expect(read.ok).toBe(true);
    expect(fetchImpl).toHaveBeenCalledTimes(1);
    const [address, init] = fetchImpl.mock.calls[0] ?? [];
    expect(address).toBe(LARGE);
    expect(init).toMatchObject({
      method: 'GET',
      mode: 'cors',
      credentials: 'omit',
      redirect: 'error',
      referrerPolicy: 'no-referrer',
    });
    // No authorization, and no header of any kind.
    expect(init?.headers).toBeUndefined();
    expect(Object.keys(init ?? {}).toSorted()).toEqual([
      'credentials',
      'method',
      'mode',
      'redirect',
      'referrerPolicy',
      'signal',
    ]);
  });

  it.each([
    'https://example.com/image.jpeg',
    'https://suno.com/api/feed/v3',
    'https://studio-api-prod.suno.com/api/clips',
    'http://cdn2.suno.ai/image_1.jpeg',
    'https://cdn2.suno.ai:444/image_1.jpeg',
    'not an address',
  ])('never requests %s, which is not on a listed Suno image host', async (address) => {
    const fetchImpl = vi.fn<ImageFetch>(() => Promise.resolve(image()));

    expect(await readImage(address, fetchImpl)).toEqual({ ok: false, failure: 'not-listed' });
    expect(fetchImpl).not.toHaveBeenCalled();
  });

  it('fails, without throwing, when the host is unreachable, refuses, or answers with no image', async () => {
    const answer = (response: Response | Error) =>
      vi.fn<ImageFetch>(() =>
        response instanceof Error ? Promise.reject(response) : Promise.resolve(response),
      );

    expect(await readImage(LARGE, answer(new TypeError('blocked')))).toEqual({
      ok: false,
      failure: 'unreachable',
    });
    expect(await readImage(LARGE, answer(new Response('', { status: 403 })))).toEqual({
      ok: false,
      failure: 'refused',
    });
    expect(await readImage(LARGE, answer(image('text/html')))).toEqual({
      ok: false,
      failure: 'not-an-image',
    });
    expect(await readImage(LARGE, answer(image('image/jpeg', 0)))).toEqual({
      ok: false,
      failure: 'not-an-image',
    });
    expect(
      await readImage(
        LARGE,
        answer(image('image/jpeg', 16, { 'Content-Length': String(MAXIMUM_IMAGE_BYTES + 1) })),
      ),
    ).toEqual({ ok: false, failure: 'not-an-image' });
  });
});
