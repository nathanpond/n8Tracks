import { expect, test, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';

function sidebar(page: Page) {
  return page.getByRole('navigation', { name: 'Main' });
}

/** Opens the New Artist dialog from the Artists page, types `name`, and submits it. */
async function submitNewArtist(page: Page, name: string): Promise<void> {
  await page.getByRole('button', { name: 'New Artist' }).first().click();
  const dialog = page.getByRole('dialog', { name: 'New Artist' });
  await expect(dialog).toBeVisible();
  await dialog.getByRole('textbox', { name: 'Name' }).fill(name);
  await dialog.getByRole('button', { name: 'Create Artist' }).click();
}

/**
 * Walks the story's Demo on the project's shared container, with names stamped with this run
 * (Artists are instance-wide): an Artist "n8" created from the Artists page and given an alias and
 * a link on its page; a second named "N8", which asks for confirmation naming the first, created
 * once confirmed; and both found in the list by a search in the URL. Each state is scanned with axe
 * in both colour schemes.
 */
test.describe('Artists', () => {
  test('keeps reusable Artist records, confirming a name another Artist has', async ({ page }) => {
    const stamp = String(Date.now()).slice(-7);
    const name = `n8 ${stamp}`;
    const alias = `Nate ${stamp}`;

    // 1. Open Artists and create "n8"; add an alias and a link.
    await page.goto('./songs');
    await sidebar(page).getByRole('link', { name: 'Artists' }).click();
    await expect(page.getByRole('heading', { level: 2, name: 'Artists' })).toBeVisible();
    await expect(sidebar(page).getByRole('link', { name: 'Artists' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    await expectAccessibleInLightAndDark(page);

    await page.getByRole('button', { name: 'New Artist' }).first().click();
    await expect(page.getByRole('dialog', { name: 'New Artist' })).toBeVisible();
    await expectModalAccessibleInBothSchemes(page);
    await page.keyboard.press('Escape');
    await submitNewArtist(page, name);

    await expect(page.getByRole('heading', { level: 2, name })).toBeVisible();
    await expect(page).toHaveURL(/\/artists\/[0-9a-f-]{36}$/);
    const first = page.url();
    const form = page.getByRole('form', { name: 'Artist details' });
    await form.getByRole('button', { name: 'Add alias' }).click();
    await form.getByRole('textbox', { name: 'Alias 1' }).fill(alias);
    await form.getByRole('button', { name: 'Add link' }).click();
    await form.getByRole('textbox', { name: 'Link 1 label' }).fill('Site');
    await form.getByRole('textbox', { name: 'Link 1 URL' }).fill('https://n8.example/');
    await form.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(form.getByText('Saved.')).toBeVisible();
    await expect(form.getByRole('link', { name: 'Site' })).toHaveAttribute(
      'href',
      'https://n8.example/',
    );
    await expect(page.getByRole('region', { name: 'Songs' })).toContainText(
      'No Songs are credited to this Artist yet.',
    );
    await expect(page.getByRole('region', { name: 'Albums' })).toContainText(
      'This Artist is not the Album Artist of any Album yet.',
    );
    await expectAccessibleInLightAndDark(page);

    // Kept after a reload.
    await page.reload();
    await expect(form.getByRole('textbox', { name: 'Alias 1' })).toHaveValue(alias);
    await expect(form.getByRole('textbox', { name: 'Link 1 URL' })).toHaveValue(
      'https://n8.example/',
    );

    // A link that is not a web address is refused before anything is sent.
    await form.getByRole('textbox', { name: 'Link 1 URL' }).fill('ftp://n8.example/');
    await form.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(
      form.getByText('Enter a web address starting with http:// or https://.'),
    ).toBeVisible();
    await form.getByRole('button', { name: 'Discard changes' }).click();

    // 2. Create a second Artist named "N8"; a confirmation appears; confirm.
    await page.getByRole('link', { name: '← Artists' }).click();
    await expect(page.getByRole('heading', { level: 2, name: 'Artists' })).toBeVisible();
    await submitNewArtist(page, name.toUpperCase());
    const dialog = page.getByRole('dialog', { name: 'New Artist' });
    await expect(dialog.getByText('Another Artist has this name')).toBeVisible();
    await expect(dialog.getByTestId('duplicate-matches')).toContainText(`${name} (its name)`);
    await expectModalAccessibleInBothSchemes(page);
    await dialog.getByRole('button', { name: 'Create anyway' }).click();
    await expect(page.getByRole('heading', { level: 2, name: name.toUpperCase() })).toBeVisible();
    expect(page.url()).not.toBe(first);

    // 3. Both appear in the list.
    await page.goto(`./artists?search=${encodeURIComponent(stamp)}`);
    const table = page.getByRole('table', { name: 'Artists' });
    await expect(table.getByRole('rowheader')).toHaveText([name, name.toUpperCase()]);
    await expect(
      table.getByRole('row').filter({ has: page.getByRole('link', { name, exact: true }) }),
    ).toContainText(alias);
    await expect(page.getByRole('searchbox', { name: 'Search Artists' })).toHaveValue(stamp);
    await expectAccessibleInLightAndDark(page);
  });
});
