import { randomUUID } from 'node:crypto';
import { expect, test, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { seedGeneration } from '../support/seeding.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  currentVersion: { id: string; shortcode: string };
}

/** The API base of the project's container, worked out on the Songs page. */
async function apiBase(page: Page): Promise<(path: string) => string> {
  await page.goto('./songs');
  const base = new URL('.', page.url());
  return (path: string) => new URL(`api/v1/${path}`, base).toString();
}

function section(page: Page) {
  return page.getByRole('region', { name: 'Versions and Generations' });
}

/** Opens Version `number`'s Generations in the Versions table unless they are open already (the route may open them). */
async function expandVersion(page: Page, number: string): Promise<void> {
  const toggle = section(page).getByRole('button', { name: `Generations of Version ${number}` });
  await expect(toggle).toBeVisible();
  if ((await toggle.getAttribute('aria-expanded')) !== 'true') {
    await toggle.click();
  }
}

/** Opens the Song page's Details panel unless it is open already (its state is remembered). */
async function openDetails(page: Page): Promise<void> {
  const details = page.getByRole('button', { name: 'Details', exact: true });
  await expect(details).toBeVisible();
  if ((await details.getAttribute('aria-expanded')) !== 'true') {
    await details.click();
  }
}

/** The Song shortcode in a Song page's address. */
function songOf(url: string): string {
  const match = /\/songs\/(n8-[0-9]+)\//.exec(url);
  expect(match).not.toBeNull();
  return match?.[1] ?? '';
}

function clip(title: string): string {
  return JSON.stringify({ id: randomUUID(), status: 'complete', title });
}

/**
 * Walks #123's Demo on the built image: on a Song with two seeded Generations, the second is opened
 * and "Create new Song from Generation" chosen; the warning is read, a title given, and the move
 * confirmed. The new Song opens with Version 1, frozen, holding the Generation as g1. The original
 * Song lists one Generation and its Details show "Source of" the new Song. The Generation's old
 * shortcode, pasted into the Go to box, opens it in the new Song and says it moved. Every state is
 * scanned with the accessibility helper. Runs on the project's shared container, on Songs of its own.
 */
test.describe('Create new Song from Generation', () => {
  test('moves a Generation to a new Song, and its old shortcode still finds it', async ({
    page,
  }, testInfo) => {
    const api = await apiBase(page);
    const stamp = String(Date.now());
    const created = await page.request.post(api('songs'), {
      headers: ANTIFORGERY_HEADERS,
      data: { title: `Origin ${stamp}` },
    });
    expect(created.status()).toBe(201);
    const song = (await created.json()) as Song;
    const first = await seedGeneration(testInfo, song.currentVersion.shortcode, clip('First take'));
    const second = await seedGeneration(
      testInfo,
      song.currentVersion.shortcode,
      clip('Second take'),
    );
    const newTitle = `Split out ${stamp}`;

    // 1. Open the second Generation and choose Create new Song from Generation; read the warning.
    await page.goto(`./songs/${song.shortcode}/generations/${second}`);
    const panel = page.getByRole('dialog', { name: `Generation ${second}` });
    await expect(panel).toBeVisible();
    await panel.getByRole('button', { name: 'Create new Song from Generation' }).click();
    const dialog = page.getByRole('dialog', { name: 'Create new Song from Generation' });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByText(`${second} will be moved, not copied`)).toBeVisible();
    await expect(dialog.getByTestId('move-what-moves')).toContainText('its rating');
    await expect(dialog.getByTestId('move-what-moves')).toContainText('its Suno data');
    const title = dialog.getByRole('textbox', { name: 'New Song title' });
    await expect(title).toHaveValue('Second take');
    await expectModalAccessibleInBothSchemes(page);

    // Give the new Song a title and confirm.
    await title.fill(newTitle);
    await dialog.getByRole('button', { name: 'Move to a new Song' }).click();

    // 2. The new Song opens with Version 1, frozen, holding the Generation as g1.
    await expect(page.getByRole('heading', { level: 2, name: newTitle })).toBeVisible();
    await expect(page).toHaveURL(/\/songs\/n8-[0-9]+\/generations\/n8-[0-9]+-v1-g1$/);
    const newShortcode = songOf(page.url());
    expect(newShortcode).not.toBe(song.shortcode);
    const moved = page.getByRole('dialog', { name: `Generation ${newShortcode}-v1-g1` });
    await expect(moved).toBeVisible();
    await expect(moved.getByTestId('generation-moved')).toHaveText(
      `${second} moved: this Generation is now ${newShortcode}-v1-g1.`,
    );
    await expectModalAccessibleInBothSchemes(page);
    await page.keyboard.press('Escape');
    await expect(moved).toBeHidden();
    const tree = page.getByRole('tree', { name: 'Versions' });
    const node = tree.locator('[role="treeitem"][data-version-number="1"]');
    await expect(node.getByRole('img', { name: 'Frozen: has a Generation' })).toBeVisible();
    await expect(page.getByTestId('frozen-notice')).toBeVisible();
    await expandVersion(page, '1');
    await expect(
      section(page).locator(`tr[data-generation="${newShortcode}-v1-g1"]`),
    ).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // 3. The original Song lists one Generation; its Details show "Source of" the new Song.
    await page.goto(`./songs/${song.shortcode}`);
    await expandVersion(page, '1');
    await expect(section(page).locator('tr[data-generation]')).toHaveCount(1);
    await expect(section(page).locator(`tr[data-generation="${first}"]`)).toBeVisible();
    await openDetails(page);
    const related = page.getByTestId('song-relationships');
    await expect(related.locator('[data-relationship-group="Source of"]')).toBeVisible();
    await expect(
      related.locator(`[data-relationship-name="Source of"][data-song-title="${newTitle}"]`),
    ).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // 4. Paste the Generation's old shortcode into the Go to box: it opens in the new Song, saying it moved.
    const goTo = page.getByRole('banner').getByRole('textbox', { name: 'Go to' });
    await goTo.fill(second);
    await goTo.press('Enter');
    const found = page.getByRole('dialog', { name: `Generation ${newShortcode}-v1-g1` });
    await expect(found).toBeVisible();
    await expect(page).toHaveURL(
      new RegExp(`/songs/${newShortcode}/generations/${newShortcode}-v1-g1$`),
    );
    await expect(found.getByTestId('generation-moved')).toHaveText(
      `${second} moved: this Generation is now ${newShortcode}-v1-g1.`,
    );
    await expectModalAccessibleInBothSchemes(page);

    // The API agrees: the old shortcode resolves as moved, for good.
    const resolved = await page.request.get(api(`resolve/${second}`));
    expect(resolved.status()).toBe(200);
    const body = (await resolved.json()) as { status: string; canonicalShortcode: string };
    expect(body.status).toBe('moved');
    expect(body.canonicalShortcode).toBe(`${newShortcode}-v1-g1`);
  });
});
