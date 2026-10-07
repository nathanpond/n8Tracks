import { randomUUID } from 'node:crypto';
import { expect, test, type Page, type TestInfo } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { docker } from '../support/containers.ts';
import { seedGeneration } from '../support/seeding.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';
import { CONTAINER_BY_PROJECT } from '../support/targets.ts';

interface Song {
  id: string;
  shortcode: string;
  state: { id: string; name: string };
  selectedGeneration: { shortcode: string } | null;
  currentVersion: { id: string; shortcode: string };
}

interface WorkflowState {
  id: string;
  name: string;
  hidden: boolean;
}

/** The API base of the project's container, worked out on the Songs page. */
async function apiBase(page: Page): Promise<(path: string) => string> {
  await page.goto('./songs');
  const base = new URL('.', page.url());
  return (path: string) => new URL(`api/v1/${path}`, base).toString();
}

/** The name of the project's container. */
function containerOf(testInfo: TestInfo): string {
  const container = CONTAINER_BY_PROJECT[testInfo.project.name];
  if (container === undefined) {
    throw new Error(`No container is known for the project "${testInfo.project.name}".`);
  }
  return container;
}

function section(page: Page) {
  return page.getByRole('region', { name: 'Versions and Generations' });
}

/** Opens Version `number`'s Generations in the Versions table unless they are open already (the route may open them). */
async function expandVersion(page: Page, number: string): Promise<void> {
  const toggle = section(page).getByRole('button', { name: `Generations of Version ${number}` });
  await expect(toggle).toBeVisible();
  if ((await toggle.getAttribute('aria-expanded')) !== 'true') {
    await toggle.click();
  }
}

function clip(title: string): string {
  return JSON.stringify({ id: randomUUID(), status: 'complete', title });
}

/**
 * Walks #124's Demo on the built image: on a Song with two seeded Generations, the second is
 * selected in its panel and Delete is chosen; the confirmation names it, says what goes and that
 * nothing in Suno changes, and demands a replacement or a workflow state. A workflow state is
 * chosen and the delete confirmed: the Generation is gone, and the Song has no selection and the
 * chosen state. In the container, `n8tracks restore-deleted <shortcode>` brings it back (still not
 * selected). Every state is scanned with the accessibility helper. Runs on the project's shared
 * container, on a Song of its own.
 */
test.describe('Delete a Generation', () => {
  test('asks what the Song selects instead, deletes it, and restore-deleted brings it back', async ({
    page,
  }, testInfo) => {
    const api = await apiBase(page);
    const created = await page.request.post(api('songs'), {
      headers: ANTIFORGERY_HEADERS,
      data: { title: `Deleting ${String(Date.now())}` },
    });
    expect(created.status()).toBe(201);
    const song = (await created.json()) as Song;
    const first = await seedGeneration(testInfo, song.currentVersion.shortcode, clip('First take'));
    const second = await seedGeneration(
      testInfo,
      song.currentVersion.shortcode,
      clip('Second take'),
    );
    const states = (
      (await (await page.request.get(api('workflow-states'))).json()) as { items: WorkflowState[] }
    ).items;
    const target = states.find((state) => !state.hidden && state.id !== song.state.id);
    expect(target).toBeDefined();
    const targetName = target?.name ?? '';

    // 1. Select the second Generation, then choose Delete: the dialog demands a choice.
    await page.goto(`./songs/${song.shortcode}/generations/${second}`);
    const panel = page.getByRole('dialog', { name: `Generation ${second}` });
    await expect(panel).toBeVisible();
    await panel.getByRole('button', { name: 'Select for the Song' }).click();
    await expect(panel.getByTestId('generation-panel-selection')).toHaveText(
      'This is the Song’s chosen output.',
    );
    await panel.getByRole('button', { name: 'Delete Generation' }).click();
    const dialog = page.getByRole('dialog', { name: `Delete Generation ${second}?` });
    await expect(dialog).toBeVisible();
    const summary = dialog.getByTestId('delete-generation-summary');
    await expect(summary).toContainText(
      `Generation ${second} will be deleted from n8Tracks with its rating, its comments (none), and its Suno artwork (none).`,
    );
    await expect(summary).toContainText('Nothing in Suno is changed.');
    await expect(dialog.getByTestId('delete-generation-selection')).toContainText(
      `${second} is the Song’s Selected Generation.`,
    );
    const remove = dialog.getByRole('button', { name: `Delete ${second}` });
    await expect(remove).toBeDisabled();
    await expectModalAccessibleInBothSchemes(page);

    // 2. Choose a workflow state and confirm: the Generation is gone; no selection, that state.
    await dialog
      .getByRole('radio', { name: 'Clear the selection and set a workflow state' })
      .check();
    const stateChoice = dialog.getByRole('radiogroup', {
      name: 'Workflow state once the selection is cleared',
    });
    await stateChoice.getByRole('radio', { name: targetName, exact: true }).check();
    await expectModalAccessibleInBothSchemes(page);
    await remove.click();

    await expect(page.getByTestId('generation-deleted')).toContainText(
      `Generation ${second} deleted.`,
    );
    await expect(dialog).toBeHidden();
    await expect(page).toHaveURL(new RegExp(`/songs/${song.shortcode}/v/1$`));
    await expect(page.getByTestId('song-selected-generation')).toContainText('None');
    await expandVersion(page, '1');
    const generations = section(page).locator('table[data-generations-of="1"]');
    await expect(generations.locator(`tr[data-generation="${first}"]`)).toBeVisible();
    await expect(generations.locator(`tr[data-generation="${second}"]`)).toHaveCount(0);
    const after = (await (await page.request.get(api(`songs/${song.shortcode}`))).json()) as Song;
    expect(after.selectedGeneration).toBeNull();
    expect(after.state.id).toBe(target?.id);
    await expectAccessibleInLightAndDark(page);

    // 3. In the container, restore-deleted brings it back, still not selected.
    const restored = await docker(
      'exec',
      containerOf(testInfo),
      'n8tracks',
      'restore-deleted',
      second,
    );
    expect(restored).toContain(`Restored Generation ${second}`);

    await page.goto(`./songs/${song.shortcode}`);
    await expandVersion(page, '1');
    await expect(generations.locator(`tr[data-generation="${second}"]`)).toBeVisible();
    await expect(page.getByTestId('song-selected-generation')).toContainText('None');
    await expectAccessibleInLightAndDark(page);
  });
});
