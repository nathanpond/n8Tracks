import { expect, test, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
  expectNoA11yViolations,
} from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  title: string;
  genres: { id: string; name: string }[];
}

/** The app's base (the root or the sub-path), as the Songs page resolves it. */
async function appBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

/** Creates a Song through the API. */
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

async function openSong(page: Page, song: Song): Promise<void> {
  await page.goto(`./songs/${song.shortcode}`);
  await expect(page.getByRole('heading', { level: 2, name: song.title })).toBeVisible();
}

function detailsButton(page: Page) {
  return page.getByRole('button', { name: 'Details', exact: true });
}

function genresField(page: Page) {
  return page.getByRole('combobox', { name: 'Genres' });
}

function chosen(page: Page, name: string) {
  return page.getByRole('button', { name: `Remove Genre ${name}` });
}

/**
 * Types a new name into the Genres picker and creates it from the create option. The name ends in
 * this run's stamp: Genres are instance-wide and the containers are shared.
 */
async function createGenre(page: Page, typed: string, name: string): Promise<void> {
  const field = genresField(page);
  await field.fill(typed);
  await expect(page.getByRole('option', { name: `Create Genre “${typed}”` })).toBeVisible();
  await field.fill(name);
  await page.getByRole('option', { name: `Create Genre “${name}”` }).click();
  await expect(chosen(page, name)).toBeVisible();
}

/**
 * Walks the story's Demo on the project's shared container (at the root and under the sub-path),
 * on Songs and Genres of its own: Details opened beside the lyrics, two Genres created on the spot,
 * the open panel remembered across a reload, a Genre suggested on another Song, and the Songs table
 * filtered by it. Each state is scanned with axe.
 */
test.describe('the Details panel and Genres', () => {
  test('gives a Song Genres beside its lyrics and filters the Songs table by one', async ({
    page,
  }) => {
    const stamp = String(Date.now()).slice(-7);
    const indieRock = `Indie Rock ${stamp}`;
    const folk = `Folk ${stamp}`;
    const base = await appBase(page);
    const first = await createSong(page, base, `Genres A ${stamp}`);
    const second = await createSong(page, base, `Genres B ${stamp}`);

    // 1. Open a Song and open Details; it starts closed.
    await openSong(page, first);
    await expect(detailsButton(page)).toHaveAttribute('aria-expanded', 'false');
    await detailsButton(page).click();
    const panel = page.getByRole('complementary', { name: 'Details' });
    await expect(panel).toBeVisible();
    await expect(page.getByRole('textbox', { name: 'Lyrics' })).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // Type "ind", create "Indie Rock", and add "Folk" the same way.
    await genresField(page).click();
    await genresField(page).fill('ind');
    await expect(page.getByRole('option', { name: 'Create Genre “ind”' })).toBeVisible();
    await expectNoA11yViolations(page);
    await createGenre(page, 'ind', indieRock);
    await createGenre(page, 'fol', folk);
    await expect(page.getByRole('button', { name: /^Remove Genre / })).toHaveCount(2);
    expect((await readSong(page, base, first)).genres.map((genre) => genre.name)).toEqual([
      folk,
      indieRock,
    ]);

    // Notes, in the same panel.
    await panel.getByRole('button', { name: 'Edit notes' }).click();
    await panel.getByRole('textbox', { name: 'Notes' }).fill('Try it slower.');
    await panel.getByRole('textbox', { name: 'Notes' }).press('Control+Enter');
    await expect(panel.getByText('Try it slower.')).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // 2. Close and reopen the page: the panel is still open and shows both Genres.
    await page.reload();
    await expect(page.getByRole('heading', { level: 2, name: first.title })).toBeVisible();
    await expect(page.getByRole('complementary', { name: 'Details' })).toBeVisible();
    await expect(chosen(page, folk)).toBeVisible();
    await expect(chosen(page, indieRock)).toBeVisible();

    // 3. On another Song, type "indie": "Indie Rock" is suggested; choose it with the keyboard.
    await openSong(page, second);
    await expect(page.getByRole('complementary', { name: 'Details' })).toBeVisible();
    await genresField(page).click();
    await genresField(page).pressSequentially(`indie rock ${stamp}`);
    await expect(page.getByRole('option', { name: indieRock, exact: true })).toBeVisible();
    await expect(page.getByRole('option', { name: /^Create Genre/ })).toHaveCount(0);
    await genresField(page).press('Enter');
    await expect(chosen(page, indieRock)).toBeVisible();

    // 4. In the Songs table, filter by "Indie Rock": both Songs, and then only the first by Folk.
    await page.goto('./songs');
    const filter = page.getByRole('combobox', { name: 'Genre' });
    await filter.click();
    await filter.fill(indieRock);
    await page.getByRole('option', { name: indieRock, exact: true }).click();
    const table = page.getByRole('table', { name: 'Songs' });
    await expect(table.getByRole('rowheader')).toHaveText([second.shortcode, first.shortcode]);
    await expect(page).toHaveURL(/[?&]genre=/);
    await page.keyboard.press('Escape');
    await expectAccessibleInLightAndDark(page);

    await page.goto(`./songs?genre=${(await readSong(page, base, first)).genres[0]?.id ?? ''}`);
    await expect(table.getByRole('rowheader')).toHaveText([first.shortcode]);
  });

  test('overlays the editor on a narrow window and closes with Escape', async ({ page }) => {
    const song = await createSong(
      page,
      await appBase(page),
      `Narrow ${String(Date.now()).slice(-7)}`,
    );
    await page.setViewportSize({ width: 900, height: 800 });
    await openSong(page, song);

    // The remembered state is ignored here: the overlay starts closed.
    await expect(detailsButton(page)).toHaveAttribute('aria-expanded', 'false');
    await detailsButton(page).click();
    const dialog = page.getByRole('dialog', { name: 'Details' });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole('combobox', { name: 'Genres' })).toBeVisible();
    await expectModalAccessibleInBothSchemes(page);

    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    await expect(detailsButton(page)).toBeFocused();
  });
});
