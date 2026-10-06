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

interface Artist {
  id: string;
  name: string;
}

interface Song {
  shortcode: string;
  credits: { primary: Artist | null; featured: Artist[] };
}

async function createArtist(page: Page, name: string): Promise<Artist> {
  const created = await page.request.post(`${FRESH_URL}api/v1/artists`, {
    headers: ANTIFORGERY_HEADERS,
    data: { name },
  });
  expect(created.status()).toBe(201);
  return (await created.json()) as Artist;
}

async function readSong(page: Page, shortcode: string): Promise<Song> {
  const read = await page.request.get(`${FRESH_URL}api/v1/songs/${shortcode}`);
  expect(read.status()).toBe(200);
  return (await read.json()) as Song;
}

/** Types into an Artist picker and chooses the suggestion named `name`. */
async function pick(page: Page, label: string, typed: string, name: string) {
  await page.getByRole('textbox', { name: label }).fill(typed);
  await page.getByRole('option', { name, exact: true }).click();
}

function featured(page: Page) {
  return page.getByTestId('song-credits').locator('[data-featured-artist]');
}

/**
 * Walks #88's Demo on a container of its own: the default Artist is the whole instance's, so
 * setting it on a shared container would credit every Song the other tests create. Set up through
 * the API with the test administrator, started afresh for each attempt and removed afterwards.
 */
test.describe('credits', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-credits-'));
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

  test('credit each Song properly, with the usual Artist filled in', async ({ page }) => {
    const n8 = await createArtist(page, 'n8');
    const guest = await createArtist(page, 'Guest Singer');
    await createArtist(page, 'Choir');
    const before = await page.request.post(`${FRESH_URL}api/v1/songs`, {
      headers: ANTIFORGERY_HEADERS,
      data: { title: 'Before the default' },
    });
    expect(before.status()).toBe(201);

    // 1. In Settings → Catalog, choose "n8" as default Artist.
    await page.goto(`${FRESH_URL}settings/catalog`);
    await expect(page.getByRole('heading', { level: 2, name: 'Catalog' })).toBeVisible();
    await expect(page.getByTestId('default-artist')).toHaveText('No default Artist.');
    await expectAccessibleInLightAndDark(page);
    await pick(page, 'Choose a default Artist', 'n8', 'n8');
    await expect(page.getByTestId('default-artist')).toContainText('n8');
    await expect(
      page.getByRole('status').filter({ hasText: 'New Songs are credited to n8.' }),
    ).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // 2. Create a Song; its Details show "n8" as primary Artist.
    await page.goto(`${FRESH_URL}songs`);
    await page.getByRole('button', { name: 'New Song' }).first().click();
    const dialog = page.getByRole('dialog', { name: 'New Song' });
    await expect(dialog.getByTestId('new-song-primary')).toContainText('n8 (the default Artist)');
    await expectModalAccessibleInBothSchemes(page);
    await dialog.getByRole('textbox', { name: 'Title' }).fill('Running in a Pack');
    await dialog.getByRole('button', { name: 'Create Song' }).click();
    await expect(page.getByRole('heading', { level: 2, name: 'Running in a Pack' })).toBeVisible();
    const shortcode = (await page.getByTestId('shortcode').textContent()) ?? '';
    await page.getByRole('button', { name: 'Details' }).click();
    await expect(page.getByTestId('primary-artist')).toContainText('n8');
    await expectAccessibleInLightAndDark(page);

    // 3. Add two featured Artists and reorder them.
    await pick(page, 'Add a featured Artist', 'Guest', 'Guest Singer');
    await expect(featured(page)).toHaveCount(1);
    await pick(page, 'Add a featured Artist', 'Cho', 'Choir');
    await expect(featured(page)).toHaveCount(2);
    await expect(featured(page).first()).toHaveAttribute('data-featured-artist', 'Guest Singer');
    await page.getByRole('button', { name: 'Move Choir up' }).click();
    await expect(featured(page).first()).toHaveAttribute('data-featured-artist', 'Choir');
    await expectNoA11yViolations(page);

    // An Artist created on the spot from the picker, the duplicate name confirmed in place.
    await page.getByRole('textbox', { name: 'Add a featured Artist' }).fill('N8');
    await expect(page.getByRole('option', { name: 'n8', exact: true })).toHaveCount(0);
    await page.getByRole('option', { name: 'Create Artist “N8”' }).click();
    const asked = page.getByTestId('artist-duplicate');
    await expect(asked).toContainText('An Artist already has the name “N8”.');
    await expectNoA11yViolations(page);
    await asked.getByRole('button', { name: 'Cancel' }).click();
    await expect(asked).toHaveCount(0);

    const credited = await readSong(page, shortcode);
    expect(credited.credits.primary?.name).toBe('n8');
    expect(credited.credits.featured.map((artist) => artist.name)).toEqual([
      'Choir',
      'Guest Singer',
    ]);

    // 4. Filter the Songs table by a featured Artist.
    await page.goto(`${FRESH_URL}songs`);
    const filter = page.getByRole('combobox', { name: 'Artist' });
    await filter.click();
    await filter.fill('Guest');
    await page.getByRole('option', { name: 'Guest Singer' }).click();
    await page.keyboard.press('Escape');
    await expect(page).toHaveURL(new RegExp(`artist=${guest.id}`));
    const rows = page.getByRole('table', { name: 'Songs' }).locator('tbody tr');
    await expect(rows).toHaveCount(1);
    await expect(rows.first().getByRole('cell').nth(1)).toHaveText('n8');
    await expectAccessibleInLightAndDark(page);

    // The Artist page lists the Song with each Artist's role.
    await page.goto(`${FRESH_URL}artists/${guest.id}`);
    const songs = page.getByRole('table', { name: 'Songs credited to Guest Singer' });
    await expect(songs.getByRole('row').nth(1)).toContainText('Featured');
    await page.goto(`${FRESH_URL}artists/${n8.id}`);
    await expect(
      page.getByRole('table', { name: 'Songs credited to n8' }).getByRole('row').nth(1),
    ).toContainText('Primary');
    await expectAccessibleInLightAndDark(page);

    // The Song created before the default is unchanged.
    const earlier = (await before.json()) as Song;
    expect((await readSong(page, earlier.shortcode)).credits.primary).toBeNull();
  });
});
