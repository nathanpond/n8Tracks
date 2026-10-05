import { expect, test, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
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

/** The tree's link for a Version. */
function node(page: Page, number: string) {
  return page
    .getByRole('navigation', { name: 'Versions' })
    .locator(`a[data-version-number="${number}"]`);
}

/**
 * Walks the story's Demo: branch from Version 1 (choosing the child, with a name), branch again
 * (taking the proposal), and switch the current Version back. Archiving (Demo step 4) is the
 * sibling story's (#63). Runs on the project's shared container, on a Song of its own.
 */
test.describe('the Version tree', () => {
  test('branches from a Version and switches the current one', async ({ page }) => {
    const song = await createSong(page, `Branching ${String(Date.now())}`);

    // 1. Open the Song: the tree shows Version 1, marked current.
    await page.goto(`./songs/${song.shortcode}`);
    await expect(node(page, '1')).toBeVisible();
    await expect(node(page, '1')).toContainText('Current');
    await expect(page.getByRole('heading', { level: 3, name: 'Version 1' })).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // 2. Create New Version From 1: 2 is proposed and 1.1 offered; choose 1.1 and name it.
    await page.getByRole('button', { name: 'Create New Version From 1' }).click();
    const dialog = page.getByRole('dialog', { name: 'Create New Version From 1' });
    await expect(dialog.getByRole('radio', { name: /^2 \(proposed\)/ })).toBeChecked();
    await expect(dialog.getByRole('radio', { name: /^1\.1/ })).not.toBeChecked();
    await expectModalAccessibleInBothSchemes(page);
    await dialog.getByRole('radio', { name: /^1\.1/ }).check();
    await dialog.getByRole('textbox', { name: 'Name' }).fill('Guitar experimentation');
    await dialog.getByRole('button', { name: 'Create Version' }).click();

    // 3. The tree shows 1 with child 1.1, now current and selected, and the URL names it.
    await expect(dialog).toBeHidden();
    await expect(page).toHaveURL(new RegExp(`/songs/${song.shortcode}/v/1\\.1$`));
    await expect(page.getByRole('heading', { level: 3, name: 'Version 1.1' })).toBeVisible();
    // Nested: 1.1's link is inside the list item of 1.
    const child = node(page, '1').locator('xpath=..').locator('a[data-version-number="1.1"]');
    await expect(child).toContainText('Guitar experimentation');
    await expect(child).toContainText('Current');
    await expect(child).toHaveAttribute('aria-current', 'true');
    await expect(node(page, '1')).not.toContainText('Current');
    await expectAccessibleInLightAndDark(page);

    // 4. Another from 1 proposes 2; take it.
    await node(page, '1').click();
    await page.getByRole('button', { name: 'Create New Version From 1' }).click();
    await expect(dialog.getByRole('radio', { name: /^2 \(proposed\)/ })).toBeChecked();
    await dialog.getByRole('button', { name: 'Create Version' }).click();
    await expect(page.getByRole('heading', { level: 3, name: 'Version 2' })).toBeVisible();
    await expect(node(page, '2')).toContainText('Current');

    // 5. Select 1 and make it current: the marker moves.
    await node(page, '1').click();
    await expect(page.getByRole('heading', { level: 3, name: 'Version 1' })).toBeVisible();
    await page.getByRole('button', { name: 'Make current' }).click();
    await expect(node(page, '1')).toContainText('Current');
    await expect(node(page, '2')).not.toContainText('Current');
    await expect(page.getByRole('button', { name: 'Make current' })).toBeHidden();

    // The choice is the server's: a reload shows the same tree.
    await page.reload();
    await expect(node(page, '1')).toContainText('Current');
    await expect(node(page, '1.1')).toBeVisible();
    await expect(node(page, '2')).toBeVisible();

    // A link to a number the Song does not have says so and links back to the Song.
    await page.goto(`./songs/${song.shortcode}/v/9`);
    await expect(page.getByRole('heading', { level: 3, name: 'Version not found' })).toBeVisible();
    await expectAccessibleInLightAndDark(page);
  });
});
