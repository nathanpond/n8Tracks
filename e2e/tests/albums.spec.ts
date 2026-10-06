import { expect, test, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';

function sidebar(page: Page) {
  return page.getByRole('navigation', { name: 'Main' });
}

/** The Album page's save status, once the last field saved. */
async function expectSaved(page: Page): Promise<void> {
  await expect(page.getByTestId('album-save-status')).toHaveText('Saved.');
}

/**
 * Walks the story's Demo on the project's shared container, with names stamped with this run
 * (Albums and Artists are instance-wide): "Pack EP" created from the Albums page, given an Album
 * Artist created on the spot, a release date, and a UPC; a UPC with a wrong check digit refused
 * with a field error; and the Album found in the list with its Album Artist and date. Each state is
 * scanned with axe in both colour schemes.
 */
test.describe('Albums', () => {
  test('creates an Album and records how it will be released', async ({ page }) => {
    const stamp = String(Date.now()).slice(-7);
    const title = `Pack EP ${stamp}`;
    const artist = `Pack Act ${stamp}`;

    // 1. Open Albums, create "Pack EP", choose an Album Artist, set a release date and a UPC.
    await page.goto('./songs');
    await sidebar(page).getByRole('link', { name: 'Albums' }).click();
    await expect(page.getByRole('heading', { level: 2, name: 'Albums' })).toBeVisible();
    await expect(sidebar(page).getByRole('link', { name: 'Albums' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    await expectAccessibleInLightAndDark(page);

    await page.getByRole('button', { name: 'New Album' }).first().click();
    const dialog = page.getByRole('dialog', { name: 'New Album' });
    await expect(dialog).toBeVisible();
    await expectModalAccessibleInBothSchemes(page);
    await dialog.getByRole('textbox', { name: 'Title' }).fill(title);
    await dialog.getByRole('button', { name: 'Create Album' }).click();

    await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();
    await expect(page).toHaveURL(/\/albums\/[0-9a-f-]{36}$/);

    await page.getByRole('textbox', { name: 'Choose an Album Artist' }).fill(artist);
    await page.getByRole('option', { name: `Create Artist “${artist}”` }).click();
    await expect(page.getByTestId('album-artist')).toContainText(artist);
    await expectSaved(page);

    await page.getByRole('textbox', { name: 'Release date', exact: true }).fill('2026-03-01');
    await page.getByRole('textbox', { name: 'Release date', exact: true }).press('Enter');
    await expect(page.getByTestId('releaseDate-shown')).toHaveText('Shown as March 1, 2026');
    await page.getByRole('textbox', { name: 'UPC/EAN' }).fill('0 36000 29145 2');
    await page.getByRole('textbox', { name: 'UPC/EAN' }).press('Enter');
    await expect(page.getByRole('textbox', { name: 'UPC/EAN' })).toHaveValue('036000291452');
    await expectSaved(page);
    await expectAccessibleInLightAndDark(page);

    // Kept after a reload.
    await page.reload();
    await expect(page.getByRole('textbox', { name: 'Release date', exact: true })).toHaveValue(
      '2026-03-01',
    );
    await expect(page.getByRole('textbox', { name: 'UPC/EAN' })).toHaveValue('036000291452');
    await expect(page.getByTestId('album-artist')).toContainText(artist);

    // 2. A UPC with a wrong check digit is refused with a field error, and nothing changes.
    await page.getByRole('textbox', { name: 'UPC/EAN' }).fill('036000291453');
    await page.getByRole('textbox', { name: 'UPC/EAN' }).press('Enter');
    await expect(page.getByRole('textbox', { name: 'UPC/EAN' })).toHaveAttribute(
      'aria-invalid',
      'true',
    );
    await expect(
      page.getByText('The check digit is wrong: check the code for a typing mistake.'),
    ).toBeVisible();
    await expectAccessibleInLightAndDark(page);
    await page.reload();
    await expect(page.getByRole('textbox', { name: 'UPC/EAN' })).toHaveValue('036000291452');

    // 3. The Album appears in the list, with its Album Artist and release date.
    await page.getByRole('link', { name: '← Albums' }).click();
    await expect(page.getByRole('heading', { level: 2, name: 'Albums' })).toBeVisible();
    const row = page.locator(`tr[data-album-title="${title}"]`);
    await expect(row).toBeVisible();
    await expect(row.getByRole('cell')).toHaveText([artist, '0', 'March 1, 2026']);
    await expectAccessibleInLightAndDark(page);

    // The Artist page lists the Album too.
    await page.goto('./artists?search=' + encodeURIComponent(artist));
    await page.getByRole('link', { name: artist }).click();
    await expect(
      page.getByRole('region', { name: 'Albums' }).getByRole('link', { name: title }),
    ).toBeVisible();
  });
});
