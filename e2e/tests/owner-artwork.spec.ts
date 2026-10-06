import { expect, test, type Locator, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { solidPng } from '../support/images.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Artwork {
  assetId: string;
  urls: Record<string, string>;
  squareUrls: Record<string, string>;
}

interface Owner {
  id: string;
  revision: number;
  artwork: Artwork | null;
}

/** The app's base (the root or the sub-path), as the Songs page resolves it. */
async function appBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

async function create(page: Page, base: URL, path: string, data: object): Promise<Owner> {
  const response = await page.request.post(new URL(`api/v1/${path}`, base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data,
  });
  expect(response.status()).toBe(201);
  return (await response.json()) as Owner;
}

async function read(page: Page, base: URL, path: string): Promise<Owner> {
  const answer = await page.request.get(new URL(`api/v1/${path}`, base).toString());
  expect(answer.ok()).toBe(true);
  return (await answer.json()) as Owner;
}

/** Waits until the image has been fetched and decoded: the artwork is really served to the page. */
async function expectLoaded(image: Locator): Promise<void> {
  await expect(image).toBeVisible();
  await expect
    .poll(() => image.evaluate((element) => (element as HTMLImageElement).naturalWidth))
    .toBeGreaterThan(0);
}

/**
 * Walks the story's Demo on the project's shared container, with records of its own (names and
 * image bytes stamped with this run): an Album, a Playlist, and an Artist are each given artwork on
 * their pages, and each list shows it as a square thumbnail; the Album's artwork is unchanged when
 * the artwork of a Song on it changes; and removing the Album's artwork asks first. Each state is
 * scanned with axe in both colour schemes.
 */
test.describe('Album, Playlist, and Artist artwork', () => {
  test('gives each owner its own artwork, shown in its list and independent of its Songs', async ({
    page,
  }, testInfo) => {
    const stamp = `${testInfo.project.name} ${String(Date.now())} ${String(testInfo.retry)}`;
    const short = String(Date.now()).slice(-7);
    const name = `Artwork Owner ${short}`;
    const base = await appBase(page);
    const album = await create(page, base, 'albums', { title: name });
    const playlist = await create(page, base, 'playlists', { title: name });
    const artist = await create(page, base, 'artists', { name });
    const owners = [
      { path: 'albums', row: 'data-album-title', noun: 'Album', record: album, rgb: [255, 0, 0] },
      {
        path: 'playlists',
        row: 'data-playlist-title',
        noun: 'Playlist',
        record: playlist,
        rgb: [0, 128, 0],
      },
      {
        path: 'artists',
        row: 'data-artist-name',
        noun: 'Artist',
        record: artist,
        rgb: [0, 0, 255],
      },
    ] as const;

    // A Song on the Album, with no artwork yet.
    const songResponse = await page.request.post(new URL('api/v1/songs', base).toString(), {
      headers: ANTIFORGERY_HEADERS,
      data: { title: `Artwork Track ${short}` },
    });
    expect(songResponse.status()).toBe(201);
    const song = (await songResponse.json()) as { id: string; shortcode: string; title: string };
    const added = await page.request.post(
      new URL(`api/v1/albums/${album.id}/tracks`, base).toString(),
      {
        headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(album.revision)}"` },
        data: { songId: song.id },
      },
    );
    expect(added.ok()).toBe(true);

    // Before: the neutral placeholder on the page and in the list.
    await page.goto(`./albums/${album.id}`);
    await expect(
      page.getByRole('group', { name: 'Artwork' }).getByRole('img', { name: 'No artwork' }),
    ).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // 1. Upload artwork on each owner's page; each list shows its image.
    for (const owner of owners) {
      await page.goto(`./${owner.path}/${owner.record.id}`);
      await expect(page.getByRole('heading', { level: 2, name })).toBeVisible();
      const picker = page.getByRole('group', { name: 'Artwork' });
      await expect(picker.getByRole('img', { name: 'No artwork' })).toBeVisible();

      await picker.locator('input[type="file"]').setInputFiles({
        name: 'cover.png',
        mimeType: 'image/png',
        buffer: solidPng(600, 400, `${stamp} ${owner.noun}`, owner.rgb),
      });
      await expect(picker.getByTestId('artwork-status')).toHaveText('Artwork saved.');
      const artwork = (await read(page, base, `${owner.path}/${owner.record.id}`)).artwork;
      expect(artwork).not.toBeNull();
      const pageImage = picker.getByRole('img', { name: `Artwork for ${name}` });
      await expect(pageImage).toHaveAttribute('src', artwork?.squareUrls['320'] ?? '');
      await expectLoaded(pageImage);
      await expectAccessibleInLightAndDark(page);

      await page.goto(`./${owner.path}`);
      const row = page.locator(`tr[${owner.row}="${name}"]`);
      const listImage = row.getByRole('img', { name: `Artwork for ${name}` });
      await expect(listImage).toHaveAttribute('src', artwork?.squareUrls['96'] ?? '');
      await expectLoaded(listImage);
      await expectAccessibleInLightAndDark(page);
    }

    // 2. Change the artwork of the Song on the Album: the Album's artwork is unchanged.
    const albumBefore = await read(page, base, `albums/${album.id}`);
    await page.goto(`./songs/${song.shortcode}`);
    await page.getByRole('button', { name: 'Details', exact: true }).click();
    const songPicker = page.getByRole('group', { name: 'Artwork' });
    await songPicker.locator('input[type="file"]').setInputFiles({
      name: 'track.png',
      mimeType: 'image/png',
      buffer: solidPng(400, 400, `${stamp} Song`, [255, 200, 0]),
    });
    await expect(songPicker.getByTestId('artwork-status')).toHaveText('Artwork saved.');
    const albumAfter = await read(page, base, `albums/${album.id}`);
    expect(albumAfter.artwork?.assetId).toBe(albumBefore.artwork?.assetId);
    expect(albumAfter.revision).toBe(albumBefore.revision);

    await page.goto(`./albums/${album.id}`);
    const albumPicker = page.getByRole('group', { name: 'Artwork' });
    await expect(albumPicker.getByRole('img', { name: `Artwork for ${name}` })).toHaveAttribute(
      'src',
      albumBefore.artwork?.squareUrls['320'] ?? '',
    );

    // Removing the Album's artwork asks first; once confirmed, the placeholder is back, and the
    // Playlist and the Artist keep theirs.
    await albumPicker.getByRole('button', { name: 'Remove artwork' }).click();
    const confirm = page.getByRole('dialog', { name: 'Remove the artwork?' });
    await expect(confirm.getByTestId('remove-artwork-summary')).toContainText(
      'The Album will show a placeholder instead.',
    );
    await expectModalAccessibleInBothSchemes(page);
    await confirm.getByRole('button', { name: 'Remove', exact: true }).click();
    await expect(albumPicker.getByTestId('artwork-status')).toHaveText('Artwork removed.');
    await expect(albumPicker.getByRole('img', { name: 'No artwork' })).toBeVisible();
    expect((await read(page, base, `albums/${album.id}`)).artwork).toBeNull();
    expect((await read(page, base, `playlists/${playlist.id}`)).artwork).not.toBeNull();
    expect((await read(page, base, `artists/${artist.id}`)).artwork).not.toBeNull();
    await expectAccessibleInLightAndDark(page);
  });
});
