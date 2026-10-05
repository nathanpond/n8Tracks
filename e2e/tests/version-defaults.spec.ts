import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark } from '../support/a11y.ts';
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

interface Song {
  shortcode: string;
  currentVersion: { id: string };
}

interface VersionDetail {
  inputs: Record<string, unknown>;
}

function defaults(page: Page) {
  return page.getByRole('region', { name: 'Defaults for new Songs' });
}

function part(page: Page, name: string) {
  return defaults(page).getByRole('region', { name });
}

async function createSong(page: Page, title: string): Promise<Song> {
  const created = await page.request.post(`${FRESH_URL}api/v1/songs`, {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(created.status()).toBe(201);
  return (await created.json()) as Song;
}

async function inputsOf(page: Page, song: Song): Promise<Record<string, unknown>> {
  const read = await page.request.get(`${FRESH_URL}api/v1/versions/${song.currentVersion.id}`);
  expect(read.status()).toBe(200);
  return ((await read.json()) as VersionDetail).inputs;
}

/** Moves a slider with the keyboard until it reads `target`, one arrow press at a time. */
async function slideTo(page: Page, slider: Locator, target: number) {
  await slider.focus();
  const now = Number(await slider.getAttribute('aria-valuenow'));
  const key = target > now ? 'ArrowRight' : 'ArrowLeft';
  for (let i = 0; i < Math.abs(target - now); i++) {
    await page.keyboard.press(key);
  }
  await expect(slider).toHaveAttribute('aria-valuenow', String(target));
}

/** Opens More Options unless it is open already. */
async function openMoreOptions(page: Page) {
  const more = page.getByRole('button', { name: 'More Options' });
  if ((await more.getAttribute('aria-expanded')) !== 'true') {
    await more.click();
  }
  await expect(more).toHaveAttribute('aria-expanded', 'true');
}

/**
 * Walks #115's Demo on a container of its own: the defaults and the model list are the whole
 * instance's, so changing them on a shared container would change what every other test creates.
 * Set up through the API with the test administrator, started afresh for each attempt and removed
 * afterwards.
 */
test.describe('defaults for new Songs', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-defaults-'));
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

  test('start every new Song the way they are set, and a retired model default is flagged and skipped', async ({
    page,
  }) => {
    // Suno offers v7: it is on the model list (last).
    const added = await page.request.post(`${FRESH_URL}api/v1/suno/models`, {
      headers: { ...ANTIFORGERY_HEADERS, 'If-Match': '"1"' },
      data: { name: 'v7' },
    });
    expect(added.status()).toBe(201);

    // 1. In Settings → Suno, set defaults: model v7, Variety High, Weirdness 30, Speech background music off.
    await page.goto(`${FRESH_URL}settings/suno`);
    const song = part(page, 'Song');
    const speech = part(page, 'Speech');
    await expect(song.getByRole('combobox', { name: 'Model version' })).toHaveValue('v6');
    await expect(song.getByText("Suno's default").first()).toBeVisible();
    await expect(song.getByRole('textbox')).toHaveCount(0);
    await expectAccessibleInLightAndDark(page);

    await song.getByRole('combobox', { name: 'Model version' }).selectOption('v7');
    await song.getByRole('combobox', { name: 'Variety' }).selectOption('high');
    await slideTo(page, song.getByRole('slider', { name: 'Weirdness' }), 30);
    await speech.getByRole('switch', { name: 'Background music' }).click();
    await expect(speech.getByRole('switch', { name: 'Background music' })).not.toBeChecked();
    await defaults(page).getByRole('button', { name: 'Save defaults' }).click();
    await expect(
      defaults(page).getByRole('status').filter({ hasText: 'Defaults saved' }),
    ).toBeVisible();
    await expect(song.getByText('Your default')).toHaveCount(3);
    await expectAccessibleInLightAndDark(page);

    // 2. Create a Song: its Version shows v7, Variety High, Weirdness 30. As Speech, background music is off.
    const first = await createSong(page, 'Morning Static');
    await page.goto(`${FRESH_URL}songs/${first.shortcode}/v/1`);
    await expect(page.getByRole('combobox', { name: 'Model version' })).toHaveValue('v7');
    await openMoreOptions(page);
    await expect(page.getByRole('slider', { name: 'Variety' })).toHaveAttribute(
      'aria-valuetext',
      'High',
    );
    await expect(page.getByRole('slider', { name: 'Weirdness' })).toHaveAttribute(
      'aria-valuenow',
      '30',
    );
    await expectAccessibleInLightAndDark(page);
    const kind = page.getByRole('radiogroup', { name: 'Kind' });
    await kind
      .locator('label')
      .filter({ hasText: /^Speech$/ })
      .click();
    await expect(kind.getByRole('radio', { name: 'Speech', exact: true })).toBeChecked();
    await expect(page.getByRole('switch', { name: 'Background music' })).not.toBeChecked();
    await expect(page.getByTestId('autosave').getByRole('status')).toHaveText('Saved');
    await expectAccessibleInLightAndDark(page);

    // 3. Retire v7: the settings page flags the default, and the next new Song uses the first model in the list.
    await page.goto(`${FRESH_URL}settings/suno`);
    await expect(part(page, 'Song').getByRole('combobox', { name: 'Model version' })).toHaveValue(
      'v7',
    );
    await expect(page.getByTestId('ignored-model')).toHaveCount(0);
    await page.getByRole('button', { name: 'Retire v7' }).click();
    await expect(page.getByTestId('ignored-model')).toContainText('v7 is retired');
    await expect(part(page, 'Song').getByRole('option', { name: 'v7 (retired)' })).toBeAttached();
    await expectAccessibleInLightAndDark(page);

    const next = await createSong(page, 'Evening Static');
    const inputs = await inputsOf(page, next);
    expect(inputs).toMatchObject({
      model: 'v6',
      variety: 'high',
      weirdness: 30,
      speechBackgroundMusic: false,
    });

    // The first Song's Version is unchanged by any of it.
    expect(await inputsOf(page, first)).toMatchObject({ model: 'v7', kind: 'speech' });
  });
});
