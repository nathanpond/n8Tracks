import { expect, test, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Tag {
  id: string;
  name: string;
  colour: string;
}

interface Song {
  id: string;
  shortcode: string;
  title: string;
  revision: number;
  tags: Tag[];
}

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

async function createTag(page: Page, base: URL, name: string): Promise<Tag> {
  const response = await page.request.post(new URL('api/v1/tags', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { name },
  });
  expect(response.status()).toBe(201);
  return (await response.json()) as Tag;
}

/** Gives a Song exactly `tags`, through the Song's own edit (as the Details panel does). */
async function assign(page: Page, base: URL, song: Song, tags: Tag[]): Promise<void> {
  const response = await page.request.patch(new URL(`api/v1/songs/${song.id}`, base).toString(), {
    headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(song.revision)}"` },
    data: { tagIds: tags.map((tag) => tag.id) },
  });
  expect(response.status()).toBe(200);
}

/** A Song's Tags as "name colour", in the order the Song lists them. */
async function labels(page: Page, base: URL, song: Song): Promise<string[]> {
  const answer = await page.request.get(new URL(`api/v1/songs/${song.id}`, base).toString());
  expect(answer.ok()).toBe(true);
  return ((await answer.json()) as Song).tags.map((tag) => `${tag.name} ${tag.colour}`);
}

function row(page: Page, name: string) {
  return page.locator(`tr[data-tag-name="${name}"]`);
}

/** A palette colour other than `colour` (Tags are shared, so the one "summer" got is not known). */
function anotherColour(colour: string): string {
  return colour === 'orange' ? 'teal' : 'orange';
}

/** "Orange" for "orange": how the page names a palette colour. */
function word(colour: string): string {
  return colour.charAt(0).toUpperCase() + colour.slice(1);
}

/**
 * Walks the story's Demo on the shared containers with Tags of its own (stamped, because Tags are
 * instance-wide): "summer" is recoloured in Settings → Tags, "runing" is merged into "running"
 * after a confirmation stating the Song count (and "running" keeps its colour), and a Tag in use
 * is deleted after a confirmation stating how many Songs lose it. Each state is scanned with axe.
 * This is also #85's Demo step 3 (recolour and merge in Settings → Tags).
 */
test.describe('managing the Tag list', () => {
  test('recolours a Tag, merges a misspelt one, and deletes one in use', async ({ page }) => {
    const stamp = String(Date.now()).slice(-7);
    const base = await appBase(page);
    const running = await createTag(page, base, `running ${stamp}`);
    const typo = await createTag(page, base, `runing ${stamp}`);
    const summer = await createTag(page, base, `summer ${stamp}`);
    const first = await createSong(page, base, `Tag A ${stamp}`);
    const second = await createSong(page, base, `Tag B ${stamp}`);
    await assign(page, base, first, [running, summer]);
    await assign(page, base, second, [typo]);
    const recolour = anotherColour(summer.colour);

    await page.goto('./settings/account');
    await page.getByRole('link', { name: 'Tags', exact: true }).click();
    await expect(page.getByRole('heading', { level: 2, name: 'Tags' })).toBeVisible();
    await expect(row(page, summer.name).getByTestId('tag-colour')).toHaveText(word(summer.colour));
    await expectAccessibleInLightAndDark(page);

    // 1. Recolour "summer".
    await page.getByRole('button', { name: `Edit ${summer.name}`, exact: true }).click();
    const edit = page.getByRole('dialog', { name: `Edit ${summer.name}` });
    await expect(edit).toBeVisible();
    await edit.getByRole('radio', { name: word(recolour), exact: true }).check();
    await expectModalAccessibleInBothSchemes(page);
    await edit.getByRole('button', { name: 'Save' }).click();
    await expect(edit).toBeHidden();
    await expect(page.getByRole('status').filter({ hasText: 'is now' })).toContainText(
      `${summer.name} is now ${recolour}.`,
    );
    await expect(row(page, summer.name).getByTestId('tag-colour')).toHaveText(word(recolour));
    await expect(row(page, summer.name).locator('[data-tag-colour]')).toHaveAttribute(
      'data-tag-colour',
      recolour,
    );

    // 2. Merge "runing" into "running"; the confirmation states the count and the colour kept.
    await page.getByRole('checkbox', { name: `Select ${typo.name}`, exact: true }).check();
    await page.getByRole('button', { name: 'Merge into…' }).click();
    const merge = page.getByRole('dialog', { name: `Merge ${typo.name}` });
    await expect(merge).toBeVisible();
    await merge.getByRole('combobox', { name: 'Merge into' }).selectOption({ label: running.name });
    await expect(merge.getByTestId('merge-summary')).toContainText(
      `1 Song changes. Each Song with ${typo.name} gets ${running.name} instead`,
    );
    await expect(merge.getByTestId('merge-summary')).toContainText(
      `${running.name} keeps its colour, ${running.colour}.`,
    );
    await expectModalAccessibleInBothSchemes(page);
    await merge.getByRole('button', { name: 'Merge', exact: true }).click();
    await expect(merge).toBeHidden();

    await expect(page.getByRole('status').filter({ hasText: 'merged into' })).toContainText(
      `${typo.name} merged into ${running.name}.`,
    );
    await expect(row(page, typo.name)).toHaveCount(0);
    await expect(row(page, running.name).getByRole('cell').nth(2)).toHaveText('2');
    await expect(row(page, running.name).getByTestId('tag-colour')).toHaveText(
      word(running.colour),
    );
    expect(await labels(page, base, second)).toEqual([`${running.name} ${running.colour}`]);

    // 3. Delete a Tag in use; the confirmation states how many Songs lose it.
    await page.getByRole('button', { name: `Delete ${summer.name}`, exact: true }).click();
    const remove = page.getByRole('dialog', { name: `Delete ${summer.name}` });
    await expect(remove).toBeVisible();
    await expect(remove.getByTestId('delete-summary')).toContainText(
      `1 Song has ${summer.name}. Deleting it removes it from that Song.`,
    );
    await expectModalAccessibleInBothSchemes(page);
    await remove.getByRole('button', { name: `Delete ${summer.name}` }).click();
    await expect(remove).toBeHidden();

    await expect(row(page, summer.name)).toHaveCount(0);
    expect(await labels(page, base, first)).toEqual([`${running.name} ${running.colour}`]);
    await expectAccessibleInLightAndDark(page);

    // Every Song follows: the Song page shows the surviving Tag in its own colour.
    await page.goto(`./songs/${second.shortcode}`);
    const songLabels = page.getByRole('group', { name: 'Tags' });
    await expect(songLabels.locator('[data-tag-colour]')).toHaveText([running.name]);
    await expect(songLabels.locator('[data-tag-colour]')).toHaveAttribute(
      'data-tag-colour',
      running.colour,
    );
  });
});
