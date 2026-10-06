import { expect, test, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark, expectNoA11yViolations } from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface SongTag {
  id: string;
  name: string;
  colour: string;
}

interface Song {
  id: string;
  shortcode: string;
  title: string;
  revision: number;
  tags: SongTag[];
}

/** The twelve palette colours a Tag can have. */
const PALETTE = [
  'gray',
  'red',
  'pink',
  'grape',
  'violet',
  'indigo',
  'blue',
  'cyan',
  'teal',
  'green',
  'yellow',
  'orange',
];

/** The app's base (the root or the sub-path), as the Songs page resolves it. */
async function appBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

async function createSong(page: Page, base: URL, title: string): Promise<Song> {
  const response = await page.request.post(new URL('api/v1/songs', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(response.status()).toBe(201);
  return (await response.json()) as Song;
}

async function readSong(page: Page, base: URL, song: Song): Promise<Song> {
  const answer = await page.request.get(new URL(`api/v1/songs/${song.id}`, base).toString());
  expect(answer.ok()).toBe(true);
  return (await answer.json()) as Song;
}

async function createTag(page: Page, base: URL, name: string): Promise<SongTag> {
  const response = await page.request.post(new URL('api/v1/tags', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { name },
  });
  expect([200, 201]).toContain(response.status());
  return (await response.json()) as SongTag;
}

/** Gives a Song exactly `tags` through the API, at its current revision. */
async function assignTags(page: Page, base: URL, song: Song, tags: SongTag[]): Promise<void> {
  const current = await readSong(page, base, song);
  const response = await page.request.patch(new URL(`api/v1/songs/${song.id}`, base).toString(), {
    headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(current.revision)}"` },
    data: { tagIds: tags.map((tag) => tag.id) },
  });
  expect(response.ok()).toBe(true);
}

async function openSong(page: Page, song: Song): Promise<void> {
  await page.goto(`./songs/${song.shortcode}`);
  await expect(page.getByRole('heading', { level: 2, name: song.title })).toBeVisible();
}

function tagsField(page: Page) {
  return page.getByRole('combobox', { name: 'Tags' });
}

/** Types a new Tag name into the picker and creates it from the create option. */
async function createInPicker(page: Page, name: string): Promise<void> {
  const field = tagsField(page);
  await field.click();
  await field.fill(name);
  await page.getByRole('option', { name: `Create Tag “${name}”` }).click();
  await expect(page.getByRole('button', { name: `Remove Tag ${name}` })).toBeVisible();
}

/**
 * Walks the story's Demo (steps 1 and 2; Settings → Tags is the sibling story's) on the project's
 * shared container, on Songs and Tags of its own (names stamped with this run, since Tags are
 * instance-wide): two Tags created on the spot in a Song's Details, each given a palette colour and
 * shown named as a coloured label; the Songs table showing them on the row, the first three of
 * many and "+N" revealing the rest on keyboard focus; and the table filtered by one Tag. Each state
 * is scanned with axe in both colour schemes.
 */
test.describe('Tags', () => {
  test('labels a Song with coloured Tags and finds it by one in the Songs table', async ({
    page,
  }) => {
    const stamp = String(Date.now()).slice(-7);
    const running = `running ${stamp}`;
    const summer = `summer ${stamp}`;
    const base = await appBase(page);
    const first = await createSong(page, base, `Tags A ${stamp}`);
    const second = await createSong(page, base, `Tags B ${stamp}`);

    // 1. In a Song's Details, add Tags "running" and "summer"; each gets a colour.
    await openSong(page, first);
    const details = page.getByRole('button', { name: 'Details', exact: true });
    await expect(details).toHaveAttribute('aria-expanded', 'false');
    await details.click();
    await tagsField(page).click();
    await tagsField(page).fill(running);
    await expect(page.getByRole('option', { name: `Create Tag “${running}”` })).toBeVisible();
    await expectNoA11yViolations(page);
    await createInPicker(page, running);
    await createInPicker(page, summer);

    const saved = (await readSong(page, base, first)).tags;
    expect(saved.map((tag) => tag.name)).toEqual([running, summer]);
    for (const tag of saved) {
      expect(PALETTE).toContain(tag.colour);
    }

    // Shown under the title as coloured labels, each named.
    const labels = page.getByRole('group', { name: 'Tags' });
    await expect(labels.locator('[data-tag-colour]')).toHaveText([running, summer]);
    for (const tag of saved) {
      await expect(labels.locator(`[data-tag-colour="${tag.colour}"]`).first()).toBeVisible();
    }
    await page.keyboard.press('Escape');
    await expectAccessibleInLightAndDark(page);

    // On another Song, "running" is suggested rather than created again.
    await openSong(page, second);
    await tagsField(page).click();
    await tagsField(page).fill(running.toUpperCase());
    await expect(page.getByRole('option', { name: running, exact: true })).toBeVisible();
    await expect(page.getByRole('option', { name: /^Create Tag/ })).toHaveCount(0);
    await tagsField(page).press('Enter');
    await expect(page.getByRole('button', { name: `Remove Tag ${running}` })).toBeVisible();

    // A third Song with five Tags: the table shows three and "+2".
    const many = await createSong(page, base, `Tags C ${stamp}`);
    const extra = await Promise.all(
      ['a', 'b', 'c'].map((letter) => createTag(page, base, `${letter} extra ${stamp}`)),
    );
    await assignTags(page, base, many, [...extra, ...saved]);

    // 2. In the Songs table the Tags appear on the row; filter by "running".
    await page.goto('./songs');
    const table = page.getByRole('table', { name: 'Songs' });
    const firstRow = table.getByRole('row').filter({ has: page.getByText(first.title) });
    await expect(firstRow.locator('[data-tag-colour]')).toHaveText([running, summer]);
    const manyRow = table.getByRole('row').filter({ has: page.getByText(many.title) });
    await expect(manyRow.locator('[data-tag-colour]')).toHaveCount(3);
    const more = manyRow.getByTestId('more-tags');
    await expect(more).toContainText('+2');
    // Reached with the keyboard from the row's title link (Mantine opens tooltips on visible focus).
    await manyRow.getByRole('link', { name: many.title }).focus();
    await page.keyboard.press('Tab');
    await expect(more).toBeFocused();
    const tooltip = page.getByRole('tooltip');
    await expect(tooltip.locator('[data-tag-colour]')).toHaveText([running, summer]);
    await expectNoA11yViolations(page);
    await more.blur();

    const filter = page.getByRole('combobox', { name: 'Tag', exact: true });
    await filter.click();
    await filter.fill(running);
    await page.getByRole('option', { name: running, exact: true }).click();
    await expect(page).toHaveURL(/[?&]tag=/);
    await expect(table.getByRole('rowheader')).toHaveText([
      many.shortcode,
      second.shortcode,
      first.shortcode,
    ]);
    await page.keyboard.press('Escape');
    await expectAccessibleInLightAndDark(page);

    // Only the Song with both: filter by "summer" alone, from the address.
    const summerId = saved.find((tag) => tag.name === summer)?.id ?? '';
    await page.goto(`./songs?tag=${summerId}`);
    await expect(table.getByRole('rowheader')).toHaveText([many.shortcode, first.shortcode]);
  });
});
