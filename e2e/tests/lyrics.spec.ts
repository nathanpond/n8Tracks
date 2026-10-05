import { expect, test, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark, expectNoA11yViolations } from '../support/a11y.ts';
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

/** The lyrics as the API returns them. */
async function storedLyrics(page: Page, song: Song): Promise<{ lyrics: string; styles: string }> {
  const base = new URL('.', page.url()).toString().replace(/songs\/.*$/, '');
  const response = await page.request.get(
    new URL(`api/v1/versions/${song.currentVersion.id}`, base).toString(),
  );
  expect(response.status()).toBe(200);
  const { lyrics, styles } = (await response.json()) as { lyrics: string; styles: string };
  return { lyrics, styles };
}

function lyricsEditor(page: Page) {
  return page.getByRole('textbox', { name: 'Lyrics' });
}

/** The autosave indicator's words. */
function saveStatus(page: Page) {
  return page.getByTestId('autosave').getByRole('status');
}

function completions(page: Page) {
  return page.getByRole('listbox');
}

const EXPECTED =
  '[Verse]\nRunning with the pack (ooh)\n[Chorus]\n[Bridge\n[Whisper softly, building]\n\tend  ';

/**
 * Walks #64's Demo on a Song of its own: tags and parentheticals highlighted differently, `[`
 * offering the common tags, a warning on an unclosed tag that does not stop saving, an invented tag
 * kept, and the text exactly as typed after a reload. Also: Tab inserts a tab and Escape then Tab
 * moves on. Saving is automatic (#65; its own walk is `autosave.spec.ts`).
 */
test.describe('the lyrics editor', () => {
  test('highlights, completes, warns, and saves exactly what was typed', async ({ page }) => {
    const song = await createSong(page, `Lyrics ${String(Date.now())}`);
    await page.goto(`./songs/${song.shortcode}`);
    const editor = lyricsEditor(page);
    await expect(editor).toBeVisible();
    await expectAccessibleInLightAndDark(page);

    // 1. A tag, a line, and a parenthetical: highlighted differently (bold, italic).
    await editor.click();
    await page.keyboard.type('[Verse]');
    await expect(completions(page)).toBeHidden();
    await page.keyboard.press('Enter');
    await page.keyboard.type('Running with the pack (ooh)');
    const tag = page.locator('.cm-lyrics-tag', { hasText: '[Verse]' });
    const parenthetical = page.locator('.cm-lyrics-parenthetical', { hasText: '(ooh)' });
    await expect(tag).toHaveCSS('font-weight', '700');
    await expect(parenthetical).toHaveCSS('font-style', 'italic');
    await expect(tag).not.toHaveCSS(
      'color',
      await parenthetical.evaluate((e) => getComputedStyle(e).color),
    );

    // 2. [ offers the common tags; pick Chorus.
    await page.keyboard.press('Enter');
    await page.keyboard.type('[');
    await expect(
      completions(page).getByRole('option', { name: 'Chorus', exact: true }),
    ).toBeVisible();
    await expectNoA11yViolations(page);
    await completions(page).getByRole('option', { name: 'Chorus', exact: true }).click();
    await expect(page.locator('.cm-lyrics-tag', { hasText: '[Chorus]' })).toBeVisible();

    // 3. [Bridge left open: a warning marker on that line, explained on hover and focus.
    await page.keyboard.press('Enter');
    await page.keyboard.type('[Bridge');
    await page.keyboard.press('Escape');
    await expect(completions(page)).toBeHidden();
    const marker = page.getByRole('button', { name: /^Warning, line 4: This \[ is not closed/ });
    await expect(marker).toBeVisible();
    await expect(page.getByRole('list', { name: 'Lyrics warnings' })).toContainText(
      'Line 4: This [ is not closed on its line.',
    );
    await marker.hover();
    await expect(marker.locator('.cm-lyrics-warning-tip')).toBeVisible();

    // 4. An invented tag is highlighted and kept; Tab inside the editor inserts a tab.
    await editor.click();
    await page.keyboard.press('ControlOrMeta+End');
    await page.keyboard.press('Enter');
    await page.keyboard.type('[Whisper softly, building]');
    await expect(completions(page)).toBeHidden();
    await page.keyboard.press('Enter');
    await page.keyboard.press('Tab');
    await page.keyboard.type('end  ');
    await expect(
      page.locator('.cm-lyrics-tag', { hasText: '[Whisper softly, building]' }),
    ).toBeVisible();
    await expect(editor).toBeFocused();

    // Escape, then Tab, moves on to the Styles field.
    await page.keyboard.press('Escape');
    await page.keyboard.press('Tab');
    const styles = page.getByRole('textbox', { name: 'Styles' });
    await expect(styles).toBeFocused();
    await styles.fill('indie rock,\nfast ');
    await expectNoA11yViolations(page);

    // 5. Saved automatically, warning and all; the text is stored exactly as typed.
    await expect(saveStatus(page)).toHaveText('Saved');
    await expectAccessibleInLightAndDark(page);
    await expect
      .poll(() => storedLyrics(page, song))
      .toEqual({ lyrics: EXPECTED, styles: 'indie rock,\nfast ' });

    await page.reload();
    await expect(lyricsEditor(page)).toBeVisible();
    expect(
      await page
        .getByTestId('lyrics-editor')
        .locator('.cm-editor')
        .evaluate((element) =>
          [...element.querySelectorAll('.cm-line')].map((line) => line.textContent).join('\n'),
        ),
    ).toBe(EXPECTED);
    await expect(page.getByRole('textbox', { name: 'Styles' })).toHaveValue('indie rock,\nfast ');
    await expect(saveStatus(page)).toHaveText('Saved');
    await expect(
      page.getByRole('button', { name: /^Warning, line 4: This \[ is not closed/ }),
    ).toBeVisible();
  });
});
