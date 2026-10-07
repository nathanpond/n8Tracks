import { copyFile, mkdir, mkdtemp, readdir, rm, stat } from 'node:fs/promises';
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

/** The Suno ID of the Generation the second file came from. */
const SUNO_ID = '0c90d621-e30c-4c76-814a-e1fdeb500582';
const SUGGESTED = 'My Song Title.mp3';
const FROM_SUNO = `Night Drive (suno-${SUNO_ID}).wav`;

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

async function createSong(page: Page, title: string): Promise<{ shortcode: string; v1: string }> {
  const created = await page.request.post(`${FRESH_URL}api/v1/songs`, {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(created.status()).toBe(201);
  const song = (await created.json()) as {
    shortcode: string;
    currentVersion: { shortcode: string };
  };
  return { shortcode: song.shortcode, v1: song.currentVersion.shortcode };
}

/** Every file under `root` with its size and modified time: what "nothing in the media folder changed" compares. */
async function listing(root: string): Promise<string[]> {
  const entries = await readdir(root, { recursive: true });
  const lines = await Promise.all(
    entries.map(async (entry) => {
      const info = await stat(join(root, entry));
      return `${entry}|${String(info.size)}|${String(info.mtimeMs)}`;
    }),
  );
  return lines.sort();
}

/**
 * Walks #210's Demo on a container of its own, whose media folder (mounted read-only) the test copies
 * two files into on the host: on Library → Unmatched Files the user accepts the suggestion for one
 * file, and associates the other with a Song and its Generation through the dialog; both leave the
 * list. Under Show → Associated the user removes the second file's association: it is back among the
 * unmatched, stays there after a scan (its name carries the Generation's Suno ID, and the user's
 * decision outranks the scan), and "Match by Suno ID again" associates it at once. The media folder
 * is unchanged throughout.
 */
test.describe('Associating files', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-associate-'));
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

  test('associates by suggestion and by dialog, removes an association, and the scan keeps the decision', async ({
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

    // Two Songs; two files, scanned while no Generation has the Suno ID; then the Generation.
    await createSong(page, 'My Song Title');
    const night = await createSong(page, 'Night Drive');
    await copyFile(fixture('mp3'), join(media, SUGGESTED));
    await copyFile(fixture('wav'), join(media, FROM_SUNO));
    await scan(page);
    const generation = await seedGenerationIn(
      FRESH_NAME,
      night.v1,
      JSON.stringify({ id: SUNO_ID, status: 'complete', title: 'Night Drive' }),
    );
    const before = await listing(media);

    await page.goto(`${FRESH_URL}library/unmatched?sort=name`);
    const rows = page.getByTestId('unmatched-file');
    await expect(rows).toHaveCount(2);
    await expectAccessibleInLightAndDark(page);

    // 1. Accept the suggestion on one file: it leaves the list.
    const suggested = page.locator(`[data-testid="unmatched-file"][data-file="${SUGGESTED}"]`);
    await suggested
      .getByRole('button', { name: /^Associate My Song Title\.mp3 with My Song Title/ })
      .click();
    await expect(page.getByTestId('association-announcement')).toContainText(
      'Associated My Song Title.mp3 with My Song Title',
    );
    await expect(rows).toHaveCount(1);
    await expect(suggested).toHaveCount(0);

    // 2. On the other, open the dialog, search a Song, choose a Generation, confirm.
    await page.getByRole('button', { name: `Choose a Song for ${FROM_SUNO}` }).click();
    const dialog = page.getByRole('dialog', { name: 'Associate file' });
    await expect(dialog).toBeVisible();
    await dialog.getByRole('textbox', { name: 'Song' }).fill('Night');
    await dialog.getByRole('option', { name: /Night Drive/ }).click();
    const choice = dialog.getByRole('radiogroup', { name: 'Generation' });
    await expect(choice.getByRole('radio', { name: 'None: the Song only' })).toBeChecked();
    await choice.getByRole('radio', { name: generation }).check();
    await expectModalAccessibleInBothSchemes(page);
    await dialog.getByRole('button', { name: 'Associate' }).click();
    await expect(dialog).toBeHidden();
    await expect(page.getByTestId('association-announcement')).toHaveText(
      `Associated ${FROM_SUNO} with Night Drive (${night.shortcode}), Generation ${generation}.`,
    );
    await expect(page.getByTestId('no-unmatched-files')).toHaveText('No unmatched files');
    await expectAccessibleInLightAndDark(page);

    // 3. Show the associated files, find the second, and remove its association.
    await page.getByRole('combobox', { name: 'Show' }).selectOption('Associated');
    await expect(page).toHaveURL(/show=associated/);
    await expect(rows).toHaveCount(2);
    const fromSuno = page.locator(`[data-testid="unmatched-file"][data-file="${FROM_SUNO}"]`);
    await expect(fromSuno.getByTestId('file-association')).toContainText(
      `Night Drive (${night.shortcode}), Generation ${generation}, by you`,
    );
    await fromSuno
      .getByRole('button', { name: `Change or remove the association of ${FROM_SUNO}` })
      .click();
    const change = page.getByRole('dialog', { name: 'Change association' });
    await expect(change.getByTestId('current-association')).toContainText(
      `It is associated with Night Drive (${night.shortcode}), Generation ${generation}, by you.`,
    );
    await expectModalAccessibleInBothSchemes(page);
    await change.getByRole('button', { name: 'Remove association' }).click();
    await expect(change).toBeHidden();
    await expect(page.getByTestId('association-announcement')).toHaveText(
      `Removed the association of ${FROM_SUNO}. It is back in Unmatched Files.`,
    );
    await expect(rows).toHaveCount(1);

    // Back under Unmatched, and still there after a scan, although its name carries a live Suno ID.
    await scan(page);
    await page.goto(`${FRESH_URL}library/unmatched`);
    await expect(rows).toHaveCount(1);
    await expect(fromSuno.getByTestId('unmatched-reason')).toHaveText(
      'You removed its association.',
    );
    await expectAccessibleInLightAndDark(page);

    // Match by Suno ID again: associated at once, by its Suno ID.
    await fromSuno.getByRole('button', { name: `Match by Suno ID again: ${FROM_SUNO}` }).click();
    await expect(page.getByTestId('association-announcement')).toHaveText(
      `Matched ${FROM_SUNO} by its Suno ID with Night Drive (${night.shortcode}), Generation ${generation}.`,
    );
    await expect(page.getByTestId('no-unmatched-files')).toHaveText('No unmatched files');

    // Nothing in the media folder changed (invariant 2).
    expect(await listing(media)).toEqual(before);
  });
});
