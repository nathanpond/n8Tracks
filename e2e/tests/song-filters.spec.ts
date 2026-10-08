import { expect, test, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark } from '../support/a11y.ts';
import { seedGeneration } from '../support/seeding.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  revision: number;
  currentVersion: { shortcode: string };
}

const WRITING = '01a10a6e-dc81-7001-8000-000000000002';
const IDEA = '01a10a6e-dc80-7000-8000-000000000001';

async function apiBase(page: Page): Promise<(path: string) => string> {
  await page.goto('./songs');
  const base = new URL('.', page.url());
  return (path: string) => new URL(`api/v1/${path}`, base).toString();
}

async function readSong(page: Page, api: (path: string) => string, id: string): Promise<Song> {
  const answer = await page.request.get(api(`songs/${id}`));
  expect(answer.ok()).toBe(true);
  return (await answer.json()) as Song;
}

/** Creates a Song in `stateId` with the Tag `tagId`. */
async function createSong(
  page: Page,
  api: (path: string) => string,
  title: string,
  stateId: string,
  tagId: string,
): Promise<Song> {
  const created = await page.request.post(api('songs'), {
    data: { title },
    headers: ANTIFORGERY_HEADERS,
  });
  expect(created.status(), await created.text()).toBe(201);
  const song = (await created.json()) as Song;
  const edited = await page.request.patch(api(`songs/${song.id}`), {
    data: { stateId, tagIds: [tagId] },
    headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(song.revision)}"` },
  });
  expect(edited.status(), await edited.text()).toBe(200);
  return (await edited.json()) as Song;
}

function songRow(page: Page, shortcode: string) {
  return page
    .getByRole('table', { name: 'Songs' })
    .getByRole('row')
    .filter({ has: page.getByRole('rowheader', { name: shortcode, exact: true }) });
}

function chips(page: Page) {
  return page.getByTestId('active-filters').getByRole('listitem');
}

/**
 * Walks #225's Demo on Songs of its own, told apart by a Tag made for this run: state Writing and
 * the Tag narrow the list and show two chips; "has no Selected Generation" narrows it further and
 * survives a reload; a search drops the total again; Clear all returns the full list.
 */
test.describe('song filters', () => {
  test('narrows the Songs table by combined filters kept in the address', async ({
    page,
  }, testInfo) => {
    test.setTimeout(90_000);
    const stamp = Date.now().toString(36);
    const api = await apiBase(page);
    const tagged = await page.request.post(api('tags'), {
      data: { name: `demo ${stamp}` },
      headers: ANTIFORGERY_HEADERS,
    });
    expect([200, 201]).toContain(tagged.status());
    const tag = (await tagged.json()) as { id: string; name: string };

    const lantern = await createSong(page, api, `Lantern ${stamp}`, WRITING, tag.id);
    const harbour = await createSong(page, api, `Harbour ${stamp}`, WRITING, tag.id);
    const idea = await createSong(page, api, `Idea ${stamp}`, IDEA, tag.id);
    const generation = await seedGeneration(testInfo, harbour.currentVersion.shortcode);
    const current = await readSong(page, api, harbour.id);
    const selected = await page.request.put(api(`songs/${harbour.id}/selected-generation`), {
      headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(current.revision)}"` },
      data: { generation },
    });
    expect(selected.status(), await selected.text()).toBe(200);

    // 1. State Writing and the Tag: the list narrows and two chips appear.
    await page.goto('./songs');
    const bar = page.getByRole('group', { name: 'Filters' });
    await bar
      .getByRole('group', { name: 'Workflow state' })
      .getByText('Writing', { exact: true })
      .click();
    await bar.getByRole('combobox', { name: 'Tag' }).fill(tag.name);
    await bar.getByRole('option', { name: tag.name }).click();
    await expect(chips(page)).toHaveText(['State: Writing', `Tag: ${tag.name}`]);
    await expect(songRow(page, lantern.shortcode)).toBeVisible();
    await expect(songRow(page, harbour.shortcode)).toBeVisible();
    await expect(songRow(page, idea.shortcode)).toHaveCount(0);
    await expect(page.getByTestId('songs-total')).toHaveText('2 Songs');
    await expectAccessibleInLightAndDark(page);

    // 2. Has no Selected Generation: narrower; a reload keeps all three filters.
    await bar.getByRole('combobox', { name: 'Selected Generation' }).click();
    await bar.getByRole('option', { name: 'Has no Selected Generation' }).click();
    await expect(page.getByTestId('songs-total')).toHaveText('1 Song');
    await expect(songRow(page, harbour.shortcode)).toHaveCount(0);
    await expect(page).toHaveURL(/selected=no/);
    await page.reload();
    await expect(chips(page)).toHaveText([
      'State: Writing',
      `Tag: ${tag.name}`,
      'Has no Selected Generation',
    ]);
    await expect(songRow(page, lantern.shortcode)).toBeVisible();
    await expect(page.getByTestId('songs-total')).toHaveText('1 Song');

    // 3. A search on top: the total drops again; Clear all returns the full list.
    await page
      .getByRole('search', { name: 'Songs table search' })
      .getByRole('searchbox')
      .fill(`Harbour ${stamp}`);
    await expect(page.getByTestId('no-search-results')).toBeVisible();
    await page.getByTestId('active-filters').getByRole('button', { name: 'Clear all' }).click();
    await expect(page).toHaveURL(/\/songs$/);
    await expect(chips(page)).toHaveCount(0);
    await expect(songRow(page, idea.shortcode)).toBeVisible();
    await expectAccessibleInLightAndDark(page);
  });
});
