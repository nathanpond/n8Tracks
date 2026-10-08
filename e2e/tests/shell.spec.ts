import { readFileSync } from 'node:fs';
import { expect, test } from '@playwright/test';
import { expectAccessibleInLightAndDark } from '../support/a11y.ts';
import {
  chooseColour,
  colourOption,
  componentRows,
  expectAppliedScheme,
  interceptHealth,
  openShell,
  overallStatus,
} from '../support/shell.ts';
import { signInThroughApi } from '../support/session.ts';
import { NO_MEDIA_URL, SUB_PATH, SUB_PATH_ORIGIN } from '../support/targets.ts';

/** The product version: the image is built from the root VERSION file, and the page must show it. */
const VERSION = readFileSync(new URL('../../VERSION', import.meta.url), 'utf8').trim();

const STALE_NOTICE = 'This information may be out of date: the last refresh failed.';
const HEALTH_REFRESH_MS = 30_000;

test.describe('the System page (the M0 shell page)', () => {
  test('loads and shows the product name, the version, and a healthy report', async ({ page }) => {
    await openShell(page);

    await expect(page).toHaveTitle('n8Tracks');
    await expect(page.getByRole('heading', { level: 1, name: 'n8Tracks' })).toBeVisible();
    await expect(page.getByTestId('version')).toHaveText(VERSION);
    await expect(overallStatus(page)).toHaveText('healthy');
    await expect(componentRows(page)).toHaveCount(6);
    await expect(componentRows(page).getByRole('rowheader')).toHaveText([
      'Application',
      'Database',
      'Database schema',
      'Media library',
      'Background jobs',
      'Maintenance',
    ]);
    for (const row of await componentRows(page).all()) {
      await expect(row.getByRole('cell').first()).toHaveText('healthy');
    }

    // The schema's row says how the last upgrade went and when the last safety backup was taken
    // (the shared containers may hold safety backups from other specs, so only that both are there).
    const schema = componentRows(page).filter({
      has: page.getByRole('rowheader', { name: 'Database schema' }),
    });
    await expect(schema.getByTestId('last-migration-outcome')).toHaveText(/at this start$/);
    await expect(schema.getByTestId('last-safety-backup')).toHaveText(
      /^(No safety backup yet|Last safety backup .+)$/,
    );

    await expectAccessibleInLightAndDark(page);
  });

  test('switches between light, dark, and auto, and keeps the choice over a reload', async ({
    page,
  }) => {
    await page.emulateMedia({ colorScheme: 'light' });
    await openShell(page);
    await expect(overallStatus(page)).toHaveText('healthy');
    // Nothing chosen yet: auto.
    await expect(colourOption(page, 'auto')).toBeChecked();

    await chooseColour(page, 'dark');
    await expectAppliedScheme(page, 'dark');
    await page.reload();
    await expect(colourOption(page, 'dark')).toBeChecked();
    await expectAppliedScheme(page, 'dark');

    await chooseColour(page, 'light');
    await expectAppliedScheme(page, 'light');
    await page.reload();
    await expect(colourOption(page, 'light')).toBeChecked();
    await expectAppliedScheme(page, 'light');

    await chooseColour(page, 'auto');
    await expectAppliedScheme(page, 'light');
    await page.reload();
    await expect(colourOption(page, 'auto')).toBeChecked();
    await expectAppliedScheme(page, 'light');
  });

  test('follows the system colour scheme when set to auto', async ({ page }) => {
    await page.emulateMedia({ colorScheme: 'dark' });
    await openShell(page);
    await expect(overallStatus(page)).toHaveText('healthy');

    // Leave auto and come back to it, so auto is a choice the user made, not only the default.
    await chooseColour(page, 'light');
    await expectAppliedScheme(page, 'light');
    await chooseColour(page, 'auto');
    await expectAppliedScheme(page, 'dark');

    await page.emulateMedia({ colorScheme: 'light' });
    await expectAppliedScheme(page, 'light');
    await expect(colourOption(page, 'auto')).toBeChecked();
  });

  test(
    'shows degraded when the media library is unavailable',
    { tag: '@root-only' },
    async ({ page }) => {
      // The project's session is for the root container; this one needs its own.
      await signInThroughApi(page.request, NO_MEDIA_URL);
      await page.goto(`${NO_MEDIA_URL}settings/system`);

      await expect(overallStatus(page)).toHaveText('degraded');
      await expect(componentRows(page)).toHaveCount(6);
      const media = componentRows(page).filter({ hasText: 'Media library' });
      await expect(media.getByRole('cell').first()).toHaveText('degraded');
      await expect(media.getByRole('cell').last()).not.toBeEmpty();

      await expectAccessibleInLightAndDark(page);
    },
  );

  test('shows the loading state until the health request is answered', async ({ page }) => {
    let release = (): void => undefined;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    await interceptHealth(page, async (route) => {
      await held;
      await route.continue();
    });

    await openShell(page);
    await expect(page.getByText('Loading health information…')).toBeVisible();
    await expect(overallStatus(page)).toBeHidden();
    await expectAccessibleInLightAndDark(page);

    // Still loading after both scans: the scans looked at the loading state, not at data.
    await expect(page.getByText('Loading health information…')).toBeVisible();
    release();
    await expect(overallStatus(page)).toHaveText('healthy');
    await expect(page.getByText('Loading health information…')).toBeHidden();
  });

  test('shows the error state when the first load fails, and Retry recovers', async ({ page }) => {
    let failing = true;
    await interceptHealth(page, async (route) => {
      if (failing) {
        await route.fulfill({ status: 500, contentType: 'text/plain', body: 'failed by the test' });
      } else {
        await route.continue();
      }
    });

    await openShell(page);
    await expect(page.getByText('Health information is unavailable.')).toBeVisible();
    await expect(overallStatus(page)).toBeHidden();
    await expectAccessibleInLightAndDark(page);

    failing = false;
    await page.getByRole('button', { name: 'Retry' }).click();
    await expect(overallStatus(page)).toHaveText('healthy');
    await expect(page.getByTestId('version')).toHaveText(VERSION);
    await expect(componentRows(page)).toHaveCount(6);
    await expect(page.getByText('Health information is unavailable.')).toBeHidden();
  });

  test('keeps the last report and says it may be out of date when a refresh fails', async ({
    page,
  }) => {
    let failing = false;
    await interceptHealth(page, async (route) => {
      if (failing) {
        await route.fulfill({ status: 500, contentType: 'text/plain', body: 'failed by the test' });
      } else {
        await route.continue();
      }
    });
    await page.clock.install();

    await openShell(page);
    await expect(overallStatus(page)).toHaveText('healthy');
    await expect(page.getByText(STALE_NOTICE)).toBeHidden();

    failing = true;
    await page.clock.fastForward(HEALTH_REFRESH_MS);
    await expect(page.getByText(STALE_NOTICE)).toBeVisible();
    // The last report is still on the page.
    await expect(overallStatus(page)).toHaveText('healthy');
    await expect(page.getByTestId('version')).toHaveText(VERSION);
    await expect(componentRows(page)).toHaveCount(6);

    await expectAccessibleInLightAndDark(page);
    await expect(page.getByText(STALE_NOTICE)).toBeVisible();
  });
});

test.describe('the sub-path', { tag: '@subpath-only' }, () => {
  test('serves the page under the sub-path and nothing outside it', async ({ page, request }) => {
    // The app root is the dashboard (#228), under the sub-path.
    await page.goto('./');
    await expect(page.getByRole('heading', { level: 2, name: 'Dashboard' })).toBeVisible();
    expect(new URL(page.url()).pathname).toBe(`${SUB_PATH}/`);
    await expect(page.locator('base')).toHaveAttribute('href', `${SUB_PATH}/`);

    await openShell(page);
    expect(new URL(page.url()).pathname).toBe(`${SUB_PATH}/settings/system`);
    await expect(overallStatus(page)).toHaveText('healthy');

    // A base path the app ignored would answer here, at the root, and the suite must not pass.
    for (const outside of ['/', '/health', '/index.html', '/somewhere/else']) {
      const response = await request.get(`${SUB_PATH_ORIGIN}${outside}`);
      expect(response.status(), `GET ${outside} outside the sub-path`).toBe(404);
    }
  });
});
