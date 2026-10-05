import { expect, test } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';

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
