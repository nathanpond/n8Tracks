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
  revision: number;
  updatedAt: string;
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

async function readSong(page: Page, base: URL, shortcode: string): Promise<Song> {
  const response = await page.request.get(new URL(`api/v1/songs/${shortcode}`, base).toString());
  expect(response.status()).toBe(200);
  return (await response.json()) as Song;
}

/** Creates an Album or Playlist titled `title` holding `songs` in order; its ID. */
async function createCollection(
  page: Page,
  base: URL,
  collection: 'albums' | 'playlists',
  title: string,
  songs: Song[],
): Promise<string> {
  const created = await page.request.post(new URL(`api/v1/${collection}`, base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(created.status()).toBe(201);
  const id = ((await created.json()) as { id: string }).id;
  let revision = 1;
  for (const song of songs) {
    const added = await page.request.post(
      new URL(
        `api/v1/${collection}/${id}/${collection === 'albums' ? 'tracks' : 'songs'}`,
        base,
      ).toString(),
      {
        headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(revision)}"` },
        data: { songId: song.id },
      },
    );
    expect(added.status()).toBe(200);
    revision = ((await added.json()) as { revision: number }).revision;
  }
  return id;
}

/** Opens the Song's Details panel, unless it is open already (its state is remembered). */
async function openDetails(page: Page): Promise<void> {
  const control = page.getByRole('button', { name: 'Details', exact: true });
  if ((await control.getAttribute('aria-expanded')) !== 'true') {
    await control.click();
  }
}

/**
 * Walks the story's Demo steps for Albums and Playlists on the project's shared container, with
 * names stamped with this run: a Playlist of three Songs deleted from its page (the confirmation
 * states the count, that the Songs stay, and that it is permanent), after which the Playlists list
 * says so and the three Songs remain, at the same revisions; and an Album deleted from its page,
 * after which its Song's Details no longer list it. Artist deletion (Demo step 2) is #104's.
 * Each state is scanned with axe in both colour schemes.
 */
test.describe('Deleting Albums and Playlists', () => {
  test('deletes a Playlist and keeps its Songs', async ({ page }) => {
    const stamp = String(Date.now()).slice(-7);
    const base = await appBase(page);
    const songs = [
      await createSong(page, base, `Kept one ${stamp}`),
      await createSong(page, base, `Kept two ${stamp}`),
      await createSong(page, base, `Kept three ${stamp}`),
    ];
    const title = `Doomed mix ${stamp}`;
    const id = await createCollection(page, base, 'playlists', title, songs);
    const before = await Promise.all(songs.map((song) => readSong(page, base, song.shortcode)));

    await page.goto(`./playlists/${id}`);
    await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();
    await page.getByRole('button', { name: 'Delete Playlist' }).click();
    const dialog = page.getByRole('dialog', { name: `Delete “${title}”?` });
    await expect(dialog.getByTestId('delete-collection-summary')).toContainText('is permanent');
    await expect(dialog.getByTestId('delete-collection-songs')).toHaveText(
      'It holds 3 Songs. The Songs themselves are not deleted: they stay in the catalog and are only taken off this Playlist.',
    );
    await expectModalAccessibleInBothSchemes(page);

    await dialog.getByRole('button', { name: 'Delete Playlist' }).click();
    await expect(page.getByRole('heading', { level: 2, name: 'Playlists' })).toBeVisible();
    await expect(page.getByTestId('collection-deleted-notice')).toHaveText(
      `Deleted the Playlist “${title}”. Its Songs were not deleted.`,
    );
    await expect(page.getByRole('link', { name: title })).toHaveCount(0);
    await expectAccessibleInLightAndDark(page);

    // The Songs remain, at the same revisions, on no Playlist of this run.
    for (const [index, song] of songs.entries()) {
      const after = (await readSong(page, base, song.shortcode)) as Song & {
        playlists: { id: string }[];
      };
      expect(after.revision).toBe(before[index]?.revision);
      expect(after.playlists.map((playlist) => playlist.id)).not.toContain(id);
    }
    const gone = await page.request.get(new URL(`api/v1/playlists/${id}`, base).toString());
    expect(gone.status()).toBe(404);
  });

  test("deletes an Album, and its Songs' Details no longer list it", async ({ page }) => {
    const stamp = String(Date.now()).slice(-7);
    const base = await appBase(page);
    const song = await createSong(page, base, `Album track ${stamp}`);
    const other = await createSong(page, base, `Other track ${stamp}`);
    const title = `Doomed EP ${stamp}`;
    const id = await createCollection(page, base, 'albums', title, [song, other]);

    // The Song's Details list the Album before.
    await page.goto(`./songs/${song.shortcode}`);
    await openDetails(page);
    const albums = page.getByRole('group', { name: 'Albums' });
    await expect(albums.getByRole('listitem')).toHaveText(`${title}, disc 1, track 1`);

    await albums.getByRole('link', { name: title }).click();
    await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();
    await page.getByRole('button', { name: 'Delete Album' }).click();
    const dialog = page.getByRole('dialog', { name: `Delete “${title}”?` });
    await expect(dialog.getByTestId('delete-collection-songs')).toContainText(
      'It holds 2 Songs. The Songs themselves are not deleted',
    );
    await expectModalAccessibleInBothSchemes(page);
    await dialog.getByRole('button', { name: 'Delete Album' }).click();

    await expect(page.getByRole('heading', { level: 2, name: 'Albums' })).toBeVisible();
    await expect(page.getByTestId('collection-deleted-notice')).toHaveText(
      `Deleted the Album “${title}”. Its Songs were not deleted.`,
    );
    await expectAccessibleInLightAndDark(page);

    // Its Songs remain, and their Details no longer list it.
    await page.goto(`./songs/${song.shortcode}`);
    await expect(page.getByRole('heading', { level: 2, name: song.title })).toBeVisible();
    await openDetails(page);
    await expect(page.getByRole('group', { name: 'Albums' })).toContainText('Not on any Album.');
    await expectAccessibleInLightAndDark(page);
    expect((await readSong(page, base, other.shortcode)).title).toBe(other.title);
    const gone = await page.request.get(new URL(`api/v1/albums/${id}`, base).toString());
    expect(gone.status()).toBe(404);
  });
});
