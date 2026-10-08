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

const SELECTED_ID = '3a5c7e9b-1d2f-4a6b-8c0d-2e4f6a8b0c1d';
const OPEN_FIRST_ID = '4b6d8f0a-2c3e-4b7c-9d1e-3f5a7b9c1d2e';
const OPEN_SECOND_ID = '5c7e9a1b-3d4f-4c8d-8e2f-4a6b8c0d2e3f';
const MASTER = 'Masters/Harbour Lights master.wav';

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

interface SongRecord {
  id: string;
  shortcode: string;
  revision: number;
  currentVersion: { shortcode: string };
  selectedGeneration: { shortcode: string } | null;
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

async function createSong(page: Page, title: string): Promise<SongRecord> {
  const created = await page.request.post(`${FRESH_URL}api/v1/songs`, {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(created.status()).toBe(201);
  return (await created.json()) as SongRecord;
}

async function readSong(page: Page, id: string): Promise<SongRecord> {
  const response = await page.request.get(`${FRESH_URL}api/v1/songs/${id}`);
  expect(response.status()).toBe(200);
  return (await response.json()) as SongRecord;
}

/** Writes with the record's revision, and expects it to be accepted. */
async function put(page: Page, path: string, revision: number, data: unknown): Promise<void> {
  const response = await page.request.put(`${FRESH_URL}api/v1/${path}`, {
    headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(revision)}"` },
    data,
  });
  expect(response.status(), await response.text()).toBe(200);
}

/** The cataloged file at `path`. */
async function cataloged(page: Page, path: string): Promise<{ id: string; revision: number }> {
  const response = await page.request.get(`${FRESH_URL}api/v1/audio-files`);
  const { items } = (await response.json()) as {
    items: { id: string; path: string; revision: number }[];
  };
  const file = items.find((item) => item.path === path);
  if (file === undefined) {
    throw new Error(`${path} was not cataloged.`);
  }
  return file;
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

/** The audio element's source. */
function source(page: Page): Promise<string | null> {
  return page.locator('audio[data-testid="player-audio"]').getAttribute('src');
}

/**
 * Walks #219's Demo on a container of its own, whose read-only media folder holds generated WAVs:
 * (1) Play in the header of a Song with a Selected Generation plays that Generation's file and the
 * bar names the rule; (2) on a Song with Generations but none selected, Play opens the chooser, which
 * traps focus, lists the Generations, plays the pick, gives focus back to Play, and leaves the Song
 * with no selection; (3) a Song-level preferred file plays for its Song. A Song with nothing to offer
 * shows Play disabled in the Songs table. Each state is scanned with axe in light and dark.
 */
test.describe('Play a Song', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-song-play-'));
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

  test('plays the Song’s choice, or asks which Generation, and never selects one by itself', async ({
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

    const chosen = await createSong(page, 'Night Drive');
    const open = await createSong(page, 'Open Road');
    const harbour = await createSong(page, 'Harbour Lights');
    await createSong(page, 'Blank Page');
    const selected = await seedGenerationIn(
      FRESH_NAME,
      chosen.currentVersion.shortcode,
      JSON.stringify({ id: SELECTED_ID, status: 'complete', title: 'Night Drive' }),
    );
    const openFirst = await seedGenerationIn(
      FRESH_NAME,
      open.currentVersion.shortcode,
      JSON.stringify({ id: OPEN_FIRST_ID, status: 'complete', title: 'Open Road' }),
    );
    const openSecond = await seedGenerationIn(
      FRESH_NAME,
      open.currentVersion.shortcode,
      JSON.stringify({ id: OPEN_SECOND_ID, status: 'complete', title: 'Open Road, take two' }),
    );
    await writeFile(join(media, `Night Drive (suno-${SELECTED_ID}).wav`), toneWav(90));
    await writeFile(join(media, `Open Road (suno-${OPEN_FIRST_ID}).wav`), toneWav(90));
    await writeFile(join(media, `Open Road two (suno-${OPEN_SECOND_ID}).wav`), toneWav(90));
    await mkdir(join(media, 'Masters'));
    await writeFile(join(media, MASTER), toneWav(90));
    await scan(page);

    await put(page, `songs/${chosen.id}/selected-generation`, chosen.revision, {
      generation: selected,
    });
    const master = await cataloged(page, MASTER);
    await put(page, `audio-files/${master.id}/association`, master.revision, {
      song: harbour.shortcode,
    });
    await put(
      page,
      `songs/${harbour.id}/preferred-audio-file`,
      (await readSong(page, harbour.id)).revision,
      { audioFile: master.id },
    );

    // The Songs table: a Song with nothing to offer shows Play disabled, with the reason.
    await page.goto(`${FRESH_URL}songs`);
    const blank = page.getByRole('button', { name: 'Play Blank Page' });
    await expect(blank).toHaveAttribute('aria-disabled', 'true');
    await expect(blank).toHaveAccessibleDescription(
      'Nothing to play: this Song has no Generations and no audio files.',
    );
    await expect(page.getByRole('button', { name: 'Play Night Drive', exact: true })).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // 1. A Song with a Selected Generation: Play in the header plays it, and the bar says so.
    await page.goto(`${FRESH_URL}songs/${chosen.shortcode}`);
    await page.getByTestId('song-play-button').click();
    const bar = page.getByRole('contentinfo', { name: 'Player' });
    await expect(bar.getByTestId('player-detail')).toHaveText(`Version 1 · ${selected} · WAV`);
    await expect(bar.getByTestId('player-via')).toHaveText(
      'The Song’s choice: its Selected Generation',
    );
    await expect(
      page.getByRole('button', { name: 'Pause Night Drive', exact: true }),
    ).toBeVisible();
    await playsPast(page, 0.5);
    await expectAccessibleInLightAndDark(page);

    // 2. A Song with Generations but none selected: Play asks.
    await page.goto(`${FRESH_URL}songs/${open.shortcode}`);
    const play = page.getByTestId('song-play-button');
    await play.click();
    const dialog = page.getByRole('dialog', { name: 'Play Open Road' });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole('radio')).toHaveCount(2);
    await expect(dialog.getByRole('radio', { name: new RegExp(openFirst) })).toBeEnabled();
    await expect(dialog.getByRole('checkbox')).toHaveCount(0);
    await expectModalAccessibleInBothSchemes(page);

    // Focus stays in the dialog however far the user tabs.
    for (let press = 0; press < 8; press++) {
      await page.keyboard.press('Tab');
      expect(
        await page.evaluate(() => document.activeElement?.closest('[role="dialog"]') !== null),
      ).toBe(true);
    }

    await dialog.getByRole('radio', { name: new RegExp(openSecond) }).check();
    await expect(
      dialog.getByRole('checkbox', { name: 'Make this the Selected Generation' }),
    ).not.toBeChecked();
    await dialog.getByRole('button', { name: 'Play', exact: true }).click();
    await expect(dialog).toHaveCount(0);
    await expect(play).toBeFocused();
    await expect(bar.getByTestId('player-detail')).toHaveText(`Version 1 · ${openSecond} · WAV`);
    await expect(bar.getByTestId('player-via')).toHaveText('Chosen for this listen');
    await playsPast(page, 0.3);
    await expectAccessibleInLightAndDark(page);
    // The Song still has no Selected Generation.
    expect((await readSong(page, open.id)).selectedGeneration).toBeNull();
    await expect(page.getByTestId('song-selected-generation')).toHaveText(
      'Selected Generation:None',
    );

    // 3. A Song-level preferred file plays for its Song.
    await page.goto(`${FRESH_URL}songs/${harbour.shortcode}`);
    await page.getByTestId('song-play-button').click();
    await expect(bar.getByTestId('player-detail')).toHaveText(
      'Song-level file · Harbour Lights master.wav',
    );
    await expect(bar.getByTestId('player-via')).toHaveText(
      'The Song’s choice: its Song-level file',
    );
    expect(await source(page)).toContain(`/api/v1/audio-files/${master.id}/content`);
    await playsPast(page, 0.3);
    await expectAccessibleInLightAndDark(page);
  });
});
