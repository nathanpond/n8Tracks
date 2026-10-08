import { randomUUID } from 'node:crypto';
import { expect, test, type Locator, type Page, type TestInfo } from '@playwright/test';
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

interface RelationshipType {
  id: string;
  system: boolean;
  sunoAction: string | null;
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

/** What every test starts from: a Song with two seeded Generations, and a Song of its own to derive. */
interface Fixture {
  api: (path: string) => string;
  originalTitle: string;
  first: string;
  second: string;
  derived: Song;
}

async function setUp(page: Page, testInfo: TestInfo): Promise<Fixture> {
  const api = await apiBase(page);
  const stamp = `${String(Date.now())} ${String(testInfo.retry)}`;
  const originalTitle = `Source original ${stamp}`;
  const original = await createSong(page, api, originalTitle);
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
  return { api, originalTitle, first, second, derived };
}

/**
 * Gives the derived Version the sources the page would have saved by then: the same PATCH the
 * Sources section sends (the system relationship type of `action`, then each Generation by ID).
 */
async function saveSources(
  page: Page,
  fixture: Fixture,
  action: 'cover' | 'mashup',
  generations: string[],
): Promise<void> {
  const { api, derived } = fixture;
  const { items } = (await (await page.request.get(api('relationship-types'))).json()) as {
    items: RelationshipType[];
  };
  const type = items.find((candidate) => candidate.system && candidate.sunoAction === action);
  expect(type, `the system ${action} relationship type`).toBeDefined();
  const ids = await Promise.all(
    generations.map(
      async (shortcode) =>
        ((await (await page.request.get(api(`generations/${shortcode}`))).json()) as { id: string })
          .id,
    ),
  );
  const version = (await (
    await page.request.get(api(`versions/${derived.currentVersion.id}`))
  ).json()) as { revision: number };
  const saved = await page.request.patch(api(`versions/${derived.currentVersion.id}`), {
    headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(version.revision)}"` },
    data: {
      inputs: {
        sources: ids.map((generation) => ({
          typeId: type?.id ?? '',
          generation,
          continueAtSeconds: null,
          secondaryIds: null,
        })),
      },
    },
  });
  expect(saved.status(), await saved.text()).toBe(200);
}

/** Opens the derived Song, waiting for its Sources section to be editable. */
async function openDerived(page: Page, fixture: Fixture): Promise<Locator> {
  await page.goto(`./songs/${fixture.derived.shortcode}`);
  await expect(page.getByRole('heading', { name: 'Sources' })).toBeVisible();
  const action = sources(page).getByRole('combobox', { name: 'Action' });
  await expect(action).toBeEnabled();
  return action;
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
 * Walks #125's Demo on the built image, on Songs of their own on the project's shared container:
 * 1. On a mutable Version, Cover is chosen and a seeded Generation of another Song picked (with the
 *    keyboard only); the source shows its shortcode.
 * 2. The action becomes Mashup, which asks for a second source, and one is added.
 * 3. An audio file note is added: the section says the audio action will be replaced, and that is
 *    confirmed.
 * 4. A Generation is then seeded on the Version, and the section becomes read-only.
 * Every state is scanned with the accessibility helper, in light and in dark. The four steps are
 * three tests, so each stays well inside the test timeout on a slow CI runner (as one, it ran out
 * there; #406). A test that starts after step 1 or 2 reaches that state through the PATCH the
 * Sources section itself sends.
 */
test.describe('Choose a Version’s sources', () => {
  test('sets up a Cover from another Song’s Generation, by keyboard', async ({
    page,
  }, testInfo) => {
    const fixture = await setUp(page, testInfo);
    const action = await openDerived(page, fixture);
    await expect(sources(page).getByTestId('pro-label')).toHaveCount(2);

    // 1. Cover, from a seeded Generation of another Song: it shows with its shortcode.
    await action.selectOption({ label: 'Cover' });
    await sources(page).getByRole('button', { name: 'Choose the Cover source' }).click();
    await pickWithKeyboard(page, fixture.originalTitle, fixture.first);
    const audio = sources(page).getByRole('list', { name: 'Audio sources' });
    await expect(audio.getByTestId('source-shortcode')).toHaveText(fixture.first);
    await expect(audio.getByTestId('source-title')).toHaveText('First take');
    // Inspiration is hidden while Cover is chosen.
    await expect(page.getByRole('heading', { name: 'Inspiration' })).toBeHidden();
    await expect(indicator(page)).toHaveText('Saved');
    await expectAccessibleInLightAndDark(page);
  });

  test('turns a Cover into a Mashup, which asks for a second source', async ({
    page,
  }, testInfo) => {
    const fixture = await setUp(page, testInfo);
    // Where step 1 ends: a Cover of the first Generation.
    await saveSources(page, fixture, 'cover', [fixture.first]);
    const action = await openDerived(page, fixture);
    const audio = sources(page).getByRole('list', { name: 'Audio sources' });
    await expect(audio.getByTestId('source-shortcode')).toHaveText(fixture.first);

    // 2. Mashup asks for a second source; one is added.
    await action.selectOption({ label: 'Mashup' });
    await expect(sources(page).getByTestId('mashup-needs-second')).toHaveText(
      'A Mashup needs a second source.',
    );
    await expect(audio.getByTestId('source-shortcode')).toHaveText(fixture.first);
    await sources(page).getByRole('button', { name: 'Add the second source' }).click();
    await pickWithKeyboard(page, fixture.originalTitle, fixture.second);
    await expect(audio.getByTestId('source-shortcode')).toHaveText([fixture.first, fixture.second]);
    await expect(sources(page).getByTestId('mashup-needs-second')).toBeHidden();
    await expect(indicator(page)).toHaveText('Saved');
    await expectAccessibleInLightAndDark(page);
  });

  test('replaces the Mashup with an audio file note, then freezes it', async ({
    page,
  }, testInfo) => {
    const fixture = await setUp(page, testInfo);
    // Where step 2 ends: a Mashup of both Generations.
    await saveSources(page, fixture, 'mashup', [fixture.first, fixture.second]);
    const action = await openDerived(page, fixture);
    const audio = sources(page).getByRole('list', { name: 'Audio sources' });
    await expect(audio.getByTestId('source-shortcode')).toHaveText([fixture.first, fixture.second]);

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
    await seedGeneration(testInfo, fixture.derived.currentVersion.shortcode);
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
