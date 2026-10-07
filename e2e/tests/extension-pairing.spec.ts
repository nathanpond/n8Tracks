import { expect, test, type APIRequestContext } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';

/**
 * What the extension's handshake gets for `token`, from a client with no cookies, sending the
 * versions the extension would.
 */
async function handshake(
  request: APIRequestContext,
  baseURL: string,
  token: string,
): Promise<{ status: number; body: Record<string, unknown> }> {
  const response = await request.get(new URL('api/v1/extension/handshake', baseURL).toString(), {
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: 'application/json',
      'X-N8Tracks-Extension-Version': '0.1.0',
      'X-N8Tracks-Adapter-Version': '1',
    },
  });
  return { status: response.status(), body: (await response.json()) as Record<string, unknown> };
}

/**
 * The web side of the extension pairing Demo, on the shared container: an extension credential
 * with the two Suno scopes (step 1), the handshake the extension makes with its token (step 3),
 * the versions it reported shown in Settings, and the handshake refused after revocation (step 4).
 * The browser's permission prompt (step 2) is the owner's manual step.
 */
test.describe('Extension pairing (web side)', () => {
  test('an extension credential with the Suno scopes passes the handshake until revoked', async ({
    page,
    playwright,
  }) => {
    const name = `Chrome at home ${String(Date.now())}`;

    await page.goto('./settings/credentials');
    const baseURL = new URL('..', page.url()).toString();
    await expect(page.getByRole('heading', { level: 2, name: 'Credentials' })).toBeVisible();

    // 1. Create a credential of kind extension with catalog.read, suno.sync, and suno.generate.
    await page.getByRole('button', { name: 'Create credential' }).click();
    const form = page.getByRole('form', { name: 'Create credential' });
    await expect(form).toBeVisible();
    await form.getByLabel('Name').fill(name);
    await form.getByRole('radio', { name: 'Extension' }).check();
    await expect(form.getByRole('radio', { name: 'Extension' })).toBeChecked();
    await expect(form.getByRole('checkbox', { name: /^suno\.sync/ })).toBeVisible();
    await expect(form.getByRole('checkbox', { name: /^suno\.generate/ })).toBeVisible();
    await form.getByRole('checkbox', { name: /^catalog\.read/ }).check();
    await form.getByRole('checkbox', { name: /^suno\.sync/ }).check();
    await form.getByRole('checkbox', { name: /^suno\.generate/ }).check();
    await expectModalAccessibleInBothSchemes(page);
    await form.getByRole('button', { name: 'Create credential' }).click();

    const shown = page.getByRole('dialog', { name: 'Copy the new token' });
    await expect(shown).toBeVisible();
    const token = await shown.getByLabel('Token').inputValue();
    await shown.getByRole('button', { name: 'Done' }).click();
    await expect(shown).toBeHidden();

    const row = page
      .getByRole('table', { name: 'Credentials' })
      .getByRole('row')
      .filter({ has: page.getByRole('rowheader', { name }) });
    await expect(row.getByTestId('extension-seen')).toHaveText('Extension not connected yet');

    // 3. The extension's handshake: the credential's name and scopes come back.
    const tokenOnly = await playwright.request.newContext({ storageState: undefined });
    try {
      const first = await handshake(tokenOnly, baseURL, token);
      expect(first.status).toBe(200);
      expect(first.body.credentialName).toBe(name);
      expect(first.body.scopes).toEqual(['catalog.read', 'suno.sync', 'suno.generate']);
      expect(typeof first.body.applicationVersion).toBe('string');
      expect(typeof first.body.compatible).toBe('boolean');

      // Settings shows the versions it reported.
      await page.reload();
      await expect(row.getByTestId('extension-seen')).toContainText('Extension 0.1.0, adapter 1');
      await expectAccessibleInLightAndDark(page);

      // 4. Revoke it: the next handshake is refused, which the extension shows as Disconnected.
      await row.getByRole('button', { name: `Revoke ${name}` }).click();
      const confirm = page.getByRole('dialog', { name: 'Revoke credential?' });
      await confirm.getByRole('button', { name: 'Revoke credential' }).click();
      await expect(confirm).toBeHidden();
      await expect(row.getByText(/^Revoked /)).toBeVisible();

      const after = await handshake(tokenOnly, baseURL, token);
      expect(after.status).toBe(401);
      expect(after.body.code).toBe('invalid_token');
    } finally {
      await tokenOnly.dispose();
    }
  });
});
