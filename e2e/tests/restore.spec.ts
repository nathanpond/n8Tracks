import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test, type Route } from '@playwright/test';
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
import {
  ANTIFORGERY_HEADERS,
  signInHeading,
  signInThroughApi,
  signInWithTheForm,
} from '../support/session.ts';
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
 * Walks Demo steps 1 and 2 of restore validation on the project's shared container, signed in:
 * choosing Restore on a backup shows its summary and the field to type RESTORE, and a file that is
 * not a backup is refused with the reason. Nothing is confirmed, so the instance never enters
 * maintenance and nothing changes (the restore itself is walked by its own story).
 */
test.describe('Restore from Settings → Backups', () => {
  test('summarises a backup for confirmation and refuses a file that is not one', async ({
    page,
  }) => {
    await page.goto('./songs');
    await page
      .getByRole('navigation', { name: 'Main' })
      .getByRole('link', { name: 'Backups' })
      .click();
    await expect(page.getByRole('heading', { level: 2, name: 'Backups' })).toBeVisible();

    // A backup to restore from.
    await page.getByRole('button', { name: 'Back up now' }).click();
    const finished = page.getByTestId('backup-status').getByText(/^Backup finished: /);
    await expect(finished).toBeVisible({ timeout: 30_000 });
    const name =
      /Backup finished: (n8tracks-backup-\S+\.zip)\./.exec(
        (await finished.textContent()) ?? '',
      )?.[1] ?? '';
    const row = page.locator(`tr[data-backup="${name}"]`);
    await expect(row).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // 1. Restore on the backup: its summary, and a field to type RESTORE.
    await row.getByRole('button', { name: `Restore ${name}` }).click();
    const dialog = page.getByRole('dialog', { name: 'Restore from this backup?' });
    const summary = dialog.getByRole('table', { name: 'Backup to restore' });
    await expect(summary).toBeVisible({ timeout: 30_000 });
    await expect(summary).toContainText(name);
    await expect(summary).toContainText('Manual');
    await expect(summary).toContainText(/n8Tracks \S+/);
    const confirm = dialog.getByRole('button', { name: 'Restore' });
    await expect(confirm).toBeDisabled();
    await expectModalAccessibleInBothSchemes(page);

    const field = dialog.getByRole('textbox', { name: 'Type RESTORE to confirm' });
    await field.fill('restore');
    await expect(confirm).toBeDisabled();
    await field.fill('RESTORE');
    await expect(confirm).toBeEnabled();

    // Not confirmed: this container is shared.
    await dialog.getByRole('button', { name: 'Cancel' }).click();
    await expect(dialog).toBeHidden();

    // 2. A file that is not a backup is refused with the reason, and nothing changes.
    const choosing = page.waitForEvent('filechooser');
    await page.getByRole('button', { name: 'Restore from a file…' }).click();
    await (
      await choosing
    ).setFiles({
      name: 'notes.zip',
      mimeType: 'application/zip',
      buffer: Buffer.from('These are notes, not a backup.'),
    });
    const refused = page.getByRole('dialog', { name: 'Restore from a file?' });
    await expect(refused.getByTestId('restore-refusal')).toContainText('not a ZIP archive', {
      timeout: 30_000,
    });
    await expect(refused.getByText('Nothing was changed.')).toBeVisible();
    await expect(refused.getByRole('textbox')).toHaveCount(0);
    await expectModalAccessibleInBothSchemes(page);
    await refused.getByRole('button', { name: 'Close' }).last().click();
    await expect(refused).toBeHidden();

    const maintenance = await page.request.get(new URL('../api/v1/maintenance', page.url()).href);
    expect(maintenance.status()).toBe(200);
    expect(((await maintenance.json()) as { active: boolean }).active).toBe(false);

    // Tidy the shared container: the backup made here goes.
    await row.getByRole('button', { name: `Delete ${name}` }).click();
    const deleting = page.getByRole('dialog', { name: 'Delete backup?' });
    await deleting.getByRole('button', { name: 'Delete backup' }).click();
    await expect(row).toHaveCount(0);
  });
});

/**
 * A route for the maintenance status: the server answers every poll, but while `hold.scanning` an
 * answer saying maintenance has ended is passed on as "finishing, 100%", so the page stays in its
 * progress state for the accessibility scan. Every other answer passes through unchanged.
 */
function holdEndUntilScanned(hold: { scanning: boolean }) {
  return async (route: Route) => {
    const response = await route.fetch();
    const status = (await response.json()) as { active: boolean };
    const json =
      hold.scanning && !status.active
        ? { active: true, stage: 'finishing', percent: 100, outcome: null }
        : status;
    await route.fulfill({ response, json });
  };
}

/**
 * Walks the restore story's Demo on a container of its own (a restore replaces the whole instance):
 * a Song, a backup, a second Song; Restore on the backup with the typed confirmation; the
 * maintenance page with its progress, then the sign-in page; signed in again, only the first Song
 * is there and a safety backup is listed. Started afresh for each attempt and removed afterwards.
 */
test.describe('Restoring a backup', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-restore-'));
    await mkdir(join(work, 'media'));
    await startContainer(fresh, work);
    await waitForHealth(fresh);
    await completeSetup(FRESH_URL);
    await signInThroughApi(page.request, FRESH_URL);
  });

  test.afterEach(async () => {
    await removeContainers(FRESH_NAME);
    if (work !== undefined) {
      await rm(work, { recursive: true, force: true });
    }
  });

  test('brings the instance back to the backup, through maintenance and a new sign-in', async ({
    page,
  }) => {
    const create = async (title: string) => {
      const created = await page.request.post(`${FRESH_URL}api/v1/songs`, {
        headers: ANTIFORGERY_HEADERS,
        data: { title },
      });
      expect(created.status()).toBe(201);
    };

    // 1. A Song, a backup, then a second Song.
    await create('Before the backup');
    await page.goto(`${FRESH_URL}settings/backups`);
    await expect(page.getByRole('heading', { level: 2, name: 'Backups' })).toBeVisible();
    await page.getByRole('button', { name: 'Back up now' }).click();
    const finished = page.getByTestId('backup-status').getByText(/^Backup finished: /);
    await expect(finished).toBeVisible({ timeout: 30_000 });
    const name =
      /Backup finished: (n8tracks-backup-\S+\.zip)\./.exec(
        (await finished.textContent()) ?? '',
      )?.[1] ?? '';
    await create('After the backup');

    // 2. Restore on the backup, and type the confirmation.
    const row = page.locator(`tr[data-backup="${name}"]`);
    await row.getByRole('button', { name: `Restore ${name}` }).click();
    const dialog = page.getByRole('dialog', { name: 'Restore from this backup?' });
    await expect(dialog.getByRole('table', { name: 'Backup to restore' })).toBeVisible({
      timeout: 30_000,
    });
    await expect(dialog.getByText(/administrator account and its password/)).toBeVisible();
    await dialog.getByRole('textbox', { name: 'Type RESTORE to confirm' }).fill('RESTORE');
    await expectModalAccessibleInBothSchemes(page);

    // The restore of a small instance takes about a second, too short to scan the page it shows,
    // so the maintenance status is held at "finishing" until the scan is done (see holdTheEnd).
    const hold = { scanning: true };
    const holdTheEnd = holdEndUntilScanned(hold);
    await page.route('**/api/v1/maintenance', holdTheEnd);
    await dialog.getByRole('button', { name: 'Restore' }).click();

    // 3. The maintenance page with its progress, then the sign-in page.
    await expect(page.getByRole('heading', { level: 2, name: 'Restoring a backup' })).toBeVisible();
    await expect(page.getByRole('progressbar', { name: 'Restore progress' })).toBeVisible();
    await expect(page.getByTestId('maintenance-stage')).toContainText('%');
    await expectAccessibleInLightAndDark(page);
    hold.scanning = false;
    await page.unroute('**/api/v1/maintenance', holdTheEnd);

    await expect(signInHeading(page)).toBeVisible({ timeout: 30_000 });
    await expectAccessibleInLightAndDark(page);

    // 4. Signed in, only the first Song exists, and a new safety backup is listed.
    await signInWithTheForm(page, TEST_ADMIN.username, TEST_ADMIN.password);
    await expect(signInHeading(page)).toBeHidden({ timeout: 15_000 });
    await page.goto(`${FRESH_URL}songs`);
    await expect(page.getByRole('link', { name: 'Before the backup' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'After the backup' })).toHaveCount(0);

    await page.goto(`${FRESH_URL}settings/backups`);
    await expect(page.locator(`tr[data-backup="${name}"]`)).toBeVisible();
    await expect(page.getByTestId('backup-kind').filter({ hasText: 'Safety' })).toHaveCount(1);
    const note = page.getByTestId('last-restore');
    await expect(note).toContainText(`Restored from ${name}`);
    await expect(note).toContainText('every session was ended');
    await expectAccessibleInLightAndDark(page);
  });
});
