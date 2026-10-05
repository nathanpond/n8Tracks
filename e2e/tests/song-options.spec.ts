import { expect, test, type Locator, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  currentVersion: { id: string; shortcode: string };
}

interface VersionDetail {
  id: string;
  number: string;
  current: boolean;
  lyrics: string;
  inputs: Record<string, string | number | boolean | null>;
}

const LYRICS = '[Verse]\nHeadlights on the hill\n';

/** The options Demo step 1 sets, as the API stores them. */
const SET = {
  songMode: 'advanced',
  weirdness: 80,
  vocalGender: 'female',
  durationMode: 'custom',
  durationSeconds: 120,
  variety: 'high',
};

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
 * Chooses `option` in a segmented control: its radio button is visually hidden, so the label
 * showing it is what a user clicks.
 */
async function choose(page: Page, option: string) {
  await page
    .locator('label')
    .filter({ hasText: new RegExp(`^${option}$`) })
    .click();
  await expect(page.getByRole('radio', { name: option })).toBeChecked();
}

/** Opens More Options unless it is open already (it stays open while the page is). */
async function openMoreOptions(page: Page) {
  const more = page.getByRole('button', { name: 'More Options' });
  if ((await more.getAttribute('aria-expanded')) !== 'true') {
    await more.click();
  }
  await expect(more).toHaveAttribute('aria-expanded', 'true');
}

/** Moves a slider with the keyboard until it reads `target`, one arrow press at a time. */
async function slideTo(page: Page, slider: Locator, target: number) {
  await slider.focus();
  const now = Number(await slider.getAttribute('aria-valuenow'));
  const key = target > now ? 'ArrowRight' : 'ArrowLeft';
  for (let i = 0; i < Math.abs(target - now); i++) {
    await page.keyboard.press(key);
  }
  await expect(slider).toHaveAttribute('aria-valuenow', String(target));
}

/**
 * Walks #112's Demo against the real image: a Song's options set in Advanced mode's More Options,
 * a Simple prompt with the lyrics section added (Advanced's lyrics), the Advanced values found
 * again on switching back and after a reload, and a new Version created from it with the options
 * copied. Each mode is scanned for accessibility in both colour schemes.
 */
test.describe('a Song’s options', () => {
  test('are set in Advanced and Simple modes, kept across both, and copied to a new Version', async ({
    page,
  }) => {
    const api = await apiBase(page);
    const created = await page.request.post(api('songs'), {
      data: { title: `Options ${String(Date.now())}` },
      headers: ANTIFORGERY_HEADERS,
    });
    expect(created.status()).toBe(201);
    const song = (await created.json()) as Song;
    const written = await page.request.patch(api(`versions/${song.currentVersion.id}`), {
      data: { lyrics: LYRICS },
      headers: { ...ANTIFORGERY_HEADERS, 'If-Match': '"1"' },
    });
    expect(written.status()).toBe(200);

    // 1. A Song in Advanced mode. Open More Options and set Weirdness, Vocal Gender, Duration, Variety.
    await page.goto(`./songs/${song.shortcode}/v/1`);
    await expect(
      page.getByRole('radiogroup', { name: 'Kind' }).getByRole('radio', { name: 'Song' }),
    ).toBeChecked();
    await expect(page.getByRole('radio', { name: 'Advanced' })).toBeChecked();
    await openMoreOptions(page);

    const weirdness = page.getByRole('slider', { name: 'Weirdness' });
    await expect(page.getByText('Turn it up for wild, unexpected results')).toBeVisible();
    await slideTo(page, weirdness, 80);
    await expect(weirdness).toHaveAttribute('aria-valuetext', '80 percent');
    await choose(page, 'Female');
    await choose(page, 'Custom');
    const duration = page.getByRole('slider', { name: 'Custom duration' });
    await slideTo(page, duration, 120);
    await expect(duration).toHaveAttribute('aria-valuetext', '2 minutes');
    const variety = page.getByRole('slider', { name: 'Variety' });
    await variety.focus();
    await page.keyboard.press('ArrowRight');
    await expect(variety).toHaveAttribute('aria-valuetext', 'High');
    await expect(saveStatus(page)).toHaveText('Saved');
    await expectAccessibleInLightAndDark(page);

    // 2. Switch to Simple and type a prompt; add the lyrics section: Advanced's lyrics are there.
    await choose(page, 'Simple');
    await expect(page.getByRole('button', { name: 'More Options' })).toHaveCount(0);
    await expect(page.getByRole('textbox', { name: 'Lyrics' })).toHaveCount(0);
    await page.getByRole('textbox', { name: 'Song description' }).fill('A night drive home');
    await page.getByRole('button', { name: 'Add lyrics' }).click();
    await expect(page.getByRole('textbox', { name: 'Lyrics' })).toContainText(
      'Headlights on the hill',
    );
    await expect(saveStatus(page)).toHaveText('Saved');
    await expectAccessibleInLightAndDark(page);

    // 3. Back to Advanced: Weirdness is still 80. Reload: everything is as set.
    await choose(page, 'Advanced');
    await openMoreOptions(page);
    await expect(page.getByRole('slider', { name: 'Weirdness' })).toHaveAttribute(
      'aria-valuenow',
      '80',
    );
    await expect(saveStatus(page)).toHaveText('Saved');
    await page.reload();
    await expect(page.getByRole('radio', { name: 'Advanced' })).toBeChecked();
    await openMoreOptions(page);
    await expect(page.getByRole('slider', { name: 'Weirdness' })).toHaveAttribute(
      'aria-valuenow',
      '80',
    );
    await expect(page.getByRole('radio', { name: 'Female' })).toBeChecked();
    await expect(page.getByRole('slider', { name: 'Custom duration' })).toHaveAttribute(
      'aria-valuenow',
      '120',
    );
    await expect(page.getByRole('slider', { name: 'Variety' })).toHaveAttribute(
      'aria-valuetext',
      'High',
    );
    await choose(page, 'Simple');
    await expect(page.getByRole('textbox', { name: 'Song description' })).toHaveValue(
      'A night drive home',
    );
    await expect(page.getByRole('textbox', { name: 'Lyrics' })).toContainText(
      'Headlights on the hill',
    );
    await choose(page, 'Advanced');
    await expect(saveStatus(page)).toHaveText('Saved');

    const one = (await (
      await page.request.get(api(`versions/${song.currentVersion.id}`))
    ).json()) as VersionDetail;
    expect(one.inputs).toMatchObject({
      ...SET,
      simplePrompt: 'A night drive home',
      simpleLyricsAdded: true,
    });
    expect(one.lyrics).toBe(LYRICS);

    // 4. Create a new Version from it: the options are copied.
    await page
      .getByRole('button', { name: 'Create New Version From 1', exact: true })
      .first()
      .click();
    const branching = page.getByRole('dialog', { name: 'Create New Version From 1' });
    await expect(branching.getByRole('radio', { name: '2 (proposed)' })).toBeChecked();
    await expectModalAccessibleInBothSchemes(page);
    await branching.getByRole('button', { name: 'Create Version' }).click();
    await expect(page.getByRole('heading', { name: 'Version 2' })).toBeVisible();
    await openMoreOptions(page);
    await expect(page.getByRole('slider', { name: 'Weirdness' })).toHaveAttribute(
      'aria-valuenow',
      '80',
    );
    await expect(page.getByRole('radio', { name: 'Female' })).toBeChecked();

    const versions = (await (
      await page.request.get(api(`songs/${song.shortcode}/versions`))
    ).json()) as { items: VersionDetail[] };
    const two = versions.items.find((item) => item.number === '2');
    expect(two?.current).toBe(true);
    const twoDetail = (await (
      await page.request.get(api(`versions/${two?.id ?? ''}`))
    ).json()) as VersionDetail;
    expect(twoDetail.inputs).toEqual(one.inputs);
  });
});
