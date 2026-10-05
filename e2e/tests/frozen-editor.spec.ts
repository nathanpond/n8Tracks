import { expect, test, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
  expectNoA11yViolations,
} from '../support/a11y.ts';
import { seedGeneration } from '../support/seeding.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  currentVersion: { id: string; shortcode: string };
}

interface VersionDetail {
  number: string;
  current: boolean;
  isFrozen: boolean;
  lyrics: string;
  styles: string;
}

const GENERATED = { lyrics: '[Verse]\nThe take that made it\n', styles: 'synthwave' };
const EARLIER = { lyrics: '[Verse]\nAn earlier idea\n', styles: 'folk, slow' };

/** The API base of the project's container, worked out on the Songs page. */
async function apiBase(page: Page): Promise<(path: string) => string> {
  await page.goto('./songs');
  const base = new URL('.', page.url());
  return (path: string) => new URL(`api/v1/${path}`, base).toString();
}

/** The autosave indicator's words. */
function saveStatus(page: Page) {
  return page.getByTestId('autosave').getByRole('status');
}

/**
 * Walks #70's Demo against the real image: a Generation attached with the test-only seeding
 * command makes the Version's editor read only, with a notice and its Create New Version From
 * action and a lock in the tree; the name and notes still save; and a snapshot in History is
 * restored into a new Version, which is editable and holds that text.
 */
test.describe('a frozen Version in the editor', () => {
  test('is read only, steers to a new Version, and restores History into one', async ({
    page,
  }, testInfo) => {
    const api = await apiBase(page);
    const created = await page.request.post(api('songs'), {
      data: { title: `Locked ${String(Date.now())}` },
      headers: ANTIFORGERY_HEADERS,
    });
    expect(created.status()).toBe(201);
    const song = (await created.json()) as Song;
    const version = song.currentVersion;
    const snapshot = await page.request.post(api(`versions/${version.id}/snapshots`), {
      data: EARLIER,
      headers: ANTIFORGERY_HEADERS,
    });
    expect(snapshot.status()).toBe(201);
    const written = await page.request.patch(api(`versions/${version.id}`), {
      data: GENERATED,
      headers: { ...ANTIFORGERY_HEADERS, 'If-Match': '"1"' },
    });
    expect(written.status()).toBe(200);

    // 1. Attach a Generation with the test-only seeding command.
    await seedGeneration(testInfo, version.shortcode);

    // 2. The editor is read only, with the notice and its action; the tree shows a lock.
    await page.goto(`./songs/${song.shortcode}/v/1`);
    const lyrics = page.getByRole('textbox', { name: 'Lyrics' });
    await expect(lyrics).toHaveAttribute('aria-readonly', 'true');
    await expect(lyrics).toContainText('The take that made it');
    await expect(page.getByRole('textbox', { name: 'Styles' })).toHaveAttribute('readonly', '');
    const notice = page.getByTestId('frozen-notice');
    await expect(notice).toContainText(
      'This Version has a Generation, so its lyrics and settings can no longer be changed. Create a new Version to keep working.',
    );
    await expect(
      notice.getByRole('button', { name: 'Create New Version From 1', exact: true }),
    ).toBeVisible();
    const node = page.locator('[role="treeitem"][data-version-number="1"]');
    await expect(node).toHaveAccessibleName(/frozen$/);
    await expect(node.getByRole('img', { name: 'Frozen: has a Generation' })).toBeVisible();

    // Typing changes nothing.
    await lyrics.click();
    await page.keyboard.type('ignored');
    await expect(lyrics).not.toContainText('ignored');
    await expectAccessibleInLightAndDark(page);

    // The notice's action opens the branching dialog with the proposal chosen; Cancel changes nothing.
    await notice.getByRole('button', { name: 'Create New Version From 1', exact: true }).click();
    const branching = page.getByRole('dialog', { name: 'Create New Version From 1' });
    await expect(branching.getByRole('radio', { name: '2 (proposed)' })).toBeChecked();
    await expectModalAccessibleInBothSchemes(page);
    await branching.getByRole('button', { name: 'Cancel' }).click();
    await expect(branching).toBeHidden();

    // 3. Rename the Version and edit its notes: both still save.
    await page.getByRole('textbox', { name: 'Name' }).fill('The keeper');
    await page.getByRole('textbox', { name: 'Notes' }).fill('Generated on the first try.');
    await expect(saveStatus(page)).toHaveText('Saved');
    await expectNoA11yViolations(page);

    // 4. History stays readable; Restore is replaced by Restore into a new Version.
    await page.getByRole('button', { name: 'Show history' }).click();
    const list = page.getByRole('list', { name: 'Snapshots' });
    await list.getByRole('button').first().click();
    const shown = page.getByTestId('snapshot');
    await expect(
      shown.getByRole('list', { name: 'Lyrics: changes from the snapshot to the editor now' }),
    ).toContainText('Removed: An earlier idea');
    await expect(shown.getByRole('button', { name: 'Restore this snapshot' })).toHaveCount(0);
    await expectAccessibleInLightAndDark(page);

    await shown.getByRole('button', { name: 'Restore into a new Version' }).click();
    await expect(branching).toContainText('starts with the lyrics and styles carried over');
    await expect(branching.getByRole('radio', { name: '2 (proposed)' })).toBeChecked();
    await expectModalAccessibleInBothSchemes(page);
    await branching.getByRole('button', { name: 'Create Version' }).click();

    // The new Version is current, selected, editable, and holds the snapshot's text.
    await expect(page.getByRole('heading', { name: 'Version 2' })).toBeVisible();
    await expect(page.getByTestId('frozen-notice')).toHaveCount(0);
    const newLyrics = page.getByRole('textbox', { name: 'Lyrics' });
    await expect(newLyrics).not.toHaveAttribute('aria-readonly', 'true');
    await expect(newLyrics).toContainText('An earlier idea');
    await expect(page.getByRole('textbox', { name: 'Styles' })).toHaveValue(EARLIER.styles);
    await newLyrics.click();
    await page.keyboard.press('ControlOrMeta+End');
    await page.keyboard.type('More');
    await expect(saveStatus(page)).toHaveText('Saved');
    await expectAccessibleInLightAndDark(page);

    const versions = (await (
      await page.request.get(api(`songs/${song.shortcode}/versions`))
    ).json()) as { items: (VersionDetail & { id: string })[] };
    const two = versions.items.find((item) => item.number === '2');
    expect(two?.current).toBe(true);
    expect(two?.isFrozen).toBe(false);
    const twoDetail = (await (
      await page.request.get(api(`versions/${two?.id ?? ''}`))
    ).json()) as VersionDetail;
    expect(twoDetail.lyrics).toBe(`${EARLIER.lyrics}More`);
    expect(twoDetail.styles).toBe(EARLIER.styles);
    // The frozen Version kept its text.
    const one = (await (
      await page.request.get(api(`versions/${version.id}`))
    ).json()) as VersionDetail;
    expect(one.lyrics).toBe(GENERATED.lyrics);
    expect(one.isFrozen).toBe(true);
  });
});
