import { expect, test, type Page, type TestInfo } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { docker } from '../support/containers.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';
import { CONTAINER_BY_PROJECT } from '../support/targets.ts';

interface Song {
  id: string;
  shortcode: string;
  title: string;
  revision: number;
}

/** The API's base on the project's container, worked out on the Songs page. */
async function apiBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

/** The name of the project's container. */
function containerOf(testInfo: TestInfo): string {
  const container = CONTAINER_BY_PROJECT[testInfo.project.name];
  if (container === undefined) {
    throw new Error(`No container is known for the project "${testInfo.project.name}".`);
  }
  return container;
}

/** Opens the Song's Details panel, unless it is open already (its state is remembered). */
async function openDetails(page: Page): Promise<void> {
  const control = page.getByRole('button', { name: 'Details', exact: true });
  if ((await control.getAttribute('aria-expanded')) !== 'true') {
    await control.click();
  }
}

/** `n8tracks <args>` in the project's container, as `docker exec` runs it for the owner; its standard output. */
async function n8tracks(container: string, ...args: string[]): Promise<string> {
  return docker('exec', container, 'n8tracks', ...args);
}

/**
 * Walks #105's Demo on the project's shared container, with a stamped title: a Song with two
 * Versions and notes is deleted in the app; `n8tracks list-deleted` lists it with its shortcode;
 * `n8tracks restore-deleted <shortcode>` puts it back while the app runs; the Songs table shows it
 * again on reload, and its page has both Versions and its notes. Restoring it again is refused.
 * Each visited state is scanned in both colour schemes.
 */
test.describe('recovering a deleted Song with the container commands', () => {
  test('lists the deleted Song and restores it with its Versions and Details', async ({
    page,
  }, testInfo) => {
    const container = containerOf(testInfo);
    const stamp = String(Date.now());
    const title = `Deleted by mistake ${stamp}`;
    const notes = `Kept notes ${stamp}`;
    const base = await apiBase(page);

    const created = await page.request.post(new URL('api/v1/songs', base).toString(), {
      headers: ANTIFORGERY_HEADERS,
      data: { title },
    });
    expect(created.status()).toBe(201);
    const song = (await created.json()) as Song;
    const version = await page.request.post(
      new URL(`api/v1/songs/${song.id}/versions`, base).toString(),
      {
        headers: ANTIFORGERY_HEADERS,
        data: { sourceVersionId: `${song.shortcode}-v1`, number: '2' },
      },
    );
    expect(version.status()).toBe(201);
    const current = (await (
      await page.request.get(new URL(`api/v1/songs/${song.id}`, base).toString())
    ).json()) as Song;
    const edited = await page.request.patch(new URL(`api/v1/songs/${song.id}`, base).toString(), {
      headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(current.revision)}"` },
      data: { notes },
    });
    expect(edited.status()).toBe(200);

    // 1. Delete the Song in the app.
    await page.goto(`./songs/${song.shortcode}`);
    await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();
    await page.getByRole('button', { name: 'Delete Song', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: `Delete “${title}”?` });
    await expect(dialog.getByTestId('delete-song-counts')).toContainText('2 Versions');
    await dialog.getByRole('textbox', { name: 'Type the Song’s title to confirm' }).fill(title);
    await expectModalAccessibleInBothSchemes(page);
    await dialog.getByRole('button', { name: 'Delete Song' }).click();
    await expect(page.getByTestId('song-deleted-notice')).toContainText(
      `Deleted ${song.shortcode} “${title}”.`,
    );

    // 2. list-deleted lists it with its shortcode.
    const listed = await n8tracks(container, 'list-deleted');
    const row = listed
      .split('\n')
      .find((line) => line.includes(`Song ${song.shortcode} (${title})`));
    expect(row, listed).toBeDefined();
    expect(row).toMatch(new RegExp(`^\\S+\\s+Song\\s+.*\\s${song.shortcode}\\s`));

    // 3. restore-deleted puts it back while the app runs; the Songs table shows it on reload.
    const restored = await n8tracks(container, 'restore-deleted', song.shortcode);
    expect(restored).toContain(`Restored Song ${song.shortcode} (${title})`);
    expect(restored).toContain('Version: 2');

    await page.goto(`./songs?title=${encodeURIComponent(title)}`);
    const table = page.getByRole('table', { name: 'Songs' });
    await expect(
      table
        .getByRole('row')
        .filter({ has: page.getByRole('rowheader', { name: song.shortcode, exact: true }) }),
    ).toContainText(title);
    await expectAccessibleInLightAndDark(page);

    // Its page has both Versions and its Details.
    await page.goto(`./songs/${song.shortcode}`);
    await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();
    const tree = page.getByRole('tree', { name: 'Versions' });
    await expect(tree.locator('[role="treeitem"][data-version-number="1"]')).toBeVisible();
    await expect(tree.locator('[role="treeitem"][data-version-number="2"]')).toBeVisible();
    await openDetails(page);
    await expect(
      page.getByRole('complementary', { name: 'Details' }).getByText(notes),
    ).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // Restoring it again is refused: nothing with that shortcode is in retention any more.
    await expect(n8tracks(container, 'restore-deleted', song.shortcode)).rejects.toThrow(
      /Nothing deleted as/,
    );
  });
});
