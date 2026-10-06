import { randomUUID } from 'node:crypto';
import { expect, test, type Locator, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { seedGeneration } from '../support/seeding.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  currentVersion: { id: string; shortcode: string };
}

/** The API base of the project's container, worked out on the Songs page. */
async function apiBase(page: Page): Promise<(path: string) => string> {
  await page.goto('./songs');
  const base = new URL('.', page.url());
  return (path: string) => new URL(`api/v1/${path}`, base).toString();
}

async function createSong(page: Page, api: (path: string) => string, title: string): Promise<Song> {
  const created = await page.request.post(api('songs'), {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(created.status()).toBe(201);
  return (await created.json()) as Song;
}

function clip(title: string): string {
  return JSON.stringify({
    id: randomUUID(),
    status: 'complete',
    title,
    metadata: { duration: 95.5 },
  });
}

function sources(page: Page): Locator {
  return page.getByTestId('sources-section');
}

/** The autosave indicator's words. */
function indicator(page: Page): Locator {
  return page.getByTestId('autosave').getByRole('status');
}

/**
 * In the open source picker, with the keyboard only: types the Song's title in the search, takes the
 * first suggestion with Enter, then moves the focus to the Generation's Choose button and presses
 * Enter.
 */
async function pickWithKeyboard(page: Page, songTitle: string, generation: string): Promise<void> {
  const dialog = page.getByRole('dialog', { name: /^Choose/ });
  await expect(dialog).toBeVisible();
  const search = dialog.getByRole('textbox', { name: 'Song', exact: true });
  await search.focus();
  await page.keyboard.type(songTitle);
  await expect(dialog.getByRole('option', { name: new RegExp(songTitle) })).toBeVisible();
  await page.keyboard.press('Enter');
  const choose = dialog.getByRole('button', { name: `Choose ${generation}` });
  await expect(choose).toBeVisible();
  await expectModalAccessibleInBothSchemes(page);
  await choose.focus();
  await page.keyboard.press('Enter');
  await expect(dialog).toBeHidden();
}

/**
 * Walks #125's Demo on the built image: on a mutable Version, Cover is chosen and a seeded Generation
 * of another Song picked (with the keyboard only); the source shows its shortcode. The action becomes
 * Mashup, which asks for a second source, and one is added. An audio file note is added: the section
 * says the audio action will be replaced, and that is confirmed. A Generation is then seeded on the
 * Version, and the section becomes read-only. Every state is scanned with the accessibility helper.
 * Runs on the project's shared container, on Songs of its own.
 */
test.describe('Choose a Version’s sources', () => {
  test('sets up a Cover, a Mashup, and an audio file note, then freezes them', async ({
    page,
  }, testInfo) => {
    const api = await apiBase(page);
    const stamp = String(Date.now());
    const original = await createSong(page, api, `Source original ${stamp}`);
    const first = await seedGeneration(
      testInfo,
      original.currentVersion.shortcode,
      clip('First take'),
    );
    const second = await seedGeneration(
      testInfo,
      original.currentVersion.shortcode,
      clip('Second take'),
    );
    const derived = await createSong(page, api, `Derived ${stamp}`);

    await page.goto(`./songs/${derived.shortcode}`);
    await expect(page.getByRole('heading', { name: 'Sources' })).toBeVisible();
    const action = sources(page).getByRole('combobox', { name: 'Action' });
    await expect(action).toBeEnabled();
    await expect(sources(page).getByTestId('pro-label')).toHaveCount(2);

    // 1. Cover, from a seeded Generation of another Song: it shows with its shortcode.
    await action.selectOption({ label: 'Cover' });
    await sources(page).getByRole('button', { name: 'Choose the Cover source' }).click();
    await pickWithKeyboard(page, `Source original ${stamp}`, first);
    const audio = sources(page).getByRole('list', { name: 'Audio sources' });
    await expect(audio.getByTestId('source-shortcode')).toHaveText(first);
    await expect(audio.getByTestId('source-title')).toHaveText('First take');
    // Inspiration is hidden while Cover is chosen.
    await expect(page.getByRole('heading', { name: 'Inspiration' })).toBeHidden();
    await expect(indicator(page)).toHaveText('Saved');
    await expectAccessibleInLightAndDark(page);

    // 2. Mashup asks for a second source; one is added.
    await action.selectOption({ label: 'Mashup' });
    await expect(sources(page).getByTestId('mashup-needs-second')).toHaveText(
      'A Mashup needs a second source.',
    );
    await expect(audio.getByTestId('source-shortcode')).toHaveText(first);
    await sources(page).getByRole('button', { name: 'Add the second source' }).click();
    await pickWithKeyboard(page, `Source original ${stamp}`, second);
    await expect(audio.getByTestId('source-shortcode')).toHaveText([first, second]);
    await expect(sources(page).getByTestId('mashup-needs-second')).toBeHidden();
    await expect(indicator(page)).toHaveText('Saved');
    await expectAccessibleInLightAndDark(page);

    // 3. An audio file note replaces the audio action, once confirmed.
    await sources(page).getByRole('button', { name: 'Add an audio file note' }).click();
    const note = page.getByRole('dialog', { name: 'Audio file note' });
    await expect(note).toBeVisible();
    await expect(note.getByTestId('audio-note-replaces')).toContainText(
      'replaces this Version’s audio action (Mashup)',
    );
    await note.getByRole('textbox', { name: 'Description' }).fill('Hummed demo, 30 seconds');
    await expectModalAccessibleInBothSchemes(page);
    await note.getByRole('button', { name: 'Replace the audio action' }).click();
    await expect(note).toBeHidden();
    await expect(sources(page).getByTestId('file-note-description')).toHaveText(
      'Hummed demo, 30 seconds',
    );
    await expect(audio).toBeHidden();
    await expect(action).toHaveValue('');
    await expect(sources(page).getByText(/attach it by hand in Suno/)).toBeVisible();
    await expect(indicator(page)).toHaveText('Saved');
    await expectAccessibleInLightAndDark(page);

    // The note was stored: reading the Version again shows it.
    await page.reload();
    await expect(sources(page).getByTestId('file-note-description')).toHaveText(
      'Hummed demo, 30 seconds',
    );

    // 4. A Generation seeded on the Version freezes it: the section becomes read-only.
    await seedGeneration(testInfo, derived.currentVersion.shortcode);
    await page.reload();
    await expect(sources(page).getByTestId('sources-frozen')).toBeVisible();
    await expect(sources(page).getByTestId('file-note-description')).toHaveText(
      'Hummed demo, 30 seconds',
    );
    await expect(sources(page).getByRole('button')).toHaveCount(0);
    await expect(sources(page).getByRole('combobox')).toHaveCount(0);
    await expectAccessibleInLightAndDark(page);
  });
});
