import { expect, test, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark } from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  currentVersion: { id: string; shortcode: string; kind: string };
}

interface VersionDetail {
  kind: string;
  lyrics: string;
  inputs: Record<string, string | number | boolean | null>;
  effectiveInputs: Record<string, string | number | boolean | null>;
}

const LYRICS = '[Verse]\nStreetlights hum\n';
const SCRIPT = 'Welcome back to the late show.';

/** The API base of the project's container, worked out on the Songs page. */
async function apiBase(page: Page): Promise<(path: string) => string> {
  await page.goto('./songs');
  const base = new URL('.', page.url());
  return (path: string) => new URL(`api/v1/${path}`, base).toString();
}

/** The autosave indicator's words. */
function saveStatus(page: Page) {
  return page.getByTestId('autosave').getByRole('status');
}

/**
 * Chooses `option` in a segmented control or a chip group: the radio button is visually hidden, so
 * the label showing it is what a user clicks. `within` narrows it to one group.
 */
async function choose(page: Page, option: string, within = page.locator('body')) {
  await within
    .locator('label')
    .filter({ hasText: new RegExp(`^${option.replace(/[#]/g, '\\$&')}$`) })
    .click();
  await expect(within.getByRole('radio', { name: option, exact: true })).toBeChecked();
}

function kind(page: Page) {
  return page.getByRole('radiogroup', { name: 'Kind' });
}

/**
 * Walks #113's Demo against the real image: a Version changed to Speech (Script, Tone, and the
 * Advanced options; no lyrics editor), a script typed, then Sound with a description, Loop, BPM
 * 120, key A, and scale Minor; back to Song finds the lyrics, back to Speech the script, and the
 * Songs table shows the Song's kind. Each kind's panel is scanned for accessibility in both colour
 * schemes.
 */
test.describe('a Speech’s and a Sound’s options', () => {
  test('replace a Song’s, keep every kind’s values, and show as the Song’s kind', async ({
    page,
  }) => {
    const api = await apiBase(page);
    const title = `Kinds ${String(Date.now())}`;
    const created = await page.request.post(api('songs'), {
      data: { title },
      headers: ANTIFORGERY_HEADERS,
    });
    expect(created.status()).toBe(201);
    const song = (await created.json()) as Song;
    expect(song.currentVersion.kind).toBe('song');
    const written = await page.request.patch(api(`versions/${song.currentVersion.id}`), {
      data: { lyrics: LYRICS },
      headers: { ...ANTIFORGERY_HEADERS, 'If-Match': '"1"' },
    });
    expect(written.status()).toBe(200);

    // 1. Change the kind to Speech: Script, Tone, and the Advanced options appear; the lyrics editor goes.
    await page.goto(`./songs/${song.shortcode}/v/1`);
    await expect(page.getByTestId('lyrics-editor')).toBeVisible();
    await expect(page.getByTestId('song-kind')).toHaveText('Kind: Song');
    await choose(page, 'Speech', kind(page));
    await expect(page.getByRole('textbox', { name: 'Script' })).toBeVisible();
    await expect(page.getByRole('textbox', { name: 'Tone' })).toBeVisible();
    await expect(page.getByRole('radiogroup', { name: 'Vocal Gender' })).toBeVisible();
    await expect(page.getByRole('switch', { name: 'Background music' })).toBeChecked();
    await expect(page.getByRole('slider', { name: 'Variety' })).toHaveAttribute(
      'aria-valuetext',
      'Normal',
    );
    await expect(page.getByTestId('lyrics-editor')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Show history' })).toHaveCount(0);

    // 2. Enter a script.
    await page.getByRole('textbox', { name: 'Script' }).fill(SCRIPT);
    await expect(saveStatus(page)).toHaveText('Saved');
    await expect(page.getByTestId('song-kind')).toHaveText('Kind: Speech');
    await expectAccessibleInLightAndDark(page);

    // ...switch to Sound, describe a sound, choose Loop, BPM 120, key A, scale Minor.
    await choose(page, 'Sound', kind(page));
    await expect(page.getByRole('textbox', { name: 'Script' })).toHaveCount(0);
    await page.getByRole('textbox', { name: 'Sound', exact: true }).fill('Vinyl crackle and rain');
    await choose(page, 'Loop');
    await page.getByRole('textbox', { name: 'BPM' }).fill('120');
    const key = page.getByRole('radiogroup', { name: 'Key', exact: true });
    await expect(page.getByRole('radiogroup', { name: 'Key scale' })).toHaveCount(0);
    await choose(page, 'A', key);
    const scale = page.getByRole('radiogroup', { name: 'Key scale' });
    await expect(scale.getByRole('radio', { name: 'None' })).toBeChecked();
    await choose(page, 'Minor', scale);
    await expect(saveStatus(page)).toHaveText('Saved');
    await expect(page.getByTestId('song-kind')).toHaveText('Kind: Sound');
    await expectAccessibleInLightAndDark(page);

    // The key grid is keyboard-operable: from A, the arrow key moves to A#.
    await key.getByRole('radio', { name: 'A', exact: true }).focus();
    await page.keyboard.press('ArrowRight');
    await expect(key.getByRole('radio', { name: 'A#' })).toBeChecked();
    await page.keyboard.press('ArrowLeft');
    await expect(key.getByRole('radio', { name: 'A', exact: true })).toBeChecked();
    await expect(saveStatus(page)).toHaveText('Saved');

    const sound = (await (
      await page.request.get(api(`versions/${song.currentVersion.id}`))
    ).json()) as VersionDetail;
    expect(sound.kind).toBe('sound');
    expect(sound.effectiveInputs).toEqual({
      kind: 'sound',
      soundsModel: 'v6',
      soundDescription: 'Vinyl crackle and rain',
      soundType: 'loop',
      soundBpm: 120,
      soundKey: 'A',
      soundScale: 'minor',
    });

    // 3. Back to Song: the earlier lyrics are there. Back to Speech: the script is there.
    await choose(page, 'Song', kind(page));
    await expect(page.getByRole('textbox', { name: 'Lyrics' })).toContainText('Streetlights hum');
    await choose(page, 'Speech', kind(page));
    await expect(page.getByRole('textbox', { name: 'Script' })).toHaveValue(SCRIPT);
    await expect(saveStatus(page)).toHaveText('Saved');

    // ...and the Songs table shows the Song's kind.
    await page.goto('./songs?sort=updated&direction=desc');
    const row = page.locator(`tr[data-song="${song.shortcode}"]`);
    await expect(row.getByRole('cell', { name: 'Speech', exact: true })).toBeVisible();
    await expect(page.getByRole('columnheader', { name: 'Kind' })).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    const speech = (await (
      await page.request.get(api(`versions/${song.currentVersion.id}`))
    ).json()) as VersionDetail;
    expect(speech.lyrics).toBe(LYRICS);
    expect(speech.inputs).toMatchObject({
      kind: 'speech',
      speechScript: SCRIPT,
      soundDescription: 'Vinyl crackle and rain',
      soundScale: 'minor',
    });
  });
});
