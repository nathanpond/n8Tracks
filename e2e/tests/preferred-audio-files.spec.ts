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

/** The Suno ID of the Generation both files came from. */
const SUNO_ID = '6f1e2d3c-4b5a-4987-a6b5-c4d3e2f1a0b9';
const WAV = `Night Drive (suno-${SUNO_ID}).wav`;
const MP3 = `Night Drive (suno-${SUNO_ID}).mp3`;

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

/** What plays for the Generation, as the playback read answers it. */
async function playback(page: Page, generation: string): Promise<{ file: string; reason: string }> {
  const response = await page.request.get(`${FRESH_URL}api/v1/generations/${generation}/playback`);
  expect(response.status()).toBe(200);
  const answer = (await response.json()) as {
    audioFile: { fileName: string } | null;
    reason: string;
  };
  return { file: answer.audioFile?.fileName ?? '', reason: answer.reason };
}

function fileRow(page: Page, path: string) {
  return page.locator(`[data-testid="song-audio-file"][data-file="${path}"]`);
}

/**
 * Walks #212's Demo on a container of its own, whose media folder (mounted read-only) the test copies
 * a WAV and an MP3 named for one Generation's Suno ID into: with no choice the WAV plays now; choosing
 * the MP3 on the Song page makes it preferred and what plays, in the Audio Files section and in the
 * Generation panel. Moving the MP3 out of the folder and scanning plays the WAV again, the MP3 still
 * preferred and Missing; moving it back and scanning plays it again with no action.
 */
test.describe('Preferred audio files', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-preferred-'));
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

  test('plays the WAV until the MP3 is chosen, falls back while it is away, and plays it again once back', async ({
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
    await copyFile(fixture('wav'), join(media, WAV));
    await copyFile(fixture('mp3'), join(media, MP3));
    await scan(page);

    // 1. With no choice, the WAV plays now.
    await page.goto(`${FRESH_URL}songs/${song.shortcode}`);
    const section = page.getByRole('region', { name: 'Audio Files' });
    await expect(section.getByTestId('song-audio-file')).toHaveCount(2);
    await expect(fileRow(page, WAV)).toHaveAttribute('data-plays-now', 'true');
    await expect(fileRow(page, WAV).getByTestId('audio-file-plays-now')).toHaveText('Plays now');
    await expect(fileRow(page, MP3)).toHaveAttribute('data-plays-now', 'false');
    await expect(fileRow(page, MP3)).toHaveAttribute('data-preferred', 'false');
    expect(await playback(page, generation)).toEqual({ file: WAV, reason: 'format_order' });
    await expectAccessibleInLightAndDark(page);

    // 2. Choose the MP3: it is marked preferred and plays now.
    await fileRow(page, MP3)
      .getByRole('button', { name: `Make preferred for Generation ${generation}: ${MP3}` })
      .click();
    await expect(section.getByTestId('audio-files-announcement')).toHaveText(
      `${MP3} is now the preferred file of Generation ${generation}.`,
    );
    await expect(fileRow(page, MP3)).toHaveAttribute('data-preferred', 'true');
    await expect(fileRow(page, MP3)).toHaveAttribute('data-plays-now', 'true');
    await expect(fileRow(page, MP3).getByTestId('audio-file-preferred')).toHaveText('Preferred');
    await expect(fileRow(page, WAV)).toHaveAttribute('data-plays-now', 'false');
    expect(await playback(page, generation)).toEqual({ file: MP3, reason: 'generation_preferred' });
    await expectAccessibleInLightAndDark(page);

    // The Generation panel marks it the same way.
    await fileRow(page, MP3).getByRole('link', { name: generation }).click();
    const panel = page.getByRole('dialog', { name: `Generation ${generation}` });
    await expect(panel).toBeVisible();
    const panelMp3 = panel.locator(`[data-testid="generation-audio-file"][data-file="${MP3}"]`);
    await expect(panelMp3).toHaveAttribute('data-preferred', 'true');
    await expect(panelMp3).toHaveAttribute('data-plays-now', 'true');
    await expectModalAccessibleInBothSchemes(page);
    await panel.getByRole('button', { name: 'Close' }).click();
    await expect(panel).toBeHidden();

    // 3. The MP3 moved out of the folder and scanned: the WAV plays, the MP3 is still preferred, Missing.
    await rename(join(media, MP3), join(work ?? '', 'away.mp3'));
    await scan(page);
    await page.reload();
    await expect(fileRow(page, MP3).getByTestId('file-status')).toHaveText('Missing');
    await expect(fileRow(page, MP3)).toHaveAttribute('data-preferred', 'true');
    await expect(fileRow(page, WAV)).toHaveAttribute('data-plays-now', 'true');
    await expect(fileRow(page, MP3).getByTestId('audio-file-preference-note')).toHaveText(
      `Preferred, but Missing: ${WAV} plays instead.`,
    );
    expect(await playback(page, generation)).toEqual({
      file: WAV,
      reason: 'preferred_missing_fallback',
    });

    // 4. Moved back and scanned: the MP3 plays again, with no action.
    await rename(join(work ?? '', 'away.mp3'), join(media, MP3));
    await scan(page);
    await page.reload();
    await expect(fileRow(page, MP3)).toHaveAttribute('data-plays-now', 'true');
    await expect(fileRow(page, MP3).getByTestId('file-status')).toHaveText('Available');
    expect(await playback(page, generation)).toEqual({ file: MP3, reason: 'generation_preferred' });
  });
});
