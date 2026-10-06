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

interface Artist {
  id: string;
  name: string;
}

interface Song {
  id: string;
  shortcode: string;
  title: string;
  revision: number;
  credits: { primary: Artist | null; featured: Artist[] };
}

/** The app's base (the root or the sub-path), as the Songs page resolves it. */
async function appBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

async function createArtist(page: Page, base: URL, name: string): Promise<Artist> {
  const created = await page.request.post(new URL('api/v1/artists', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { name, confirmDuplicate: true },
  });
  expect(created.status()).toBe(201);
  return (await created.json()) as Artist;
}

/** Creates a Song with no primary Artist, then credits it as given. */
async function createSong(
  page: Page,
  base: URL,
  title: string,
  primary: Artist | null,
  featured: Artist[],
): Promise<Song> {
  const created = await page.request.post(new URL('api/v1/songs', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { title, primaryArtistId: null },
  });
  expect(created.status()).toBe(201);
  const song = (await created.json()) as Song;
  const credited = await page.request.put(
    new URL(`api/v1/songs/${song.shortcode}/credits`, base).toString(),
    {
      headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(song.revision)}"` },
      data: {
        primaryArtistId: primary?.id ?? null,
        featuredArtistIds: featured.map((artist) => artist.id),
      },
    },
  );
  expect(credited.status()).toBe(200);
  return (await credited.json()) as Song;
}

async function readSong(page: Page, base: URL, shortcode: string): Promise<Song> {
  const read = await page.request.get(new URL(`api/v1/songs/${shortcode}`, base).toString());
  expect(read.status()).toBe(200);
  return (await read.json()) as Song;
}

/** Creates an Album whose Album Artist is `artist`; its ID. */
async function createAlbum(page: Page, base: URL, title: string, artist: Artist): Promise<string> {
  const created = await page.request.post(new URL('api/v1/albums', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(created.status()).toBe(201);
  const id = ((await created.json()) as { id: string }).id;
  const patched = await page.request.patch(new URL(`api/v1/albums/${id}`, base).toString(), {
    headers: { ...ANTIFORGERY_HEADERS, 'If-Match': '"1"' },
    data: { albumArtistId: artist.id },
  });
  expect(patched.status()).toBe(200);
  return id;
}

/** Opens the Song's Details panel, unless it is open already (its state is remembered). */
async function openDetails(page: Page): Promise<void> {
  const control = page.getByRole('button', { name: 'Details', exact: true });
  if ((await control.getAttribute('aria-expanded')) !== 'true') {
    await control.click();
  }
}

/** Opens the Artist's page and its delete confirmation; the dialog. */
async function openDeleteDialog(page: Page, url: string, artist: Artist) {
  await page.goto(url);
  await expect(page.getByRole('heading', { level: 2, name: artist.name })).toBeVisible();
  await page.getByRole('button', { name: 'Delete Artist' }).click();
  const dialog = page.getByRole('dialog', { name: `Delete “${artist.name}”?` });
  await expect(dialog.getByTestId('delete-artist-summary')).toContainText('is permanent');
  await expect(dialog.getByLabel('Checking the Artist')).toHaveCount(0);
  return dialog;
}

/**
 * Walks the story's Demo steps 1 and 3 on the project's shared container, with names stamped with
 * this run: an Artist credited on two Songs and as an Album's Album Artist, reached from that
 * Album (#103's Demo step 2), is deleted with its credits reassigned to another Artist, after which
 * both Songs and the Album credit that Artist; and an Artist nothing credits is deleted with a
 * plain confirmation. Each state is scanned with axe in both colour schemes.
 */
test.describe('Deleting Artists', () => {
  test('deletes a credited Artist, reassigning its credits to another', async ({ page }) => {
    const stamp = String(Date.now()).slice(-7);
    const base = await appBase(page);
    const retiring = await createArtist(page, base, `Retiring ${stamp}`);
    const heir = await createArtist(page, base, `Heir ${stamp}`);
    const led = await createSong(page, base, `Led by them ${stamp}`, retiring, []);
    const featuring = await createSong(page, base, `Featuring them ${stamp}`, null, [retiring]);
    const albumTitle = `Their EP ${stamp}`;
    const albumId = await createAlbum(page, base, albumTitle, retiring);

    // From the Album, its Album Artist's page.
    await page.goto(`./albums/${albumId}`);
    const albumArtist = page.getByTestId('album-artist');
    await expect(albumArtist).toContainText(retiring.name);
    await albumArtist.getByRole('link', { name: retiring.name }).click();

    const dialog = await openDeleteDialog(page, page.url(), retiring);
    await expect(dialog.getByTestId('delete-artist-credits')).toHaveText(
      'It is credited on 2 Songs and 1 Album.',
    );
    await expect(dialog.getByRole('button', { name: 'Delete Artist' })).toBeDisabled();
    await dialog.getByRole('radio', { name: 'Reassign them to another Artist' }).check();
    await dialog.getByRole('textbox', { name: 'Reassign to' }).fill(heir.name);
    await dialog.getByRole('option', { name: heir.name, exact: true }).click();
    await expect(dialog.getByTestId('delete-artist-target')).toHaveText(
      `The credits go to “${heir.name}”.`,
    );
    await expectModalAccessibleInBothSchemes(page);

    await dialog.getByRole('button', { name: 'Delete Artist' }).click();
    await expect(page.getByRole('heading', { level: 2, name: 'Artists' })).toBeVisible();
    await expect(page.getByTestId('collection-deleted-notice')).toHaveText(
      `Deleted the Artist “${retiring.name}”. Its credits went to “${heir.name}”. No Song or Album was deleted.`,
    );
    await expectAccessibleInLightAndDark(page);

    // Both Songs now credit the other Artist, in the same roles, at their next revisions.
    const ledAfter = await readSong(page, base, led.shortcode);
    expect(ledAfter.credits.primary?.id).toBe(heir.id);
    expect(ledAfter.revision).toBe(led.revision + 1);
    const featuringAfter = await readSong(page, base, featuring.shortcode);
    expect(featuringAfter.credits.featured.map((artist) => artist.id)).toEqual([heir.id]);
    await page.goto(`./songs/${led.shortcode}`);
    await expect(page.getByRole('heading', { level: 2, name: led.title })).toBeVisible();
    await openDetails(page);
    await expect(page.getByTestId('primary-artist')).toContainText(heir.name);
    await expectAccessibleInLightAndDark(page);

    // So does the Album.
    await page.goto(`./albums/${albumId}`);
    await expect(page.getByTestId('album-artist')).toContainText(heir.name);
    await expectAccessibleInLightAndDark(page);
    const gone = await page.request.get(new URL(`api/v1/artists/${retiring.id}`, base).toString());
    expect(gone.status()).toBe(404);
  });

  test('deletes an Artist nothing credits with a plain confirmation', async ({ page }) => {
    const stamp = String(Date.now()).slice(-7);
    const base = await appBase(page);
    const unused = await createArtist(page, base, `Unused ${stamp}`);

    const dialog = await openDeleteDialog(page, `./artists/${unused.id}`, unused);
    await expect(dialog.getByTestId('delete-artist-credits')).toHaveText(
      'No Song or Album credits it, so nothing else changes.',
    );
    await expect(dialog.getByRole('radio')).toHaveCount(0);
    await expectModalAccessibleInBothSchemes(page);

    await dialog.getByRole('button', { name: 'Delete Artist' }).click();
    await expect(page.getByTestId('collection-deleted-notice')).toHaveText(
      `Deleted the Artist “${unused.name}”. No Song or Album was deleted.`,
    );
    await expect(page.getByRole('link', { name: unused.name })).toHaveCount(0);
    await expectAccessibleInLightAndDark(page);
  });
});

const fresh: Target = {
  name: FRESH_NAME,
  port: FRESH_PORT,
  url: FRESH_URL,
  media: true,
  expectedStatus: 'healthy',
};

/**
 * Walks Demo step 2 on a container of its own: the default Artist is the whole instance's, so
 * setting it on a shared container would credit every Song the other tests create meanwhile.
 */
test.describe('deleting the default Artist', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-artist-delete-'));
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

  test('says the default for new Songs will be cleared, and clears it', async ({ page }) => {
    const base = new URL(FRESH_URL);
    const usual = await createArtist(page, base, 'The Usual');
    await createArtist(page, base, 'Someone Else');
    const settings = await page.request.put(`${FRESH_URL}api/v1/settings/catalog`, {
      headers: { ...ANTIFORGERY_HEADERS, 'If-Match': '"1"' },
      data: { defaultArtistId: usual.id },
    });
    expect(settings.status()).toBe(200);

    const dialog = await openDeleteDialog(page, `${FRESH_URL}artists/${usual.id}`, usual);
    await expect(dialog.getByTestId('delete-artist-default')).toContainText(
      'It is the default Artist for new Songs. The default will be cleared',
    );
    await expectModalAccessibleInBothSchemes(page);
    await dialog.getByRole('button', { name: 'Delete Artist' }).click();
    await expect(page.getByTestId('collection-deleted-notice')).toContainText(
      'Deleted the Artist “The Usual”.',
    );

    await page.goto(`${FRESH_URL}settings/catalog`);
    await expect(page.getByTestId('default-artist')).toHaveText('No default Artist.');
    await expectAccessibleInLightAndDark(page);
  });
});
