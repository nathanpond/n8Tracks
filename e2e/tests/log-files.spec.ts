import { mkdir, mkdtemp, readdir, readFile, rm, stat, writeFile } from 'node:fs/promises';
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

const MEGABYTE = 1024 * 1024;

function form(page: Page) {
  return page.getByRole('form', { name: 'Log settings' });
}

async function save(page: Page) {
  await form(page).getByRole('button', { name: 'Save log settings' }).click();
}

/** The log files in the container's data folder, read on the host (the container writes as us). */
async function logFiles(logs: string): Promise<string[]> {
  return (await readdir(logs)).filter((name) => /^n8tracks-\d{8}(_\d+)?\.jsonl$/.test(name)).sort();
}

/** Whether the newest log file has a Debug line for a `GET /health` request. */
async function newestHasDebugHealthLine(logs: string): Promise<boolean> {
  const newest = (await logFiles(logs)).at(-1);
  if (newest === undefined) {
    return false;
  }
  const text = await readFile(join(logs, newest), 'utf8');
  return text
    .split('\n')
    .filter((line) => line.length > 0)
    .map((line) => JSON.parse(line) as { level: string; properties: Record<string, unknown> })
    .some((line) => line.level === 'Debug' && line.properties.path === '/health');
}

async function folderBytes(logs: string): Promise<number> {
  let total = 0;
  for (const name of await readdir(logs)) {
    total += (await stat(join(logs, name))).size;
  }
  return total;
}

/** The date `days` before today in UTC, as a log file names it. */
function daysAgo(days: number): string {
  const date = new Date(Date.now() - days * 24 * 60 * 60 * 1000);
  return date.toISOString().slice(0, 10).replaceAll('-', '');
}

/**
 * Walks #234's Demo on a container of its own (the settings are the whole instance's), reading the
 * log files on the host from the data folder it mounts. The cap under heavy traffic, retention by
 * date, rolling by size and by day, and Debug ending after 24 hours are covered by the API tests,
 * which control the clock and write megabytes in milliseconds.
 */
test.describe('Settings → Diagnostics: log files', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-logs-'));
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

  test('Debug puts the health checks in the newest file, and a 10 MB cap holds the folder under it', async ({
    page,
  }) => {
    const logs = join(work ?? '', `data-${FRESH_NAME}`, 'logs');

    // The app writes its log to files from the start, at Information.
    await expect.poll(() => logFiles(logs), { timeout: 30_000 }).not.toEqual([]);

    // 1. Settings → Diagnostics: set the level to Debug.
    await page.goto(`${FRESH_URL}settings/diagnostics`);
    await expect(page.getByRole('heading', { level: 2, name: 'Diagnostics' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Diagnostics' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    await expect(page.getByTestId('logging-summary')).toContainText(
      'Logging at Information, set by N8TRACKS_LOG_LEVEL',
    );
    await expectAccessibleInLightAndDark(page);

    await form(page)
      .getByRole('combobox', { name: /^Log level/ })
      .selectOption('debug');
    await save(page);
    await expect(page.getByTestId('logging-status')).toHaveText('Log settings saved.');
    await expect(page.getByTestId('debug-until')).toContainText(
      'Debug switches back to Information on',
    );
    await expectAccessibleInLightAndDark(page);

    // Within a minute, GET /health requests appear in the newest log file at Debug.
    await expect
      .poll(
        async () => {
          await page.request.get(`${FRESH_URL}health`);
          return newestHasDebugHealthLine(logs);
        },
        { timeout: 60_000, intervals: [1_000] },
      )
      .toBe(true);

    // A lower retention that would delete a file asks first, and says how much.
    await writeFile(join(logs, `n8tracks-${daysAgo(10)}.jsonl`), 'x'.repeat(2048));
    await form(page)
      .getByRole('textbox', { name: /^Days to keep log files/ })
      .fill('7');
    await save(page);
    const dialog = page.getByRole('dialog', { name: 'Delete log files?' });
    await expect(dialog.getByTestId('deletion-summary')).toHaveText(
      'These limits delete 1 log file (2 KB) now. This cannot be undone.',
    );
    await expectModalAccessibleInBothSchemes(page);
    await dialog.getByRole('button', { name: 'Delete and save' }).click();
    await expect(page.getByTestId('logging-status')).toHaveText('Log settings saved.');
    expect(await logFiles(logs)).not.toContain(`n8tracks-${daysAgo(10)}.jsonl`);

    // 2. Set the cap to 10 MB and generate traffic: the folder stays under it.
    await form(page)
      .getByRole('textbox', { name: /^Most space for log files/ })
      .fill('10');
    await save(page);
    await expect(page.getByTestId('logging-status')).toHaveText('Log settings saved.');
    await expect(page.getByTestId('logging-summary')).toContainText(
      'kept for 7 days and take at most 10 MB together',
    );
    await page.reload();
    await expect(page.getByTestId('logging-summary')).toContainText('Logging at Debug.');
    await expectAccessibleInLightAndDark(page);

    for (let round = 0; round < 10; round += 1) {
      await Promise.all(Array.from({ length: 50 }, () => page.request.get(`${FRESH_URL}health`)));
      expect(await folderBytes(logs)).toBeLessThanOrEqual(10 * MEGABYTE);
    }
  });
});
