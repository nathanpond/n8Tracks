import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';

/** The pattern of a credential's token: the prefix and 43 base62 characters. */
const TOKEN_PATTERN = /^n8t_[0-9A-Za-z]{43}$/;

function credentialsTable(page: Page) {
  return page.getByRole('table', { name: 'Credentials' });
}

function credentialRow(page: Page, name: string) {
  return credentialsTable(page)
    .getByRole('row')
    .filter({ has: page.getByRole('rowheader', { name }) });
}

/**
 * What `curl -H "Authorization: Bearer <token>" <base>/api/v1/jobs` gets, from a client with no
 * cookies. Background jobs arrive with their own story (#57); until then the path is the API's
 * 404, which answers any valid token and refuses an invalid one with 401 like every endpoint.
 */
async function callWithToken(
  request: APIRequestContext,
  baseURL: string,
  token: string,
): Promise<{ status: number; code: unknown }> {
  const response = await request.get(new URL('api/v1/jobs', baseURL).toString(), {
    headers: { Authorization: `Bearer ${token}`, Accept: 'application/json' },
  });
  const body: unknown = await response.json();
  const code = typeof body === 'object' && body !== null && 'code' in body ? body.code : undefined;
  return { status: response.status(), code };
}

/** Walks the story's Demo on the project's shared container, signed in. */
test.describe('Settings → Credentials', () => {
  test('creates a token shown once, which works until it is revoked', async ({
    page,
    context,
    playwright,
  }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    // Unique per attempt: a failed attempt leaves its credential in force, holding its name.
    const name = `test script ${String(Date.now())}`;

    // 1. Settings → Credentials, from the sidebar.
    await page.goto('./songs');
    // The app's base (the root or the sub-path), as the page itself resolves it.
    const baseURL = new URL('.', page.url()).toString();
    await page
      .getByRole('navigation', { name: 'Main' })
      .getByRole('link', { name: 'Credentials' })
      .click();
    await expect(page.getByRole('heading', { level: 2, name: 'Credentials' })).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // Create "test script" of kind api with only catalog.read.
    await page.getByRole('button', { name: 'Create credential' }).click();
    const form = page.getByRole('form', { name: 'Create credential' });
    await expect(form).toBeVisible();
    await form.getByLabel('Name').fill(name);
    await expect(form.getByRole('radio', { name: 'API' })).toBeChecked();
    await form.getByRole('checkbox', { name: /catalog\.read/ }).check();
    await expectModalAccessibleInBothSchemes(page);
    await form.getByRole('button', { name: 'Create credential' }).click();

    // 2. The token, shown this once, with a copy control.
    const shown = page.getByRole('dialog', { name: 'Copy the new token' });
    await expect(shown).toBeVisible();
    const token = await shown.getByLabel('Token').inputValue();
    expect(token).toMatch(TOKEN_PATTERN);
    await shown.getByRole('button', { name: 'Copy token' }).click();
    await expect(shown.getByText('The token is on the clipboard.')).toBeVisible();
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(token);
    await expectModalAccessibleInBothSchemes(page);
    await shown.getByRole('button', { name: 'Done' }).click();
    await expect(shown).toBeHidden();

    // After a reload only the name, kind, scopes, and dates are shown: never the token.
    await page.reload();
    const row = credentialRow(page, name);
    await expect(row).toBeVisible();
    await expect(row.getByRole('cell').nth(0)).toHaveText('API');
    await expect(row.getByRole('cell').nth(1)).toHaveText('catalog.read');
    await expect(row.getByRole('cell').nth(4)).toHaveText('Active');
    await expect(page.locator('body')).not.toContainText(token.slice(4));
    await expectAccessibleInLightAndDark(page);

    // 3. The token authenticates a request from a client with no session (a 404 until #57, not 401).
    const curl = await playwright.request.newContext();
    try {
      expect(await callWithToken(curl, baseURL, token)).toEqual({ status: 404, code: 'not_found' });

      // The use shows as the last-used date once the page loads the list again.
      await page.reload();
      await expect(row.getByRole('cell').nth(3)).not.toHaveText('Never');

      // 4. Revoke, after confirming; the same request is now refused.
      await row.getByRole('button', { name: `Revoke ${name}` }).click();
      const confirm = page.getByRole('dialog', { name: 'Revoke credential?' });
      await expect(confirm).toBeVisible();
      await expectModalAccessibleInBothSchemes(page);
      await confirm.getByRole('button', { name: 'Revoke credential' }).click();
      await expect(confirm).toBeHidden();
      await expect(row.getByRole('cell').nth(4)).toHaveText(/^Revoked /);
      await expect(row.getByRole('button')).toHaveCount(0);
      await expectAccessibleInLightAndDark(page);

      expect(await callWithToken(curl, baseURL, token)).toEqual({
        status: 401,
        code: 'invalid_token',
      });
    } finally {
      await curl.dispose();
    }
  });
});
