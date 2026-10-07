import { copyFile, mkdir, mkdtemp, rename, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
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
import { seedGenerationIn } from '../support/seeding.ts';
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

/** The Suno ID of the Generation the matched file came from. */
const SUNO_ID = '0c90d621-e30c-4c76-814a-e1fdeb500582';
const MATCHED = `Night Drive (suno-${SUNO_ID}).wav`;
const SONG_LEVEL = 'Night Drive demo.mp3';

/** The API tests' audio fixtures: 1.5 s tones made with ffmpeg. */
function fixture(format: string): string {
  return fileURLToPath(
    new URL(`../../tests/n8Tracks.Api.Tests/Media/Fixtures/tone.${format}`, import.meta.url),
  );
}

interface Status {
  activeScanJobId: string | null;
  lastScan: { jobId: string } | null;
}

async function status(page: Page): Promise<Status> {
  const response = await page.request.get(`${FRESH_URL}api/v1/media/status`);
  expect(response.status()).toBe(200);
  return (await response.json()) as Status;
}

/** Scans the library through the API and waits until that scan is the last one. */
async function scan(page: Page): Promise<void> {
  const response = await page.request.post(`${FRESH_URL}api/v1/media/scans`, {
    headers: ANTIFORGERY_HEADERS,
  });
  expect([200, 202]).toContain(response.status());
  const { jobId } = (await response.json()) as { jobId: string };
  await expect
    .poll(
      async () => {
        const current = await status(page);
        return current.lastScan?.jobId === jobId && current.activeScanJobId === null;
      },
      { timeout: 60_000, intervals: [1_000] },
    )
    .toBe(true);
}

/** Associates the unmatched file at `path` with the Song `song` alone, through #210's API. */
async function associateWithSong(page: Page, path: string, song: string): Promise<void> {
  const listed = await page.request.get(`${FRESH_URL}api/v1/audio-files?association=none`);
  expect(listed.status()).toBe(200);
  const { items } = (await listed.json()) as {
    items: { id: string; path: string; revision: number }[];
  };
  const file = items.find((item) => item.path === path);
  expect(file).toBeDefined();
  const response = await page.request.put(
    `${FRESH_URL}api/v1/audio-files/${file?.id ?? ''}/association`,
    {
      headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(file?.revision ?? 0)}"` },
      data: { song },
    },
  );
  expect(response.status()).toBe(200);
}

function fileRow(page: Page, path: string) {
  return page.locator(`[data-testid="song-audio-file"][data-file="${path}"]`);
}

/**
 * Walks #211's Demo on a container of its own, whose media folder (mounted read-only) the test copies
 * two files into on the host: a file named for a Generation's Suno ID, which the scan matches, and one
 * the user associates with the Song alone. The Song page's Audio Files section lists both with how
 * each was associated; the Generation's row in the Versions table says "1 file · WAV", its panel lists
 * the file, and the Songs table counts both. Renaming the matched file on disk and scanning marks it
 * Missing, still listed, and the row says so.
 */
test.describe('A Song’s audio files', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-song-files-'));
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

  test('lists the Song’s files with their origins, counts each Generation’s, and marks a renamed one Missing', async ({
    page,
  }) => {
    test.setTimeout(300_000);
    const media = join(work ?? '', 'media');

    // The startup scan of the empty folder ends first.
    await expect
      .poll(
        async () => {
          const current = await status(page);
          return current.lastScan !== null && current.activeScanJobId === null;
        },
        { timeout: 120_000, intervals: [1_000] },
      )
      .toBe(true);

    // A Song with a Generation; one file named for its Suno ID, and one the user associates.
    const created = await page.request.post(`${FRESH_URL}api/v1/songs`, {
      headers: ANTIFORGERY_HEADERS,
      data: { title: 'Night Drive' },
    });
    expect(created.status()).toBe(201);
    const song = (await created.json()) as {
      shortcode: string;
      currentVersion: { shortcode: string };
    };
    const generation = await seedGenerationIn(
      FRESH_NAME,
      song.currentVersion.shortcode,
      JSON.stringify({ id: SUNO_ID, status: 'complete', title: 'Night Drive' }),
    );
    await copyFile(fixture('wav'), join(media, MATCHED));
    await copyFile(fixture('mp3'), join(media, SONG_LEVEL));
    await scan(page);
    await associateWithSong(page, SONG_LEVEL, song.shortcode);

    // 1. Both files are listed on the Song page, Song-level first, each with how it was associated.
    await page.goto(`${FRESH_URL}songs/${song.shortcode}`);
    const section = page.getByRole('region', { name: 'Audio Files' });
    const rows = section.getByTestId('song-audio-file');
    await expect(rows).toHaveCount(2);
    await expect(rows.nth(0)).toHaveAttribute('data-file', SONG_LEVEL);
    await expect(fileRow(page, SONG_LEVEL).getByTestId('audio-file-generation')).toHaveText(
      'Song-level',
    );
    await expect(fileRow(page, SONG_LEVEL).getByTestId('audio-file-origin')).toHaveText('By you');
    await expect(fileRow(page, MATCHED).getByTestId('audio-file-generation')).toHaveText(
      generation,
    );
    await expect(fileRow(page, MATCHED).getByTestId('audio-file-origin')).toHaveText('By Suno ID');
    await expect(fileRow(page, MATCHED).getByTestId('file-status')).toHaveText('Available');
    await expectAccessibleInLightAndDark(page);

    // 2. Expand the Version: the Generation's row says how many local files it has, and in what format.
    await page.getByRole('button', { name: 'Generations of Version 1' }).click();
    const generationRow = page.locator(`tr[data-generation="${generation}"]`);
    await expect(generationRow.getByTestId('generation-local-files')).toHaveText('1 file · WAV');
    await expectAccessibleInLightAndDark(page);

    // The shortcode opens the Generation panel, which lists its own file.
    await fileRow(page, MATCHED).getByRole('link', { name: generation }).click();
    const panel = page.getByRole('dialog', { name: `Generation ${generation}` });
    await expect(panel).toBeVisible();
    const panelFiles = panel.getByTestId('generation-audio-file');
    await expect(panelFiles).toHaveCount(1);
    await expect(panelFiles.first()).toHaveAttribute('data-file', MATCHED);
    await expectModalAccessibleInBothSchemes(page);
    await panel.getByRole('button', { name: 'Close' }).click();
    await expect(panel).toBeHidden();

    // The Songs table counts both files.
    await page.goto(`${FRESH_URL}songs`);
    await expect(
      page.locator(`tr[data-song="${song.shortcode}"]`).getByTestId('song-audio-file-count'),
    ).toHaveText('2');

    // 3. Rename the matched file on disk and scan: it is listed as Missing, never hidden.
    await rename(join(media, MATCHED), join(media, 'Night Drive renamed.wav'));
    await scan(page);
    await page.goto(`${FRESH_URL}songs/${song.shortcode}`);
    await expect(rows).toHaveCount(2);
    await expect(fileRow(page, MATCHED).getByTestId('file-status')).toHaveText('Missing');
    await page.getByRole('button', { name: 'Generations of Version 1' }).click();
    await expect(generationRow.getByTestId('generation-local-files')).toHaveText(
      '1 file · WAV · 1 missing',
    );
    await expectAccessibleInLightAndDark(page);
  });
});
