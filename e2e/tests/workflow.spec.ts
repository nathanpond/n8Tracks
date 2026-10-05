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
import { ANTIFORGERY_HEADERS, signInThroughApi } from '../support/session.ts';
import { completeSetup } from '../support/setup.ts';
import { FRESH_NAME, FRESH_PORT, FRESH_URL } from '../support/targets.ts';

const fresh: Target = {
  name: FRESH_NAME,
  port: FRESH_PORT,
  url: FRESH_URL,
  media: true,
  expectedStatus: 'healthy',
};

function states(page: Page) {
  return page.getByRole('list', { name: 'Workflow states' });
}

function stateRow(page: Page, name: string) {
  return states(page).locator(`li[data-state-name="${name}"]`);
}

async function stateNames(page: Page): Promise<string[]> {
  return states(page)
    .locator('li[data-state-name]')
    .evaluateAll((rows) => rows.map((row) => row.getAttribute('data-state-name') ?? ''));
}

function stateButton(page: Page, state: string) {
  return page.getByRole('button', { name: `State: ${state}` });
}

async function openWorkflow(page: Page): Promise<void> {
  await page.goto(`${FRESH_URL}settings/account`);
  await page.getByRole('link', { name: 'Workflow' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Workflow' })).toBeVisible();
  await expect(states(page)).toBeVisible();
}

/**
 * Walks the story's Demo on a container of its own: the workflow is shared by the whole instance,
 * so changing it on a shared container would change what every other test sees. Set up through the
 * API with the test administrator, started afresh for each attempt and removed afterwards.
 */
test.describe('Settings → Workflow', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-workflow-'));
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

  test('adds, moves, renames, recolours, hides, and deletes states, moving Songs to a replacement', async ({
    page,
  }) => {
    const created = await page.request.post(`${FRESH_URL}api/v1/songs`, {
      headers: ANTIFORGERY_HEADERS,
      data: { title: 'Running in a Pack' },
    });
    expect(created.status()).toBe(201);
    const song = (await created.json()) as { shortcode: string };

    // The states in order, each with its colour and Song count.
    await openWorkflow(page);
    expect(await stateNames(page)).toEqual([
      'Idea',
      'Writing',
      'Generating',
      'Refining',
      'Final',
      'Released',
      'Archived',
    ]);
    await expect(stateRow(page, 'Idea')).toContainText('1 Song');
    await expect(stateRow(page, 'Writing')).toContainText('No Songs');
    await expectAccessibleInLightAndDark(page);

    // 1. Add "Mixing" (at the end), and drag it between Refining and Final.
    await page.getByRole('textbox', { name: /New state/ }).fill('Mixing');
    await page.getByRole('button', { name: 'Add state' }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Mixing is added' })).toBeVisible();
    expect((await stateNames(page)).at(-1)).toBe('Mixing');
    await stateRow(page, 'Mixing').dragTo(stateRow(page, 'Final'));
    await expect
      .poll(() => stateNames(page))
      .toEqual([
        'Idea',
        'Writing',
        'Generating',
        'Refining',
        'Mixing',
        'Final',
        'Released',
        'Archived',
      ]);
    await expectNoA11yViolations(page);

    // ...and by keyboard: Move down, then Move up, puts it back between Refining and Final.
    const moveDown = stateRow(page, 'Mixing').getByRole('button', { name: 'Move Mixing down' });
    await moveDown.focus();
    await page.keyboard.press('Enter');
    await expect
      .poll(() => stateNames(page))
      .toEqual([
        'Idea',
        'Writing',
        'Generating',
        'Refining',
        'Final',
        'Mixing',
        'Released',
        'Archived',
      ]);
    await expect(moveDown).toBeFocused();
    await stateRow(page, 'Mixing').getByRole('button', { name: 'Move Mixing up' }).press('Enter');
    await expect
      .poll(() => stateNames(page))
      .toEqual([
        'Idea',
        'Writing',
        'Generating',
        'Refining',
        'Mixing',
        'Final',
        'Released',
        'Archived',
      ]);

    // 2. Rename "Idea" to "Spark" and change its colour.
    await stateRow(page, 'Idea').getByRole('button', { name: 'Edit Idea' }).click();
    const edit = page.getByRole('dialog', { name: 'Edit Idea' });
    await edit.getByRole('textbox', { name: 'Name' }).fill('Spark');
    await edit.getByRole('radio', { name: 'Cyan' }).check();
    await expectModalAccessibleInBothSchemes(page);
    await edit.getByRole('button', { name: 'Save' }).click();
    await expect(edit).toBeHidden();
    await expect(stateRow(page, 'Spark').locator('[data-state-colour]')).toHaveAttribute(
      'data-state-colour',
      'cyan',
    );

    // 3. Hide "Archived" (with an Undo on offer); a Song's state menu does not offer it.
    await stateRow(page, 'Archived').getByRole('button', { name: 'Hide Archived' }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Archived is hidden.' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Undo' })).toBeVisible();
    await expect(stateRow(page, 'Archived')).toContainText('Hidden');
    await expectAccessibleInLightAndDark(page);

    await page.goto(`${FRESH_URL}songs/${song.shortcode}`);
    await stateButton(page, 'Spark').click();
    await expect(page.getByRole('menuitem', { name: /Mixing/ })).toBeVisible();
    await expect(page.getByRole('menuitem', { name: /Archived/ })).toHaveCount(0);
    await expectNoA11yViolations(page);

    // 4. Put the Song in "Mixing", then delete "Mixing": the dialog asks for a replacement.
    await page.getByRole('menuitem', { name: /Mixing/ }).click();
    await expect(stateButton(page, 'Mixing')).toBeVisible();

    await openWorkflow(page);
    await expect(stateRow(page, 'Mixing')).toContainText('1 Song');
    await stateRow(page, 'Mixing').getByRole('button', { name: 'Delete Mixing' }).click();
    const remove = page.getByRole('dialog', { name: 'Delete Mixing' });
    await expect(remove).toContainText('1 Song is in Mixing');
    await expectModalAccessibleInBothSchemes(page);
    await remove.getByRole('combobox', { name: 'Move its Songs to' }).selectOption('Final');
    await remove.getByRole('button', { name: 'Move Songs and delete' }).click();
    await expect(remove).toBeHidden();
    await expect(
      page.getByRole('status').filter({ hasText: 'its Songs are now in Final' }),
    ).toBeVisible();
    expect(await stateNames(page)).not.toContain('Mixing');
    await expect(stateRow(page, 'Final')).toContainText('1 Song');
    await expectAccessibleInLightAndDark(page);

    // The Song is now "Final". The Songs filter offers a hidden state only while Songs are in it.
    await page.goto(`${FRESH_URL}songs/${song.shortcode}`);
    await expect(stateButton(page, 'Final')).toBeVisible();
    await page.goto(`${FRESH_URL}songs`);
    const filter = page.getByRole('group', { name: 'Workflow state' });
    await expect(filter.getByRole('checkbox', { name: 'Spark' })).toHaveCount(1);
    await expect(filter.getByRole('checkbox', { name: 'Archived' })).toHaveCount(0);
    await expectNoA11yViolations(page);

    const current = (await (
      await page.request.get(`${FRESH_URL}api/v1/songs/${song.shortcode}`)
    ).json()) as { id: string; revision: number };
    const list = (await (await page.request.get(`${FRESH_URL}api/v1/workflow-states`)).json()) as {
      items: { id: string; name: string }[];
    };
    const archived = list.items.find((state) => state.name === 'Archived');
    const moved = await page.request.patch(`${FRESH_URL}api/v1/songs/${current.id}`, {
      headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(current.revision)}"` },
      data: { stateId: archived?.id },
    });
    expect(moved.status()).toBe(200);
    await page.reload();
    await expect(filter.getByRole('checkbox', { name: 'Archived' })).toHaveCount(1);
  });
});
