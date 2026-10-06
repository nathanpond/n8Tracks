import { expect, test, type Locator, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { solidPng } from '../support/images.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  title: string;
  artwork: { assetId: string; urls: Record<string, string> } | null;
}

/** The app's base (the root or the sub-path), as the Songs page resolves it. */
async function appBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

async function createSong(page: Page, base: URL, title: string): Promise<Song> {
  const response = await page.request.post(new URL('api/v1/songs', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(response.status()).toBe(201);
  return (await response.json()) as Song;
}

async function readSong(page: Page, base: URL, song: Song): Promise<Song> {
  const answer = await page.request.get(new URL(`api/v1/songs/${song.id}`, base).toString());
  expect(answer.ok()).toBe(true);
  return (await answer.json()) as Song;
}

/** Waits until the image has been fetched and decoded: the artwork is really served to the page. */
async function expectLoaded(image: Locator): Promise<void> {
  await expect(image).toBeVisible();
  await expect
    .poll(() => image.evaluate((element) => (element as HTMLImageElement).naturalWidth))
    .toBeGreaterThan(0);
}

/**
 * Walks the story's Demo on the project's shared container, on a Song of its own (title and image
 * bytes stamped with this run): a PNG uploaded in the Details panel becomes the Song's artwork in
 * the panel, the header, and the Songs table; a text file renamed `cover.jpg` is refused and changes
 * nothing; replacing the artwork shows the new image everywhere; and removing it asks first, then
 * leaves the placeholder. Each state is scanned with axe in both colour schemes.
 */
test.describe('Song artwork', () => {
  test('uploads, refuses a renamed text file, replaces, and removes a Song’s artwork', async ({
    page,
  }, testInfo) => {
    const stamp = `${testInfo.project.name} ${String(Date.now())} ${String(testInfo.retry)}`;
    const base = await appBase(page);
    const song = await createSong(page, base, `Artwork ${String(Date.now()).slice(-7)}`);
    const alt = `Artwork for ${song.title}`;

    await page.goto(`./songs/${song.shortcode}`);
    await expect(page.getByRole('heading', { level: 2, name: song.title })).toBeVisible();
    const header = page.getByTestId('song-header-artwork');
    await expect(header.getByRole('img', { name: 'No artwork' })).toBeVisible();
    await page.getByRole('button', { name: 'Details', exact: true }).click();
    const picker = page.getByRole('group', { name: 'Artwork' });
    await expect(picker.getByRole('img', { name: 'No artwork' })).toBeVisible();
    const file = picker.locator('input[type="file"]');
    const status = picker.getByTestId('artwork-status');
    await expectAccessibleInLightAndDark(page);

    // 1. Upload a PNG: it appears in the panel, the header, and the table.
    await file.setInputFiles({
      name: 'cover.png',
      mimeType: 'image/png',
      buffer: solidPng(600, 400, `${stamp} first`),
    });
    await expect(status).toHaveText('Artwork saved.');
    const first = (await readSong(page, base, song)).artwork;
    expect(first).not.toBeNull();
    const panelImage = picker.getByRole('img', { name: alt });
    await expect(panelImage).toHaveAttribute('src', first?.urls['320'] ?? '');
    await expectLoaded(panelImage);
    await expectLoaded(header.getByRole('img', { name: alt }));
    await expectAccessibleInLightAndDark(page);

    // The panel's image opens the 1,024-pixel image, with the original a link away.
    await picker.getByRole('button', { name: `Show the artwork for ${song.title} larger` }).click();
    const enlarged = page.getByRole('dialog', { name: alt });
    await expectLoaded(enlarged.getByRole('img', { name: alt }));
    await expect(enlarged.getByRole('link', { name: 'View original' })).toHaveAttribute(
      'href',
      first?.urls.original ?? '',
    );
    await expectModalAccessibleInBothSchemes(page);
    await page.keyboard.press('Escape');
    await expect(enlarged).toBeHidden();

    await page.goto('./songs');
    const row = page
      .getByRole('table', { name: 'Songs' })
      .getByRole('row')
      .filter({ has: page.getByRole('rowheader', { name: song.shortcode, exact: true }) });
    const tableImage = row.getByRole('img', { name: alt });
    await expect(tableImage).toHaveAttribute('src', first?.urls['96'] ?? '');
    await expectLoaded(tableImage);
    await expectAccessibleInLightAndDark(page);

    // 2. A text file renamed cover.jpg is refused, and the artwork stays as it was.
    await page.goto(`./songs/${song.shortcode}`);
    await expect(picker).toBeVisible();
    await file.setInputFiles({
      name: 'cover.jpg',
      mimeType: 'image/jpeg',
      buffer: Buffer.from('This is a text file, not a picture.'),
    });
    await expect(picker.getByRole('alert')).toContainText('not a JPEG, PNG, or WebP image');
    await expect(picker.getByRole('alert')).toContainText('The artwork was not changed.');
    expect((await readSong(page, base, song)).artwork?.assetId).toBe(first?.assetId);
    await expect(panelImage).toHaveAttribute('src', first?.urls['320'] ?? '');
    await expectAccessibleInLightAndDark(page);

    // 3. Replace the artwork: the new image shows everywhere.
    await file.setInputFiles({
      name: 'cover-2.png',
      mimeType: 'image/png',
      buffer: solidPng(400, 400, `${stamp} second`, [0, 0, 255]),
    });
    await expect(status).toHaveText('Artwork replaced.');
    const second = (await readSong(page, base, song)).artwork;
    expect(second?.assetId).not.toBe(first?.assetId);
    await expect(panelImage).toHaveAttribute('src', second?.urls['320'] ?? '');
    await expect(header.getByRole('img', { name: alt })).toHaveAttribute(
      'src',
      second?.urls['320'] ?? '',
    );
    await expectLoaded(panelImage);
    await page.goto('./songs');
    await expect(tableImage).toHaveAttribute('src', second?.urls['96'] ?? '');
    await expectLoaded(tableImage);

    // Removing asks first; once confirmed, the placeholder is back.
    await page.goto(`./songs/${song.shortcode}`);
    await picker.getByRole('button', { name: 'Remove artwork' }).click();
    const confirm = page.getByRole('dialog', { name: 'Remove the artwork?' });
    await expect(confirm.getByTestId('remove-artwork-summary')).toBeVisible();
    await expectModalAccessibleInBothSchemes(page);
    await confirm.getByRole('button', { name: 'Remove', exact: true }).click();
    await expect(status).toHaveText('Artwork removed.');
    await expect(picker.getByRole('img', { name: 'No artwork' })).toBeVisible();
    await expect(header.getByRole('img', { name: 'No artwork' })).toBeVisible();
    expect((await readSong(page, base, song)).artwork).toBeNull();
    await expectAccessibleInLightAndDark(page);
  });
});
