import { expect, test, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  currentVersion: { id: string };
}

/** The API's base on the project's container, worked out on the Songs page. */
async function apiBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

/** Creates a Song through the API and returns it. */
async function createSong(page: Page, base: URL, title: string): Promise<Song> {
  const response = await page.request.post(new URL('api/v1/songs', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(response.status()).toBe(201);
  return (await response.json()) as Song;
}

/** Takes a snapshot of the Version's text through the API, dated `capturedAt`. */
async function takeSnapshot(
  page: Page,
  base: URL,
  song: Song,
  lyrics: string,
  capturedAt: string,
): Promise<void> {
  const response = await page.request.post(
    new URL(`api/v1/versions/${song.currentVersion.id}/snapshots`, base).toString(),
    { headers: ANTIFORGERY_HEADERS, data: { lyrics, styles: '', capturedAt } },
  );
  expect(response.status()).toBe(201);
}

/** The Version's snapshot IDs as the API lists them, newest first. */
async function snapshotIds(page: Page, base: URL, song: Song): Promise<string[]> {
  const response = await page.request.get(
    new URL(`api/v1/versions/${song.currentVersion.id}/snapshots`, base).toString(),
  );
  expect(response.status()).toBe(200);
  return ((await response.json()) as { items: { id: string }[] }).items.map((item) => item.id);
}

/**
 * Walks #96's Demo on a Song of its own: open the Version's History, select an entry, choose
 * Delete, and confirm (the confirmation calls it permanent and never mentions the 30 days); the
 * entry disappears and the newest remaining entry is selected. Step 3 (the recovery listing) is the
 * sibling story's.
 */
test.describe('deleting a history entry', () => {
  test('deletes the selected entry after confirmation and selects the newest remaining one', async ({
    page,
  }) => {
    const base = await apiBase(page);
    const song = await createSong(page, base, `History delete ${String(Date.now())}`);
    await takeSnapshot(page, base, song, '[Verse]\nOldest draft', '2026-01-01T09:00:00Z');
    await takeSnapshot(page, base, song, '[Verse]\nMiddle draft', '2026-01-01T09:10:00Z');
    await takeSnapshot(page, base, song, '[Verse]\nNewest draft', '2026-01-01T09:20:00Z');
    const [newest, middle, oldest] = await snapshotIds(page, base, song);

    // 1. Open History and select the middle entry.
    await page.goto(`./songs/${song.shortcode}`);
    await expect(page.getByRole('textbox', { name: 'Lyrics' })).toBeVisible();
    await page.getByRole('button', { name: 'Show history' }).click();
    const list = page.getByRole('list', { name: 'Snapshots' });
    const entries = list.getByRole('button');
    await expect(entries).toHaveCount(3);
    await entries.nth(1).click();
    await expect(entries.nth(1)).toHaveAttribute('aria-pressed', 'true');
    const shown = page.getByTestId('snapshot');
    await expect(shown.getByRole('button', { name: 'Delete this snapshot' })).toBeVisible();
    await expectAccessibleInLightAndDark(page);
    const middleTime = await entries.nth(1).textContent();
    const newestTime = await entries.nth(0).textContent();

    // Choose Delete: the confirmation calls it permanent, without the retention period.
    await shown.getByRole('button', { name: 'Delete this snapshot' }).click();
    const confirm = page.getByRole('dialog', { name: 'Delete this snapshot?' });
    await expect(confirm).toBeVisible();
    await expect(confirm).toContainText('is deleted permanently. This cannot be undone.');
    await expect(confirm).not.toContainText(/30|days/);
    await expectModalAccessibleInBothSchemes(page);

    // Cancel first: nothing changes.
    await confirm.getByRole('button', { name: 'Cancel' }).click();
    await expect(confirm).toBeHidden();
    expect(await snapshotIds(page, base, song)).toEqual([newest, middle, oldest]);

    // 2. Delete and confirm: the entry disappears and the newest remaining one is selected.
    await shown.getByRole('button', { name: 'Delete this snapshot' }).click();
    await expect(confirm).toBeVisible();
    await confirm.getByRole('button', { name: 'Delete' }).click();
    await expect(confirm).toBeHidden();
    await expect(entries).toHaveCount(2);
    await expect(list.getByRole('button', { name: middleTime ?? 'missing' })).toHaveCount(0);
    const selected = list.getByRole('button', { name: newestTime ?? 'missing' });
    await expect(selected).toHaveAttribute('aria-pressed', 'true');
    await expect(selected).toBeFocused();
    await expect(page.getByTestId('snapshot-deleted')).toHaveText(
      `Deleted the snapshot from ${middleTime ?? ''}.`,
    );
    await expect(
      shown.getByRole('heading', { name: `Snapshot from ${newestTime ?? ''}` }),
    ).toBeVisible();
    expect(await snapshotIds(page, base, song)).toEqual([newest, oldest]);
    await expectAccessibleInLightAndDark(page);

    // It stays gone after a reload.
    await page.reload();
    await page.getByRole('button', { name: 'Show history' }).click();
    await expect(entries).toHaveCount(2);
  });
});
