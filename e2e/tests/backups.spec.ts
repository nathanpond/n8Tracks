import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { expect, test } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { readZip } from '../support/zip.ts';

interface ManifestFile {
  path: string;
  size: number;
  sha256: string;
}

/**
 * Walks the story's Demo on the project's shared container, signed in. The containers mount no
 * backup folder, so backups go to the data folder and the page warns about it (Demo step 3).
 */
test.describe('Settings → Backups', () => {
  test('backs up now, lists the verified archive, downloads it, and deletes it', async ({
    page,
  }) => {
    // 1. Settings → Backups, from the sidebar.
    await page.goto('./songs');
    await page
      .getByRole('navigation', { name: 'Main' })
      .getByRole('link', { name: 'Backups' })
      .click();
    await expect(page.getByRole('heading', { level: 2, name: 'Backups' })).toBeVisible();

    // No backup mount: the shared-disk warning shows, and Back up now still works.
    await expect(page.getByText('Backups share a disk with your data')).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // Back up now: progress, then the outcome, then a new row.
    await page.getByRole('button', { name: 'Back up now' }).click();
    const status = page.getByTestId('backup-status');
    const finished = status.getByText(/^Backup finished: /);
    await expect(finished).toBeVisible({ timeout: 30_000 });
    const name = /Backup finished: (n8tracks-backup-\S+\.zip)\./.exec(
      (await finished.textContent()) ?? '',
    )?.[1];
    expect(name).toMatch(/^n8tracks-backup-\d{8}-\d{6}-v[0-9A-Za-z.-]+\.zip$/);
    const row = page.locator(`tr[data-backup="${name ?? ''}"]`);
    await expect(row).toBeVisible();
    await expect(row.getByRole('cell').nth(2)).toHaveText('Data folder');
    await expect(row.getByRole('cell').nth(3)).toHaveText('Valid');
    await expectAccessibleInLightAndDark(page);

    // 2. Download it and open the archive: the database, a manifest, and settings, and the
    // managed-assets folder with whatever artwork other specs stored on the shared container.
    const downloading = page.waitForEvent('download');
    await row.getByRole('link', { name: `Download ${name ?? ''}` }).click();
    const download = await downloading;
    expect(download.suggestedFilename()).toBe(name);
    const entries = readZip(await readFile(await download.path()));
    const isAsset = (path: string) => /^assets\/./.test(path);
    expect([...entries.keys()].filter((path) => !isAsset(path)).sort()).toEqual([
      'assets/',
      'manifest.json',
      'n8tracks.db',
      'settings.json',
    ]);
    expect(entries.get('n8tracks.db')?.subarray(0, 16).toString('latin1')).toBe(
      'SQLite format 3\0',
    );
    const manifest = JSON.parse(entries.get('manifest.json')?.toString('utf8') ?? '{}') as {
      formatVersion: number;
      kind: string;
      files: ManifestFile[];
    };
    expect(manifest.formatVersion).toBe(1);
    expect(manifest.kind).toBe('manual');
    expect(manifest.files.map((file) => file.path).filter((path) => !isAsset(path))).toEqual([
      'n8tracks.db',
      'settings.json',
    ]);
    expect(manifest.files.filter((file) => isAsset(file.path))).toHaveLength(
      [...entries.keys()].filter(isAsset).length,
    );
    for (const file of manifest.files) {
      const content = entries.get(file.path);
      expect(content?.length).toBe(file.size);
      expect(
        createHash('sha256')
          .update(content ?? Buffer.alloc(0))
          .digest('hex'),
      ).toBe(file.sha256);
    }
    const settings = JSON.parse(entries.get('settings.json')?.toString('utf8') ?? '{}') as {
      environment?: { timeZone?: string };
    };
    expect(settings.environment?.timeZone).toBeTruthy();

    // Deleting asks first.
    await row.getByRole('button', { name: `Delete ${name ?? ''}` }).click();
    const dialog = page.getByRole('dialog', { name: 'Delete backup?' });
    await expect(dialog).toBeVisible();
    await expectModalAccessibleInBothSchemes(page);
    await dialog.getByRole('button', { name: 'Delete backup' }).click();
    await expect(dialog).toBeHidden();
    await expect(row).toHaveCount(0);
  });
});
