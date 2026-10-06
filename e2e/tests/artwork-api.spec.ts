import { expect, test } from '@playwright/test';
import { solidPng } from '../support/images.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

/** The fields of an uploaded asset the API answers with that this walk reads. */
interface Asset {
  id: string;
  mediaType: string;
  bytes: number;
  width: number;
  height: number;
  sizes: number[];
  urls: Record<string, string>;
}

/**
 * The managed artwork store (#97) against the built image, signed in: an upload is decoded and
 * thumbnailed by the image's own native imaging library, the original comes back byte for byte,
 * and a renamed non-image is refused. The artwork UI is walked in `song-artwork.spec.ts`.
 */
test.describe('Artwork API', () => {
  test('stores an uploaded image with WebP thumbnails and refuses a renamed non-image', async ({
    page,
  }, testInfo) => {
    await page.goto('./songs');
    const base = new URL('.', page.url());
    const api = (path: string) => new URL(`api/v1/${path}`, base).toString();
    const site = (path: string) => new URL(path, base.origin).toString();
    const request = page.request;
    const image = solidPng(
      400,
      200,
      `${testInfo.project.name} ${String(Date.now())} ${String(testInfo.retry)}`,
    );

    const uploaded = await request.post(api('artwork'), {
      headers: ANTIFORGERY_HEADERS,
      multipart: {
        file: { name: 'cover.bin', mimeType: 'application/octet-stream', buffer: image },
      },
    });
    expect(uploaded.status(), await uploaded.text()).toBe(201);
    const asset = (await uploaded.json()) as Asset;
    expect(asset).toMatchObject({
      mediaType: 'image/png',
      bytes: image.length,
      width: 400,
      height: 200,
      sizes: [96, 320],
    });

    const original = await request.get(site(asset.urls.original ?? ''));
    expect(original.status()).toBe(200);
    expect(original.headers()['content-type']).toBe('image/png');
    expect(original.headers()['x-content-type-options']).toBe('nosniff');
    expect(Buffer.compare(await original.body(), image)).toBe(0);

    const thumbnail = await request.get(site(asset.urls['320'] ?? ''));
    expect(thumbnail.status()).toBe(200);
    expect(thumbnail.headers()['content-type']).toBe('image/webp');
    const webp = await thumbnail.body();
    expect(webp.subarray(0, 4).toString('ascii')).toBe('RIFF');
    expect(webp.subarray(8, 12).toString('ascii')).toBe('WEBP');

    const renamed = await request.post(api('artwork'), {
      headers: ANTIFORGERY_HEADERS,
      multipart: {
        file: {
          name: 'cover.jpg',
          mimeType: 'image/jpeg',
          buffer: Buffer.from('This is a text file.'),
        },
      },
    });
    expect(renamed.status()).toBe(415);
    expect(((await renamed.json()) as { code: string }).code).toBe('artwork_type_not_supported');
  });
});
