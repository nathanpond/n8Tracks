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
  genres: { id: string; name: string }[];
}

interface Genre {
  id: string;
  name: string;
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

async function createGenre(page: Page, base: URL, name: string): Promise<Genre> {
  const response = await page.request.post(new URL('api/v1/genres', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { name },
  });
  expect(response.status()).toBe(201);
  return (await response.json()) as Genre;
}

/** Gives a Song exactly `genres`, through the Song's own edit (as the Details panel does). */
async function assign(page: Page, base: URL, song: Song, genres: Genre[]): Promise<void> {
  const response = await page.request.patch(new URL(`api/v1/songs/${song.id}`, base).toString(), {
    headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(song.revision)}"` },
    data: { genreIds: genres.map((genre) => genre.id) },
  });
  expect(response.status()).toBe(200);
}

async function genreNames(page: Page, base: URL, song: Song): Promise<string[]> {
  const answer = await page.request.get(new URL(`api/v1/songs/${song.id}`, base).toString());
  expect(answer.ok()).toBe(true);
  return ((await answer.json()) as Song).genres.map((genre) => genre.name);
}

function row(page: Page, name: string) {
  return page.locator(`tr[data-genre-name="${name}"]`);
}

/**
 * Walks the story's Demo on the shared containers with Genres of its own (stamped, because Genres
 * are instance-wide): "Indie Rock" and "indie-rock" on different Songs are merged in Settings →
 * Genres after a confirmation stating the count, both Songs then show "Indie Rock", and a Genre in
 * use is deleted with its Songs reassigned to another. Each state is scanned with axe.
 */
test.describe('managing the Genre list', () => {
  test('merges a duplicate Genre and deletes one in use, reassigning its Songs', async ({
    page,
  }) => {
    const stamp = String(Date.now()).slice(-7);
    const base = await appBase(page);
    const indieRock = await createGenre(page, base, `Indie Rock ${stamp}`);
    const dashed = await createGenre(page, base, `indie-rock ${stamp}`);
    const alternative = await createGenre(page, base, `Alternative ${stamp}`);
    const first = await createSong(page, base, `Merge A ${stamp}`);
    const second = await createSong(page, base, `Merge B ${stamp}`);
    // 1. "Indie Rock" and "indie-rock" on different Songs.
    await assign(page, base, first, [indieRock]);
    await assign(page, base, second, [dashed]);

    // 2. In Settings → Genres, merge "indie-rock" into "Indie Rock"; the confirmation states the count.
    await page.goto('./settings/account');
    await page.getByRole('link', { name: 'Genres' }).click();
    await expect(page.getByRole('heading', { level: 2, name: 'Genres' })).toBeVisible();
    await expect(row(page, dashed.name)).toContainText('1');
    await expectAccessibleInLightAndDark(page);

    await page.getByRole('checkbox', { name: `Select ${dashed.name}`, exact: true }).check();
    await page.getByRole('button', { name: 'Merge into…' }).click();
    const merge = page.getByRole('dialog', { name: `Merge ${dashed.name}` });
    await expect(merge).toBeVisible();
    await merge
      .getByRole('combobox', { name: 'Merge into' })
      .selectOption({ label: indieRock.name });
    await expect(merge.getByTestId('merge-summary')).toContainText(
      `1 Song changes. Each Song with ${dashed.name} gets ${indieRock.name} instead`,
    );
    await expectModalAccessibleInBothSchemes(page);
    await merge.getByRole('button', { name: 'Merge', exact: true }).click();
    await expect(merge).toBeHidden();

    // 3. Both Songs now show "Indie Rock", and "indie-rock" is gone.
    await expect(page.getByRole('status').filter({ hasText: 'merged into' })).toContainText(
      `${dashed.name} merged into ${indieRock.name}.`,
    );
    await expect(row(page, dashed.name)).toHaveCount(0);
    await expect(row(page, indieRock.name).getByRole('cell').nth(1)).toHaveText('2');
    expect(await genreNames(page, base, first)).toEqual([indieRock.name]);
    expect(await genreNames(page, base, second)).toEqual([indieRock.name]);

    // 4. Delete a Genre in use, choosing to reassign its Songs to another.
    await page.getByRole('button', { name: `Delete ${indieRock.name}`, exact: true }).click();
    const remove = page.getByRole('dialog', { name: `Delete ${indieRock.name}` });
    await expect(remove).toBeVisible();
    await expect(remove.getByTestId('delete-summary')).toContainText(
      `2 Songs have ${indieRock.name}.`,
    );
    await remove.getByRole('radio', { name: 'Reassign them to another Genre' }).check();
    await remove
      .getByRole('combobox', { name: 'Reassign to' })
      .selectOption({ label: alternative.name });
    await expectModalAccessibleInBothSchemes(page);
    await remove.getByRole('button', { name: `Delete ${indieRock.name}` }).click();
    await expect(remove).toBeHidden();

    await expect(row(page, indieRock.name)).toHaveCount(0);
    await expect(row(page, alternative.name).getByRole('cell').nth(1)).toHaveText('2');
    expect(await genreNames(page, base, first)).toEqual([alternative.name]);
    expect(await genreNames(page, base, second)).toEqual([alternative.name]);
    await expectAccessibleInLightAndDark(page);
  });
});
