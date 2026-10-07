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

test.use({ permissions: ['clipboard-read', 'clipboard-write'] });

/** The API base of the project's container, worked out on the Songs page. */
async function apiBase(page: Page): Promise<(path: string) => string> {
  await page.goto('./songs');
  const base = new URL('.', page.url());
  return (path: string) => new URL(`api/v1/${path}`, base).toString();
}

/** A Suno clip as Suno sends it, with a Suno ID of its own (Suno IDs are unique among live Generations). */
function clip(title: string, duration: number): string {
  return JSON.stringify({
    id: randomUUID(),
    status: 'complete',
    title,
    major_model_version: 'v5',
    created_at: '2026-10-01T09:30:00Z',
    metadata: { duration },
  });
}

function section(page: Page) {
  return page.getByRole('region', { name: 'Versions and Generations' });
}

/** The table's row of the Version numbered `number`. */
function versionRow(page: Page, number: string) {
  return section(page).locator(`tr[data-version-row="${number}"]`);
}

function generationRow(page: Page, shortcode: string) {
  return section(page).locator(`tr[data-generation="${shortcode}"]`);
}

/** The tree's node for a Version. */
function node(page: Page, number: string) {
  return page
    .getByRole('tree', { name: 'Versions' })
    .locator(`[role="treeitem"][data-version-number="${number}"]`);
}

function goToBox(page: Page) {
  return page.getByRole('banner').getByRole('textbox', { name: 'Go to' });
}

/**
 * Walks #118's Demo on the built image: two Generations seeded on Version 1 and one on Version 2
 * with the test-only seeding command; the Versions table lists both Versions with their counts;
 * Version 1 is expanded and the second Generation's shortcode copied, then given to the Go to box,
 * which opens the same Generation's panel; Version 2 is archived from the tree, leaves the table, and
 * comes back with Show archived Versions. The table and its expansion are worked by keyboard alone,
 * and every state is scanned. Runs on the project's shared container, on a Song of its own.
 */
test.describe('the Versions table', () => {
  test('shows each Version with its Generations, copies a Generation shortcode, and follows the archive toggle', async ({
    page,
  }, testInfo) => {
    const api = await apiBase(page);
    const title = `Versions table ${String(Date.now())}`;
    const created = await page.request.post(api('songs'), {
      headers: ANTIFORGERY_HEADERS,
      data: { title },
    });
    expect(created.status()).toBe(201);
    const song = (await created.json()) as Song;
    const branched = await page.request.post(api(`songs/${song.id}/versions`), {
      headers: ANTIFORGERY_HEADERS,
      data: { sourceVersionId: song.currentVersion.id, number: '2' },
    });
    expect(branched.status()).toBe(201);
    // Version 1 stays the current one, so archiving Version 2 takes it out of the table.
    const current = await page.request.put(api(`songs/${song.shortcode}/current-version`), {
      headers: ANTIFORGERY_HEADERS,
      data: { versionId: song.currentVersion.id },
    });
    expect(current.status()).toBe(200);

    // 1. Two Generations on Version 1, one on Version 2.
    const one = song.currentVersion.shortcode;
    const two = `${song.shortcode}-v2`;
    expect(await seedGeneration(testInfo, one, clip('First take', 61))).toBe(`${one}-g1`);
    const second = await seedGeneration(testInfo, one, clip('Second take', 187));
    expect(second).toBe(`${one}-g2`);
    expect(await seedGeneration(testInfo, two, clip('Other take', 90))).toBe(`${two}-g1`);

    // 2. The Versions section lists both Versions, with 2 and 1 Generations.
    await page.goto(`./songs/${song.shortcode}`);
    await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();
    await expect(versionRow(page, '1').getByTestId('generation-count')).toHaveText('2');
    await expect(versionRow(page, '2').getByTestId('generation-count')).toHaveText('1');
    await expect(versionRow(page, '1').getByText('Current', { exact: true })).toBeVisible();
    await expect(versionRow(page, '1').getByText('Frozen', { exact: true })).toBeVisible();
    await expect(versionRow(page, '1').getByTestId('highest-rating')).toHaveText('Not rated');
    await expectAccessibleInLightAndDark(page);

    // 3. By keyboard alone: expand Version 1 with its chevron, then copy the second Generation's
    //    shortcode with its copy control.
    const chevron = section(page).getByRole('button', {
      name: 'Generations of Version 1',
      exact: true,
    });
    await chevron.focus();
    await expect(chevron).toHaveAttribute('aria-expanded', 'false');
    await page.keyboard.press('Enter');
    await expect(chevron).toHaveAttribute('aria-expanded', 'true');
    await expect(generationRow(page, `${one}-g1`)).toBeVisible();
    await expect(generationRow(page, second).getByTestId('generation-duration')).toHaveText('3:07');
    await expect(generationRow(page, second).getByText('v5', { exact: true })).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    const copy = generationRow(page, second).getByRole('button', {
      name: `Copy shortcode ${second}`,
      exact: true,
    });
    await copy.focus();
    await page.keyboard.press('Enter');
    await expect(
      generationRow(page, second).getByRole('status').filter({ hasText: 'Copied' }),
    ).toBeVisible();
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(second);

    // Paste it into the Go to box: the same Generation's panel opens, at its own address.
    await goToBox(page).click();
    await page.keyboard.press('ControlOrMeta+V');
    await expect(goToBox(page)).toHaveValue(second);
    await goToBox(page).press('Enter');
    const panel = page.getByRole('dialog', { name: `Generation ${second}` });
    await expect(panel).toBeVisible();
    await expect(page).toHaveURL(new RegExp(`/songs/${song.shortcode}/generations/${second}$`));
    await expect(panel.getByTestId('generation-panel-shortcode')).toHaveText(second);
    await expect(panel.getByText('Second take', { exact: true })).toBeVisible();
    await expectModalAccessibleInBothSchemes(page);
    await page.keyboard.press('Escape');
    await expect(panel).toBeHidden();
    await expect(page).toHaveURL(new RegExp(`/songs/${song.shortcode}/v/1$`));

    // A Generation row opens the same panel by keyboard.
    const link = generationRow(page, second).getByRole('link', {
      name: `Second take, ${second}`,
    });
    await link.focus();
    await page.keyboard.press('Enter');
    await expect(panel).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(panel).toBeHidden();

    // Choosing Version 2's row shows it in the editor without making it current.
    await versionRow(page, '2').getByRole('link', { name: 'Version 2', exact: true }).focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { level: 3, name: 'Version 2' })).toBeVisible();
    await expect(node(page, '2')).toHaveAttribute('aria-selected', 'true');
    await expect(versionRow(page, '1').getByText('Current', { exact: true })).toBeVisible();

    // 4. Archive Version 2 from the tree: it leaves the table; Show archived Versions brings it back.
    await node(page, '2').getByRole('button', { name: 'Actions for Version 2' }).click();
    await page.getByRole('menuitem', { name: 'Archive' }).click();
    await expect(page.getByRole('status').filter({ hasText: 'archived.' })).toContainText(
      'Version 2 archived.',
    );
    await expect(versionRow(page, '2')).toHaveCount(0);
    await expect(versionRow(page, '1')).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    const toggle = section(page).getByRole('switch', { name: 'Show archived Versions' });
    await toggle.focus();
    await page.keyboard.press('Space');
    await expect(toggle).toBeChecked();
    await expect(versionRow(page, '2')).toBeVisible();
    await expect(versionRow(page, '2').getByText('Archived', { exact: true })).toBeVisible();
    await expect(page).toHaveURL(/[?&]archived=1/);
    await expectAccessibleInLightAndDark(page);

    // The choice is the URL's: a reload keeps it.
    await page.reload();
    await expect(versionRow(page, '2')).toBeVisible();
    await expect(
      section(page).getByRole('switch', { name: 'Show archived Versions' }),
    ).toBeChecked();
  });
});
