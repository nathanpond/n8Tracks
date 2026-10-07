import { copyFile, mkdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test, type Page } from '@playwright/test';
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

interface ListedFile {
  fileName: string;
  song: unknown;
}

async function unassociated(page: Page): Promise<ListedFile[]> {
  const response = await page.request.get(`${FRESH_URL}api/v1/audio-files?association=none`);
  expect(response.status()).toBe(200);
  return ((await response.json()) as { items: ListedFile[] }).items;
}

/**
 * Walks #209's Demo on a container of its own, whose media folder (mounted read-only) the test copies
 * a file into on the host: `My Song Title.mp3` for an existing Song titled "My Song Title" is listed
 * on Library → Unmatched Files with that Song suggested and the reason shown, and is still listed,
 * still unassociated, after another scan.
 */
test.describe('Library → Unmatched Files', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-unmatched-'));
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

  test('lists an unmatched file with the Song it is named for suggested, scan after scan', async ({
    page,
  }) => {
    test.setTimeout(240_000);
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

    // 1. A file with no Suno ID, named for an existing Song; scan.
    const created = await page.request.post(`${FRESH_URL}api/v1/songs`, {
      headers: ANTIFORGERY_HEADERS,
      data: { title: 'My Song Title' },
    });
    expect(created.status()).toBe(201);
    const song = (await created.json()) as { shortcode: string };
    await copyFile(fixture('mp3'), join(media, 'My Song Title.mp3'));
    await scan(page);

    // 2. From the Media page's Unmatched count to Library → Unmatched Files.
    await page.goto(`${FRESH_URL}library/media`);
    await expect(page.getByTestId('files-unmatched')).toHaveText('1');
    await page.getByRole('link', { name: '1 unmatched: open Unmatched Files' }).click();
    await expect(page.getByRole('heading', { level: 2, name: 'Unmatched Files' })).toBeVisible();
    await expect(page).toHaveURL(`${FRESH_URL}library/unmatched`);
    const library = page
      .getByRole('navigation', { name: 'Main' })
      .getByRole('group', { name: 'Library' });
    await expect(library.getByRole('link', { name: 'Unmatched Files' })).toHaveAttribute(
      'aria-current',
      'page',
    );

    const row = page.getByTestId('unmatched-file');
    await expect(row).toHaveCount(1);
    await expect(row.getByRole('rowheader')).toHaveText('My Song Title.mp3');
    await expect(row.getByTestId('file-status')).toHaveText('Available');
    const suggestions = row.getByRole('list', { name: 'Suggested Songs for My Song Title.mp3' });
    const suggested = suggestions.getByRole('link', { name: 'My Song Title' });
    await expect(suggested).toHaveAttribute('href', `/go/${song.shortcode}`);
    await expect(suggestions.getByText('Title matches the file name')).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // A search that matches nothing says so.
    await page.getByRole('textbox', { name: 'Search' }).fill('nothing like it');
    await page.getByRole('button', { name: 'Search' }).click();
    await expect(page.getByTestId('no-unmatched-files')).toHaveText('No unmatched file matches.');
    await expectAccessibleInLightAndDark(page);

    // 3. Scan again: still listed, still unassociated.
    await scan(page);
    await page.goto(`${FRESH_URL}library/unmatched?sort=name`);
    await expect(page.getByTestId('unmatched-file')).toHaveCount(1);
    await expect(
      page.getByTestId('unmatched-file').getByRole('link', { name: 'My Song Title' }),
    ).toBeVisible();
    const listed = await unassociated(page);
    expect(listed.map((file) => [file.fileName, file.song])).toEqual([['My Song Title.mp3', null]]);
  });
});
