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

function clip(title: string): string {
  return JSON.stringify({ id: randomUUID(), status: 'complete', title });
}

/**
 * Walks #120's Demo on the built image: a Song with Generations seeded on two Versions (with the
 * test-only seeding command). A Generation of Version 1 is selected in its panel and the header
 * names it; one of Version 2 replaces it and the first is no longer marked; the selected one is
 * archived and stays selected, shown Archived; and the selection is cleared from the row's actions
 * menu. Every state is scanned with the accessibility helper. Runs on the project's shared
 * container, on a Song of its own.
 */
test.describe('the Song’s Selected Generation', () => {
  test('is chosen, replaced, kept when archived, and cleared', async ({ page }, testInfo) => {
    const api = await apiBase(page);
    const created = await page.request.post(api('songs'), {
      headers: ANTIFORGERY_HEADERS,
      data: { title: `Chosen ${String(Date.now())}` },
    });
    expect(created.status()).toBe(201);
    const song = (await created.json()) as Song;
    const first = await seedGeneration(testInfo, song.currentVersion.shortcode, clip('First take'));
    const branched = await page.request.post(api(`songs/${song.shortcode}/versions`), {
      headers: ANTIFORGERY_HEADERS,
      data: { sourceVersionId: song.currentVersion.id, number: '2' },
    });
    expect(branched.status()).toBe(201);
    const second = await seedGeneration(testInfo, `${song.shortcode}-v2`, clip('Second take'));
    const selected = page.getByTestId('song-selected-generation');

    // 1. Select the Version 1 Generation in its panel: the header shows its shortcode.
    await page.goto(`./songs/${song.shortcode}/generations/${first}`);
    const firstPanel = page.getByRole('dialog', { name: `Generation ${first}` });
    await expect(firstPanel).toBeVisible();
    await expect(selected).toContainText('None');
    await firstPanel.getByRole('button', { name: 'Select for the Song' }).click();
    await expect(
      selected.getByRole('link', { name: `Selected Generation ${first}` }),
    ).toBeVisible();
    await expect(firstPanel.getByTestId('generation-panel-selection')).toHaveText(
      'This is the Song’s chosen output.',
    );
    await expectModalAccessibleInBothSchemes(page);

    // 2. Select the Version 2 Generation: the header changes; the first is no longer marked.
    await page.goto(`./songs/${song.shortcode}/generations/${second}`);
    const secondPanel = page.getByRole('dialog', { name: `Generation ${second}` });
    await expect(secondPanel).toBeVisible();
    await secondPanel.getByRole('button', { name: 'Select for the Song' }).click();
    await expect(
      selected.getByRole('link', { name: `Selected Generation ${second}` }),
    ).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(secondPanel).toBeHidden();
    await section(page).getByRole('button', { name: 'Generations of Version 1' }).click();
    const firstRow = section(page).locator(`tr[data-generation="${first}"]`);
    const secondRow = section(page).locator(`tr[data-generation="${second}"]`);
    await expect(firstRow).toBeVisible();
    await expect(firstRow.getByTestId('selected-generation')).toHaveCount(0);
    await expect(secondRow.getByTestId('selected-generation')).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // 3. Archive the selected Generation: it stays selected and shows Archived.
    await selected.getByRole('link', { name: `Selected Generation ${second}` }).click();
    await expect(secondPanel).toBeVisible();
    await secondPanel.getByRole('button', { name: 'Archive' }).click();
    await expect(secondPanel.getByRole('button', { name: 'Reactivate' })).toBeVisible();
    await expect(secondPanel.getByTestId('generation-state')).toContainText('Archived');
    await expect(secondPanel.getByTestId('generation-state')).toContainText('Selected');
    await expect(selected.getByText('Archived')).toBeVisible();
    await expectModalAccessibleInBothSchemes(page);
    await page.keyboard.press('Escape');
    await expect(secondPanel).toBeHidden();

    // 4. Clear the selection from the row's actions menu.
    await secondRow.getByRole('button', { name: `Actions for ${second}` }).click();
    await page.getByRole('menuitem', { name: 'Clear the selection' }).click();
    await expect(selected).toContainText('None');
    await expect(section(page).getByTestId('selected-generation')).toHaveCount(0);
    await expectAccessibleInLightAndDark(page);

    // The Songs table says it has none; the API agrees.
    const read = await page.request.get(api(`songs/${song.shortcode}`));
    const body = (await read.json()) as {
      hasSelectedGeneration: boolean;
      selectedGeneration: unknown;
    };
    expect(body.hasSelectedGeneration).toBe(false);
    expect(body.selectedGeneration).toBeNull();
  });
});
