import { expect, test, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark } from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  title: string;
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

async function createAlbum(page: Page, base: URL, title: string): Promise<string> {
  const response = await page.request.post(new URL('api/v1/albums', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(response.status()).toBe(201);
  return ((await response.json()) as { id: string }).id;
}

/** Each disc's tracks as `title track`, disc by disc, in order. */
function discs(page: Page): Promise<string[][]> {
  return page
    .locator('ol[aria-labelledby^="album-disc-"]')
    .evaluateAll((lists) =>
      lists.map((list) =>
        [...list.querySelectorAll('li')].map(
          (row) =>
            `${row.getAttribute('data-song-title') ?? ''} ${row.getAttribute('data-track') ?? ''}`,
        ),
      ),
    );
}

function trackRow(page: Page, title: string) {
  return page.locator(`li[data-song-title="${title}"]`);
}

/** Finds a Song with the shared Song search and adds it. */
async function addSong(page: Page, title: string): Promise<void> {
  await page.getByRole('textbox', { name: 'Add a Song' }).fill(title);
  await page.getByRole('option', { name: new RegExp(title) }).click();
  await expect(page.getByTestId('album-track-status')).toContainText(`Added ${title}`);
}

/**
 * Walks the story's Demo on the project's shared container, with names stamped with this run
 * (Albums and Songs are instance-wide): three Songs added to "Pack EP" as tracks 1 to 3 on disc 1,
 * each incomplete; track 3 dragged to the top and the disc renumbered; a track moved to a new disc
 * and back; adding the first Song again refused (disabled in the search, and 409 from the API); and
 * that Song's Details listing "Pack EP, disc 1, track 2". Each state is scanned with axe in both
 * colour schemes.
 */
test.describe('Album tracks', () => {
  test('sequences Songs on an Album by disc and track', async ({ page }) => {
    const stamp = String(Date.now()).slice(-7);
    const title = `Pack EP ${stamp}`;
    const base = await appBase(page);
    const opener = `Opener ${stamp}`;
    const middle = `Middle ${stamp}`;
    const closer = `Closer ${stamp}`;
    const first = await createSong(page, base, opener);
    await createSong(page, base, middle);
    await createSong(page, base, closer);
    const albumId = await createAlbum(page, base, title);

    // 1. Add three Songs: tracks 1 to 3 on disc 1, each incomplete.
    await page.goto(`./albums/${albumId}`);
    await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();
    await expect(page.getByText('No tracks yet. Find a Song above to add it.')).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    for (const song of [opener, middle, closer]) {
      await addSong(page, song);
    }
    expect(await discs(page)).toEqual([[`${opener} 1`, `${middle} 2`, `${closer} 3`]]);
    await expect(page.getByRole('list', { name: 'Disc 1' })).toBeVisible();
    await expect(page.getByTestId('track-incomplete')).toHaveCount(3);
    await expectAccessibleInLightAndDark(page);

    // 2. Drag track 3 to the top: the disc renumbers itself 1 to 3.
    await trackRow(page, closer).dragTo(trackRow(page, opener));
    await expect.poll(() => discs(page)).toEqual([[`${closer} 1`, `${opener} 2`, `${middle} 3`]]);
    await expect(page.getByTestId('album-track-status')).toContainText(
      `Moved ${closer} to track 1 of 3 on disc 1.`,
    );

    // The keyboard way, and a move to a new disc and back.
    await trackRow(page, opener)
      .getByRole('button', { name: `Move ${opener} down` })
      .press('Enter');
    await expect.poll(() => discs(page)).toEqual([[`${closer} 1`, `${middle} 2`, `${opener} 3`]]);
    await trackRow(page, opener)
      .getByRole('button', { name: `Move ${opener} up` })
      .press('Enter');
    await expect.poll(() => discs(page)).toEqual([[`${closer} 1`, `${opener} 2`, `${middle} 3`]]);

    await page.getByRole('button', { name: `Move ${middle} to disc` }).click();
    await page.getByRole('menuitem', { name: 'New disc 2' }).click();
    await expect.poll(() => discs(page)).toEqual([[`${closer} 1`, `${opener} 2`], [`${middle} 1`]]);
    await expect(page.getByRole('list', { name: 'Disc 2' })).toBeVisible();
    await expectAccessibleInLightAndDark(page);
    await page.getByRole('button', { name: `Move ${middle} to disc` }).click();
    await page.getByRole('menuitem', { name: 'Disc 1' }).click();
    await expect.poll(() => discs(page)).toEqual([[`${closer} 1`, `${opener} 2`, `${middle} 3`]]);
    await expect(page.getByRole('list', { name: 'Disc 2' })).toHaveCount(0);

    await page.reload();
    await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();
    await expect.poll(() => discs(page)).toEqual([[`${closer} 1`, `${opener} 2`, `${middle} 3`]]);

    // 3. Adding the first Song again is refused: disabled in the search, and 409 from the API.
    const current = await page.request.get(new URL(`api/v1/albums/${albumId}`, base).toString());
    const revision = ((await current.json()) as { revision: number }).revision;
    const twice = await page.request.post(
      new URL(`api/v1/albums/${albumId}/tracks`, base).toString(),
      {
        headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(revision)}"` },
        data: { songId: first.shortcode },
      },
    );
    expect(twice.status()).toBe(409);
    expect(((await twice.json()) as { code: string }).code).toBe('song_already_on_album');
    await page.getByRole('textbox', { name: 'Add a Song' }).fill(opener);
    const option = page.getByRole('option', { name: new RegExp(opener) });
    await expect(option).toHaveAttribute('aria-disabled', 'true');
    await expect(option).toContainText('on this Album');
    await expectAccessibleInLightAndDark(page);
    await page.getByRole('textbox', { name: 'Add a Song' }).press('Enter');
    await page.getByRole('heading', { level: 2, name: title }).click();
    expect(await discs(page)).toEqual([[`${closer} 1`, `${opener} 2`, `${middle} 3`]]);

    // 4. That Song's Details list the Album with its disc and track, linking to it.
    await page.goto(`./songs/${first.shortcode}`);
    await page.getByRole('button', { name: 'Details' }).click();
    const albums = page.getByRole('group', { name: 'Albums' });
    await expect(albums.getByRole('listitem')).toHaveText(`${title}, disc 1, track 2`);
    await expectAccessibleInLightAndDark(page);
    await albums.getByRole('link', { name: title }).click();
    await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();
  });
});
