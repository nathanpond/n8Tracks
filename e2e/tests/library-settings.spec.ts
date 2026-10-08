import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
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

/** The relative paths of every cataloged audio file. */
async function listedPaths(page: Page): Promise<string[]> {
  const response = await page.request.get(`${FRESH_URL}api/v1/audio-files`);
  expect(response.status()).toBe(200);
  const body = (await response.json()) as { items: { path: string }[] };
  return body.items.map((item) => item.path);
}

function form(page: Page) {
  return page.getByRole('form', { name: 'Scan schedule' });
}

async function saveSchedule(page: Page) {
  await form(page).getByRole('button', { name: 'Save schedule' }).click();
  await expect(page.getByTestId('scan-schedule-status').getByText('Schedule saved.')).toBeVisible();
}

/**
 * Walks #204's Demo on a container of its own, whose media folder (mounted read-only) the test
 * copies files into on the host: the schedule is the whole instance's, and a one-minute schedule
 * would scan under every other test. It waits for real minutes to pass. The startup scan, the
 * 15-minute default, skipping while a scan runs, and a simulated day with the schedule off are
 * covered by the API tests, which control the clock.
 */
test.describe('Settings → Library: scheduled scans', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-library-'));
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

  test('finds a copied file within the interval, and nothing with the schedule off until a scan is asked for', async ({
    page,
  }) => {
    test.setTimeout(420_000);
    const media = join(work ?? '', 'media');

    // 1. Settings → Library: set the interval to 1 minute and save; reload and see it kept.
    await page.goto(`${FRESH_URL}settings/library`);
    await expect(page.getByRole('heading', { level: 2, name: 'Library' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Library' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    await expect(page.getByTestId('scan-schedule-summary')).toHaveText(
      'Scheduled scans run every 15 minutes, counted from the end of the previous scan.',
    );
    await expectAccessibleInLightAndDark(page);

    const interval = form(page).getByRole('textbox', { name: /^Minutes between scans/ });
    await interval.fill('1');
    await saveSchedule(page);
    await page.reload();
    await expect(interval).toHaveValue('1');
    await expect(page.getByTestId('scan-schedule-summary')).toContainText('every minute');
    await expectAccessibleInLightAndDark(page);

    // An interval out of range is refused on the page.
    await interval.fill('1441');
    await form(page).getByRole('button', { name: 'Save schedule' }).click();
    await expect(
      form(page).getByText('Enter a whole number of minutes from 1 to 1,440.'),
    ).toBeVisible();
    await expectAccessibleInLightAndDark(page);
    await page.reload();
    await expect(interval).toHaveValue('1');

    // 2. Copy a file into the media folder and wait: it is listed without a scan being requested.
    await writeFile(join(media, 'Dropped In.wav'), Buffer.from('not really audio'));
    await expect
      .poll(() => listedPaths(page), { timeout: 150_000, intervals: [2_000] })
      .toContain('Dropped In.wav');

    // 3. Turn the schedule off and copy another file in: two minutes later it is still not listed.
    await form(page).getByRole('switch', { name: 'Scan on a schedule' }).click();
    await expect(interval).toBeDisabled();
    await saveSchedule(page);
    await expect(page.getByTestId('scan-schedule-summary')).toContainText(
      'Scheduled scans are off.',
    );
    await expectAccessibleInLightAndDark(page);
    await page.reload();
    await expect(form(page).getByRole('switch', { name: 'Scan on a schedule' })).not.toBeChecked();
    await expect(interval).toHaveValue('1');
    await expect(interval).toBeDisabled();

    await writeFile(join(media, 'Later.wav'), Buffer.from('not really audio either'));
    // Looked at every few seconds for two minutes: "listed" at any look fails at once.
    const until = Date.now() + 120_000;
    await expect
      .poll(
        async () =>
          (await listedPaths(page)).includes('Later.wav')
            ? 'listed'
            : Date.now() >= until
              ? 'still not listed after two minutes'
              : 'not listed yet',
        { timeout: 150_000, intervals: [5_000] },
      )
      .toBe('still not listed after two minutes');

    // Asked for, a scan finds it.
    const scan = await page.request.post(`${FRESH_URL}api/v1/media/scans`, {
      headers: ANTIFORGERY_HEADERS,
    });
    expect([200, 202]).toContain(scan.status());
    await expect
      .poll(() => listedPaths(page), { timeout: 30_000, intervals: [1_000] })
      .toContain('Later.wav');
  });
});
