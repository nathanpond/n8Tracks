import { expect, test, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  title: string;
  relationships: { name: string; song: { title: string } }[];
}

/** The nine system types, in the order Settings → Relationships lists them. */
const SYSTEM_TYPES = [
  'Cover',
  'Extend',
  'Reuse Prompt',
  'Mashup',
  'Sample This Song',
  'Use as Inspiration',
  'Voice',
  'Remix',
  'Derived From',
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

/** Opens a Song and its Details panel, and answers the Related group. */
async function openRelated(page: Page, song: Song) {
  await page.goto(`./songs/${song.shortcode}`);
  await expect(page.getByRole('heading', { level: 2, name: song.title })).toBeVisible();
  await page.getByRole('button', { name: 'Details', exact: true }).click();
  const related = page.getByRole('group', { name: 'Related' });
  await expect(related).toBeVisible();
  return related;
}

/** "Name: title" for each relationship the open Song shows, in order. */
function shown(page: Page) {
  return page.locator('[data-relationship-name]');
}

function row(page: Page, name: string) {
  return page.locator(`tr[data-type-name="${name}"]`);
}

/**
 * Walks the story's Demo on the shared containers with a type and Songs of its own (stamped,
 * because types are instance-wide): "Sequel to" / "Has sequel" is added in Settings →
 * Relationships, Song A is related as "Sequel to" Song B from its Details, Song B shows "Has
 * sequel: Song A", and the nine system types are listed and cannot be edited. Then the
 * relationship is removed from Song B after a confirmation, and the type, in use again, is deleted
 * after a confirmation stating how many relationships go. Each state is scanned with axe.
 */
test.describe('relating Songs', () => {
  test('relates two Songs with a type of the user’s own and shows it from both', async ({
    page,
  }) => {
    const stamp = String(Date.now()).slice(-7);
    const sequel = `Sequel to ${stamp}`;
    const hasSequel = `Has sequel ${stamp}`;
    const base = await appBase(page);
    const songA = await createSong(page, base, `Song A ${stamp}`);
    const songB = await createSong(page, base, `Song B ${stamp}`);

    // 1. In Settings → Relationships, add "Sequel to" with reverse "Has sequel".
    await page.goto('./settings/account');
    await page.getByRole('link', { name: 'Relationships', exact: true }).click();
    await expect(page.getByRole('heading', { level: 2, name: 'Relationships' })).toBeVisible();
    const form = page.getByRole('form', { name: 'Add a type' });
    await form.getByRole('textbox', { name: 'Name', exact: true }).fill(sequel);
    await form.getByRole('textbox', { name: 'Reverse name' }).fill(hasSequel);
    await form.getByRole('button', { name: 'Add type' }).click();
    await expect(page.getByRole('status').filter({ hasText: 'is added' })).toContainText(
      `${sequel} / ${hasSequel} is added.`,
    );
    await expect(row(page, sequel).getByRole('cell').first()).toHaveText(hasSequel);
    await expectAccessibleInLightAndDark(page);

    // 2. On Song A's Details, add "Sequel to" Song B.
    const relatedA = await openRelated(page, songA);
    await expect(relatedA.getByText('Not related to any Song.')).toBeVisible();
    await relatedA.getByRole('combobox', { name: /Relationship/ }).selectOption({ label: sequel });
    const search = relatedA.getByRole('textbox', { name: /Related Song/ });
    await search.fill(songB.title);
    await page.getByRole('option', { name: new RegExp(songB.title) }).click();
    await expect(shown(page)).toHaveCount(1);
    await expect(shown(page).first()).toHaveAttribute('data-relationship-name', sequel);
    await expect(relatedA.getByRole('link', { name: songB.title })).toBeVisible();

    // Song B is now disabled for "Sequel to", and Song A itself is never offered.
    await search.fill(stamp);
    await expect(page.getByRole('option', { name: new RegExp(songB.title) })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
    await expect(page.getByRole('option', { name: new RegExp(songA.title) })).toHaveCount(0);
    await search.press('Escape');
    await search.fill('');
    await expectAccessibleInLightAndDark(page);

    // 3. Open Song B: its Details show "Has sequel: Song A".
    await relatedA.getByRole('link', { name: songB.title }).click();
    await expect(page.getByRole('heading', { level: 2, name: songB.title })).toBeVisible();
    const relatedB = page.getByRole('group', { name: 'Related' });
    await expect(relatedB.locator(`[data-relationship-group="${hasSequel}"]`)).toContainText(
      `${hasSequel}:`,
    );
    await expect(shown(page)).toHaveCount(1);
    await expect(shown(page).first()).toHaveAttribute('data-relationship-name', hasSequel);
    await expect(shown(page).first()).toHaveAttribute('data-song-title', songA.title);
    await expectAccessibleInLightAndDark(page);

    // Removed from Song B after a confirmation, it goes from both Songs.
    await relatedB.getByRole('button', { name: `Remove ${hasSequel}: ${songA.title}` }).click();
    const remove = page.getByRole('dialog', { name: 'Remove relationship' });
    await expect(remove).toBeVisible();
    await expect(remove.getByTestId('remove-relationship-summary')).toContainText(
      `Remove ${hasSequel}: ${songA.title}? It is removed from ${songA.title} as well.`,
    );
    await expectModalAccessibleInBothSchemes(page);
    await remove.getByRole('button', { name: 'Remove', exact: true }).click();
    await expect(remove).toBeHidden();
    await expect(relatedB.getByText('Not related to any Song.')).toBeVisible();
    expect((await readSong(page, base, songA)).relationships).toEqual([]);

    // Related again (through the API), so the type is in use for the delete below.
    const again = await page.request.post(
      new URL(`api/v1/songs/${songA.id}/relationships`, base).toString(),
      {
        headers: ANTIFORGERY_HEADERS,
        data: {
          typeId: await typeId(page, base, sequel),
          direction: 'forward',
          otherSong: songB.shortcode,
        },
      },
    );
    expect(again.status()).toBe(201);

    // 4. The nine system types are listed first and cannot be edited.
    await page.goto('./settings/relationships');
    const names = page.locator('tbody tr[data-system]');
    await expect(names).toHaveCount(SYSTEM_TYPES.length);
    expect(
      await names.evaluateAll((rows) => rows.map((tr) => tr.getAttribute('data-type-name'))),
    ).toEqual(SYSTEM_TYPES);
    for (const name of SYSTEM_TYPES) {
      await expect(row(page, name).getByText('System type')).toBeVisible();
      await expect(row(page, name).getByRole('button')).toHaveCount(0);
    }

    // Deleting the type in use asks first, stating how many relationships go with it.
    await page.getByRole('button', { name: `Delete ${sequel}`, exact: true }).click();
    const confirm = page.getByRole('dialog', { name: `Delete ${sequel} / ${hasSequel}` });
    await expect(confirm).toBeVisible();
    await expect(confirm.getByTestId('delete-summary')).toContainText(
      `1 relationship uses ${sequel} / ${hasSequel}. Deleting the type removes that relationship from their Songs.`,
    );
    await expectModalAccessibleInBothSchemes(page);
    await confirm.getByRole('button', { name: `Delete ${sequel}` }).click();
    await expect(confirm).toBeHidden();
    await expect(row(page, sequel)).toHaveCount(0);
    expect((await readSong(page, base, songB)).relationships).toEqual([]);
    await expectAccessibleInLightAndDark(page);
  });
});

/** The ID of the type named `name`. */
async function typeId(page: Page, base: URL, name: string): Promise<string> {
  const answer = await page.request.get(new URL('api/v1/relationship-types', base).toString());
  expect(answer.ok()).toBe(true);
  const { items } = (await answer.json()) as { items: { id: string; name: string }[] };
  const found = items.find((type) => type.name === name);
  expect(found).toBeDefined();
  return found?.id ?? '';
}
