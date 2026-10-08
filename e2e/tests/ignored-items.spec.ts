import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

/** The first library clip of the committed TS-003 fixture: an Advanced Song with lyrics, in a workspace. */
const LIBRARY_CLIP = (
  JSON.parse(
    readFileSync(
      new URL(
        '../../extension/fixtures/suno/feed-v3.library-page-1.response.json',
        import.meta.url,
      ),
      'utf8',
    ),
  ) as { clips: Record<string, unknown>[] }
).clips[0];

/** The app's base (the root or the sub-path), as the Songs page resolves it. */
async function appBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

/** The fixture clip as Suno would list it with these fields (a fresh Suno ID). */
function clip(title: string, workspace: string, createdAt: string): Record<string, unknown> {
  const copy = structuredClone(LIBRARY_CLIP) as Record<string, unknown> & {
    metadata: Record<string, unknown>;
    project: Record<string, unknown>;
  };
  copy.id = randomUUID();
  copy.title = title;
  copy.created_at = createdAt;
  copy.batch_index = 0;
  copy.project = { ...copy.project, id: workspace };
  copy.metadata = { ...copy.metadata, prompt: `[Verse]\n${title}` };
  return copy;
}

/** Uploads one export of `clips` with the extension's token, as the extension does; returns its ID once ready. */
async function uploadExport(
  extension: APIRequestContext,
  base: URL,
  token: string,
  clips: Record<string, unknown>[],
): Promise<string> {
  const headers = { Authorization: `Bearer ${token}`, Accept: 'application/json' };
  const exports = new URL('api/v1/suno/exports', base).toString();
  const created = await extension.post(exports, {
    headers,
    data: {
      format: 'n8tracks.suno-export',
      formatVersion: 1,
      extensionVersion: '0.1.0',
      adapterVersion: '3',
      capturedAt: new Date().toISOString(),
      scope: { kind: 'workspaces', ids: [] },
      libraryComplete: false,
      trashedComplete: false,
      workspaces: [],
      workspacesComplete: false,
      playlists: [],
    },
  });
  expect(created.status()).toBe(201);
  const { id } = (await created.json()) as { id: string };
  const part = await extension.post(`${exports}/${id}/parts`, {
    headers,
    data: { partNumber: 1, clips, trashedClips: [] },
  });
  expect(part.status()).toBe(200);
  const completed = await extension.post(`${exports}/${id}/complete`, { headers });
  expect(completed.status()).toBe(200);
  expect(((await completed.json()) as { state: string }).state).toBe('ready');
  return id;
}

function reviewRow(page: Page, title: string) {
  return page
    .getByRole('table', { name: 'Records in this import' })
    .getByRole('row')
    .filter({ has: page.getByRole('rowheader', { name: title, exact: true }) });
}

function listRow(page: Page, title: string) {
  return page
    .getByRole('table', { name: 'Ignored Suno items' })
    .getByRole('row')
    .filter({ has: page.getByRole('rowheader', { name: title, exact: true }) });
}

/** Discards the export the review page shows. */
async function discard(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Discard import' }).click();
  await page.getByRole('button', { name: 'Discard', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'This import was discarded' })).toBeVisible();
}

/**
 * Walks #143's Demo on the shared containers with Suno IDs and titles of its own, the exports uploaded
 * with an extension token as `curl` would. 1. In a review two new clips are set to Don't copy and the
 * import confirmed. 2. Ignored Suno Items (from the sidebar) lists both; a search finds one by title.
 * 3. The same export again shows both Ignored, set to Don't copy. 4. One is removed from the list
 * after a confirmation; the export uploaded again shows it New. Each visited state is scanned with axe.
 */
test.describe('Ignored Suno items', () => {
  test('ignores two clips at a commit, lists and searches them, and makes one eligible again', async ({
    page,
    playwright,
  }, testInfo) => {
    const stamp = `${String(Date.now()).slice(-7)}${testInfo.project.name}`;
    const workspace = `ws-ignored-${stamp}`;
    const first = `Ignored dawn ${stamp}`;
    const second = `Ignored dusk ${stamp}`;
    const base = await appBase(page);
    const credential = await page.request.post(new URL('api/v1/credentials', base).toString(), {
      headers: ANTIFORGERY_HEADERS,
      data: { name: `Ignore list ${stamp}`, kind: 'extension', scopes: ['suno.sync'] },
    });
    expect(credential.status()).toBe(201);
    const { token } = (await credential.json()) as { token: string };
    const extension = await playwright.request.newContext({ storageState: undefined });

    try {
      const clips = [
        clip(first, workspace, new Date(Date.now() - 7_200_000).toISOString()),
        clip(second, workspace, new Date(Date.now() - 3_600_000).toISOString()),
      ];

      // 1. Both set to Don't copy in the review, and confirmed.
      const id = await uploadExport(extension, base, token, clips);
      await page.goto(`./suno/imports/${id}`);
      await reviewRow(page, first).getByRole('checkbox').check();
      await reviewRow(page, second).getByRole('checkbox').check();
      await page.getByRole('radio', { name: 'Don’t copy' }).check();
      await page.getByRole('button', { name: 'Apply to 2 records' }).click();
      await expect(reviewRow(page, first).getByTestId('record-choice')).toHaveText('Don’t copy');
      await expect(reviewRow(page, second).getByTestId('record-choice')).toHaveText('Don’t copy');
      await expect(page.getByTestId('summary-ignored')).toHaveText(
        'Not copy 2 records (Don’t copy: on the ignore list).',
      );
      await page.getByRole('button', { name: 'Confirm import' }).click();
      await page
        .getByRole('dialog', { name: 'Confirm this import?' })
        .getByRole('button', { name: 'Confirm import' })
        .click();
      await expect(page.getByRole('heading', { name: 'This import was confirmed' })).toBeVisible({
        timeout: 30_000,
      });

      // 2. The list, from the sidebar: both, then one found by its title.
      await page
        .getByRole('navigation', { name: 'Main' })
        .getByRole('link', { name: 'Ignored Suno items' })
        .click();
      await expect(
        page.getByRole('heading', { level: 2, name: 'Ignored Suno items' }),
      ).toBeVisible();
      await page
        .getByRole('main')
        .getByRole('textbox', { name: /Search/ })
        .fill(stamp);
      await page.getByRole('button', { name: 'Search', exact: true }).click();
      await expect(listRow(page, first)).toBeVisible();
      await expect(listRow(page, second)).toBeVisible();
      await expect(listRow(page, first)).toContainText(workspace);
      await expect(listRow(page, first).getByTestId('item-status')).toHaveText('Present');
      await expectAccessibleInLightAndDark(page);
      await page
        .getByRole('main')
        .getByRole('textbox', { name: /Search/ })
        .fill(first);
      await page.getByRole('button', { name: 'Search', exact: true }).click();
      await expect(page).toHaveURL(/[?&]q=/);
      await expect(listRow(page, second)).toHaveCount(0);
      await expect(listRow(page, first)).toBeVisible();
      await expectAccessibleInLightAndDark(page);

      // 3. The same export again: both Ignored, set to Don't copy.
      const again = await uploadExport(extension, base, token, clips);
      await page.goto(`./suno/imports/${again}`);
      for (const title of [first, second]) {
        await expect(reviewRow(page, title).getByTestId('record-class')).toHaveText('Ignored');
        await expect(reviewRow(page, title).getByTestId('record-choice')).toHaveText('Don’t copy');
      }
      await expectAccessibleInLightAndDark(page);
      await discard(page);

      // 4. One removed from the list after a confirmation; the next upload shows it New.
      await page.goto(`./suno/ignored?q=${encodeURIComponent(stamp)}`);
      await listRow(page, first).getByRole('checkbox').check();
      await page.getByRole('button', { name: 'Remove 1 item' }).click();
      const asked = page.getByRole('dialog', { name: 'Remove 1 item from the ignore list?' });
      await expect(asked).toBeVisible();
      await expectModalAccessibleInBothSchemes(page);
      await asked.getByRole('button', { name: 'Remove 1 item' }).click();
      await expect(page.getByTestId('items-removed')).toHaveText(
        'Removed 1 item from the ignore list. Nothing was imported.',
      );
      await expect(listRow(page, first)).toHaveCount(0);
      await expect(listRow(page, second)).toBeVisible();
      await expectAccessibleInLightAndDark(page);

      const third = await uploadExport(extension, base, token, clips);
      await page.goto(`./suno/imports/${third}`);
      await expect(reviewRow(page, first).getByTestId('record-class')).toHaveText('New');
      await expect(reviewRow(page, second).getByTestId('record-class')).toHaveText('Ignored');
      await expectAccessibleInLightAndDark(page);
      await discard(page);
    } finally {
      await extension.dispose();
    }
  });
});
