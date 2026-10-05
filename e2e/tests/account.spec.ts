import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark, expectNoA11yViolations } from '../support/a11y.ts';
import {
  removeContainers,
  startContainer,
  waitForHealth,
  type Target,
} from '../support/containers.ts';
import {
  ANTIFORGERY_HEADERS,
  signInHeading,
  signInWithTheForm,
  userMenu,
} from '../support/session.ts';
import { componentRows, overallStatus } from '../support/shell.ts';
import { completeSetup, TEST_ADMIN } from '../support/setup.ts';
import { FRESH_NAME, FRESH_PORT, FRESH_URL } from '../support/targets.ts';

const fresh: Target = {
  name: FRESH_NAME,
  port: FRESH_PORT,
  url: FRESH_URL,
  media: true,
  expectedStatus: 'healthy',
};

/** Test values, not real credentials. */
const SECOND_PASSWORD = 'e2e-second-password';
const THIRD_PASSWORD = 'e2e-third-password';

const CHANGED = 'Your password has been changed. Every other session has been signed out.';
const PROMPT = 'Your session has ended';

/**
 * The prompt is modal, so the colour control behind it cannot be clicked: the scheme the page is
 * drawn in is switched the way the control switches it, on the root element, and the page scanned
 * in each. The scheme the page was in is put back.
 */
async function expectPromptAccessibleInBothSchemes(page: Page): Promise<void> {
  const html = page.locator('html');
  const before = await html.getAttribute('data-mantine-color-scheme');
  for (const scheme of ['light', 'dark'] as const) {
    await html.evaluate((element, value) => {
      element.setAttribute('data-mantine-color-scheme', value);
    }, scheme);
    await expectNoA11yViolations(page);
  }
  await html.evaluate((element, value) => {
    element.setAttribute('data-mantine-color-scheme', value ?? 'dark');
  }, before);
}

function sidebar(page: Page) {
  return page.getByRole('navigation', { name: 'Main' });
}

function changePasswordForm(page: Page) {
  return page.getByRole('form', { name: 'Change password' });
}

async function fillChange(page: Page, current: string, next: string): Promise<void> {
  const form = changePasswordForm(page);
  await form.getByLabel('Current password').fill(current);
  await form.getByLabel(/^New password/).fill(next);
  await form.getByLabel('Confirm new password').fill(next);
}

/**
 * Walks the story's Demo on a container of its own (the password changes), set up through the API
 * with the test administrator, started afresh for each attempt and removed afterwards.
 */
test.describe(
  'the signed-in shell, re-signing in, and changing the password',
  { tag: '@root-only' },
  () => {
    // A private window: no session to start from.
    test.use({ storageState: { cookies: [], origins: [] } });

    let work: string | undefined;

    test.beforeEach(async () => {
      test.setTimeout(180_000);
      await removeContainers(FRESH_NAME);
      work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-account-'));
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

    test('lands where it was going, changes the password, and signs in again in place', async ({
      browser,
      page,
    }) => {
      // 1. Sign in from a deep link: that page, inside the shell.
      await page.goto(`${FRESH_URL}settings/account`);
      await expect(signInHeading(page)).toBeVisible();
      await signInWithTheForm(page, TEST_ADMIN.username, TEST_ADMIN.password);
      await expect(page.getByRole('heading', { level: 2, name: 'Account' })).toBeVisible();
      await expect(page).toHaveURL(`${FRESH_URL}settings/account`);
      await expect(sidebar(page).getByRole('link')).toHaveText(['Songs', 'Account', 'System']);
      await expect(sidebar(page).getByRole('link', { name: 'Account' })).toHaveAttribute(
        'aria-current',
        'page',
      );
      await expect(userMenu(page)).toHaveText(TEST_ADMIN.username);
      await expectAccessibleInLightAndDark(page);

      await userMenu(page).click();
      await expect(page.getByRole('menuitem', { name: 'Sign out', exact: true })).toBeVisible();
      await expect(page.getByRole('menuitem', { name: 'Sign out everywhere' })).toBeVisible();
      await expectNoA11yViolations(page);
      await page.keyboard.press('Escape');

      const second = await browser.newContext({ storageState: { cookies: [], origins: [] } });
      try {
        const other = await second.newPage();
        await other.goto(FRESH_URL);
        await signInWithTheForm(other, TEST_ADMIN.username, TEST_ADMIN.password);
        await expect(other.getByRole('heading', { level: 2, name: 'Songs' })).toBeVisible();

        // 2. A wrong current password is refused at the field...
        await fillChange(page, 'not the current password', SECOND_PASSWORD);
        await changePasswordForm(page).getByRole('button', { name: 'Change password' }).click();
        await expect(page.getByText('The current password is incorrect.')).toBeVisible();
        await expect(page.getByRole('dialog')).toBeHidden();
        await expectAccessibleInLightAndDark(page);

        // ...the right one changes it, and the second browser is signed out on its next request.
        await fillChange(page, TEST_ADMIN.password, SECOND_PASSWORD);
        await changePasswordForm(page).getByRole('button', { name: 'Change password' }).click();
        await expect(page.getByText(CHANGED)).toBeVisible();
        await expectAccessibleInLightAndDark(page);

        const refused = await other.request.get(`${FRESH_URL}api/v1/session`);
        expect(refused.status()).toBe(401);
        await other.reload();
        await expect(signInHeading(other)).toBeVisible();

        // 3. With this page open and work typed in, the session ends elsewhere (sign out everywhere).
        await signInWithTheForm(other, TEST_ADMIN.username, SECOND_PASSWORD);
        await expect(userMenu(other)).toHaveText(TEST_ADMIN.username);
        await fillChange(page, SECOND_PASSWORD, THIRD_PASSWORD);
        await userMenu(other).click();
        await other.getByRole('menuitem', { name: 'Sign out everywhere' }).click();
        await expect(signInHeading(other)).toBeVisible();

        // Clicking something brings up the prompt over the page, which is still as it was.
        await changePasswordForm(page).getByRole('button', { name: 'Change password' }).click();
        const prompt = page.getByRole('dialog', { name: PROMPT });
        await expect(prompt).toBeVisible();
        await expect(prompt.getByRole('textbox', { name: 'Username' })).toHaveValue(
          TEST_ADMIN.username,
        );
        await expect(changePasswordForm(page).getByLabel('Confirm new password')).toHaveValue(
          THIRD_PASSWORD,
        );
        await expectPromptAccessibleInBothSchemes(page);

        // A wrong password in the prompt is the prompt's own error; the prompt stays.
        await signInWithTheForm(page, TEST_ADMIN.username, 'not the right password');
        await expect(prompt.getByText('The username or password is incorrect.')).toBeVisible();
        await expectNoA11yViolations(page);

        // Signing in completes the action that was held: the password is changed.
        await signInWithTheForm(page, TEST_ADMIN.username, SECOND_PASSWORD);
        await expect(prompt).toBeHidden();
        await expect(page.getByText(CHANGED)).toBeVisible();
        await expect(page).toHaveURL(`${FRESH_URL}settings/account`);
        await expect(userMenu(page)).toHaveText(TEST_ADMIN.username);

        const signIn = await other.request.post(`${FRESH_URL}api/v1/session`, {
          headers: ANTIFORGERY_HEADERS,
          data: { username: TEST_ADMIN.username, password: THIRD_PASSWORD },
        });
        expect(signIn.status()).toBe(201);
      } finally {
        await second.close();
      }

      // 4. Settings → System: the version and the health components.
      await sidebar(page).getByRole('link', { name: 'System' }).click();
      await expect(page.getByRole('heading', { level: 2, name: 'System' })).toBeVisible();
      await expect(page).toHaveURL(`${FRESH_URL}settings/system`);
      await expect(page.getByTestId('version')).not.toBeEmpty();
      await expect(overallStatus(page)).toHaveText('healthy');
      await expect(componentRows(page)).toHaveCount(4);
      await expectAccessibleInLightAndDark(page);
    });
  },
);
