import { expect, test, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark } from '../support/a11y.ts';

interface ListedBackup {
  name: string;
  kind: string | null;
}

/** The configured time zone, as the health report names it. */
async function configuredTimeZone(page: Page): Promise<string> {
  const response = await page.request.get('./health');
  const body = (await response.json()) as { timeZone?: string };
  return body.timeZone ?? 'UTC';
}

/** `HH:mm` of `time` in `timeZone`. */
function clockTime(time: Date, timeZone: string): string {
  return new Intl.DateTimeFormat('en-GB', {
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
    timeZone,
  }).format(time);
}

/** The next whole minute at least ten seconds after `now`. */
function nextMinuteWithMargin(now: number): Date {
  return new Date(Math.ceil((now + 10_000) / 60_000) * 60_000);
}

async function scheduledBackups(page: Page): Promise<ListedBackup[]> {
  const response = await page.request.get('./api/v1/backups');
  expect(response.status()).toBe(200);
  const body = (await response.json()) as { items: ListedBackup[] };
  return body.items.filter((item) => item.kind === 'scheduled');
}

async function setSchedule(page: Page, time: string, keep: string) {
  const form = page.getByRole('form', { name: 'Backup schedule' });
  await form.getByLabel(/^Time of day/).fill(time);
  await form.getByLabel(/^Scheduled backups to keep/).fill(keep);
  await form.getByRole('button', { name: 'Save schedule' }).click();
  await expect(page.getByTestId('schedule-save-status').getByText('Schedule saved.')).toBeVisible();
}

/**
 * Walks the story's Demo step 2 on the root project's container, signed in: the schedule is moved
 * to the next whole minute, the server's scheduler (which looks just after each minute turns)
 * runs a scheduled backup, and the Backups page shows it. It waits for a real minute to pass.
 * Retention and missed runs are covered by the API tests, which control the clock.
 */
test.describe('Settings → Backups: the schedule', { tag: '@root-only' }, () => {
  test('runs a scheduled backup at the time set on the page', async ({ page }) => {
    test.setTimeout(240_000);

    await page.goto('./settings/backups');
    await expect(page.getByRole('heading', { level: 2, name: 'Backups' })).toBeVisible();
    await expect(page.getByRole('heading', { level: 3, name: 'Schedule' })).toBeVisible();
    await expect(page.getByTestId('backup-schedule-status')).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    const before = (await scheduledBackups(page)).map((item) => item.name);
    const timeZone = await configuredTimeZone(page);

    // The next whole minute, with enough margin to save before it (else the one after).
    const target = nextMinuteWithMargin(Date.now());
    const time = clockTime(target, timeZone);
    await setSchedule(page, time, '7');
    await expect(
      page.getByTestId('backup-schedule-status').getByText(`Daily at ${time}, keep 7`),
    ).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // Wait for the scheduler: shortly after the minute, a scheduled backup runs and succeeds.
    await expect
      .poll(
        async () =>
          (await scheduledBackups(page)).filter((item) => !before.includes(item.name)).length,
        {
          timeout: 180_000,
          intervals: [5_000],
        },
      )
      .toBe(1);
    await expect
      .poll(
        async () => {
          await page.reload();
          return page.getByText(/^The latest scheduled backup succeeded/).isVisible();
        },
        { timeout: 60_000, intervals: [2_000] },
      )
      .toBe(true);
    const created = (await scheduledBackups(page)).find((item) => !before.includes(item.name));
    const row = page.locator(`tr[data-backup="${created?.name ?? ''}"]`);
    await expect(row.getByTestId('backup-kind')).toHaveText('Scheduled');
    await expect(page.getByTestId('last-success')).not.toHaveText('none yet');
    await expectAccessibleInLightAndDark(page);

    // Leave the instance's schedule as it was.
    await setSchedule(page, '03:00', '7');
  });
});
