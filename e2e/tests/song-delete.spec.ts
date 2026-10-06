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

/** The API's base on the project's container, worked out on the Songs page. */
async function apiBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

/** Creates a Song through the API with Version 1 and then each of `numbers`, each from Version 1. */
async function songWithVersions(
  page: Page,
  base: URL,
  title: string,
  numbers: string[] = [],
): Promise<Song> {
  const created = await page.request.post(new URL('api/v1/songs', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(created.status()).toBe(201);
  const song = (await created.json()) as Song;
  for (const number of numbers) {
    const response = await page.request.post(
      new URL(`api/v1/songs/${song.id}/versions`, base).toString(),
      { headers: ANTIFORGERY_HEADERS, data: { sourceVersionId: `${song.shortcode}-v1`, number } },
    );
    expect(response.status()).toBe(201);
  }
  return song;
}

/** Creates an Album holding `songs` in order, through the API; its ID. */
async function albumOf(page: Page, base: URL, title: string, songs: Song[]): Promise<string> {
  const created = await page.request.post(new URL('api/v1/albums', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(created.status()).toBe(201);
  const album = (await created.json()) as { id: string; revision: number };
  let revision = album.revision;
  for (const song of songs) {
    const added = await page.request.post(
      new URL(`api/v1/albums/${album.id}/tracks`, base).toString(),
      {
        headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(revision)}"` },
        data: { songId: song.id },
      },
    );
    expect(added.status()).toBe(200);
    revision = ((await added.json()) as { revision: number }).revision;
  }
  return album.id;
}

async function openDeleteDialog(page: Page, song: Song) {
  await page.goto(`./songs/${song.shortcode}`);
  await expect(page.getByRole('heading', { level: 2, name: song.title })).toBeVisible();
  await page.getByRole('button', { name: 'Delete Song', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: `Delete “${song.title}”?` });
  await expect(dialog.getByTestId('delete-song-counts')).toBeVisible();
  return dialog;
}

/**
 * Walks #102's Demo, each Song its own (stamped titles, shared containers): a new Song with one empty
 * Version is deleted with a plain confirmation; a Song on an Album with three Versions lists its
 * counts and asks for its title; once deleted, the Album no longer lists it, and `/go/<shortcode>`
 * says it was deleted.
 */
test.describe('deleting a Song', () => {
  test('confirms plainly or by title, takes the Song off its Album, and its shortcode says it was deleted', async ({
    page,
  }) => {
    const stamp = String(Date.now());
    const base = await apiBase(page);

    // 1. A new Song with one empty Version: a plain confirmation is enough.
    const plain = await songWithVersions(page, base, `Plain delete ${stamp}`);
    const first = await openDeleteDialog(page, plain);
    await expect(first.getByTestId('delete-song-summary')).toContainText('is permanent');
    await expect(first.getByTestId('delete-song-counts')).toContainText('1 Version');
    await expect(first.getByRole('textbox')).toHaveCount(0);
    await expectModalAccessibleInBothSchemes(page);
    await first.getByRole('button', { name: 'Delete Song' }).click();

    await expect(page.getByRole('heading', { level: 2, name: 'Songs' })).toBeVisible();
    const notice = page.getByTestId('song-deleted-notice');
    await expect(notice).toContainText(`Deleted ${plain.shortcode} “${plain.title}”.`);
    await expectAccessibleInLightAndDark(page);
    await notice.getByRole('button', { name: 'Dismiss' }).click();
    await expect(notice).toBeHidden();

    // 2. A Song on an Album with three Versions: the counts, and its title must be typed.
    const doomed = await songWithVersions(page, base, `Album delete ${stamp}`, ['2', '3']);
    const mate = await songWithVersions(page, base, `Album mate ${stamp}`);
    const albumId = await albumOf(page, base, `Delete demo ${stamp}`, [mate, doomed]);
    const second = await openDeleteDialog(page, doomed);
    const counts = second.getByTestId('delete-song-counts');
    await expect(counts).toContainText('3 Versions');
    await expect(counts).toContainText('1 Album membership');
    const typed = second.getByRole('textbox', { name: 'Type the Song’s title to confirm' });
    await expect(typed).toBeVisible();
    const confirm = second.getByRole('button', { name: 'Delete Song' });
    await expect(confirm).toBeDisabled();
    await typed.fill(doomed.title.toUpperCase());
    await expect(confirm).toBeDisabled();
    await expectModalAccessibleInBothSchemes(page);

    // 3. Type it and delete: the Album no longer lists it, and its shortcode says it was deleted.
    await typed.fill(doomed.title);
    await expect(confirm).toBeEnabled();
    await confirm.click();
    await expect(page.getByTestId('song-deleted-notice')).toContainText(
      `Deleted ${doomed.shortcode} “${doomed.title}”.`,
    );

    await page.goto(`./albums/${albumId}`);
    await expect(page.locator(`li[data-song-title="${mate.title}"]`)).toBeVisible();
    await expect(page.locator(`li[data-song-title="${doomed.title}"]`)).toHaveCount(0);
    await expectAccessibleInLightAndDark(page);

    await page.goto(`./go/${doomed.shortcode}`);
    await expect(
      page.getByRole('heading', { level: 2, name: 'This Song was deleted' }),
    ).toBeVisible();
    await expect(page.getByTestId('song-deleted')).toContainText(
      `${doomed.shortcode} “${doomed.title}” was deleted on`,
    );
    await expectAccessibleInLightAndDark(page);

    // Its Versions' shortcodes say so too.
    const resolved = await page.request.get(
      new URL(`api/v1/resolve/${doomed.shortcode}-v2`, base).toString(),
    );
    expect(resolved.status()).toBe(200);
    expect(((await resolved.json()) as { status: string }).status).toBe('deleted');
  });
});
