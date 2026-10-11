import { randomUUID } from 'node:crypto';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

/** A very wide window: the pane beside the 220px sidebar is well over 2000px. */
const WIDE = { width: 2560, height: 1200 };

/**
 * What a page must reach on that window. Before #424 the shell capped every page at about 1000px
 * (a Mantine `Container size="md"`); the pane is about 2300px, so this leaves room for padding only.
 */
const MINIMUM_WIDTH = 2000;

/** The app's base (the root or the sub-path), as the Songs page resolves it. */
async function appBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

async function widthOf(locator: Locator): Promise<number> {
  await expect(locator).toBeVisible();
  const box = await locator.boundingBox();
  expect(box).not.toBeNull();
  return box?.width ?? 0;
}

/** #424: on a very wide window the content uses the pane's width instead of stopping near 1000px. */
test.describe('Content width on a wide window', () => {
  test('the Songs table and a Song page use the full pane width', async ({ page }, testInfo) => {
    await page.setViewportSize(WIDE);
    const stamp = `${String(Date.now()).slice(-7)}${testInfo.project.name}`;
    const base = await appBase(page);
    const created = await page.request.post(new URL('api/v1/songs', base).toString(), {
      headers: ANTIFORGERY_HEADERS,
      data: { title: `Wide ${stamp}` },
    });
    expect(created.status()).toBe(201);
    const song = (await created.json()) as { shortcode: string };

    await page.goto('./songs');
    expect(await widthOf(page.getByRole('table', { name: 'Songs' }))).toBeGreaterThan(
      MINIMUM_WIDTH,
    );

    await page.goto(`./songs/${song.shortcode}`);
    await expect(page.getByRole('heading', { level: 2 }).first()).toBeVisible();
    expect(await widthOf(page.locator('main > *').first())).toBeGreaterThan(MINIMUM_WIDTH);
  });

  test('the Suno import review table uses the full pane width', async ({
    page,
    playwright,
  }, testInfo) => {
    await page.setViewportSize(WIDE);
    const stamp = `${String(Date.now()).slice(-7)}${testInfo.project.name}`;
    const base = await appBase(page);
    const credential = await page.request.post(new URL('api/v1/credentials', base).toString(), {
      headers: ANTIFORGERY_HEADERS,
      data: { name: `Content width ${stamp}`, kind: 'extension', scopes: ['suno.sync'] },
    });
    expect(credential.status()).toBe(201);
    const { token } = (await credential.json()) as { token: string };
    const extension = await playwright.request.newContext({ storageState: undefined });

    try {
      const headers = { Authorization: `Bearer ${token}`, Accept: 'application/json' };
      const exports = new URL('api/v1/suno/exports', base).toString();
      const upload = await extension.post(exports, {
        headers,
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
      expect(upload.status()).toBe(201);
      const { id } = (await upload.json()) as { id: string };
      const part = await extension.post(`${exports}/${id}/parts`, {
        headers,
        data: {
          partNumber: 1,
          clips: [{ id: randomUUID(), status: 'complete', title: `Wide clip ${stamp}` }],
          trashedClips: [],
        },
      });
      expect(part.status()).toBe(200);
      const completed = await extension.post(`${exports}/${id}/complete`, { headers });
      expect(completed.status()).toBe(200);

      await page.goto('./suno/imports');
      const table = page.getByRole('table', { name: 'Records in this import' });
      await expect(table.getByRole('rowheader', { name: `Wide clip ${stamp}` })).toBeVisible();
      expect(await widthOf(table)).toBeGreaterThan(MINIMUM_WIDTH);

      // Leave nothing pending for other specs.
      await page.getByRole('button', { name: 'Discard import' }).click();
      await page.getByRole('button', { name: 'Discard', exact: true }).click();
      await expect(page.getByRole('heading', { name: 'This import was discarded' })).toBeVisible();
    } finally {
      await extension.dispose();
    }
  });
});
