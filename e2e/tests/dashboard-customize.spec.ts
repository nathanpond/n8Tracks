import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import {
  removeContainers,
  startContainer,
  waitForHealth,
  type Target,
} from '../support/containers.ts';
import { ANTIFORGERY_HEADERS, signInWithTheForm, userMenu } from '../support/session.ts';
import { completeSetup, TEST_ADMIN } from '../support/setup.ts';
import { FRESH_NAME, FRESH_PORT, FRESH_URL } from '../support/targets.ts';

const fresh: Target = {
  name: FRESH_NAME,
  port: FRESH_PORT,
  url: FRESH_URL,
  media: true,
  expectedStatus: 'healthy',
};

/** The sections in the default order, by their headings. */
const DEFAULT_ORDER = [
  'Recently edited',
  'Unmatched Files',
  'Suno reviews',
  'Suno problems',
  'By workflow state',
  'Without a Selected Generation',
];

function section(page: Page, name: string) {
  return page.getByRole('region', { name });
}

/** The headings of the sections the dashboard shows, in the order it shows them. */
async function shownSections(page: Page): Promise<string[]> {
  return page.locator('[data-testid^="dashboard-section-"] h3').allTextContents();
}

/**
 * Walks #230's Demo on a container of its own, so the arrangement starts at the default and no
 * other test sees it: Scan Library confirms and stays disabled until the scan finishes; Customize
 * hides By workflow state and moves Unmatched Files to the top (by keyboard), which holds after a
 * reload; Reset brings the default back. Each state is scanned with axe in light and dark.
 */
test.describe('customizing the dashboard', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async () => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-dashboard-customize-'));
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

  test('scans from the quick actions, and hides, moves, saves, and resets the sections', async ({
    page,
  }) => {
    test.setTimeout(240_000);

    await page.goto(FRESH_URL);
    await signInWithTheForm(page, TEST_ADMIN.username, TEST_ADMIN.password);
    await expect(userMenu(page)).toHaveText(TEST_ADMIN.username);
    // A Song, so the catalog sections show rather than the welcome.
    const created = await page.request.post(`${FRESH_URL}api/v1/songs`, {
      data: { title: 'Night Drive' },
      headers: ANTIFORGERY_HEADERS,
    });
    expect(created.status(), await created.text()).toBe(201);
    await page.reload();

    const actions = section(page, 'Quick actions');
    await expect(section(page, 'Recently edited')).toBeVisible();
    await expect.poll(() => shownSections(page)).toEqual(DEFAULT_ORDER);
    // No Song opened yet: Open last Song is disabled, saying why.
    await expect(actions.getByRole('button', { name: 'Open last Song' })).toBeDisabled();
    await expect(actions.getByTestId('last-song-reason')).toHaveText(
      'No Song has been opened yet.',
    );
    await expectAccessibleInLightAndDark(page);

    // 1. Scan Library: a notice confirms, and the button is disabled until the scan finishes.
    const scan = actions.getByRole('button', { name: 'Scan Library' });
    // A scan the instance started by itself may still be running: wait for it.
    await expect(scan).toBeEnabled({ timeout: 60_000 });
    await scan.click();
    const notice = actions.getByTestId('scan-library-notice');
    await expect(notice).toContainText('Scan Library is available again when it finishes.');
    await expect(scan).toBeDisabled();
    await expect(actions.getByTestId('scan-library-reason')).toHaveText(
      'A library scan is queued or running.',
    );
    await expect(scan).toBeEnabled({ timeout: 60_000 });
    await expect(notice).toHaveText('The library scan finished.');

    // 2. Customize: hide By workflow state, move Unmatched Files to the top, save.
    await page.getByRole('button', { name: 'Customize', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Customize the dashboard' });
    await expect(dialog).toBeVisible();
    await dialog.getByRole('checkbox', { name: 'By workflow state' }).uncheck();
    await dialog.getByRole('button', { name: 'Move Unmatched Files up' }).press('Enter');
    await expect(dialog.getByTestId('customize-announcement')).toHaveText(
      'Unmatched Files moved to position 1 of 6.',
    );
    // At the top, focus goes to the button that still works.
    await expect(dialog.getByRole('button', { name: 'Move Unmatched Files down' })).toBeFocused();
    await expectModalAccessibleInBothSchemes(page);
    await dialog.getByRole('button', { name: 'Save' }).click();
    await expect(dialog).toBeHidden();

    const arranged = [
      'Unmatched Files',
      'Recently edited',
      'Suno reviews',
      'Suno problems',
      'Without a Selected Generation',
    ];
    await expect.poll(() => shownSections(page)).toEqual(arranged);
    await expect(section(page, 'By workflow state')).toHaveCount(0);

    // Reload: the arrangement holds (it is saved on the server, so it applies on every browser).
    await page.reload();
    await expect(section(page, 'Unmatched Files')).toBeVisible();
    await expect.poll(() => shownSections(page)).toEqual(arranged);
    await expect(section(page, 'By workflow state')).toHaveCount(0);
    await expect(actions.getByRole('button', { name: 'Scan Library' })).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // 3. Reset, then Save: the default returns.
    await page.getByRole('button', { name: 'Customize', exact: true }).click();
    await expect(dialog).toBeVisible();
    await dialog.getByRole('button', { name: 'Reset' }).click();
    await expect(dialog.getByRole('checkbox', { name: 'By workflow state' })).toBeChecked();
    await dialog.getByRole('button', { name: 'Save' }).click();
    await expect(dialog).toBeHidden();
    await expect.poll(() => shownSections(page)).toEqual(DEFAULT_ORDER);

    await page.reload();
    await expect(section(page, 'By workflow state')).toBeVisible();
    await expect.poll(() => shownSections(page)).toEqual(DEFAULT_ORDER);
  });
});
