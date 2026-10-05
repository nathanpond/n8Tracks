import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test } from '@playwright/test';
import { expectAccessibleInLightAndDark, expectNoA11yViolations } from '../support/a11y.ts';
import {
  docker,
  removeContainers,
  startContainer,
  waitForHealth,
  type Target,
} from '../support/containers.ts';
import { SESSION_COOKIE, signInHeading, signInWithTheForm, userMenu } from '../support/session.ts';
import { completeSetup, TEST_ADMIN } from '../support/setup.ts';
import { FRESH_NAME, FRESH_PORT, FRESH_URL } from '../support/targets.ts';

const fresh: Target = {
  name: FRESH_NAME,
  port: FRESH_PORT,
  url: FRESH_URL,
  media: true,
  expectedStatus: 'healthy',
};

/**
 * Walks the story's Demo on a container of its own (it is restarted), set up through the API with
 * the test administrator, started afresh for each attempt and removed afterwards.
 */
test.describe('signing in and out', { tag: '@root-only' }, () => {
  // A private window: no session to start from.
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async () => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-sign-in-'));
    await mkdir(join(work, 'media'));
    await startContainer(fresh, work);
    await waitForHealth(fresh);
    await completeSetup(FRESH_URL);
  });

  test.afterEach(async () => {
    await removeContainers(FRESH_NAME);
    if (work !== undefined) {
      await rm(work, { recursive: true, force: true });
    }
  });

  test('signs in, survives a restart, and signs out everywhere', async ({ browser, page }) => {
    // 1. Open the app in a private window: the sign-in page appears, remembering where it was going.
    await page.goto(`${FRESH_URL}settings/system`);
    await expect(signInHeading(page)).toBeVisible();
    await expect(page).toHaveURL(`${FRESH_URL}sign-in?returnTo=%2Fsettings%2Fsystem`);
    await expect(userMenu(page)).toBeHidden();
    await expectAccessibleInLightAndDark(page);

    // 2. A wrong password: one generic error.
    await signInWithTheForm(page, TEST_ADMIN.username, 'not the right password');
    await expect(page.getByText('The username or password is incorrect.')).toBeVisible();
    await expect(signInHeading(page)).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // 3. Sign in (the username in another case): the app, at the page asked for, with the username in a menu.
    await signInWithTheForm(page, TEST_ADMIN.username.toUpperCase(), TEST_ADMIN.password);
    await expect(page.getByRole('heading', { level: 2, name: 'System' })).toBeVisible();
    await expect(page).toHaveURL(`${FRESH_URL}settings/system`);
    await expect(userMenu(page)).toHaveText(TEST_ADMIN.username);
    await expectAccessibleInLightAndDark(page);

    const [cookie] = (await page.context().cookies(FRESH_URL)).filter(
      (candidate) => candidate.name === SESSION_COOKIE,
    );
    expect(cookie).toMatchObject({ httpOnly: true, sameSite: 'Lax', secure: false, path: '/' });

    await userMenu(page).click();
    await expect(page.getByRole('menuitem', { name: 'Sign out everywhere' })).toBeVisible();
    await expect(page.getByRole('menuitem', { name: 'Sign out', exact: true })).toBeVisible();
    await expectNoA11yViolations(page);
    await page.keyboard.press('Escape');

    // 4. Restart the container and reload: still signed in.
    await docker('restart', FRESH_NAME);
    await waitForHealth(fresh);
    await page.reload();
    await expect(userMenu(page)).toHaveText(TEST_ADMIN.username);
    await expect(page.getByRole('heading', { level: 2, name: 'System' })).toBeVisible();

    // A second browser, signed in too.
    const second = await browser.newContext({ storageState: { cookies: [], origins: [] } });
    try {
      const other = await second.newPage();
      await other.goto(FRESH_URL);
      await expect(signInHeading(other)).toBeVisible();
      await signInWithTheForm(other, TEST_ADMIN.username, TEST_ADMIN.password);
      await expect(userMenu(other)).toHaveText(TEST_ADMIN.username);

      // 5. Sign out everywhere: the sign-in page returns here...
      await userMenu(page).click();
      await page.getByRole('menuitem', { name: 'Sign out everywhere' }).click();
      await expect(signInHeading(page)).toBeVisible();
      await expectAccessibleInLightAndDark(page);

      // ...and the second browser is signed out on its next request.
      const refused = await other.request.get(`${FRESH_URL}api/v1/session`);
      expect(refused.status()).toBe(401);
      await other.reload();
      await expect(signInHeading(other)).toBeVisible();
    } finally {
      await second.close();
    }
  });
});
