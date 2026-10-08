import { createHash } from 'node:crypto';
import { copyFile, mkdir, mkdtemp, readdir, readFile, rm, stat } from 'node:fs/promises';
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

/** The Suno IDs of the Generation deleted and the one moved. */
const DELETED_ID = '0c90d621-e30c-4c76-814a-e1fdeb500582';
const MOVED_ID = '6f1e2d3c-4b5a-4987-a6b5-c4d3e2f1a0b9';
const DELETED_WAV = `Night Drive (suno-${DELETED_ID}).wav`;
const DELETED_MP3 = `Night Drive (suno-${DELETED_ID}).mp3`;
const MOVED_WAV = `Night Drive alt (suno-${MOVED_ID}).wav`;

/** The API tests' audio fixtures: 1.5 s tones made with ffmpeg. */
function fixture(format: string): string {
  return fileURLToPath(
    new URL(`../../tests/n8Tracks.Api.Tests/Media/Fixtures/tone.${format}`, import.meta.url),
  );
}

/** Every file in `folder` by name, size, modified time, and SHA-256: what "nothing changed" compares. */
async function listing(folder: string): Promise<string[]> {
  const entries = await readdir(folder);
  const lines = await Promise.all(
    entries.map(async (name) => {
      const path = join(folder, name);
      const info = await stat(path);
      const hash = createHash('sha256')
        .update(await readFile(path))
        .digest('hex');
      return `${name} ${String(info.size)} ${String(info.mtimeMs)} ${hash}`;
    }),
  );
  return lines.sort();
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

/**
 * Walks #213's Demo on a container of its own, whose media folder (mounted read-only) the test copies
 * three files into on the host: two named for one Generation's Suno ID and one for another's. Deleting
 * the first Generation names its two local audio files in the confirmation and says they stay on disk;
 * Unmatched Files then lists both with "Its Generation was deleted." Creating a new Song from the
 * second Generation says its file moves too, and the new Song's Audio Files lists it. The files on
 * disk are the same, byte for byte, at the end.
 */
test.describe('Audio files when Generations are deleted or moved', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-file-lifecycle-'));
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

  test('a deleted Generation’s files stay on disk and appear in Unmatched Files; a moved one’s follow it', async ({
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

    // A Song with two Generations, and files named for their Suno IDs.
    const created = await page.request.post(`${FRESH_URL}api/v1/songs`, {
      headers: ANTIFORGERY_HEADERS,
      data: { title: 'Night Drive' },
    });
    expect(created.status()).toBe(201);
    const song = (await created.json()) as {
      shortcode: string;
      currentVersion: { shortcode: string };
    };
    const deleted = await seedGenerationIn(
      FRESH_NAME,
      song.currentVersion.shortcode,
      JSON.stringify({ id: DELETED_ID, status: 'complete', title: 'Night Drive' }),
    );
    const moving = await seedGenerationIn(
      FRESH_NAME,
      song.currentVersion.shortcode,
      JSON.stringify({ id: MOVED_ID, status: 'complete', title: 'Night Drive alt' }),
    );
    await copyFile(fixture('wav'), join(media, DELETED_WAV));
    await copyFile(fixture('mp3'), join(media, DELETED_MP3));
    await copyFile(fixture('wav'), join(media, MOVED_WAV));
    await scan(page);
    const before = await listing(media);

    // 1. Delete the Generation with two files: the confirmation names them and says they stay on disk.
    await page.goto(`${FRESH_URL}songs/${song.shortcode}/generations/${deleted}`);
    const panel = page.getByRole('dialog', { name: `Generation ${deleted}` });
    await expect(panel).toBeVisible();
    await panel.getByRole('button', { name: 'Delete Generation' }).click();
    const dialog = page.getByRole('dialog', { name: `Delete Generation ${deleted}?` });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByTestId('deletion-audio-files')).toHaveText(
      '2 local audio files are associated with it. They stay on disk and will appear in Unmatched Files.',
    );
    await expectModalAccessibleInBothSchemes(page);
    await dialog.getByRole('button', { name: `Delete ${deleted}` }).click();
    await expect(page.getByTestId('generation-deleted')).toBeVisible();

    // 2. Unmatched Files lists both, with the reason, and a scan does not match them again.
    await scan(page);
    await page.goto(`${FRESH_URL}library/unmatched`);
    const rows = page.getByTestId('unmatched-file');
    await expect(rows).toHaveCount(2);
    for (const path of [DELETED_WAV, DELETED_MP3]) {
      await expect(
        page
          .locator(`[data-testid="unmatched-file"][data-file="${path}"]`)
          .getByTestId('unmatched-reason'),
      ).toHaveText('Its Generation was deleted.');
    }
    await expectAccessibleInLightAndDark(page);

    // 3. Create a new Song from the other Generation: the warning says its file moves, and the new
    // Song's Audio Files lists it.
    await page.goto(`${FRESH_URL}songs/${song.shortcode}/generations/${moving}`);
    const movingPanel = page.getByRole('dialog', { name: `Generation ${moving}` });
    await expect(movingPanel).toBeVisible();
    await movingPanel.getByRole('button', { name: 'Create new Song from Generation' }).click();
    const move = page.getByRole('dialog', { name: 'Create new Song from Generation' });
    await expect(move).toBeVisible();
    await expect(move.getByTestId('move-audio-files')).toHaveText(
      `Its 1 local audio file and its Preferred Audio File choice move with it; the files stay where they are on disk. Files associated with ${song.shortcode} itself stay with ${song.shortcode}.`,
    );
    await expectModalAccessibleInBothSchemes(page);
    await move.getByRole('textbox', { name: 'New Song title' }).fill('Night Drive Alt');
    await move.getByRole('button', { name: 'Move to a new Song' }).click();
    await expect(page.getByRole('heading', { level: 2, name: 'Night Drive Alt' })).toBeVisible();
    const movedPanel = page.getByRole('dialog', { name: /^Generation n8-[0-9]+-v1-g1$/ });
    await expect(movedPanel).toBeVisible();
    await movedPanel.getByRole('button', { name: 'Close' }).click();
    await expect(movedPanel).toBeHidden();

    const files = page.getByRole('region', { name: 'Audio Files' }).getByTestId('song-audio-file');
    await expect(files).toHaveCount(1);
    await expect(files.first()).toHaveAttribute('data-file', MOVED_WAV);
    await expectAccessibleInLightAndDark(page);

    // Nothing in the media folder changed.
    expect(await listing(media)).toEqual(before);
  });
});
