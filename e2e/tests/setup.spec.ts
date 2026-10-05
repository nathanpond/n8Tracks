import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark } from '../support/a11y.ts';
import {
  removeContainers,
  startContainer,
  waitForHealth,
  type Target,
} from '../support/containers.ts';
import { signInHeading, signInWithTheForm, userMenu } from '../support/session.ts';
import { isSetupComplete } from '../support/setup.ts';
import { FRESH_NAME, FRESH_PORT, FRESH_URL } from '../support/targets.ts';

const fresh: Target = {
  name: FRESH_NAME,
  port: FRESH_PORT,
  url: FRESH_URL,
  media: true,
  expectedStatus: 'healthy',
};

const OWNER = { username: 'demo-owner', password: 'a long demo password' };

function stepHeading(page: Page, name: string) {
  return page.getByRole('heading', { level: 3, name });
}

/**
 * Walks the story's Demo on a container of its own, started with an empty data folder for each
 * attempt (so a retry meets a new instance too), and removed afterwards.
 */
test.describe('first-run setup', { tag: '@root-only' }, () => {
  // A new instance has no session to start from.
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async () => {
    test.setTimeout(120_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-fresh-'));
    await mkdir(join(work, 'media'));
    await startContainer(fresh, work);
    await waitForHealth(fresh);
  });

  test.afterEach(async () => {
    await removeContainers(FRESH_NAME);
    if (work !== undefined) {
      await rm(work, { recursive: true, force: true });
    }
  });

  test('walks a new instance through setup, after which the wizard never comes back', async ({
    page,
    request,
  }) => {
    // 1. A container with an empty data folder: the API refuses everything but setup.
    expect(await isSetupComplete(FRESH_URL)).toBe(false);
    const refused = await request.get(`${FRESH_URL}api/v1/songs`);
    expect(refused.status()).toBe(503);
    expect(await refused.json()).toMatchObject({ code: 'setup_required' });

    // 2. Any page redirects to the wizard; storage is writable.
    await page.goto(`${FRESH_URL}library/deep/link`);
    await expect(page).toHaveURL(`${FRESH_URL}setup`);
    await expect(page.getByRole('heading', { level: 2, name: 'Set up n8Tracks' })).toBeVisible();
    await expect(stepHeading(page, 'Storage')).toBeVisible();
    await expect(page.getByText('writable', { exact: true })).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // The media step shows the mounted (empty) media folder as available.
    await page.getByRole('button', { name: 'Next' }).click();
    await expect(stepHeading(page, 'Media library')).toBeVisible();
    await expect(page.getByText('available', { exact: true })).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // The backup step is skipped: the next step is the administrator.
    await page.getByRole('button', { name: 'Next' }).click();
    await expect(stepHeading(page, 'Administrator')).toBeVisible();
    await expect(page.getByText('Skipped for now')).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // A refused submission shows its field errors (the API checks the confirmation).
    await page.getByLabel('Username').fill(OWNER.username);
    await page.getByLabel(/^Password/).fill(OWNER.password);
    await page.getByLabel('Repeat the password').fill(`${OWNER.password} typo`);
    await page.getByRole('button', { name: 'Finish setup' }).click();
    await expect(page.getByText('The passwords do not match.')).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // 3. Enter a matching password and finish.
    await page.getByLabel('Repeat the password').fill(OWNER.password);
    await page.getByRole('button', { name: 'Finish setup' }).click();

    // 4. The app asks the new administrator to sign in, and then shows the shell.
    await expect(signInHeading(page)).toBeVisible();
    await expect(page).toHaveURL(`${FRESH_URL}sign-in`);
    expect(await isSetupComplete(FRESH_URL)).toBe(true);
    await expectAccessibleInLightAndDark(page);
    await signInWithTheForm(page, OWNER.username, OWNER.password);
    await expect(page.getByRole('heading', { level: 2, name: 'Songs' })).toBeVisible();
    await expect(userMenu(page)).toHaveText(OWNER.username);
    await expect(page).toHaveURL(`${FRESH_URL}songs`);
    await expectAccessibleInLightAndDark(page);

    // 5. Reload any URL: the wizard does not come back.
    for (const [path, heading] of [
      ['setup', 'Songs'],
      ['settings/system', 'System'],
      ['', 'Songs'],
    ] as const) {
      await page.goto(`${FRESH_URL}${path}`);
      await expect(page.getByRole('heading', { level: 2, name: heading })).toBeVisible();
      await expect(page.getByRole('heading', { name: 'Set up n8Tracks' })).toBeHidden();
    }
    await expect(page).toHaveURL(`${FRESH_URL}songs`);

    // And the API: setup is refused, the other endpoint is no longer 503 but asks for a session.
    const again = await request.post(`${FRESH_URL}api/v1/setup`, {
      data: { username: 'late', password: OWNER.password, passwordConfirmation: OWNER.password },
    });
    expect(again.status()).toBe(409);
    expect(await again.json()).toMatchObject({ code: 'setup_already_complete' });
    expect((await request.get(`${FRESH_URL}api/v1/songs`)).status()).toBe(401);
  });
});
