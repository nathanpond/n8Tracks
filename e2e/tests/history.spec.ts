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

/** The Version's snapshots as the API lists them, newest first. */
async function snapshots(page: Page, base: URL, song: Song): Promise<{ id: string }[]> {
  const response = await page.request.get(
    new URL(`api/v1/versions/${song.currentVersion.id}/snapshots`, base).toString(),
  );
  expect(response.status()).toBe(200);
  return ((await response.json()) as { items: { id: string }[] }).items;
}

/** One snapshot's text. */
async function snapshotText(page: Page, base: URL, song: Song, id: string): Promise<string> {
  const response = await page.request.get(
    new URL(`api/v1/versions/${song.currentVersion.id}/snapshots/${id}`, base).toString(),
  );
  expect(response.status()).toBe(200);
  return ((await response.json()) as { lyrics: string }).lyrics;
}

function saveStatus(page: Page) {
  return page.getByTestId('autosave').getByRole('status');
}

/**
 * Walks #66's Demo on a Song of its own: a verse, a 30-second pause, a complete rewrite, another
 * pause (Playwright's clock stands in for the pauses); History lists both, compares the earlier
 * one with the editor in words, and restores it after confirmation, keeping the rewrite in
 * History; then a tool changes the lyrics with a bearer token, and History holds the text from
 * before that change.
 */
test.describe('editing history', () => {
  test('keeps snapshots on a pause, compares and restores them, and covers a tool’s edit', async ({
    page,
  }) => {
    await page.clock.install();
    const base = await apiBase(page);
    const song = await createSong(page, base, `History ${String(Date.now())}`);
    await page.goto(`./songs/${song.shortcode}`);
    const editor = page.getByRole('textbox', { name: 'Lyrics' });
    await expect(editor).toBeVisible();

    // 1. Type a verse and pause for 30 seconds: one snapshot.
    await editor.click();
    await page.keyboard.type('[Verse]\nFirst light on the water');
    await expect(saveStatus(page)).toHaveText('Saved');
    await page.clock.fastForward(31_000);
    await expect.poll(async () => (await snapshots(page, base, song)).length).toBe(1);

    // Rewrite it completely and pause again: a second one.
    await editor.click();
    await page.keyboard.press('ControlOrMeta+a');
    await page.keyboard.type('[Verse]\nNothing like the first');
    await expect(saveStatus(page)).toHaveText('Saved');
    await page.clock.fastForward(31_000);
    await expect.poll(async () => (await snapshots(page, base, song)).length).toBe(2);

    // 2. Open History: both are listed, newest first. The earlier one compared with the editor.
    await page.getByRole('button', { name: 'Show history' }).click();
    const list = page.getByRole('list', { name: 'Snapshots' });
    await expect(list.getByRole('button')).toHaveCount(2);
    await expectAccessibleInLightAndDark(page);
    await list.getByRole('button').last().click();
    const shown = page.getByTestId('snapshot');
    const lyrics = shown.getByRole('list', {
      name: 'Lyrics: changes from the snapshot to the editor now',
    });
    await expect(lyrics.getByRole('listitem')).toHaveText([
      '[Verse]',
      '− Removed: First light on the water',
      '+ Added: Nothing like the first',
    ]);
    await expectAccessibleInLightAndDark(page);

    // 3. Restore it, after confirming: the editor shows the earlier verse.
    await shown.getByRole('button', { name: 'Restore this snapshot' }).click();
    const confirm = page.getByRole('dialog', { name: 'Restore this snapshot?' });
    await expect(confirm).toBeVisible();
    await expectModalAccessibleInBothSchemes(page);
    await confirm.getByRole('button', { name: 'Restore' }).click();
    await expect(editor).toHaveText(/First light on the water/);
    await expect(editor).not.toContainText('Nothing like the first');
    await expect(page.getByText(/^Restored the snapshot from /)).toBeVisible();
    await expect(saveStatus(page)).toHaveText('Saved');
    // History has an entry holding the text the restore replaced: the rewrite, newest.
    const afterRestore = await snapshots(page, base, song);
    expect(await snapshotText(page, base, song, afterRestore[0]?.id ?? 'missing')).toBe(
      '[Verse]\nNothing like the first',
    );
    await expectAccessibleInLightAndDark(page);

    // 4. A tool changes the lyrics with a bearer token through the API.
    const created = await page.request.post(new URL('api/v1/credentials', base).toString(), {
      headers: ANTIFORGERY_HEADERS,
      data: {
        name: `History tool ${String(Date.now())}`,
        kind: 'api',
        scopes: ['versions.write', 'catalog.read'],
      },
    });
    expect(created.status()).toBe(201);
    const { token } = (await created.json()) as { token: string };
    const tool = { Authorization: `Bearer ${token}`, Accept: 'application/json' };
    const versionUrl = new URL(`api/v1/versions/${song.currentVersion.id}`, base).toString();
    const current = (await (await page.request.get(versionUrl)).json()) as { revision: number };
    const edited = await page.request.patch(versionUrl, {
      headers: { ...tool, 'If-Match': `"${String(current.revision)}"` },
      data: { lyrics: '[Verse]\nWritten by a tool' },
    });
    expect(edited.status()).toBe(200);

    // History shows an entry holding the text from before that change.
    await page.reload();
    await expect(editor).toHaveText(/Written by a tool/);
    await page.getByRole('button', { name: 'Show history' }).click();
    await expect(list.getByRole('button')).toHaveCount(3);
    await list.getByRole('button').first().click();
    await expect(
      shown
        .getByRole('list', { name: 'Lyrics: changes from the snapshot to the editor now' })
        .getByRole('listitem'),
    ).toHaveText(['[Verse]', '− Removed: First light on the water', '+ Added: Written by a tool']);
    await expectAccessibleInLightAndDark(page);
  });
});
