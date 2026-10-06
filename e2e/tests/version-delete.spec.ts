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
}

/** The API's base on the project's container, worked out on the Songs page. */
async function apiBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

/** Creates a Song through the API with Version 1 and then each of `numbers`, each from its parent (or 1). */
async function songWithVersions(
  page: Page,
  base: URL,
  title: string,
  numbers: string[],
): Promise<Song> {
  const created = await page.request.post(new URL('api/v1/songs', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(created.status()).toBe(201);
  const song = (await created.json()) as Song;
  for (const number of numbers) {
    const parent = number.includes('.') ? number.slice(0, number.lastIndexOf('.')) : '1';
    const response = await page.request.post(
      new URL(`api/v1/songs/${song.id}/versions`, base).toString(),
      {
        headers: ANTIFORGERY_HEADERS,
        data: { sourceVersionId: `${song.shortcode}-v${parent}`, number },
      },
    );
    expect(response.status()).toBe(201);
  }
  return song;
}

/** The numbers of the Song's Versions, as the API lists them. */
async function versionNumbers(page: Page, base: URL, song: Song): Promise<string[]> {
  const response = await page.request.get(
    new URL(`api/v1/songs/${song.id}/versions`, base).toString(),
  );
  expect(response.status()).toBe(200);
  return ((await response.json()) as { items: { number: string }[] }).items.map(
    (item) => item.number,
  );
}

function tree(page: Page) {
  return page.getByRole('tree', { name: 'Versions' });
}

function node(page: Page, number: string) {
  return tree(page).locator(`[role="treeitem"][data-version-number="${number}"]`);
}

/**
 * Walks #101's Demo, each Song its own (stamped titles, shared containers): delete 1.1 of a Song
 * with 1, 1.1, 1.1.1, and 2, and the confirmation says one descendant remains; the tree shows a
 * "Deleted Version 1.1" placeholder with 1.1.1 beneath it; a new Version from 1 is not offered
 * 1.1; and deleting a Song's only Version leaves it an empty Version 2. The deleted Version's page
 * says it was deleted.
 */
test.describe('deleting a Version', () => {
  test('keeps the descendants under a placeholder, never offers the number again, and replaces the last Version', async ({
    page,
  }) => {
    const stamp = String(Date.now());
    const base = await apiBase(page);
    const song = await songWithVersions(page, base, `Version delete ${stamp}`, [
      '1.1',
      '1.1.1',
      '2',
    ]);

    // 1. Open 1.1 and delete it: the confirmation says one descendant will remain.
    await page.goto(`./songs/${song.shortcode}/v/1.1`);
    await expect(page.getByRole('heading', { level: 3, name: 'Version 1.1' })).toBeVisible();
    await expect(page.getByRole('textbox', { name: 'Lyrics' })).toBeVisible();
    await page.getByRole('button', { name: 'Delete', exact: true }).click();
    const confirm = page.getByRole('dialog', { name: 'Delete Version 1.1?' });
    await expect(confirm).toBeVisible();
    const summary = confirm.getByTestId('delete-version-summary');
    await expect(summary).toContainText('1 descendant Version will remain');
    await expect(summary).toContainText('deleted permanently');
    await expectModalAccessibleInBothSchemes(page);
    await confirm.getByRole('button', { name: 'Delete Version 1.1' }).click();
    await expect(confirm).toBeHidden();

    // 2. The tree: 1, the placeholder for 1.1 with 1.1.1 beneath it, and 2, which is selected.
    const placeholder = tree(page).getByRole('treeitem', { name: 'Deleted Version 1.1' });
    await expect(placeholder).toBeVisible();
    await expect(placeholder).toHaveAttribute('aria-disabled', 'true');
    await expect(
      tree(page)
        .locator('[id="version-group-deleted-1.1"]')
        .locator('[data-version-number="1.1.1"]'),
    ).toBeVisible();
    await expect(node(page, '1')).toBeVisible();
    await expect(node(page, '2')).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByTestId('version-deleted')).toContainText('Version 1.1 deleted.');
    await expect(page.getByRole('heading', { level: 3, name: 'Version 2' })).toBeVisible();
    expect(await versionNumbers(page, base, song)).toEqual(['1', '1.1.1', '2']);
    await expectAccessibleInLightAndDark(page);

    // Its page says it was deleted.
    await page.goto(`./songs/${song.shortcode}/v/1.1`);
    await expect(
      page.getByRole('heading', { level: 3, name: 'Version 1.1 was deleted' }),
    ).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // 3. Create a new Version from 1: 1.1 is not offered.
    await node(page, '1').click();
    await expect(page.getByRole('heading', { level: 3, name: 'Version 1' })).toBeVisible();
    await page.getByRole('button', { name: 'Create New Version From 1' }).click();
    const create = page.getByRole('dialog', { name: 'Create New Version From 1' });
    await expect(create.getByRole('radio').first()).toBeVisible();
    await expect(create.getByRole('radio', { name: /^1\.2/ })).toBeVisible();
    await expect(create.getByRole('radio', { name: /^1\.1/ })).toHaveCount(0);
    await create.getByRole('button', { name: 'Cancel' }).click();
    await expect(create).toBeHidden();

    // 4. A Song with only Version 1: deleting it leaves an empty Version 2.
    const only = await songWithVersions(page, base, `Only Version ${stamp}`, []);
    await page.goto(`./songs/${only.shortcode}`);
    await expect(page.getByRole('textbox', { name: 'Lyrics' })).toBeVisible();
    await page.getByRole('button', { name: 'Actions for Version 1' }).click();
    await expect(page.getByRole('menuitem', { name: 'Delete' })).toBeVisible();
    await expectNoA11yViolations(page);
    await page.getByRole('menuitem', { name: 'Delete' }).click();
    const last = page.getByRole('dialog', { name: 'Delete Version 1?' });
    await expect(last.getByTestId('delete-version-summary')).toContainText(
      'a new blank Version is created',
    );
    await last.getByRole('button', { name: 'Delete Version 1' }).click();
    await expect(last).toBeHidden();
    await expect(page.getByTestId('version-deleted')).toContainText(
      'a new blank Version 2 was created and is current',
    );
    await expect(page.getByRole('heading', { level: 3, name: 'Version 2' })).toBeVisible();
    await expect(node(page, '2')).toHaveAttribute('aria-current', 'true');
    expect(await versionNumbers(page, base, only)).toEqual(['2']);
    await expectAccessibleInLightAndDark(page);
  });
});
