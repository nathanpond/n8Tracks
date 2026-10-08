import { randomUUID } from 'node:crypto';
import { copyFile, mkdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark } from '../support/a11y.ts';
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

/** The API tests' audio fixtures: 1.5 s tones made with ffmpeg. */
function fixture(format: string): string {
  return fileURLToPath(
    new URL(`../../tests/n8Tracks.Api.Tests/Media/Fixtures/tone.${format}`, import.meta.url),
  );
}

interface Status {
  activeScanJobId: string | null;
  lastScan: { jobId: string } | null;
}

/** Scans the library through the API and waits until that scan is the last one. */
async function scan(page: Page): Promise<void> {
  const response = await page.request.post(`${FRESH_URL}api/v1/media/scans`, {
    headers: ANTIFORGERY_HEADERS,
  });
  expect([200, 202]).toContain(response.status());
  const { jobId } = (await response.json()) as { jobId: string };
  await expect
    .poll(
      async () => {
        const current = (await (
          await page.request.get(`${FRESH_URL}api/v1/media/status`)
        ).json()) as Status;
        return current.lastScan?.jobId === jobId && current.activeScanJobId === null;
      },
      { timeout: 60_000, intervals: [1_000] },
    )
    .toBe(true);
}

/** Starts an export with the extension's token, as the extension does, and returns its ID. */
async function startExport(extension: APIRequestContext, token: string): Promise<string> {
  const created = await extension.post(`${FRESH_URL}api/v1/suno/exports`, {
    headers: { Authorization: `Bearer ${token}` },
    data: {
      format: 'n8tracks.suno-export',
      formatVersion: 1,
      extensionVersion: '0.1.0',
      adapterVersion: '3',
      capturedAt: new Date().toISOString(),
      scope: { kind: 'library', ids: [] },
      libraryComplete: true,
      trashedComplete: true,
      workspaces: [],
      workspacesComplete: false,
      playlists: [],
    },
  });
  expect(created.status(), await created.text()).toBe(201);
  return ((await created.json()) as { id: string }).id;
}

/** An export of two new clips, completed and ready for review; its ID. */
async function uploadExport(extension: APIRequestContext, token: string): Promise<string> {
  const id = await startExport(extension, token);
  const headers = { Authorization: `Bearer ${token}` };
  const part = await extension.post(`${FRESH_URL}api/v1/suno/exports/${id}/parts`, {
    headers,
    data: {
      partNumber: 1,
      clips: [
        { id: randomUUID(), status: 'complete', title: 'Tide Pool' },
        { id: randomUUID(), status: 'complete', title: 'Low Water' },
      ],
      trashedClips: [],
    },
  });
  expect(part.status(), await part.text()).toBe(200);
  const completed = await extension.post(`${FRESH_URL}api/v1/suno/exports/${id}/complete`, {
    headers,
  });
  expect(completed.status(), await completed.text()).toBe(200);
  expect(((await completed.json()) as { state: string }).state).toBe('ready');
  return id;
}

function section(page: Page, name: string) {
  return page.getByRole('region', { name });
}

async function goHome(page: Page): Promise<void> {
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Home' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Dashboard' })).toBeVisible();
}

/**
 * Walks #229's Demo on a container of its own, so every count is known: with two unmatched files and
 * one export waiting for review, the dashboard shows both; associating one file brings the count to
 * one, as the Unmatched Files page's own total; the review link opens that export's review. Then a
 * sync the extension reports failed is listed under Suno problems and noticed on the Suno import
 * page, where Dismiss takes it off both. Each state is scanned with axe in light and dark.
 */
test.describe('what needs attention on the dashboard', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async () => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-attention-'));
    await mkdir(join(work, 'media', 'loose'), { recursive: true });
    await copyFile(fixture('mp3'), join(work, 'media', 'loose', 'Tide Pool.mp3'));
    await copyFile(fixture('mp3'), join(work, 'media', 'loose', 'Low Water.mp3'));
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

  test('shows the unmatched files, the export waiting, and a failed sync, each leading to where it is resolved', async ({
    page,
    playwright,
  }) => {
    test.setTimeout(240_000);

    await page.goto(FRESH_URL);
    await signInWithTheForm(page, TEST_ADMIN.username, TEST_ADMIN.password);
    await expect(userMenu(page)).toHaveText(TEST_ADMIN.username);
    await expect(page.getByRole('heading', { level: 2, name: 'Dashboard' })).toBeVisible();

    // Nothing to report yet: each section says so in one line, and is still there.
    await expect(section(page, 'Unmatched Files')).toContainText(
      'No audio files are waiting to be placed.',
    );
    await expect(section(page, 'Suno reviews')).toContainText(
      'No Suno import is waiting for review.',
    );
    await expect(section(page, 'Suno problems')).toContainText('No Suno problems to report.');
    await expectAccessibleInLightAndDark(page);

    // The conditions: a Song, two files never placed, and an export the extension sent.
    const created = await page.request.post(`${FRESH_URL}api/v1/songs`, {
      data: { title: 'Harbour Lights' },
      headers: ANTIFORGERY_HEADERS,
    });
    expect(created.status()).toBe(201);
    const song = (await created.json()) as { shortcode: string };
    await scan(page);
    const credential = await page.request.post(`${FRESH_URL}api/v1/credentials`, {
      headers: ANTIFORGERY_HEADERS,
      data: { name: 'Attention extension', kind: 'extension', scopes: ['suno.sync'] },
    });
    expect(credential.status()).toBe(201);
    const { token } = (await credential.json()) as { token: string };
    const extension = await playwright.request.newContext({ storageState: undefined });

    try {
      const exportId = await uploadExport(extension, token);

      // 1. The dashboard shows both.
      await page.reload();
      const unmatched = section(page, 'Unmatched Files');
      const count = unmatched.getByRole('link', {
        name: '2 audio files unmatched: open Unmatched Files',
      });
      await expect(count).toBeVisible();
      const review = section(page, 'Suno reviews').getByTestId('suno-review');
      await expect(review).toHaveCount(1);
      await expect(review).toHaveAttribute('data-export', exportId);
      await expect(review.getByTestId('suno-review-counts')).toHaveText(
        '2 records; 0 changed and 0 Conflicts to resolve',
      );
      await expectAccessibleInLightAndDark(page);

      // The count is the Unmatched Files page's own total.
      await count.click();
      await expect(page).toHaveURL(`${FRESH_URL}library/unmatched`);
      await expect(page.getByTestId('unmatched-total')).toContainText('2');

      // 2. Associate one file; return: the count is one, as the page's.
      const files = await page.request.get(`${FRESH_URL}api/v1/audio-files?association=none`);
      const [file] = ((await files.json()) as { items: { id: string; revision: number }[] }).items;
      expect(file).toBeDefined();
      const associated = await page.request.put(
        `${FRESH_URL}api/v1/audio-files/${file?.id ?? ''}/association`,
        {
          headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(file?.revision ?? 0)}"` },
          data: { song: song.shortcode },
        },
      );
      expect(associated.status(), await associated.text()).toBe(200);
      await goHome(page);
      await expect(
        section(page, 'Unmatched Files').getByRole('link', {
          name: '1 audio file unmatched: open Unmatched Files',
        }),
      ).toBeVisible();
      await section(page, 'Unmatched Files')
        .getByRole('link', { name: '1 audio file unmatched: open Unmatched Files' })
        .click();
      await expect(page.getByTestId('unmatched-total')).toContainText('1');

      // 3. The review link opens that export's review.
      await goHome(page);
      await section(page, 'Suno reviews').getByRole('link', { name: 'Review the import' }).click();
      await expect(page).toHaveURL(`${FRESH_URL}suno/imports/${exportId}`);
      await expect(
        page.getByRole('heading', { level: 2, name: 'Review Suno import' }),
      ).toBeVisible();

      // A sync the extension reports failed at a step: a Suno problem, and the Suno page's notice.
      const failed = await startExport(extension, token);
      const discarded = await extension.post(`${FRESH_URL}api/v1/suno/exports/${failed}/discard`, {
        headers: { Authorization: `Bearer ${token}` },
        data: { reason: 'failed', step: 'Read the library' },
      });
      expect(discarded.status(), await discarded.text()).toBe(200);
      await goHome(page);
      const problem = section(page, 'Suno problems').getByTestId('suno-problem');
      await expect(problem).toHaveCount(1);
      await expect(problem).toHaveAttribute('data-kind', 'failedSync');
      await expect(problem).toContainText(
        'Your last Suno sync failed at the step “Read the library”.',
      );
      await expectAccessibleInLightAndDark(page);

      // The review still waits, so the Suno import entry opens it; the notice is on its own page.
      await page.goto(`${FRESH_URL}suno/imports/${exportId}`);
      await expect(
        page.getByRole('heading', { level: 2, name: 'Review Suno import' }),
      ).toBeVisible();
      const discard = await page.request.post(
        `${FRESH_URL}api/v1/suno/exports/${exportId}/discard`,
        { headers: ANTIFORGERY_HEADERS, data: { reason: 'cancelled' } },
      );
      expect(discard.status(), await discard.text()).toBe(200);
      await goHome(page);
      await section(page, 'Suno problems').getByRole('link', { name: 'Open Suno import' }).click();
      await expect(page).toHaveURL(`${FRESH_URL}suno/imports`);
      const notice = page.getByTestId('failed-sync-notice');
      await expect(notice).toContainText('Your last sync failed');
      await expect(notice).toContainText('Read the library');
      await expect(notice).toContainText('choose Sync');
      await expectAccessibleInLightAndDark(page);

      // Dismiss: it leaves the page and the dashboard.
      await notice.getByRole('button', { name: 'Dismiss' }).click();
      await expect(notice).toBeHidden();
      await goHome(page);
      await expect(section(page, 'Suno problems')).toContainText('No Suno problems to report.');
      await expect(section(page, 'Suno reviews')).toContainText(
        'No Suno import is waiting for review.',
      );
    } finally {
      await extension.dispose();
    }
  });
});
