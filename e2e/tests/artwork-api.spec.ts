import { crc32, deflateSync } from 'node:zlib';
import { expect, test } from '@playwright/test';
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

function chunk(type: string, data: Buffer): Buffer {
  const typed = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(typed));
  return Buffer.concat([length, typed, crc]);
}

/**
 * A `width` × `height` opaque red PNG, with `stamp` in a text chunk so that each run's bytes, and
 * so its asset, are new on the shared container.
 */
function redPng(width: number, height: number, stamp: string): Buffer {
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header.set([8, 2, 0, 0, 0], 8);
  const row = Buffer.concat([Buffer.from([0]), Buffer.alloc(width * 3, Buffer.from([255, 0, 0]))]);
  const pixels = deflateSync(Buffer.concat(Array.from({ length: height }, () => row)));
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('tEXt', Buffer.from(`Comment\0${stamp}`, 'latin1')),
    chunk('IDAT', pixels),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/**
 * The managed artwork store (#97) against the built image, signed in: an upload is decoded and
 * thumbnailed by the image's own native imaging library, the original comes back byte for byte,
 * and a renamed non-image is refused. The story has no screen; the artwork UI comes with #98.
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
    const image = redPng(
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
