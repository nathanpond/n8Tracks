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
import { signInThroughApi } from '../support/session.ts';
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

/**
 * Walks the first step of #208's Demo on a container of its own, whose media folder (mounted
 * read-only) the test copies files into on the host: open Library → Media with a few files in the
 * folder, press Scan Library, watch the bar, and see the counts appear. The first read of the scan's
 * job is held back until the bar has been checked, so the bar is seen however fast the scan is.
 * Unavailable and recovered folders (Demo steps 2 and 3) are covered by the API and component tests.
 */
test.describe('Library → Media', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-media-'));
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

  test('scans the library on demand and shows the bar, then the counts', async ({ page }) => {
    test.setTimeout(240_000);
    const media = join(work ?? '', 'media');

    // The startup scan of the empty folder ends first, so the counts below come from Scan Library.
    await expect
      .poll(
        async () => {
          const current = await status(page);
          return current.lastScan !== null && current.activeScanJobId === null;
        },
        { timeout: 120_000, intervals: [1_000] },
      )
      .toBe(true);

    await copyFile(fixture('mp3'), join(media, 'First Song.mp3'));
    await copyFile(fixture('ogg'), join(media, 'Second Song.ogg'));
    await mkdir(join(media, 'Album'));
    await copyFile(fixture('flac'), join(media, 'Album', 'Third Song.flac'));

    // 1. Library → Media from the sidebar.
    await page.goto(`${FRESH_URL}songs`);
    const library = page
      .getByRole('navigation', { name: 'Main' })
      .getByRole('group', { name: 'Library' });
    await library.getByRole('link', { name: 'Media' }).click();
    await expect(page.getByRole('heading', { level: 2, name: 'Media' })).toBeVisible();
    await expect(library.getByRole('link', { name: 'Media' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    await expect(page.getByTestId('media-folder-state')).toHaveText('Available');
    await expect(page.getByTestId('media-folder-path')).toHaveText('/media');
    await expect(page.getByTestId('files-available')).toHaveText('0');
    await expect(page.getByTestId('scan-finished')).toContainText('Started when n8Tracks started.');
    await expect(page.getByTestId('next-scheduled-scan')).toContainText(
      'The next scheduled scan is at',
    );
    await expectAccessibleInLightAndDark(page);

    // Press Scan Library: the bar shows, with its name, and the button waits.
    let release: () => void = () => undefined;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    await page.route('**/api/v1/jobs/*', async (route) => {
      await held;
      await route.continue();
    });
    const button = page.getByRole('button', { name: 'Scan Library' });
    await button.click();
    const bar = page.getByRole('progressbar', { name: 'Scan progress' });
    await expect(bar).toBeVisible();
    await expect(button).toBeDisabled();
    await expect(page.getByTestId('scan-announcement')).toHaveText('The scan has started.');
    await expectAccessibleInLightAndDark(page);
    release();

    // It ends: the counts appear without a reload, and the end is announced.
    await expect(page.getByTestId('files-available')).toHaveText('3', { timeout: 60_000 });
    await expect(bar).toBeHidden();
    await expect(button).toBeEnabled();
    const announcement = page.getByTestId('scan-announcement');
    await expect(announcement).toHaveAttribute('aria-live', 'polite');
    await expect(announcement).toHaveText('The scan has finished.');
    await expect(page.getByTestId('files-unmatched')).toHaveText('3');
    await expect(page.getByTestId('scan-finished')).toContainText('Started with Scan Library.');
    await expect(page.getByTestId('scan-count-new')).toHaveText('3');
    await expect(page.getByTestId('scan-count-seen')).toHaveText('3');
    await expectAccessibleInLightAndDark(page);
  });
});
