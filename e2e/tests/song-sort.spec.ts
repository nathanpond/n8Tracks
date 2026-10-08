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

async function apiBase(page: Page): Promise<(path: string) => string> {
  await page.goto('./songs');
  const base = new URL('.', page.url());
  return (path: string) => new URL(`api/v1/${path}`, base).toString();
}

/** Creates a Song titled `title` with the Tag `tagId`, so the run's own Songs can be listed alone. */
async function createSong(
  page: Page,
  api: (path: string) => string,
  title: string,
  tagId: string,
): Promise<Song> {
  const created = await page.request.post(api('songs'), {
    data: { title },
    headers: ANTIFORGERY_HEADERS,
  });
  expect(created.status(), await created.text()).toBe(201);
  const song = (await created.json()) as Song;
  const edited = await page.request.patch(api(`songs/${song.id}`), {
    data: { tagIds: [tagId] },
    headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(song.revision)}"` },
  });
  expect(edited.status(), await edited.text()).toBe(200);
  return (await edited.json()) as Song;
}

function rowHeaders(page: Page) {
  return page.getByRole('table', { name: 'Songs' }).getByRole('rowheader');
}

function header(page: Page, name: string) {
  return page
    .getByRole('table', { name: 'Songs' })
    .getByRole('columnheader', { name: new RegExp(`^${name}`) });
}

function sortControl(page: Page) {
  return page.getByRole('group', { name: 'Sort' });
}

/**
 * Walks #226's Demo on three Songs of its own, told apart by a Tag made for this run: Bravo and
 * Charlie have a Generation (Bravo's newer, and rated), alpha has none. Title descending shows the
 * arrow; last Generation date puts alpha last both ways; a search orders by Relevance until
 * Updated is picked, which a reload keeps.
 */
test.describe('song sort', () => {
  test('sorts the Songs table from its headers and the Sort control, kept in the address', async ({
    page,
  }, testInfo) => {
    test.setTimeout(90_000);
    const stamp = `sortrun${Date.now().toString(36)}`;
    const api = await apiBase(page);
    const tagged = await page.request.post(api('tags'), {
      data: { name: `sort ${stamp}` },
      headers: ANTIFORGERY_HEADERS,
    });
    expect([200, 201]).toContain(tagged.status());
    const tag = (await tagged.json()) as { id: string };

    const alpha = await createSong(page, api, `alpha ${stamp}`, tag.id);
    const bravo = await createSong(page, api, `Bravo ${stamp}`, tag.id);
    const charlie = await createSong(page, api, `Charlie ${stamp}`, tag.id);
    await seedGeneration(testInfo, charlie.currentVersion.shortcode);
    const rated = await seedGeneration(testInfo, bravo.currentVersion.shortcode);
    const rating = await page.request.patch(api(`generations/${rated}`), {
      data: { rating: 4 },
      headers: { ...ANTIFORGERY_HEADERS, 'If-Match': '"1"' },
    });
    expect(rating.status(), await rating.text()).toBe(200);

    // 1. Title, descending: the header shows the arrow and says so.
    await page.goto(`./songs?tag=${tag.id}`);
    await expect(rowHeaders(page)).toHaveCount(3);
    await header(page, 'Title').getByRole('button').click();
    await expect(header(page, 'Title')).toHaveAttribute('aria-sort', 'ascending');
    await header(page, 'Title').getByRole('button').click();
    await expect(header(page, 'Title')).toHaveAttribute('aria-sort', 'descending');
    await expect(header(page, 'Title')).toHaveText('Title ▼');
    await expect(rowHeaders(page)).toHaveText([
      charlie.shortcode,
      bravo.shortcode,
      alpha.shortcode,
    ]);
    await expect(page).toHaveURL(/[?&]sort=title&direction=desc(&|$)/);
    await expect(
      page
        .getByRole('row')
        .filter({ has: page.getByRole('rowheader', { name: bravo.shortcode }) })
        .getByTestId('song-rating'),
    ).toContainText('4');
    await expectAccessibleInLightAndDark(page);

    // 2. Last Generation date, from the Sort control: the Song with none is last, either way.
    await sortControl(page)
      .getByRole('combobox', { name: 'Sort by' })
      .selectOption({ label: 'Last Generation date' });
    await expect(page).toHaveURL(/[?&]sort=lastGeneration(&|$)/);
    await expect(rowHeaders(page)).toHaveText([
      bravo.shortcode,
      charlie.shortcode,
      alpha.shortcode,
    ]);
    await sortControl(page)
      .getByRole('combobox', { name: 'Order' })
      .selectOption({ label: 'Oldest first' });
    await expect(rowHeaders(page)).toHaveText([
      charlie.shortcode,
      bravo.shortcode,
      alpha.shortcode,
    ]);
    await expect(page.getByRole('columnheader').and(page.locator('[aria-sort]'))).toHaveCount(0);
    await expectAccessibleInLightAndDark(page);

    // 3. A search orders by Relevance; Updated replaces it; a reload keeps it.
    await page.goto(`./songs?tag=${tag.id}`);
    await page
      .getByRole('search', { name: 'Songs table search' })
      .getByRole('searchbox')
      .fill(stamp);
    await expect(page).toHaveURL(new RegExp(`[?&]search=${stamp}(&|$)`));
    await expect(sortControl(page).getByRole('combobox', { name: 'Sort by' })).toHaveValue(
      'relevance',
    );
    await expect(rowHeaders(page)).toHaveCount(3);
    await sortControl(page)
      .getByRole('combobox', { name: 'Sort by' })
      .selectOption({ label: 'Updated' });
    await expect(page).toHaveURL(/[?&]sort=updated(&|$)/);
    await expect(header(page, 'Updated')).toHaveAttribute('aria-sort', 'descending');
    await expect(rowHeaders(page)).toHaveText([
      bravo.shortcode,
      charlie.shortcode,
      alpha.shortcode,
    ]);
    await page.reload();
    await expect(sortControl(page).getByRole('combobox', { name: 'Sort by' })).toHaveValue(
      'updated',
    );
    await expect(header(page, 'Updated')).toHaveAttribute('aria-sort', 'descending');
    await expect(rowHeaders(page)).toHaveText([
      bravo.shortcode,
      charlie.shortcode,
      alpha.shortcode,
    ]);
    await expectAccessibleInLightAndDark(page);
  });
});
