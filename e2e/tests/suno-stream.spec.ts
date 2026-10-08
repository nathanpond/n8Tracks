import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test, type Page, type Request } from '@playwright/test';
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

const SUNO_ID = '5d3c2b1a-0f9e-4d8c-8b7a-6e5d4c3b2a19';
/** The clip's playback address on Suno's listed audio host: Playwright answers it, not Suno. */
const STREAM = `https://d2lwuy8qc234o3.cloudfront.net/1/clip/${SUNO_ID}.m4a`;
const LOCAL_WAV = `Night Drive (suno-${SUNO_ID}).wav`;

/** A WAV of `seconds` of a quiet 440 Hz tone, mono, 8 kHz, 16-bit. */
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

async function playsPast(page: Page, seconds: number): Promise<void> {
  await expect.poll(() => position(page), { timeout: 20_000 }).toBeGreaterThan(seconds);
}

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
 * Walks #221's Demo on a container of its own, with Suno's audio host answered by Playwright (the
 * server never contacts it): a Generation imported with no local file plays from Suno's address and
 * the bar says "Streaming from Suno", and the request to Suno carries no n8Tracks cookie and no
 * referrer path; when the address no longer plays, the bar says so and offers Open in Suno with the
 * sync suggestion; once the Generation's file is in the media folder and scanned, Play uses it and the
 * bar says "Local file". Every response carries the media policy. Each state of the bar is scanned
 * with axe in light and dark.
 */
test.describe('Streaming from Suno', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-suno-stream-'));
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

  test('plays from Suno with no local file, offers Open in Suno when that fails, and prefers the local file once scanned', async ({
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
    const shortcode = await seedGenerationIn(
      FRESH_NAME,
      song.currentVersion.shortcode,
      JSON.stringify({
        id: SUNO_ID,
        status: 'complete',
        title: 'Night Drive',
        audio_url: 'https://studio-api.prod.suno.com/api/forbidden',
        media_urls: [{ url: STREAM, content_type: 'm4a-opus', delivery: 'progressive' }],
      }),
    );

    // Suno's host, as Playwright answers it: each request is kept to check what it carried.
    const toSuno: Request[] = [];
    let answer: 'audio' | 'gone' = 'audio';
    const tone = toneWav(60);
    await page.route(STREAM, async (route) => {
      toSuno.push(route.request());
      await (answer === 'audio'
        ? route.fulfill({ status: 200, contentType: 'audio/wav', body: tone })
        : route.fulfill({ status: 404, body: '' }));
    });

    // 1. Play on a Generation with no local file: the bar says "Streaming from Suno", and it plays.
    const opened = await page.goto(`${FRESH_URL}songs/${song.shortcode}`);
    expect(opened?.headers()['content-security-policy']).toBe(
      "media-src 'self' https://d2lwuy8qc234o3.cloudfront.net",
    );
    await openVersionOne(page);
    await generationRow(page, shortcode)
      .getByRole('button', { name: `Play ${shortcode}` })
      .click();
    const bar = page.getByRole('contentinfo', { name: 'Player' });
    await expect(bar.getByTestId('player-source')).toHaveText('Streaming from Suno');
    await expect(bar.getByTestId('player-detail')).toHaveText(`Version 1 · ${shortcode}`);
    await playsPast(page, 0.5);
    expect(toSuno.length).toBeGreaterThan(0);
    for (const request of toSuno) {
      const headers = await request.allHeaders();
      expect(headers.cookie).toBeUndefined();
      expect(headers.authorization).toBeUndefined();
      // At most n8Tracks' origin, never a path (no referrer at all is fine too).
      expect(new URL(headers.referer ?? FRESH_URL).pathname).toBe('/');
    }
    await expectAccessibleInLightAndDark(page);

    // 2. The address no longer plays: the bar offers Open in Suno and the sync suggestion.
    answer = 'gone';
    await bar.getByRole('button', { name: 'Close the player' }).click();
    await generationRow(page, shortcode)
      .getByRole('button', { name: `Play ${shortcode}` })
      .click();
    await expect(bar.getByRole('alert')).toHaveText(
      `The Suno audio of ${shortcode} could not be played. Suno may have moved it: sync with Suno again to refresh its address.`,
    );
    await expect(bar.getByTestId('player-bar')).toHaveAttribute('data-status', 'error');
    const openInSuno = bar.getByRole('link', { name: 'Open in Suno (opens a new tab)' });
    await expect(openInSuno).toHaveAttribute('href', `https://suno.com/song/${SUNO_ID}`);
    await expect(openInSuno).toHaveAttribute('target', '_blank');
    await expect(bar.getByRole('button', { name: 'Retry' })).toHaveCount(0);
    await expectAccessibleInLightAndDark(page);

    // 3. The Generation's file in the media folder, scanned: Play uses it, and says so.
    await writeFile(join(media, LOCAL_WAV), toneWav(30));
    await scan(page);
    await bar.getByRole('button', { name: 'Close the player' }).click();
    await page.reload();
    await openVersionOne(page);
    const asked = toSuno.length;
    await generationRow(page, shortcode)
      .getByRole('button', { name: `Play ${shortcode}` })
      .click();
    await expect(bar.getByTestId('player-source')).toHaveText('Local file · WAV');
    await expect(bar.getByTestId('player-detail')).toHaveText(`Version 1 · ${shortcode} · WAV`);
    await playsPast(page, 0.5);
    expect(toSuno).toHaveLength(asked);
    await expectAccessibleInLightAndDark(page);
  });
});
