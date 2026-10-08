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

const FIRST_ID = '2b4e8f0a-6c1d-4e3f-9a5b-7d2c1e0f3a4b';
const SECOND_ID = '9c8b7a6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d';
const FIRST_WAV = `Night Drive (suno-${FIRST_ID}).wav`;
const SECOND_WAV = `Night Drive take two (suno-${SECOND_ID}).wav`;
/** A file named like audio that is not: it cannot be decoded. */
const BROKEN = 'Broken take.mp3';

/**
 * A WAV of `seconds` of a quiet 440 Hz tone, mono, 8 kHz, 16-bit: long enough that playback is still
 * going after a few page changes, small enough to write in the test.
 */
function toneWav(seconds: number): Buffer {
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
      Math.round(Math.sin((2 * Math.PI * 440 * index) / rate) * 2000),
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

/** Whether the audio element is the same one marked earlier (route changes never recreate it). */
function stillTheMarkedElement(page: Page): Promise<boolean> {
  return page.evaluate(() => {
    const audio = document.querySelector('audio[data-testid="player-audio"]');
    return audio !== null && audio.getAttribute('data-e2e-mark') === 'first';
  });
}

function versionNode(page: Page, number: string) {
  return page
    .getByRole('tree', { name: 'Versions' })
    .locator(`[role="treeitem"][data-version-number="${number}"]`);
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

function generationRow(page: Page, shortcode: string) {
  return page.locator(`tr[data-generation="${shortcode}"]`);
}

/**
 * Walks #218's Demo on a container of its own, whose read-only media folder holds two generated
 * WAVs named for two Generations' Suno IDs and one file that cannot be decoded: Play on a Generation
 * row brings up the bar and plays; typing in the lyrics, switching Versions, and opening Settings
 * leave it playing, on the same audio element; the seek bar and volume work by keyboard, and the
 * volume is as it was left after a reload; Play on the other Generation replaces the first; and a
 * file that cannot be decoded says so in the bar. Each state of the bar (playing, paused, error) is
 * scanned with axe in light and dark.
 */
test.describe('The player', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-player-'));
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

  test('plays a Generation while the user edits and moves around, by mouse and keyboard', async ({
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
      id: string;
      shortcode: string;
      currentVersion: { shortcode: string };
    };
    const first = await seedGenerationIn(
      FRESH_NAME,
      song.currentVersion.shortcode,
      JSON.stringify({ id: FIRST_ID, status: 'complete', title: 'Night Drive' }),
    );
    const second = await seedGenerationIn(
      FRESH_NAME,
      song.currentVersion.shortcode,
      JSON.stringify({ id: SECOND_ID, status: 'complete', title: 'Night Drive, take two' }),
    );
    // A second Version, not frozen, to type in and switch to.
    const version = await page.request.post(`${FRESH_URL}api/v1/songs/${song.id}/versions`, {
      headers: ANTIFORGERY_HEADERS,
      data: { sourceVersionId: song.currentVersion.shortcode, number: '2' },
    });
    expect(version.status()).toBe(201);
    await writeFile(join(media, FIRST_WAV), toneWav(90));
    await writeFile(join(media, SECOND_WAV), toneWav(90));
    await writeFile(join(media, BROKEN), Buffer.alloc(64 * 1024, 0x5a));
    await scan(page);

    // 1. Play on the Generation row: the bar appears and audio plays.
    await page.goto(`${FRESH_URL}songs/${song.shortcode}/v/2`);
    await openVersionOne(page);
    await expect(page.getByRole('contentinfo', { name: 'Player' })).toHaveCount(0);
    await generationRow(page, first)
      .getByRole('button', { name: `Play ${first}` })
      .click();
    const bar = page.getByRole('contentinfo', { name: 'Player' });
    await expect(bar).toBeVisible();
    await expect(bar.getByRole('link', { name: 'Night Drive' })).toBeVisible();
    await expect(bar.getByTestId('player-detail')).toHaveText(`Version 1 · ${first} · WAV`);
    await expect(bar.getByTestId('player-toggle')).toHaveText('Pause');
    await expect(
      generationRow(page, first).getByRole('button', { name: `Pause ${first}` }),
    ).toBeVisible();
    await playsPast(page, 0.5);
    await page.locator('audio[data-testid="player-audio"]').evaluate((audio) => {
      audio.setAttribute('data-e2e-mark', 'first');
    });
    await expect(bar.getByTestId('player-duration')).toHaveText('1:30');
    await expectAccessibleInLightAndDark(page);

    // 2. Type in the lyrics editor, switch Versions, open Settings: the audio carries on.
    const lyrics = page.getByRole('textbox', { name: 'Lyrics' });
    await lyrics.click();
    await page.keyboard.type('[Verse]\nHeadlights on the wet road');
    await expect(page.getByTestId('autosave').getByRole('status')).toHaveText('Saved', {
      timeout: 15_000,
    });
    let before = await position(page);
    await versionNode(page, '1').click();
    await expect(page.getByRole('heading', { level: 3, name: 'Version 1' })).toBeVisible();
    await page
      .getByRole('navigation', { name: 'Main' })
      .getByRole('link', { name: 'Account' })
      .click();
    await expect(page.getByRole('heading', { level: 2, name: 'Account' })).toBeVisible();
    await playsPast(page, before + 0.5);
    expect(await stillTheMarkedElement(page)).toBe(true);
    await expect(bar).toBeVisible();

    // 3. The seek bar and volume by keyboard; the space bar on the seek bar pauses and plays.
    const seek = bar.getByRole('slider', { name: 'Seek' });
    await seek.focus();
    before = await position(page);
    await page.keyboard.press('PageUp');
    expect(await position(page)).toBeGreaterThanOrEqual(before + 29);
    before = await position(page);
    await page.keyboard.press('ArrowLeft');
    expect(await position(page)).toBeLessThanOrEqual(before - 4);
    await expect(seek).toHaveAttribute('aria-valuetext', /^\d+:\d\d of 1:30$/);
    await page.keyboard.press('Space');
    await expect(bar.getByTestId('player-toggle')).toHaveText('Play');
    await expect(bar.getByTestId('player-bar')).toHaveAttribute('data-status', 'paused');
    const paused = await position(page);
    await expectAccessibleInLightAndDark(page);
    expect(await position(page)).toBe(paused);
    await seek.focus();
    await page.keyboard.press('Space');
    await expect(bar.getByTestId('player-toggle')).toHaveText('Pause');
    await playsPast(page, paused + 0.3);

    const volume = bar.getByRole('slider', { name: 'Volume' });
    await volume.focus();
    await page.keyboard.press('ArrowLeft');
    await page.keyboard.press('ArrowLeft');
    await expect(volume).toHaveValue('90');
    await expect(volume).toHaveAttribute('aria-valuetext', '90%');
    await bar.getByRole('button', { name: 'Mute' }).click();
    await expect(bar.getByRole('button', { name: 'Mute' })).toHaveAttribute('aria-pressed', 'true');
    await bar.getByRole('button', { name: 'Mute' }).click();

    // Reloaded, nothing plays; played again, the volume is as it was left.
    await page.goto(`${FRESH_URL}songs/${song.shortcode}/v/1`);
    await expect(page.getByRole('contentinfo', { name: 'Player' })).toHaveCount(0);
    await openVersionOne(page);
    await generationRow(page, first)
      .getByRole('button', { name: `Play ${first}` })
      .click();
    await expect(bar.getByRole('slider', { name: 'Volume' })).toHaveValue('90');
    await playsPast(page, 0.3);

    // 4. Play on the other Generation replaces the first.
    await generationRow(page, second)
      .getByRole('button', { name: `Play ${second}` })
      .click();
    await expect(bar.getByTestId('player-detail')).toHaveText(`Version 1 · ${second} · WAV`);
    await expect(
      generationRow(page, second).getByRole('button', { name: `Pause ${second}` }),
    ).toBeVisible();
    await expect(
      generationRow(page, first).getByRole('button', { name: `Play ${first}` }),
    ).toBeVisible();
    await expect(page.locator('audio')).toHaveCount(1);
    await playsPast(page, 0.3);

    // A file that cannot be decoded, played from Unmatched Files: the bar says so and names it.
    await page
      .getByRole('navigation', { name: 'Main' })
      .getByRole('link', { name: 'Unmatched Files' })
      .click();
    const broken = page.locator(`[data-testid="unmatched-file"][data-file="${BROKEN}"]`);
    await broken.getByRole('button', { name: `Play ${BROKEN}` }).click();
    await expect(bar.getByRole('alert')).toHaveText(
      `${BROKEN} could not be played: the audio could not be loaded or decoded.`,
    );
    await expect(bar.getByTestId('player-bar')).toHaveAttribute('data-status', 'error');
    await expect(bar.getByRole('button', { name: 'Retry' })).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // Close stops playback and hides the bar.
    await bar.getByRole('button', { name: 'Close the player' }).click();
    await expect(page.getByRole('contentinfo', { name: 'Player' })).toHaveCount(0);
  });
});
