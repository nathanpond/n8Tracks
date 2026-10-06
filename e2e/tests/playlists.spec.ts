import { expect, test, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  title: string;
}

function sidebar(page: Page) {
  return page.getByRole('navigation', { name: 'Main' });
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

/** The titles of the Songs on the Playlist page, in order. */
function songTitles(page: Page): Promise<(string | null)[]> {
  return page
    .getByRole('list', { name: 'Songs' })
    .getByRole('listitem')
    .evaluateAll((rows) => rows.map((row) => row.getAttribute('data-song-title')));
}

function songRow(page: Page, title: string) {
  return page.locator(`li[data-song-title="${title}"]`);
}

/** Finds a Song with the shared Song search and adds it. */
async function addSong(page: Page, title: string): Promise<void> {
  await page.getByRole('textbox', { name: 'Add a Song' }).fill(title);
  await page.getByRole('option', { name: new RegExp(title) }).click();
  await expect(page.getByTestId('playlist-status')).toContainText(`Added ${title}`);
}

/**
 * Walks the story's Demo on the project's shared container, with names stamped with this run
 * (Playlists and Songs are instance-wide): "Road trip" created from the Playlists page, three Songs
 * added by search, the last dragged to the top and the order still there after a reload; adding a
 * Song a second time refused (disabled in the search, and 409 from the API); and the Song's Details
 * listing the Playlist. Each state is scanned with axe in both colour schemes.
 */
test.describe('Playlists', () => {
  test('gathers Songs into a Playlist and arranges them', async ({ page }) => {
    const stamp = String(Date.now()).slice(-7);
    const title = `Road trip ${stamp}`;
    const base = await appBase(page);
    const songs = [
      await createSong(page, base, `Highway ${stamp}`),
      await createSong(page, base, `Sunrise ${stamp}`),
      await createSong(page, base, `Toll booth ${stamp}`),
    ];
    const [highway, sunrise, toll] = songs.map((song) => song.title);

    // 1. Create "Road trip", add three Songs, drag the last to the top; reload; the order holds.
    await sidebar(page).getByRole('link', { name: 'Playlists' }).click();
    await expect(page.getByRole('heading', { level: 2, name: 'Playlists' })).toBeVisible();
    await expect(sidebar(page).getByRole('link', { name: 'Playlists' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    await expectAccessibleInLightAndDark(page);

    await page.getByRole('button', { name: 'New Playlist' }).first().click();
    const dialog = page.getByRole('dialog', { name: 'New Playlist' });
    await expect(dialog).toBeVisible();
    await expectModalAccessibleInBothSchemes(page);
    await dialog.getByRole('textbox', { name: 'Title' }).fill(title);
    await dialog.getByRole('button', { name: 'Create Playlist' }).click();
    await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();
    await expect(page).toHaveURL(/\/playlists\/[0-9a-f-]{36}$/);
    await expectAccessibleInLightAndDark(page);

    for (const song of songs) {
      await addSong(page, song.title);
    }
    expect(await songTitles(page)).toEqual([highway, sunrise, toll]);
    await expect(page.getByTestId('no-selected-generation')).toHaveCount(3);

    // The search open, with the Songs already on the Playlist disabled.
    await page.getByRole('textbox', { name: 'Add a Song' }).fill(stamp);
    await expect(page.getByRole('option', { name: new RegExp(highway ?? '') })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
    await expectAccessibleInLightAndDark(page);
    await page.getByRole('textbox', { name: 'Add a Song' }).fill('');
    await page.getByRole('heading', { level: 2, name: title }).click();

    await songRow(page, toll ?? '').dragTo(songRow(page, highway ?? ''));
    await expect.poll(() => songTitles(page)).toEqual([toll, highway, sunrise]);
    await expect(page.getByTestId('playlist-status')).toContainText(
      `Moved ${toll ?? ''} to position 1 of 3.`,
    );

    // The keyboard way: Move down, then back up.
    await songRow(page, highway ?? '')
      .getByRole('button', { name: `Move ${highway ?? ''} down` })
      .click();
    await expect.poll(() => songTitles(page)).toEqual([toll, sunrise, highway]);
    await songRow(page, highway ?? '')
      .getByRole('button', { name: `Move ${highway ?? ''} up` })
      .press('Enter');
    await expect.poll(() => songTitles(page)).toEqual([toll, highway, sunrise]);
    await expectAccessibleInLightAndDark(page);

    await page.reload();
    await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();
    await expect.poll(() => songTitles(page)).toEqual([toll, highway, sunrise]);

    // 2. Adding a Song twice is refused: the API answers 409 and the Playlist is unchanged.
    const playlistId = new URL(page.url()).pathname.split('/').at(-1) ?? '';
    const current = await page.request.get(
      new URL(`api/v1/playlists/${playlistId}`, base).toString(),
    );
    const revision = ((await current.json()) as { revision: number }).revision;
    const twice = await page.request.post(
      new URL(`api/v1/playlists/${playlistId}/songs`, base).toString(),
      {
        headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(revision)}"` },
        data: { songId: songs[0]?.shortcode },
      },
    );
    expect(twice.status()).toBe(409);
    expect(((await twice.json()) as { code: string }).code).toBe('song_already_on_playlist');
    await page.getByRole('textbox', { name: 'Add a Song' }).fill(highway ?? '');
    const option = page.getByRole('option', { name: new RegExp(highway ?? '') });
    await expect(option).toHaveAttribute('aria-disabled', 'true');
    await expect(option).toContainText('on this Playlist');
    await page.getByRole('textbox', { name: 'Add a Song' }).press('Enter');
    await page.getByRole('heading', { level: 2, name: title }).click();
    expect(await songTitles(page)).toEqual([toll, highway, sunrise]);

    // 3. A Song's Details list the Playlist, linking to it.
    await page.goto(`./songs/${songs[0]?.shortcode ?? ''}`);
    await page.getByRole('button', { name: 'Details' }).click();
    const playlists = page.getByRole('group', { name: 'Playlists' });
    await expect(playlists.getByRole('link', { name: title })).toBeVisible();
    await expectAccessibleInLightAndDark(page);
    await playlists.getByRole('link', { name: title }).click();
    await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();

    // The Playlist is in the list with its Song count.
    await sidebar(page).getByRole('link', { name: 'Playlists' }).click();
    await expect(page.locator(`tr[data-playlist-title="${title}"]`).getByRole('cell')).toHaveText([
      '3',
    ]);
  });
});
