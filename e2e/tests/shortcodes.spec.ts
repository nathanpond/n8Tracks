import { expect, test, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark } from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  currentVersion: { id: string; shortcode: string };
}

/** Creates a Song through the API on the project's container and returns it. */
async function createSong(page: Page, title: string): Promise<Song> {
  await page.goto('./songs');
  const base = new URL('.', page.url());
  const response = await page.request.post(new URL('api/v1/songs', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(response.status()).toBe(201);
  return (await response.json()) as Song;
}

function goToBox(page: Page) {
  return page.getByRole('banner').getByRole('textbox', { name: 'Go to' });
}

/** The selected Version's heading, once its pane has loaded. */
function versionHeading(page: Page, number: string) {
  return page.getByRole('heading', { level: 3, name: `Version ${number}` });
}

test.use({ permissions: ['clipboard-read', 'clipboard-write'] });

/**
 * Walks #68's Demo: copy a Song's shortcode, paste it into the Go to box from another page, type
 * a Version's shortcode in upper case, and type one that names nothing; then the same through
 * `/go/` links. Runs on the project's shared container, on a Song of its own, so its shortcode
 * is whatever the container gives it.
 */
test.describe('shortcodes', () => {
  test('copies a shortcode and goes back to it from anywhere', async ({ page }) => {
    const title = `Shortcodes ${String(Date.now())}`;
    const song = await createSong(page, title);

    // 1. On the Song, the copy control next to its shortcode copies it and confirms.
    await page.goto(`./songs/${song.shortcode}`);
    await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();
    await expect(page.getByTestId('shortcode')).toHaveText(song.shortcode);
    await expect(page.getByTestId('version-shortcode')).toHaveText(song.currentVersion.shortcode);
    await page
      .getByRole('button', { name: `Copy shortcode ${song.shortcode}`, exact: true })
      .click();
    await expect(page.getByRole('status').filter({ hasText: 'Copied' })).toBeVisible();
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(song.shortcode);
    await expectAccessibleInLightAndDark(page);

    // 2. From another page, paste it into the Go to box and press Enter: the Song opens.
    await page.goto('./settings/system');
    await expect(page.getByRole('heading', { level: 2, name: 'System' })).toBeVisible();
    await goToBox(page).click();
    await page.keyboard.press('ControlOrMeta+V');
    await expect(goToBox(page)).toHaveValue(song.shortcode);
    await goToBox(page).press('Enter');
    await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();
    await expect(page).toHaveURL(new RegExp(`/songs/${song.shortcode}$`));
    await expect(goToBox(page)).toHaveValue('');

    // 3. A Version's shortcode in upper case opens that Version.
    await goToBox(page).fill(song.currentVersion.shortcode.toUpperCase());
    await goToBox(page).press('Enter');
    await expect(page).toHaveURL(new RegExp(`/songs/${song.shortcode}/v/1$`));
    await expect(versionHeading(page, '1')).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // 4. A shortcode that names nothing stays in the box with a not-found message beside it.
    await goToBox(page).fill('n8-99999999');
    await goToBox(page).press('Enter');
    await expect(
      page.getByRole('alert').filter({ hasText: 'Nothing has that shortcode or ID.' }),
    ).toBeVisible();
    await expect(goToBox(page)).toHaveValue('n8-99999999');
    await expect(page).toHaveURL(new RegExp(`/songs/${song.shortcode}/v/1$`));
    await expectAccessibleInLightAndDark(page);
  });

  test('opens a /go/ link, and shows a not-found page for one that names nothing', async ({
    page,
  }) => {
    const title = `Go links ${String(Date.now())}`;
    const song = await createSong(page, title);

    // The link replaces itself in the history: Back returns to where it was opened from.
    await page.goto('./songs');
    await page.goto(`./go/${song.currentVersion.shortcode.toUpperCase()}`);
    await expect(page).toHaveURL(new RegExp(`/songs/${song.shortcode}/v/1$`));
    await expect(versionHeading(page, '1')).toBeVisible();
    await page.goBack();
    await expect(page).toHaveURL(/\/songs$/);

    await page.goto(`./go/${song.id}`);
    await expect(page).toHaveURL(new RegExp(`/songs/${song.shortcode}$`));
    await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();

    await page.goto('./go/n8-012');
    await expect(page.getByRole('heading', { level: 2, name: 'Not found' })).toBeVisible();
    await expect(page.getByText('Nothing has the shortcode or ID n8-012.')).toBeVisible();
    await expectAccessibleInLightAndDark(page);
  });
});
