import { randomUUID } from 'node:crypto';
import { expect, test, type Locator, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { solidPng } from '../support/images.ts';
import { seedGeneration } from '../support/seeding.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  title: string;
  revision: number;
  currentVersion: { id: string; shortcode: string };
  artwork: { assetId: string; source: string } | null;
}

interface Generation {
  artwork: { assetId: string } | null;
}

/** The API base of the project's container, worked out on the Songs page. */
async function apiBase(page: Page): Promise<(path: string) => string> {
  await page.goto('./songs');
  const base = new URL('.', page.url());
  return (path: string) => new URL(`api/v1/${path}`, base).toString();
}

function clip(title: string): string {
  return JSON.stringify({ id: randomUUID(), status: 'complete', title });
}

/** Waits until the image has been fetched and decoded: the artwork is really served to the page. */
async function expectLoaded(image: Locator): Promise<void> {
  await expect(image).toBeVisible();
  await expect
    .poll(() => image.evaluate((element) => (element as HTMLImageElement).naturalWidth))
    .toBeGreaterThan(0);
}

/** Opens Version 1's Generations in the Versions table unless they are open already. */
async function openVersionOne(page: Page): Promise<void> {
  const chevron = page
    .getByRole('region', { name: 'Versions and Generations' })
    .getByRole('button', { name: 'Generations of Version 1' });
  if ((await chevron.getAttribute('aria-expanded')) !== 'true') {
    await chevron.click();
  }
}

/** Opens the Song page's Details panel unless it is open already (its state is remembered); its artwork group. */
async function openDetails(page: Page): Promise<Locator> {
  const details = page.getByRole('button', { name: 'Details', exact: true });
  await expect(details).toBeVisible();
  if ((await details.getAttribute('aria-expanded')) !== 'true') {
    await details.click();
  }
  const picker = page.getByRole('group', { name: 'Artwork' });
  await expect(picker).toBeVisible();
  return picker;
}

/** Selects `generation` for the Song in its panel, and closes the panel. */
async function select(page: Page, song: Song, generation: string): Promise<void> {
  await page.goto(`./songs/${song.shortcode}/generations/${generation}`);
  const panel = page.getByRole('dialog', { name: `Generation ${generation}` });
  await expect(panel).toBeVisible();
  await panel.getByRole('button', { name: 'Select for the Song' }).click();
  await expect(
    page
      .getByTestId('song-selected-generation')
      .getByRole('link', { name: `Selected Generation ${generation}` }),
  ).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(panel).toBeHidden();
}

/**
 * Walks #121's Demo on the built image, on a Song of its own with three Generations seeded by the
 * test-only command:
 * 1. An image is uploaded for g1 the way the extension sends one (a multipart PUT, as `curl` would),
 *    and g1 is selected: the Song shows that image.
 * 2. In the artwork control, g2's image is chosen: the Song shows it, as its own.
 * 3. Another Generation is selected: the Song's artwork does not change.
 * 4. The Song's own artwork is removed: it shows the Selected Generation's image again.
 * Each state is scanned with the accessibility helper. Runs on the project's shared container,
 * with image bytes stamped with this run.
 */
test.describe('a Generation’s cover image as the Song’s artwork', () => {
  test('defaults to the Selected Generation’s, is picked from another, kept, and removed', async ({
    page,
  }, testInfo) => {
    const stamp = `${testInfo.project.name} ${String(Date.now())} ${String(testInfo.retry)}`;
    const api = await apiBase(page);
    const created = await page.request.post(api('songs'), {
      headers: ANTIFORGERY_HEADERS,
      data: { title: `Covers ${String(Date.now()).slice(-7)}` },
    });
    expect(created.status()).toBe(201);
    const song = (await created.json()) as Song;
    const version = song.currentVersion.shortcode;
    const first = await seedGeneration(testInfo, version, clip('First take'));
    const second = await seedGeneration(testInfo, version, clip('Second take'));
    const third = await seedGeneration(testInfo, version, clip('Third take'));

    const upload = async (generation: string, rgb: [number, number, number]) => {
      const response = await page.request.put(api(`generations/${generation}/artwork`), {
        headers: ANTIFORGERY_HEADERS,
        multipart: {
          file: {
            name: 'cover.png',
            mimeType: 'image/png',
            buffer: solidPng(400, 400, `${stamp} ${generation}`, rgb),
          },
        },
      });
      expect(response.status()).toBe(200);
      const body = (await response.json()) as Generation;
      expect(body.artwork).not.toBeNull();
      return body.artwork?.assetId ?? '';
    };
    const readSong = async () =>
      (await (await page.request.get(api(`songs/${song.id}`))).json()) as Song;
    const header = page.getByTestId('song-header-artwork');
    const alt = `Artwork for ${song.title}`;

    // 1. Upload an image for g1 and select g1: the Song shows it.
    const red = await upload(first, [220, 30, 30]);
    await select(page, song, first);
    await expectLoaded(header.locator(`img[data-artwork="${red}"]`));
    expect((await readSong()).artwork).toMatchObject({
      assetId: red,
      source: 'selectedGeneration',
    });
    const picker = await openDetails(page);
    await expect(picker.getByTestId('artwork-inherited')).toContainText(first);
    await expect(picker.getByRole('button', { name: 'Remove artwork' })).toHaveCount(0);
    await expectAccessibleInLightAndDark(page);

    // The Generation's row shows its image (its Version is open already when the panel was).
    await openVersionOne(page);
    await expectLoaded(
      page
        .locator(`tr[data-generation="${first}"]`)
        .getByRole('img', { name: `Artwork for ${first}` }),
    );

    // 2. Choose g2's image in the artwork control: the Song shows it, as its own.
    const blue = await upload(second, [30, 60, 220]);
    await page.reload();
    await expect(page.getByRole('heading', { level: 2, name: song.title })).toBeVisible();
    await openDetails(page);
    await picker.getByRole('button', { name: 'Choose from Generations' }).click();
    const chooser = page.getByRole('dialog', { name: 'Choose a Generation’s image' });
    await expect(chooser.getByRole('button', { name: `Use the image of ${second}` })).toBeVisible();
    await expectModalAccessibleInBothSchemes(page);
    await chooser.getByRole('button', { name: `Use the image of ${second}` }).click();
    await expect(picker.getByTestId('artwork-status')).toHaveText(
      `Artwork set from Generation ${second}.`,
    );
    await expectLoaded(header.locator(`img[data-artwork="${blue}"]`));
    await expect(picker.getByTestId('artwork-inherited')).toHaveCount(0);
    expect((await readSong()).artwork).toMatchObject({ assetId: blue, source: 'own' });
    await expectAccessibleInLightAndDark(page);

    // 3. Select another Generation (one with no image): the Song's artwork does not change.
    await select(page, song, third);
    await expectLoaded(header.locator(`img[data-artwork="${blue}"]`));
    expect((await readSong()).artwork).toMatchObject({ assetId: blue, source: 'own' });

    // 4. Select g1 again and remove the Song's own artwork: it shows the Selected Generation's image.
    await select(page, song, first);
    await expectLoaded(header.locator(`img[data-artwork="${blue}"]`));
    await openDetails(page);
    await picker.getByRole('button', { name: 'Remove artwork' }).click();
    const confirm = page.getByRole('dialog', { name: 'Remove the artwork?' });
    await expect(confirm.getByTestId('remove-artwork-summary')).toContainText(
      'Selected Generation’s image',
    );
    await expectModalAccessibleInBothSchemes(page);
    await confirm.getByRole('button', { name: 'Remove' }).click();
    await expect(picker.getByTestId('artwork-status')).toHaveText('Artwork removed.');
    await expectLoaded(header.getByRole('img', { name: alt }));
    await expect(header.locator(`img[data-artwork="${red}"]`)).toBeVisible();
    await expect(picker.getByTestId('artwork-inherited')).toContainText(first);
    expect((await readSong()).artwork).toMatchObject({
      assetId: red,
      source: 'selectedGeneration',
    });
    await expectAccessibleInLightAndDark(page);
  });
});
