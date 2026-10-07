import { expect, test, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
  expectNoA11yViolations,
} from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
}

/** Creates a Song through the API on the project's container and returns it. */
async function createSong(page: Page, title: string): Promise<Song> {
  await page.goto('./songs');
  const base = new URL('.', page.url());
  const response = await page.request.post(new URL('api/v1/songs', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(response.status()).toBe(201);
  return (await response.json()) as Song;
}

/** The tree's node for a Version. */
function node(page: Page, number: string) {
  return page
    .getByRole('tree', { name: 'Versions' })
    .locator(`[role="treeitem"][data-version-number="${number}"]`);
}

/** The node of `child` drawn inside the branch of `parent`. */
function childNode(page: Page, parent: string, child: string) {
  return node(page, parent)
    .locator('xpath=..')
    .locator(`[role="group"] [role="treeitem"][data-version-number="${child}"]`);
}

/** Creates a Version through the API from `sourceId` with `number`, and returns its ID. */
async function createVersion(page: Page, song: Song, sourceId: string, number: string) {
  const base = new URL('.', page.url());
  const response = await page.request.post(
    new URL(`api/v1/songs/${song.id}/versions`, base).toString(),
    { headers: ANTIFORGERY_HEADERS, data: { sourceVersionId: sourceId, number } },
  );
  expect(response.status()).toBe(201);
  return ((await response.json()) as { id: string }).id;
}

/**
 * Walks #62's Demo: branch from Version 1 (choosing the child, with a name), branch again (taking
 * the proposal), archive it and find it again with "Show archived", and switch the current Version
 * back. Runs on the project's shared container, on a Song of its own.
 */
test.describe('the Version tree', () => {
  test('branches from a Version and switches the current one', async ({ page }) => {
    const song = await createSong(page, `Branching ${String(Date.now())}`);

    // 1. Open the Song: the tree shows Version 1, marked current.
    await page.goto(`./songs/${song.shortcode}`);
    await expect(node(page, '1')).toBeVisible();
    await expect(node(page, '1')).toContainText('Current');
    await expect(page.getByRole('heading', { level: 3, name: 'Version 1' })).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // 2. Create New Version From 1: 2 is proposed and 1.1 offered; choose 1.1 and name it.
    await page.getByRole('button', { name: 'Create New Version From 1' }).click();
    const dialog = page.getByRole('dialog', { name: 'Create New Version From 1' });
    await expect(dialog.getByRole('radio', { name: /^2 \(proposed\)/ })).toBeChecked();
    await expect(dialog.getByRole('radio', { name: /^1\.1/ })).not.toBeChecked();
    await expectModalAccessibleInBothSchemes(page);
    await dialog.getByRole('radio', { name: /^1\.1/ }).check();
    await dialog.getByRole('textbox', { name: 'Name' }).fill('Guitar experimentation');
    await dialog.getByRole('button', { name: 'Create Version' }).click();

    // 3. The tree shows 1 with child 1.1, now current and selected, and the URL names it.
    await expect(dialog).toBeHidden();
    await expect(page).toHaveURL(new RegExp(`/songs/${song.shortcode}/v/1\\.1$`));
    await expect(page.getByRole('heading', { level: 3, name: 'Version 1.1' })).toBeVisible();
    // Nested: 1.1's node is in the branch of 1.
    const child = childNode(page, '1', '1.1');
    await expect(child).toContainText('Guitar experimentation');
    await expect(child).toContainText('Current');
    await expect(child).toHaveAttribute('aria-current', 'true');
    await expect(child).toHaveAttribute('aria-selected', 'true');
    await expect(node(page, '1')).not.toContainText('Current');
    await expectAccessibleInLightAndDark(page);

    // 4. Another from 1 proposes 2; take it.
    await node(page, '1').click();
    await page.getByRole('button', { name: 'Create New Version From 1' }).click();
    await expect(dialog.getByRole('radio', { name: /^2 \(proposed\)/ })).toBeChecked();
    await dialog.getByRole('button', { name: 'Create Version' }).click();
    await expect(page.getByRole('heading', { level: 3, name: 'Version 2' })).toBeVisible();
    await expect(node(page, '2')).toContainText('Current');

    // Archive 2 (#63): it is current, so it stays drawn, dimmed. Once 1 is current (step 5) it
    // disappears; "Show archived" brings it back dimmed.
    await page.getByRole('button', { name: 'Archive' }).click();
    await expect(page.getByRole('status').filter({ hasText: 'archived.' })).toContainText(
      'Version 2 archived.',
    );
    await expect(node(page, '2')).toHaveAttribute('data-archived', 'true');

    // 5. Select 1 and make it current: the marker moves.
    await node(page, '1').click();
    await expect(page.getByRole('heading', { level: 3, name: 'Version 1' })).toBeVisible();
    await page.getByRole('button', { name: 'Make current' }).click();
    await expect(node(page, '1')).toContainText('Current');
    await expect(page.getByRole('button', { name: 'Make current' })).toBeHidden();
    // 2 is archived and no longer current: hidden until "Show archived" is on.
    await expect(node(page, '2')).toHaveCount(0);
    await page.getByRole('switch', { name: 'Show archived', exact: true }).click();
    await expect(node(page, '2')).toBeVisible();
    await expect(node(page, '2')).toHaveAttribute('data-archived', 'true');
    await expect(node(page, '2')).not.toContainText('Current');
    await expectAccessibleInLightAndDark(page);

    // The choices are the server's (and "Show archived" this browser's): a reload shows the same tree.
    await page.reload();
    await expect(node(page, '1')).toContainText('Current');
    await expect(node(page, '1.1')).toBeVisible();
    await expect(node(page, '2')).toBeVisible();

    // A link to a number the Song does not have says so and links back to the Song.
    await page.goto(`./songs/${song.shortcode}/v/9`);
    await expect(page.getByRole('heading', { level: 3, name: 'Version not found' })).toBeVisible();
    await expectAccessibleInLightAndDark(page);
  });
});

/**
 * Walks #63's Demo on a Song with Versions 1, 1.1, and 2 (1 current): name and annotate 1.1,
 * archive 2 and find it again, archive the current Version, and work the tree from the keyboard.
 */
test.describe('naming, annotating, and archiving Versions', () => {
  test('tidies the tree and works it from the keyboard', async ({ page }) => {
    const song = await createSong(page, `Tidying ${String(Date.now())}`);
    const versions = (await (
      await page.request.get(
        new URL(`api/v1/songs/${song.id}/versions`, new URL('.', page.url())).toString(),
      )
    ).json()) as { items: { id: string }[] };
    const one = versions.items[0]?.id ?? '';
    await createVersion(page, song, one, '1.1');
    await createVersion(page, song, one, '2');
    const current = await page.request.put(
      new URL(`api/v1/songs/${song.id}/current-version`, new URL('.', page.url())).toString(),
      { headers: ANTIFORGERY_HEADERS, data: { versionId: one } },
    );
    expect(current.status()).toBe(200);

    // 1. Rename 1.1 to "Guitar experimentation" and add a note.
    await page.goto(`./songs/${song.shortcode}/v/1.1`);
    await expect(page.getByRole('heading', { level: 3, name: 'Version 1.1' })).toBeVisible();
    await page.getByRole('textbox', { name: 'Name' }).fill('Guitar experimentation');
    await expect(node(page, '1.1')).toContainText('Guitar experimentation');
    await page.getByRole('textbox', { name: 'Notes' }).fill('Try a capo on the second fret.');
    await expectNoA11yViolations(page);
    await expect(page.getByTestId('autosave').getByRole('status')).toHaveText('Saved');
    await expect(
      page.getByRole('treeitem', { name: 'Version 1.1, Guitar experimentation' }),
    ).toBeVisible();
    await page.reload();
    await expect(page.getByRole('textbox', { name: 'Notes' })).toHaveValue(
      'Try a capo on the second fret.',
    );
    await expectAccessibleInLightAndDark(page);

    // 2. Archive 2 from its actions menu; it disappears. Show archived brings it back dimmed.
    await node(page, '2').getByRole('button', { name: 'Actions for Version 2' }).click();
    await expectNoA11yViolations(page);
    await page.getByRole('menuitem', { name: 'Archive' }).click();
    await expect(page.getByRole('status').filter({ hasText: 'archived.' })).toContainText(
      'Version 2 archived.',
    );
    await expect(node(page, '2')).toHaveCount(0);
    await expectAccessibleInLightAndDark(page);
    await page.getByRole('switch', { name: 'Show archived', exact: true }).click();
    await expect(page.getByRole('treeitem', { name: 'Version 2, archived' })).toBeVisible();
    await expect(node(page, '2')).toHaveAttribute('data-archived', 'true');
    await expectAccessibleInLightAndDark(page);
    await page.getByRole('switch', { name: 'Show archived', exact: true }).click();
    await expect(node(page, '2')).toHaveCount(0);

    // 3. Archive the current Version (1): it stays drawn, dimmed and marked current, 1.1 under it.
    await node(page, '1').click();
    await expect(page.getByRole('heading', { level: 3, name: 'Version 1' })).toBeVisible();
    await page.getByRole('button', { name: 'Archive' }).click();
    await expect(page.getByRole('status').filter({ hasText: 'archived.' })).toContainText(
      'Version 1 archived.',
    );
    await expect(
      page.getByRole('treeitem', { name: 'Version 1, current working Version, archived' }),
    ).toBeVisible();
    await expect(node(page, '1')).toHaveAttribute('aria-current', 'true');
    await expect(childNode(page, '1', '1.1')).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // 4. Keyboard only: from the "Show archived" switch, Tab into the tree, move, select, and
    //    open the selected Version's actions.
    await page.getByRole('switch', { name: 'Show archived', exact: true }).focus();
    await page.keyboard.press('Tab');
    await expect(node(page, '1')).toBeFocused();
    await page.keyboard.press('ArrowDown');
    await expect(node(page, '1.1')).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { level: 3, name: 'Version 1.1' })).toBeVisible();
    await expect(page).toHaveURL(new RegExp(`/songs/${song.shortcode}/v/1\\.1$`));
    await expect(node(page, '1.1')).toHaveAttribute('aria-selected', 'true');
    await expect(node(page, '1.1')).toBeFocused();
    await page.keyboard.press('Shift+F10');
    const menu = page.getByRole('menu');
    await expect(menu.getByRole('menuitem', { name: 'Create New Version From 1.1' })).toBeVisible();
    await expect(menu.getByRole('menuitem', { name: 'Make current' })).toBeVisible();
    await expect(menu.getByRole('menuitem', { name: 'Archive' })).toBeVisible();
    await expectNoA11yViolations(page);
    await page.keyboard.press('Escape');
    await expect(menu).toBeHidden();
    await expect(node(page, '1.1')).toBeFocused();

    // The names, notes, and archive marks are the server's: a reload shows them.
    await page.reload();
    await expect(page.getByText('Try a capo on the second fret.')).toBeVisible();
    await expect(node(page, '1')).toHaveAttribute('data-archived', 'true');
    await expect(node(page, '2')).toHaveCount(0);
  });
});
