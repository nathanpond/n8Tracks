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
  title: string;
  concept: string | null;
  state: { name: string };
  revision: number;
}

/**
 * Creates a Song through the API, on the project's container. Returns it and a reader that GETs
 * it again, against the app's base (the root or the sub-path) as the Songs page resolves it.
 */
async function createSong(
  page: Page,
  title: string,
  concept: string,
): Promise<{ song: Song; read: () => Promise<Song> }> {
  await page.goto('./songs');
  const base = new URL('.', page.url());
  const response = await page.request.post(new URL('api/v1/songs', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { title, concept },
  });
  expect(response.status()).toBe(201);
  const song = (await response.json()) as Song;
  const read = async () => {
    const answer = await page.request.get(new URL(`api/v1/songs/${song.id}`, base).toString());
    expect(answer.ok()).toBe(true);
    return (await answer.json()) as Song;
  };
  return { song, read };
}

function stateButton(page: Page, state: string) {
  return page.getByRole('button', { name: `State: ${state}` });
}

/**
 * Walks the story's Demo in two pages of one session, as two tabs: a change saved in tab A makes
 * tab B's later save stale, which is refused, compared in the dialog, and reapplied on top. Runs on
 * the project's shared container (at the root and under the sub-path), on a Song of its own.
 */
test.describe('editing a Song', () => {
  test('a stale save is refused, compared, and reapplied on top of the other tab’s change', async ({
    page,
    context,
  }) => {
    const stamp = String(Date.now());
    const title = `Two tabs ${stamp}`;
    const { song, read } = await createSong(page, title, 'The concept both tabs loaded.');

    // 1. The Song open in two tabs.
    const tabA = page;
    const tabB = await context.newPage();
    for (const tab of [tabA, tabB]) {
      await tab.goto(`./songs/${song.shortcode}`);
      await expect(tab.getByRole('heading', { level: 2, name: title })).toBeVisible();
      await expect(stateButton(tab, 'Idea')).toBeEnabled();
    }
    await expectAccessibleInLightAndDark(tabA);

    // 2. Tab A changes the title (Enter saves) and the state (from the menu); both save.
    const newTitle = `Renamed in tab A ${stamp}`;
    await tabA.getByRole('button', { name: 'Edit title' }).click();
    const titleInput = tabA.getByRole('textbox', { name: 'Title', exact: true });
    await expect(titleInput).toBeFocused();
    await titleInput.fill(newTitle);
    await expectNoA11yViolations(tabA);
    await titleInput.press('Enter');
    await expect(tabA.getByRole('heading', { level: 2, name: newTitle })).toBeVisible();
    await stateButton(tabA, 'Idea').click();
    await expectNoA11yViolations(tabA);
    await tabA.getByRole('menuitem', { name: 'Writing' }).click();
    await expect(stateButton(tabA, 'Writing')).toBeVisible();
    expect(await read()).toMatchObject({
      title: newTitle,
      state: { name: 'Writing' },
      revision: 3,
    });

    // 3. Tab B, still on revision 1, changes the concept: the save is refused, and the dialog
    //    shows that the title and state changed elsewhere, alongside the concept edit.
    const concept = 'The concept tab B wrote.\nOn two lines.';
    await tabB.getByRole('button', { name: 'Edit concept' }).click();
    const area = tabB.getByRole('textbox', { name: 'Concept' });
    await area.fill(concept);
    await area.press('Control+Enter');
    const dialog = tabB.getByRole('dialog', { name: 'Changed since you loaded it' });
    await expect(dialog).toBeVisible();
    const differences = dialog.getByRole('table', { name: 'Differences' });
    const row = (field: string) =>
      differences
        .getByRole('row')
        .filter({ has: tabB.getByRole('rowheader', { name: new RegExp(`^${field}`) }) });
    await expect(row('Title')).toContainText('Changed elsewhere');
    await expect(row('Title')).toContainText(title);
    await expect(row('Title')).toContainText(newTitle);
    await expect(row('Concept')).toContainText('Your change');
    await expect(row('Concept')).toContainText('On two lines.');
    await expect(row('State')).toContainText('Idea');
    await expect(row('State')).toContainText('Writing');
    await expectModalAccessibleInBothSchemes(tabB);

    // Nothing was overwritten meanwhile.
    expect(await read()).toMatchObject({
      title: newTitle,
      concept: 'The concept both tabs loaded.',
      revision: 3,
    });

    // 4. Reapply: the concept is saved on top of tab A's title and state.
    await dialog.getByRole('button', { name: 'Reapply my change' }).click();
    await expect(dialog).toBeHidden();
    await expect(tabB.getByText('On two lines.')).toBeVisible();
    await expect(tabB.getByRole('heading', { level: 2, name: newTitle })).toBeVisible();
    await expect(stateButton(tabB, 'Writing')).toBeVisible();
    expect(await read()).toMatchObject({
      title: newTitle,
      concept,
      state: { name: 'Writing' },
      revision: 4,
    });
    await expectAccessibleInLightAndDark(tabB);

    // A title edited to empty stays open with an error, and nothing is saved.
    await tabB.getByRole('button', { name: 'Edit title' }).click();
    await tabB.getByRole('textbox', { name: 'Title', exact: true }).fill('   ');
    await tabB.getByRole('textbox', { name: 'Title', exact: true }).press('Enter');
    await expect(tabB.getByText('Enter a title.')).toBeVisible();
    await expectNoA11yViolations(tabB);
    await tabB.getByRole('textbox', { name: 'Title', exact: true }).press('Escape');
    await expect(tabB.getByRole('heading', { level: 2, name: newTitle })).toBeVisible();
    expect((await read()).revision).toBe(4);
  });
});
