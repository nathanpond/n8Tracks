import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
  expectNoA11yViolations,
} from '../support/a11y.ts';
import {
  removeContainers,
  startContainer,
  waitForHealth,
  type Target,
} from '../support/containers.ts';
import { signInThroughApi } from '../support/session.ts';
import { completeSetup } from '../support/setup.ts';
import { FRESH_NAME, FRESH_PORT, FRESH_URL } from '../support/targets.ts';

const fresh: Target = {
  name: FRESH_NAME,
  port: FRESH_PORT,
  url: FRESH_URL,
  media: true,
  expectedStatus: 'healthy',
};

const TITLE = 'Running in a Pack';
const CONCEPT =
  'A fast-paced song about running in a pack through the night, with a chorus that keeps ' +
  'coming back louder each time and a bridge where everything slows down to a walk.';

function songsTable(page: Page) {
  return page.getByRole('table', { name: 'Songs' });
}

function songRow(page: Page, shortcode: string) {
  return songsTable(page)
    .getByRole('row')
    .filter({ has: page.getByRole('rowheader', { name: shortcode, exact: true }) });
}

function stateFilter(page: Page) {
  return page.getByRole('group', { name: 'Workflow state' });
}

/** Creates a Song through the New Song dialog, scanning the dialog once filled when asked to. */
async function createThroughTheDialog(
  page: Page,
  title: string,
  concept: string,
  scan = false,
): Promise<void> {
  await page.getByRole('button', { name: 'New Song' }).click();
  const form = page.getByRole('form', { name: 'New Song' });
  await expect(form).toBeVisible();
  await form.getByLabel('Title').fill(title);
  if (concept !== '') {
    await form.getByLabel('Concept').fill(concept);
  }
  if (scan) {
    await expectModalAccessibleInBothSchemes(page);
  }
  await form.getByRole('button', { name: 'Create Song' }).click();
  await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();
}

/**
 * Walks the story's Demo on a container of its own, so the Songs screen really starts empty and
 * the first Song really is `n8-1`. Set up through the API with the test administrator, started
 * afresh for each attempt and removed afterwards.
 */
test.describe('the Songs screen, from empty', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-songs-'));
    await mkdir(join(work, 'media'));
    await startContainer(fresh, work);
    await waitForHealth(fresh);
    await completeSetup(FRESH_URL);
    await signInThroughApi(page.request, FRESH_URL);
  });

  test.afterEach(async () => {
    await removeContainers(FRESH_NAME);
    if (work !== undefined) {
      await rm(work, { recursive: true, force: true });
    }
  });

  test('creates Songs, lands on each, and keeps the table view across a reload', async ({
    page,
  }) => {
    // 1. Songs, empty.
    await page.goto(`${FRESH_URL}songs`);
    await expect(page.getByRole('heading', { level: 2, name: 'Songs' })).toBeVisible();
    await expect(page.getByText('There are no Songs yet.')).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // A blank title is refused in the dialog, which stays open.
    await page.getByRole('button', { name: 'New Song' }).click();
    const form = page.getByRole('form', { name: 'New Song' });
    await form.getByRole('button', { name: 'Create Song' }).click();
    await expect(form.getByText('Enter a title.')).toBeVisible();
    await expectModalAccessibleInBothSchemes(page);
    await form.getByRole('button', { name: 'Cancel' }).click();
    await expect(form).toBeHidden();

    // 2. New Song with a title and a concept: its page opens, showing n8-1.
    await createThroughTheDialog(page, TITLE, CONCEPT, true);
    await expect(page).toHaveURL(`${FRESH_URL}songs/n8-1`);
    await expect(page.getByTestId('shortcode')).toHaveText('n8-1');
    await expect(page.getByText(CONCEPT)).toBeVisible();
    await expect(page.getByText('Idea', { exact: true })).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // 3. Back to Songs: the row shows shortcode, title, concept, state Idea, 1 Version.
    await page.getByRole('link', { name: '← Songs' }).click();
    const first = songRow(page, 'n8-1');
    await expect(first).toBeVisible();
    await expect(first.getByRole('link', { name: TITLE })).toBeVisible();
    await expect(first.getByRole('cell').nth(0)).toHaveText(TITLE);
    // No default Artist on the fresh container, so the Artist cell is empty.
    await expect(first.getByRole('cell').nth(1)).toHaveText('');
    await expect(first.getByRole('cell').nth(2)).toHaveText(CONCEPT);
    await expect(first.getByRole('cell').nth(3)).toHaveText('Idea');
    await expect(first.getByRole('cell').nth(4)).toHaveText('Song');
    await expect(first.getByRole('cell').nth(5)).toHaveText('1');
    // The long concept is cut to one line, and all of it shows on keyboard focus.
    const concept = first.getByRole('cell').nth(2).locator('[tabindex="0"]');
    const box = await concept.boundingBox();
    expect(box?.height).toBeLessThan(30);
    // From the title link, the way a keyboard user gets there: Tab.
    await first.getByRole('link', { name: TITLE }).focus();
    await page.keyboard.press('Tab');
    await expect(concept).toBeFocused();
    await expect(page.getByRole('tooltip')).toHaveText(CONCEPT);
    await expectAccessibleInLightAndDark(page);
    await concept.blur();

    // 4. Two more with the same title: three rows, three shortcodes.
    for (const shortcode of ['n8-2', 'n8-3']) {
      await createThroughTheDialog(page, TITLE, '');
      await expect(page.getByTestId('shortcode')).toHaveText(shortcode);
      await page.getByRole('link', { name: '← Songs' }).click();
    }
    await expect(songsTable(page).getByRole('rowheader')).toHaveText(['n8-3', 'n8-2', 'n8-1']);

    // 5. Filter by state and sort by title; reload; the same view comes back.
    await stateFilter(page).getByText('Idea', { exact: true }).click();
    await stateFilter(page).getByText('Archived', { exact: true }).click();
    await expect(stateFilter(page).getByRole('checkbox', { name: 'Idea' })).toBeChecked();
    await page.getByRole('button', { name: 'Title' }).click();
    // Same titles: shortcode order breaks the tie.
    await expect(songsTable(page).getByRole('rowheader')).toHaveText(['n8-1', 'n8-2', 'n8-3']);
    await expect(page).toHaveURL(/[?&]sort=title(&|$)/);
    await expect(page).toHaveURL(/[?&]state=.*&state=/);

    await page.reload();
    await expect(songsTable(page).getByRole('rowheader')).toHaveText(['n8-1', 'n8-2', 'n8-3']);
    await expect(stateFilter(page).getByRole('checkbox', { name: 'Idea' })).toBeChecked();
    await expect(stateFilter(page).getByRole('checkbox', { name: 'Archived' })).toBeChecked();
    await expect(stateFilter(page).getByRole('checkbox', { name: 'Writing' })).not.toBeChecked();
    await expect(page.getByRole('columnheader', { name: /Title/ })).toHaveAttribute(
      'aria-sort',
      'ascending',
    );
    await expectAccessibleInLightAndDark(page);

    // A filter that matches none of them says so.
    await stateFilter(page).getByText('Idea', { exact: true }).click();
    await expect(page.getByText('No Songs are in the chosen states.')).toBeVisible();
    await expectNoA11yViolations(page);
  });
});

/**
 * On the project's shared container (at the root and under the sub-path): a Song made in the
 * dialog opens at its shortcode's address, which also works when loaded directly.
 */
test.describe('the Song page', () => {
  test('opens a new Song by its shortcode, directly too', async ({ page }) => {
    const title = `Song page ${String(Date.now())}`;
    await page.goto('./songs');
    await createThroughTheDialog(page, title, 'One line.\nAnother line.');
    const shortcode = (await page.getByTestId('shortcode').textContent()) ?? '';
    expect(shortcode).toMatch(/^n8-[1-9][0-9]*$/);
    await expect(page).toHaveURL(new RegExp(`/songs/${shortcode}$`));

    await page.reload();
    await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();
    await expect(page.getByTestId('shortcode')).toHaveText(shortcode);
    await expectNoA11yViolations(page);

    await page.goto('./songs/n8-999999');
    await expect(page.getByRole('heading', { level: 2, name: 'Song not found' })).toBeVisible();
  });
});
