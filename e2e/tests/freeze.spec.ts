import { expect, test, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark } from '../support/a11y.ts';
import { seedGeneration } from '../support/seeding.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  currentVersion: { id: string; shortcode: string };
}

interface VersionDetail {
  revision: number;
  isFrozen: boolean;
  name: string | null;
  notes: string | null;
  lyrics: string;
}

/** The API base of the project's container, worked out on the Songs page. */
async function apiBase(page: Page): Promise<(path: string) => string> {
  await page.goto('./songs');
  const base = new URL('.', page.url());
  return (path: string) => new URL(`api/v1/${path}`, base).toString();
}

/** The autosave indicator's words. */
function saveStatus(page: Page) {
  return page.getByTestId('autosave').getByRole('status');
}

/**
 * Walks #69's Demo steps 1 and 3 against the real image: a Generation is attached with the
 * test-only seeding command, after which the Version's lyrics are refused with 409
 * `version_frozen` while its name and notes still save from the Version page. The read-only editor
 * and its Create New Version From action (#70) are walked in `frozen-editor.spec.ts`.
 */
test.describe('freezing a Version once a Generation is attached', () => {
  test('refuses changing the inputs and keeps the name and notes editable', async ({
    page,
  }, testInfo) => {
    const api = await apiBase(page);
    const created = await page.request.post(api('songs'), {
      data: { title: `Frozen ${String(Date.now())}` },
      headers: ANTIFORGERY_HEADERS,
    });
    expect(created.status()).toBe(201);
    const song = (await created.json()) as Song;
    const version = song.currentVersion;
    const written = await page.request.patch(api(`versions/${version.id}`), {
      data: { lyrics: '[Verse]\nThe take that made it' },
      headers: { ...ANTIFORGERY_HEADERS, 'If-Match': '"1"' },
    });
    expect(written.status()).toBe(200);

    // 1. Attach a Generation with the documented test-only command.
    const generation = await seedGeneration(testInfo, version.shortcode);
    expect(generation).toBe(`${version.shortcode}-g1`);

    const frozen = (await (
      await page.request.get(api(`versions/${version.id}`))
    ).json()) as VersionDetail;
    expect(frozen.isFrozen).toBe(true);
    expect(frozen.revision).toBe(3);
    const refused = await page.request.patch(api(`versions/${version.id}`), {
      data: { lyrics: 'A different take' },
      headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(frozen.revision)}"` },
    });
    expect(refused.status()).toBe(409);
    const problem = (await refused.json()) as { code: string; versionShortcode: string };
    expect(problem.code).toBe('version_frozen');
    expect(problem.versionShortcode).toBe(version.shortcode);

    const resolved = await page.request.get(api(`resolve/${generation.toUpperCase()}`));
    expect(resolved.status()).toBe(200);
    expect(((await resolved.json()) as { entityType: string }).entityType).toBe('generation');

    // 3. Open the Version: rename it and edit its notes; both still save.
    await page.goto(`./songs/${song.shortcode}/v/1`);
    await expect(page.getByRole('textbox', { name: 'Lyrics' })).toBeVisible();
    await expect(saveStatus(page)).toHaveText('Saved');
    await expectAccessibleInLightAndDark(page);

    await page.getByRole('textbox', { name: 'Name' }).fill('The keeper');
    await page.getByRole('textbox', { name: 'Notes' }).fill('Generated on the first try.');
    await expect(saveStatus(page)).toHaveText('Saved');
    await expectAccessibleInLightAndDark(page);

    const after = (await (
      await page.request.get(api(`versions/${version.id}`))
    ).json()) as VersionDetail;
    expect(after.name).toBe('The keeper');
    expect(after.notes).toBe('Generated on the first try.');
    expect(after.lyrics).toBe('[Verse]\nThe take that made it');
    expect(after.isFrozen).toBe(true);

    await page.reload();
    await expect(page.getByRole('textbox', { name: 'Name' })).toHaveValue('The keeper');
    await expect(page.getByRole('textbox', { name: 'Notes' })).toHaveValue(
      'Generated on the first try.',
    );
  });
});
