import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
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

function models(page: Page) {
  return page.getByRole('list', { name: 'Suno models' });
}

function modelRow(page: Page, name: string) {
  return models(page).locator(`li[data-model-name="${name}"]`);
}

async function modelNames(page: Page): Promise<string[]> {
  return models(page)
    .locator('li[data-model-name]')
    .evaluateAll((rows) => rows.map((row) => row.getAttribute('data-model-name') ?? ''));
}

function picker(page: Page) {
  return page.getByRole('combobox', { name: 'Model version' });
}

async function pickerOptions(page: Page): Promise<string[]> {
  return picker(page).locator('option').allTextContents();
}

async function createSong(page: Page, title: string): Promise<string> {
  const created = await page.request.post(`${FRESH_URL}api/v1/songs`, {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(created.status()).toBe(201);
  return ((await created.json()) as { shortcode: string }).shortcode;
}

async function openSong(page: Page, shortcode: string): Promise<void> {
  await page.goto(`${FRESH_URL}songs/${shortcode}`);
  await expect(picker(page)).toBeVisible();
}

/**
 * Walks #114's Demo on a container of its own: the model list is shared by the whole instance, so
 * changing it on a shared container would change what every other test sees. Set up through the
 * API with the test administrator, started afresh for each attempt and removed afterwards.
 */
test.describe('Settings → Suno', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-suno-'));
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

  test('adds v7, moves it first, and retires v6-mini; pickers follow, and a Version keeps its retired model', async ({
    page,
  }) => {
    // A Version already set to v6-mini, before the list changes.
    const earlier = await createSong(page, 'Small Hours');
    await openSong(page, earlier);
    await expect(picker(page)).toHaveValue('v6');
    expect(await pickerOptions(page)).toEqual(['Not chosen', 'v6', 'v6-wild', 'v6-mini']);
    await picker(page).selectOption('v6-mini');
    await expect(page.getByTestId('autosave').getByRole('status')).toHaveText('Saved');

    // 1. In Settings → Suno, add "v7", move it to the top, and retire "v6-mini".
    await page.goto(`${FRESH_URL}settings/account`);
    await page.getByRole('link', { name: 'Suno', exact: true }).click();
    await expect(page.getByRole('heading', { level: 2, name: 'Suno' })).toBeVisible();
    await expect(models(page)).toBeVisible();
    expect(await modelNames(page)).toEqual(['v6', 'v6-wild', 'v6-mini']);
    await expect(modelRow(page, 'v6-mini')).toContainText('1 Version');
    await expect(
      modelRow(page, 'v6-mini').getByRole('button', { name: 'Delete v6-mini' }),
    ).toBeDisabled();
    await expectAccessibleInLightAndDark(page);

    await page.getByRole('textbox', { name: /New model/ }).fill('v7');
    await page.getByRole('textbox', { name: /^Note/ }).fill('Newest');
    await page.getByRole('button', { name: 'Add model' }).click();
    await expect(page.getByRole('status').filter({ hasText: 'v7 is added' })).toBeVisible();
    expect(await modelNames(page)).toEqual(['v6', 'v6-wild', 'v6-mini', 'v7']);

    const up = modelRow(page, 'v7').getByRole('button', { name: 'Move v7 up' });
    for (const expected of [
      ['v6', 'v6-wild', 'v7', 'v6-mini'],
      ['v6', 'v7', 'v6-wild', 'v6-mini'],
      ['v7', 'v6', 'v6-wild', 'v6-mini'],
    ]) {
      await up.press('Enter');
      await expect.poll(() => modelNames(page)).toEqual(expected);
    }
    await expect(up).toBeDisabled();
    await expect(modelRow(page, 'v7').getByRole('button', { name: 'Move v7 down' })).toBeFocused();

    await modelRow(page, 'v6-mini').getByRole('button', { name: 'Retire v6-mini' }).click();
    await expect(page.getByRole('status').filter({ hasText: 'v6-mini is retired' })).toBeVisible();
    await expect(modelRow(page, 'v6-mini')).toContainText('Retired');
    await expectAccessibleInLightAndDark(page);

    // The edit dialog keeps a used model's name and takes a note.
    await modelRow(page, 'v6-mini').getByRole('button', { name: 'Edit v6-mini' }).click();
    const edit = page.getByRole('dialog', { name: 'Edit v6-mini' });
    await expect(edit.getByRole('textbox', { name: 'Name' })).toBeDisabled();
    await edit.getByRole('textbox', { name: /^Note/ }).fill('Retired by Suno');
    await expectModalAccessibleInBothSchemes(page);
    await edit.getByRole('button', { name: 'Save' }).click();
    await expect(edit).toBeHidden();
    await expect(modelRow(page, 'v6-mini')).toContainText('Retired by Suno');

    // 2. A new Version's model picker: v7 is first (and chosen), and v6-mini is not offered.
    const later = await createSong(page, 'First Light');
    await openSong(page, later);
    await expect(picker(page)).toHaveValue('v7');
    expect(await pickerOptions(page)).toEqual(['Not chosen', 'v7', 'v6', 'v6-wild']);
    await expectAccessibleInLightAndDark(page);

    // 3. The Version already set to v6-mini still shows it.
    await openSong(page, earlier);
    await expect(picker(page)).toHaveValue('v6-mini');
    expect(await pickerOptions(page)).toEqual([
      'Not chosen',
      'v6-mini (retired)',
      'v7',
      'v6',
      'v6-wild',
    ]);
    await expectAccessibleInLightAndDark(page);
  });
});
