import { randomUUID } from 'node:crypto';
import { expect, test, type Page, type TestInfo } from '@playwright/test';
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

/** A Song of its own with one seeded Generation; the API base, the Song, and the Generation. */
async function setUp(
  page: Page,
  testInfo: TestInfo,
): Promise<{ api: (path: string) => string; song: Song; generation: string }> {
  const api = await apiBase(page);
  const title = `Judged ${String(Date.now())}`;
  const created = await page.request.post(api('songs'), {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(created.status()).toBe(201);
  const song = (await created.json()) as Song;
  const generation = await seedGeneration(
    testInfo,
    song.currentVersion.shortcode,
    JSON.stringify({ id: randomUUID(), status: 'complete', title: 'Piano take' }),
  );
  return { api, song, generation };
}

/**
 * Walks #119's Demo on the built image, on a Song of its own on the project's shared container,
 * with a Generation seeded with the test-only seeding command:
 * 1. Its panel gives it four stars by keyboard alone (the star control is a radio group the arrow
 *    keys change at once), which a reload keeps.
 * 2. The comment "Good piano intro" is added, edited, a second one added, and the first deleted
 *    after its confirmation.
 * 3. The Version's row shows a highest rating of four, from the same cached list as the panel.
 * Every state is scanned with the accessibility helper. The Demo is two tests, so each stays well
 * inside the test timeout on a slow CI runner (#406); the second starts with the four stars given
 * through the PATCH the star control sends.
 */
test.describe('rating and commenting on a Generation', () => {
  test('gives four stars by keyboard, which survive a reload', async ({ page }, testInfo) => {
    const { api, song, generation } = await setUp(page, testInfo);

    // 1. Open the seeded Generation's panel and give it four stars, by keyboard alone.
    await page.goto(`./songs/${song.shortcode}/generations/${generation}`);
    const panel = page.getByRole('dialog', { name: `Generation ${generation}` });
    await expect(panel).toBeVisible();
    await expectModalAccessibleInBothSchemes(page);

    const stars = panel.getByRole('radiogroup', { name: `Rating of ${generation}` });
    await stars.getByRole('radio', { name: '1 star', exact: true }).focus();
    for (let step = 0; step < 4; step++) {
      await page.keyboard.press('ArrowRight');
    }
    await expect(stars.getByRole('radio', { name: '4 stars' })).toHaveAttribute(
      'aria-checked',
      'true',
    );
    await expect(stars.getByRole('radio', { name: '4 stars' })).toBeFocused();
    await expect(panel.getByTestId('generation-panel-rating')).toHaveText('4 of 5 stars');
    await expectModalAccessibleInBothSchemes(page);

    // Reload: four stars remain.
    await expect
      .poll(async () => {
        const read = await page.request.get(api(`generations/${generation}`));
        return ((await read.json()) as { rating: number | null }).rating;
      })
      .toBe(4);
    await page.reload();
    await expect(panel).toBeVisible();
    await expect(stars.getByRole('radio', { name: '4 stars' })).toHaveAttribute(
      'aria-checked',
      'true',
    );
  });

  test('keeps comments, and shows the highest rating on the Version’s row', async ({
    page,
  }, testInfo) => {
    const { api, song, generation } = await setUp(page, testInfo);
    // Where step 1 ends: the Generation has four stars.
    const read = (await (await page.request.get(api(`generations/${generation}`))).json()) as {
      id: string;
      revision: number;
    };
    const rated = await page.request.patch(api(`generations/${read.id}`), {
      headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(read.revision)}"` },
      data: { rating: 4 },
    });
    expect(rated.status(), await rated.text()).toBe(200);

    await page.goto(`./songs/${song.shortcode}/generations/${generation}`);
    const panel = page.getByRole('dialog', { name: `Generation ${generation}` });
    await expect(panel).toBeVisible();
    await expect(
      panel
        .getByRole('radiogroup', { name: `Rating of ${generation}` })
        .getByRole('radio', { name: '4 stars' }),
    ).toHaveAttribute('aria-checked', 'true');

    // 2. Add "Good piano intro", edit it, add a second, and delete the first.
    const box = panel.getByRole('textbox', { name: 'New comment' });
    await box.fill('Good piano intro');
    await expect(panel.getByText('16 of 2,000 characters')).toBeVisible();
    await panel.getByRole('button', { name: 'Add comment' }).click();
    const comments = panel.getByRole('list');
    await expect(comments.getByText('Good piano intro', { exact: true })).toBeVisible();
    await expect(box).toHaveValue('');
    await expectModalAccessibleInBothSchemes(page);

    await comments.getByRole('button', { name: 'Edit comment 1' }).click();
    const edit = comments.getByRole('textbox', { name: 'Edit comment 1' });
    await expect(edit).toBeFocused();
    await edit.fill('Good piano intro, and a fine bridge');
    await comments.getByRole('button', { name: 'Save' }).click();
    await expect(comments.getByText('Good piano intro, and a fine bridge')).toBeVisible();
    await expect(comments.getByTestId('comment-edited')).toContainText('Edited');
    await expectModalAccessibleInBothSchemes(page);

    await box.fill('Second take is better');
    await panel.getByRole('button', { name: 'Add comment' }).click();
    await expect(comments.getByText('Second take is better')).toBeVisible();

    await comments.getByRole('button', { name: 'Delete comment 1' }).click();
    await expect(comments.getByRole('button', { name: 'Keep it' })).toBeFocused();
    await expectModalAccessibleInBothSchemes(page);
    await comments.getByRole('button', { name: 'Delete comment 1' }).click();
    await expect(comments.getByText('Good piano intro, and a fine bridge')).toHaveCount(0);
    await expect(comments.getByRole('listitem')).toHaveCount(1);
    await expect(comments.getByText('Second take is better')).toBeVisible();
    await expectModalAccessibleInBothSchemes(page);

    // 3. Closed, the Version's row shows a highest rating of four, and its Generation row the stars
    //    and one comment.
    await page.keyboard.press('Escape');
    await expect(panel).toBeHidden();
    const versionRow = section(page).locator('tr[data-version-row="1"]');
    await expect(versionRow.getByTestId('highest-rating')).toHaveText('4 of 5 stars');
    const generationRow = section(page).locator(`tr[data-generation="${generation}"]`);
    await expect(generationRow.getByTestId('generation-comment-count')).toHaveText('1');
    await expect(
      generationRow
        .getByRole('radiogroup', { name: `Rating of ${generation}` })
        .getByRole('radio', { name: '4 stars' }),
    ).toHaveAttribute('aria-checked', 'true');
    await expectAccessibleInLightAndDark(page);
  });
});
