import { expect, test, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
  expectNoA11yViolations,
} from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  currentVersion: { id: string };
}

/** Creates a Song through the API on the project's container and returns it. */
async function createSong(page: Page, title: string): Promise<Song> {
  await page.goto('./songs');
  const base = new URL('.', page.url());
  const response = await page.request.post(new URL('api/v1/songs', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(response.status()).toBe(201);
  return (await response.json()) as Song;
}

/** The Version as the API has it. */
async function stored(page: Page, song: Song): Promise<{ lyrics: string; name: string | null }> {
  const base = new URL('.', page.url()).toString().replace(/songs\/.*$/, '');
  const response = await page.request.get(
    new URL(`api/v1/versions/${song.currentVersion.id}`, base).toString(),
  );
  expect(response.status()).toBe(200);
  const { lyrics, name } = (await response.json()) as { lyrics: string; name: string | null };
  return { lyrics, name };
}

/** The autosave indicator's words. */
function saveStatus(page: Page) {
  return page.getByTestId('autosave').getByRole('status');
}

/** Makes every Version save fail as if n8Tracks were stopped (no answer); returns the undo. */
async function cutSaves(page: Page): Promise<() => Promise<void>> {
  const pattern = /\/api\/v1\/versions\/[^/]+$/;
  await page.route(pattern, (route) =>
    route.request().method() === 'PATCH' ? route.abort('connectionrefused') : route.fallback(),
  );
  return () => page.unroute(pattern);
}

/**
 * Walks #65's Demo on a Song of its own: typing saves without pressing anything ("Saving…" then
 * "Saved"), the name saves the same way, and with n8Tracks unreachable the indicator says "Not
 * saved", leaving asks, and once it is reachable again the retry brings it back to "Saved". Demo
 * steps 2 to 4 (history) belong to #66. Stopping the server is played by refusing the connection
 * of every save in the browser, so the shared containers keep running for the other specs.
 */
test.describe('saving automatically', () => {
  test('saves as the user types, and recovers after n8Tracks was unreachable', async ({ page }) => {
    const song = await createSong(page, `Autosave ${String(Date.now())}`);
    await page.goto(`./songs/${song.shortcode}`);
    const editor = page.getByRole('textbox', { name: 'Lyrics' });
    await expect(editor).toBeVisible();
    await expect(saveStatus(page)).toHaveText('Saved');
    await expect(page.getByRole('button', { name: /^Save/ })).toHaveCount(0);
    await expectAccessibleInLightAndDark(page);

    // 1. Type a verse: "Saving…", then "Saved", without pressing anything.
    await editor.click();
    await page.keyboard.type('[Verse]\nRunning with the pack');
    await expect(saveStatus(page)).toHaveText('Saving…');
    await expectNoA11yViolations(page);
    await expect(saveStatus(page)).toHaveText('Saved');
    expect((await stored(page, song)).lyrics).toBe('[Verse]\nRunning with the pack');
    await expectAccessibleInLightAndDark(page);

    // The name saves the same way, and the tree shows it.
    await page.getByRole('textbox', { name: 'Name' }).fill('First draft');
    await expect(saveStatus(page)).toHaveText('Saved');
    await expect(page.getByRole('treeitem', { name: /Version 1.*First draft/ })).toBeVisible();
    expect((await stored(page, song)).name).toBe('First draft');

    // 5. n8Tracks cannot be reached: the indicator says "Not saved", and the text stays.
    const restore = await cutSaves(page);
    await editor.click();
    await page.keyboard.press('ControlOrMeta+End');
    await page.keyboard.type(' tonight');
    await expect(saveStatus(page)).toHaveText(/^Not saved: n8Tracks cannot be reached\./);
    await expect(editor).toContainText('Running with the pack tonight');
    await expectAccessibleInLightAndDark(page);

    // Leaving now asks first; Stay keeps the page and the text.
    await page.getByRole('link', { name: /Songs/ }).first().click();
    const leave = page.getByRole('dialog', { name: 'Your changes are not saved' });
    await expect(leave).toBeVisible();
    await expect(leave).toContainText('n8Tracks cannot be reached');
    await expectModalAccessibleInBothSchemes(page);
    await leave.getByRole('button', { name: 'Stay' }).click();
    await expect(leave).toBeHidden();
    await expect(page.getByRole('heading', { level: 3, name: 'Version 1' })).toBeVisible();

    // Reachable again: a retry stores it, and the indicator recovers to "Saved".
    await restore();
    await expect(saveStatus(page)).toHaveText('Saved', { timeout: 40_000 });
    expect((await stored(page, song)).lyrics).toBe('[Verse]\nRunning with the pack tonight');
    await expectAccessibleInLightAndDark(page);

    await page.reload();
    await expect(page.getByRole('textbox', { name: 'Lyrics' })).toContainText(
      'Running with the pack tonight',
    );
    await expect(page.getByRole('textbox', { name: 'Name' })).toHaveValue('First draft');
  });
});
