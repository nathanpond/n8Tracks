import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
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

const IDS = [
  '6d8f0a2c-4e5a-4d9e-8f1a-5b7c9d1e3f4a',
  '7e9a1b3d-5f6b-4eaf-9a2b-6c8d0e2f4a5b',
  '8fab2c4e-6a7c-4fb0-8b3c-7d9e1f3a5b6c',
];

/** A WAV of `seconds` of a quiet tone at `hertz`, mono, 8 kHz, 16-bit. */
function toneWav(seconds: number, hertz: number): Buffer {
  const rate = 8000;
  const samples = rate * seconds;
  const wav = Buffer.alloc(44 + samples * 2);
  wav.write('RIFF', 0, 'ascii');
  wav.writeUInt32LE(36 + samples * 2, 4);
  wav.write('WAVE', 8, 'ascii');
  wav.write('fmt ', 12, 'ascii');
  wav.writeUInt32LE(16, 16);
  wav.writeUInt16LE(1, 20);
  wav.writeUInt16LE(1, 22);
  wav.writeUInt32LE(rate, 24);
  wav.writeUInt32LE(rate * 2, 28);
  wav.writeUInt16LE(2, 32);
  wav.writeUInt16LE(16, 34);
  wav.write('data', 36, 'ascii');
  wav.writeUInt32LE(samples * 2, 40);
  for (let index = 0; index < samples; index++) {
    wav.writeInt16LE(
      Math.round(Math.sin((2 * Math.PI * hertz * index) / rate) * 2000),
      44 + index * 2,
    );
  }
  return wav;
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

/** The player's audio element's position, in seconds. */
function position(page: Page): Promise<number> {
  return page.locator('audio[data-testid="player-audio"]').evaluate((audio) => {
    if (!(audio instanceof HTMLAudioElement)) {
      throw new Error('The player has no audio element.');
    }
    return audio.currentTime;
  });
}

/** Waits until the audio has played past `seconds`. */
async function playsPast(page: Page, seconds: number): Promise<void> {
  await expect.poll(() => position(page), { timeout: 20_000 }).toBeGreaterThan(seconds);
}

/**
 * Waits until the bar plays `shortcode` again after a switch, and checks it continued from about
 * `at` (the time carried over, plus what has played since): never from the opening bars.
 */
async function continuesAt(page: Page, shortcode: string, at: number): Promise<void> {
  const bar = page.getByRole('contentinfo', { name: 'Player' });
  await expect(bar.getByTestId('player-detail')).toHaveText(`Version 1 · ${shortcode} · WAV`);
  await expect(bar.getByTestId('player-bar')).toHaveAttribute('data-status', 'playing', {
    timeout: 15_000,
  });
  const now = await position(page);
  expect(now).toBeGreaterThanOrEqual(at - 0.5);
  expect(now).toBeLessThan(at + 6);
}

/** Opens Version 1's Generations in the Versions table unless they are open already. */
async function openVersionOne(page: Page): Promise<void> {
  const chevron = page
    .getByRole('region', { name: 'Versions and Generations' })
    .getByRole('button', { name: 'Generations of Version 1' });
  if ((await chevron.getAttribute('aria-expanded')) !== 'true') {
    await chevron.click();
  }
}

/**
 * Walks #220's Demo on a container of its own, whose read-only media folder holds a generated
 * 90-second WAV for each of a Song's three Generations: (1) play the first, move to 0:30, open
 * Compare and choose the second: it carries on from 0:30; (2) the A/B key alternates between the
 * two at the same moment; (3) Next steps through the Generations and wraps. The shortcuts are listed
 * in the controls' descriptions and do nothing in a text field; the menu works by keyboard; the bar
 * rates the playing Generation without stopping it; and no switch changes the catalog. The bar and
 * the open menu are scanned with axe in light and dark.
 */
test.describe('Compare', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-compare-'));
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

  test('switches between a Song’s Generations at the same moment, by menu, A/B, and Next', async ({
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
      data: { title: 'Tidal Hours' },
    });
    expect(created.status()).toBe(201);
    const song = (await created.json()) as {
      id: string;
      shortcode: string;
      revision: number;
      currentVersion: { shortcode: string };
    };
    const shortcodes: string[] = [];
    for (const [index, id] of IDS.entries()) {
      shortcodes.push(
        await seedGenerationIn(
          FRESH_NAME,
          song.currentVersion.shortcode,
          JSON.stringify({ id, status: 'complete', title: `Tidal Hours ${String(index + 1)}` }),
        ),
      );
      await writeFile(
        join(media, `Tidal Hours ${String(index + 1)} (suno-${id}).wav`),
        toneWav(90, 330 + index * 110),
      );
    }
    const [first, second, third] = shortcodes as [string, string, string];
    await scan(page);

    // Every request the page sends from here on.
    const sent: { method: string; url: string }[] = [];
    page.on('request', (request) => {
      sent.push({ method: request.method(), url: request.url() });
    });

    // 1. Play the first Generation and move to 0:30.
    await page.goto(`${FRESH_URL}songs/${song.shortcode}`);
    await openVersionOne(page);
    await page
      .locator(`tr[data-generation="${first}"]`)
      .getByRole('button', { name: `Play ${first}` })
      .click();
    const bar = page.getByRole('contentinfo', { name: 'Player' });
    await expect(bar.getByTestId('player-detail')).toHaveText(`Version 1 · ${first} · WAV`);
    await playsPast(page, 0.3);
    await bar.getByRole('slider', { name: 'Seek' }).focus();
    await page.keyboard.press('PageUp');
    await playsPast(page, 30);
    const compare = bar.getByRole('button', { name: `Compare: playing ${first} · WAV` });
    await expect(compare).toBeVisible();
    await expect(bar.getByRole('button', { name: 'A/B' })).toBeDisabled();
    await expect(bar.getByRole('button', { name: 'Next' })).toHaveAccessibleDescription(
      'Next source of this Song. Shortcut: ] (right bracket).',
    );
    await expect(bar.getByRole('button', { name: 'Previous' })).toHaveAccessibleDescription(
      'Previous source of this Song. Shortcut: [ (left bracket).',
    );
    await expectAccessibleInLightAndDark(page);

    // Open Compare by keyboard: the Song's three Generations, by Version and shortcode.
    await compare.focus();
    await page.keyboard.press('Enter');
    const menu = page.getByRole('menu');
    await expect(menu.getByRole('menuitem')).toHaveCount(3);
    await expect(menu).toContainText(`Version 1 · ${first} · not rated`);
    await expect(
      menu.getByRole('menuitem', { name: `${first} · WAV (playing now)` }),
    ).toBeVisible();
    await expectModalAccessibleInBothSchemes(page);
    const before = await position(page);
    await menu.getByRole('menuitem', { name: `${second} · WAV` }).click();
    await continuesAt(page, second, before);

    // 2. The A/B key alternates between the two, each time at the same moment.
    await expect(bar.getByRole('button', { name: 'A/B' })).toHaveAccessibleDescription(
      `Switches to ${first} · WAV, at the same moment. Shortcut: \\ (backslash).`,
    );
    for (const shortcode of [first, second, first]) {
      const at = await position(page);
      await page.keyboard.press('Backslash');
      await continuesAt(page, shortcode, at);
    }
    await expectAccessibleInLightAndDark(page);

    // Not while typing: the Go to box gets the bracket, and nothing switches.
    const goTo = page.getByRole('textbox', { name: 'Go to' });
    await goTo.click();
    await page.keyboard.press('BracketRight');
    await expect(goTo).toHaveValue(']');
    await expect(bar.getByTestId('player-detail')).toHaveText(`Version 1 · ${first} · WAV`);
    await goTo.fill('');
    await bar.getByRole('slider', { name: 'Seek' }).focus();

    // 3. Next steps through the Generations in order and wraps to the first.
    for (const shortcode of [second, third, first]) {
      const at = await position(page);
      await page.keyboard.press('BracketRight');
      await continuesAt(page, shortcode, at);
    }

    // The bar rates what is playing without stopping it.
    const rating = bar.getByRole('radiogroup', { name: `Rating of ${first}` });
    const playedTo = await position(page);
    await rating.getByRole('radio', { name: '4 stars' }).click();
    await expect(rating.getByRole('radio', { name: '4 stars' })).toBeChecked();
    await expect
      .poll(async () => {
        const read = await page.request.get(`${FRESH_URL}api/v1/generations/${first}`);
        return ((await read.json()) as { rating: number | null }).rating;
      })
      .toBe(4);
    await expect(bar.getByTestId('player-bar')).toHaveAttribute('data-status', 'playing');
    await playsPast(page, playedTo + 0.3);

    // Switching sent only reads: the one write was the rating.
    const writes = sent.filter((request) => request.method !== 'GET' && request.method !== 'HEAD');
    expect(writes.map((request) => request.method)).toEqual(['PATCH']);
    expect(writes[0]?.url).toMatch(/\/api\/v1\/generations\/[0-9a-f-]+$/);
    const generationsAfter = await page.request.get(
      `${FRESH_URL}api/v1/songs/${song.id}/generations`,
    );
    const ratingsAfter = (
      (await generationsAfter.json()) as { items: { shortcode: string; rating: number | null }[] }
    ).items.map((item) => [item.shortcode, item.rating]);
    expect(ratingsAfter).toEqual([
      [first, 4],
      [second, null],
      [third, null],
    ]);
    const songAfter = await page.request.get(`${FRESH_URL}api/v1/songs/${song.id}`);
    expect(((await songAfter.json()) as { revision: number }).revision).toBe(song.revision);
  });
});
