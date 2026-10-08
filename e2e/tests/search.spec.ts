import { expect, test, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark } from '../support/a11y.ts';
import { seedGeneration } from '../support/seeding.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  revision: number;
  currentVersion: { id: string; shortcode: string };
}

async function apiBase(page: Page): Promise<(path: string) => string> {
  await page.goto('./songs');
  const base = new URL('.', page.url());
  return (path: string) => new URL(`api/v1/${path}`, base).toString();
}

async function createSong(
  page: Page,
  api: (path: string) => string,
  title: string,
  concept: string,
  lyrics?: string,
): Promise<Song> {
  const created = await page.request.post(api('songs'), {
    data: { title, concept },
    headers: ANTIFORGERY_HEADERS,
  });
  expect(created.status(), await created.text()).toBe(201);
  const song = (await created.json()) as Song;
  if (lyrics !== undefined) {
    const written = await page.request.patch(api(`versions/${song.currentVersion.id}`), {
      data: { lyrics },
      headers: { ...ANTIFORGERY_HEADERS, 'If-Match': '"1"' },
    });
    expect(written.status(), await written.text()).toBe(200);
  }
  return song;
}

function headerSearch(page: Page) {
  return page.getByRole('banner').getByRole('textbox', { name: 'Search Songs' });
}

function tableSearch(page: Page) {
  return page.getByRole('search', { name: 'Songs table search' }).getByRole('searchbox');
}

function songRow(page: Page, shortcode: string) {
  return page
    .getByRole('table', { name: 'Songs' })
    .getByRole('row')
    .filter({ has: page.getByRole('rowheader', { name: shortcode, exact: true }) });
}

function matchesOf(page: Page, shortcode: string) {
  return page.getByRole('list', { name: `Where ${shortcode} matched` });
}

/**
 * Walks #224's Demo on Songs of its own, told apart by a word made for this run: a header search
 * from Settings opens the Songs table with the lyric match highlighted; a title two Songs share
 * lists both, told apart by Concept, creation date, and Selected Generation; the lyric match opens
 * the Song on its Version, and Back shows the results as they were. Also the empty state, and the
 * shortcut, which focuses the search box but types into the editor when focus is there.
 */
test.describe('search', () => {
  test('finds Songs from any page and shows where each matched', async ({ page }, testInfo) => {
    test.setTimeout(90_000);
    const stamp = Date.now().toString(36);
    const word = `lumen${stamp}`;
    const twin = `Twin ${stamp}`;
    const api = await apiBase(page);
    const lyric = await createSong(
      page,
      api,
      `Harbour ${stamp}`,
      'A night crossing',
      `the ${word} glows`,
    );
    const first = await createSong(page, api, twin, 'First take');
    const second = await createSong(page, api, twin, 'Second take');
    const generation = await seedGeneration(testInfo, first.currentVersion.shortcode);
    const current = (await (await page.request.get(api(`songs/${first.id}`))).json()) as Song;
    const selected = await page.request.put(api(`songs/${first.id}/selected-generation`), {
      headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(current.revision)}"` },
      data: { generation },
    });
    expect(selected.status(), await selected.text()).toBe(200);

    // 1. From Settings, a word from one Song's lyrics in the header search, then Enter.
    await page.goto('./settings/account');
    await headerSearch(page).fill(word);
    await headerSearch(page).press('Enter');
    await expect(page).toHaveURL(new RegExp(`/songs\\?search=${word}$`));
    await expect(songRow(page, lyric.shortcode)).toBeVisible();
    await expect(page.getByTestId('songs-total')).toHaveText(`1 Song matches “${word}”`);
    const lyricMatch = matchesOf(page, lyric.shortcode).getByRole('listitem');
    await expect(lyricMatch).toHaveCount(1);
    await expect(lyricMatch.getByRole('link', { name: 'Lyrics v1' })).toBeVisible();
    await expect(lyricMatch.getByTestId('match-highlight')).toHaveText(word);
    // The highlight is bold as well as coloured.
    await expect(lyricMatch.getByTestId('match-highlight')).toHaveCSS('font-weight', '700');
    await expect(tableSearch(page)).toHaveValue(word);
    await expect(headerSearch(page)).toHaveValue(word);
    await expectAccessibleInLightAndDark(page);

    // 3. The lyric match opens the Song on that Version.
    await lyricMatch.getByRole('link', { name: 'Lyrics v1' }).click();
    await expect(page).toHaveURL(new RegExp(`/songs/${lyric.shortcode}/v/1$`));
    await expect(page.getByTestId('shortcode')).toHaveText(lyric.shortcode);

    // 4. Back: the results as they were.
    await page.goBack();
    await expect(page).toHaveURL(new RegExp(`/songs\\?search=${word}$`));
    await expect(matchesOf(page, lyric.shortcode).getByTestId('match-highlight')).toHaveText(word);
    await expect(tableSearch(page)).toHaveValue(word);

    // 2. A title two Songs share: both, told apart by Concept, creation date, and Selected Generation.
    await headerSearch(page).fill(`"${twin}"`);
    await headerSearch(page).press('Enter');
    const firstRow = songRow(page, first.shortcode);
    const secondRow = songRow(page, second.shortcode);
    await expect(firstRow).toBeVisible();
    await expect(secondRow).toBeVisible();
    await expect(page.getByTestId('songs-total')).toHaveText(`2 Songs match “"${twin}"”`);
    await expect(firstRow.getByText('First take')).toBeVisible();
    await expect(secondRow.getByText('Second take')).toBeVisible();
    await expect(firstRow.getByTestId('song-selected')).toHaveText(generation);
    await expect(secondRow.getByTestId('song-selected')).toHaveText('None');
    await expect(firstRow.getByTestId('song-created')).not.toHaveText('');
    await expect(secondRow.getByTestId('song-created')).not.toHaveText('');
    await expect(
      matchesOf(page, first.shortcode).getByRole('link', { name: 'Title' }),
    ).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // Nothing found: said so, with the way back to every Song.
    await tableSearch(page).fill(`nothing${stamp}`);
    await tableSearch(page).press('Enter');
    await expect(page.getByTestId('no-search-results')).toHaveText(
      `No Songs match “nothing${stamp}”.`,
    );
    await expectAccessibleInLightAndDark(page);
    await page.getByRole('button', { name: 'Clear the search', exact: true }).click();
    await expect(page).toHaveURL(/\/songs$/);
    await expect(page.getByTestId('song-matches')).toHaveCount(0);
    await expect(tableSearch(page)).toHaveValue('');

    // The shortcut: in the lyrics editor `/` is typed; elsewhere it focuses the search box.
    await page.goto(`./songs/${lyric.shortcode}/v/1`);
    const editor = page.getByRole('textbox', { name: 'Lyrics' });
    await expect(editor).toBeVisible();
    await editor.click();
    await page.keyboard.press('End');
    await page.keyboard.press('/');
    await expect(editor).toBeFocused();
    await expect(editor).toContainText(`the ${word} glows/`);
    await expect(headerSearch(page)).not.toBeFocused();
    await page.getByTestId('shortcode').click();
    await page.keyboard.press('/');
    await expect(headerSearch(page)).toBeFocused();
  });
});
