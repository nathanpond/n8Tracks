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

/** The app's base (the root or the sub-path), as the Songs page resolves it. */
async function appBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

async function createSong(page: Page, base: URL, title: string, concept?: string): Promise<Song> {
  const response = await page.request.post(new URL('api/v1/songs', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { title, concept },
  });
  expect(response.status()).toBe(201);
  return (await response.json()) as Song;
}

async function openSong(page: Page, song: Song, title = song.title): Promise<void> {
  await page.goto(`./songs/${song.shortcode}`);
  await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();
}

function indicator(page: Page) {
  return page.getByTestId('same-title');
}

/**
 * Walks the story's Demo on the project's shared container, on Songs of its own: three Songs
 * titled "Working Title" (stamped with this run, and written in different case and spacing, since
 * the containers are shared), one opened; its indicator says "2 others"; the list names both, and
 * a link opens one of them; the table link shows every Song with the title under a visible,
 * clearable filter; renaming the Song makes its indicator go. Each state is scanned with axe in
 * both colour schemes.
 */
test.describe('Songs that share a title', () => {
  test('flags a shared title, lists the others, and drops the flag on rename', async ({ page }) => {
    const stamp = String(Date.now()).slice(-7);
    const title = `Working Title ${stamp}`;
    const base = await appBase(page);
    const first = await createSong(page, base, title, 'The first take.');
    const second = await createSong(page, base, `working   title ${stamp}`);
    const third = await createSong(page, base, `WORKING TITLE ${stamp}`, 'The third take.');
    // Complement: a longer title is not the same title.
    await createSong(page, base, `${title} Reprise`);

    // 1. Open one: the indicator beside the title says "2 others".
    await openSong(page, first);
    await expect(indicator(page)).toHaveText('2 others with this title');
    await expectAccessibleInLightAndDark(page);

    // 2. Open it: both others are listed, newest first; follow a link to another.
    await indicator(page).click();
    const list = page.getByRole('dialog', { name: '2 other Songs have this title' });
    await expect(list).toBeVisible();
    const rows = list.locator('tr[data-song]');
    await expect(rows).toHaveCount(2);
    await expect(rows.nth(0)).toHaveAttribute('data-song', third.shortcode);
    await expect(rows.nth(0)).toContainText('The third take.');
    await expect(rows.nth(1)).toHaveAttribute('data-song', second.shortcode);
    await expect(rows.nth(1)).toContainText('—');
    await expectModalAccessibleInBothSchemes(page);

    await list.getByRole('link', { name: third.shortcode, exact: true }).click();
    await expect(page.getByRole('heading', { level: 2, name: third.title })).toBeVisible();
    await expect(page.getByTestId('shortcode')).toHaveText(third.shortcode);
    await expect(indicator(page)).toHaveText('2 others with this title');

    // The table link shows all three under a visible, clearable title filter.
    await indicator(page).click();
    await page
      .getByRole('link', { name: 'Show every Song with this title in the Songs table' })
      .click();
    const table = page.getByRole('table', { name: 'Songs' });
    await expect(table.locator('tbody tr')).toHaveCount(3);
    const filter = page.getByRole('group', { name: 'Title filter' });
    await expect(filter).toContainText(`Title is “${third.title}”`);
    await expectAccessibleInLightAndDark(page);
    await filter.getByRole('button', { name: 'Clear the title filter' }).click();
    await expect(filter).toBeHidden();
    expect(new URL(page.url()).searchParams.has('title')).toBe(false);

    // 3. Rename the first Song: its indicator disappears.
    await openSong(page, first);
    await expect(indicator(page)).toBeVisible();
    await page.getByRole('button', { name: 'Edit title' }).click();
    const input = page.getByRole('textbox', { name: 'Title', exact: true });
    await input.fill(`Finished Title ${stamp}`);
    await input.press('Enter');
    await expect(
      page.getByRole('heading', { level: 2, name: `Finished Title ${stamp}` }),
    ).toBeVisible();
    await expect(indicator(page)).toBeHidden();
    await expectAccessibleInLightAndDark(page);

    // The other two now see only each other.
    await openSong(page, second);
    await expect(indicator(page)).toHaveText('1 other with this title');
  });
});
