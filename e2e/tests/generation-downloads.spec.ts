import { randomUUID } from 'node:crypto';
import { expect, test } from '@playwright/test';
import { expectModalAccessibleInBothSchemes } from '../support/a11y.ts';
import { seedGeneration } from '../support/seeding.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  shortcode: string;
  currentVersion: { shortcode: string };
}

/**
 * Download records on the built image (#222): with an extension credential's `suno.sync` token,
 * as the service worker would, a download is reported for a clip n8Tracks does not have yet; the
 * clip is then attached as a Generation (the test-only seeding command), and its panel lists the
 * earlier record with "Not found in media folder", accessibly in both schemes. A report sent again
 * under the same ID adds nothing. Runs on the project's shared container, on a Song of its own.
 */
test.describe('download records', () => {
  test('lists a download reported before the clip was imported on its Generation’s panel', async ({
    page,
    playwright,
  }, testInfo) => {
    await page.goto('./songs');
    const base = new URL('.', page.url());
    const api = (path: string) => new URL(`api/v1/${path}`, base).toString();
    const stamp = `${String(Date.now()).slice(-7)}${testInfo.project.name}`;
    const credential = await page.request.post(api('credentials'), {
      headers: ANTIFORGERY_HEADERS,
      data: { name: `Download stub ${stamp}`, kind: 'extension', scopes: ['suno.sync'] },
    });
    expect(credential.status()).toBe(201);
    const { token } = (await credential.json()) as { token: string };
    const extension = await playwright.request.newContext({ storageState: undefined });
    const sunoId = randomUUID();
    const report = {
      id: randomUUID(),
      sunoId,
      format: 'mp3',
      fileName: `Night drive (suno-${sunoId}) (1).mp3`,
      completedAt: new Date().toISOString(),
      sizeBytes: 4096,
      spentUnlock: true,
    };

    try {
      // 1. The extension reports a download of a clip n8Tracks does not have; again, once more.
      const headers = { Authorization: `Bearer ${token}`, Accept: 'application/json' };
      const first = await extension.post(api('suno/downloads'), { headers, data: report });
      expect(first.status()).toBe(201);
      const again = await extension.post(api('suno/downloads'), { headers, data: report });
      expect(again.status()).toBe(200);

      // 2. The clip is imported later: its Generation lists the earlier record.
      const created = await page.request.post(api('songs'), {
        headers: ANTIFORGERY_HEADERS,
        data: { title: `Downloaded ${stamp}` },
      });
      expect(created.status()).toBe(201);
      const song = (await created.json()) as Song;
      const generation = await seedGeneration(
        testInfo,
        song.currentVersion.shortcode,
        JSON.stringify({ id: sunoId, status: 'complete', title: 'Night drive' }),
      );

      await page.goto(`./songs/${song.shortcode}/generations/${generation}`);
      const panel = page.getByRole('dialog', { name: `Generation ${generation}` });
      await expect(panel).toBeVisible();
      const downloads = panel.getByRole('region', { name: 'Downloads' });
      const records = downloads.getByTestId('generation-download');
      await expect(records).toHaveCount(1);
      await expect(records.first()).toContainText(report.fileName);
      await expect(records.first()).toContainText('MP3');
      await expect(records.first()).toContainText('Used a Suno download unlock');
      await expect(records.first().getByTestId('generation-download-media')).toHaveText(
        'Not found in media folder',
      );
      await downloads.scrollIntoViewIfNeeded();
      await expectModalAccessibleInBothSchemes(page);
    } finally {
      await extension.dispose();
    }
  });
});
