import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

/** The first library clip of the committed TS-003 fixture: an Advanced Song with lyrics, in a workspace. */
const LIBRARY_CLIP = (
  JSON.parse(
    readFileSync(
      new URL(
        '../../extension/fixtures/suno/feed-v3.library-page-1.response.json',
        import.meta.url,
      ),
      'utf8',
    ),
  ) as { clips: Record<string, unknown>[] }
).clips[0];

interface Generation {
  shortcode: string;
  title: string | null;
  styleTags: string | null;
}

/** The app's base (the root or the sub-path), as the Songs page resolves it. */
async function appBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

/** The fixture clip as Suno would list it with these fields. */
function clip(fields: {
  id: string;
  title: string;
  tags: string;
  workspace: string;
  lyrics: string;
}): Record<string, unknown> {
  const copy = structuredClone(LIBRARY_CLIP) as Record<string, unknown> & {
    metadata: Record<string, unknown>;
    project: Record<string, unknown>;
  };
  copy.id = fields.id;
  copy.title = fields.title;
  copy.created_at = new Date(Date.now() - 3_600_000).toISOString();
  copy.batch_index = 0;
  copy.project = { ...copy.project, id: fields.workspace };
  copy.metadata = { ...copy.metadata, prompt: fields.lyrics, tags: fields.tags };
  return copy;
}

/** Uploads one export of `clips` with the extension's token, as the extension does; returns its ID once ready. */
async function uploadExport(
  extension: APIRequestContext,
  base: URL,
  token: string,
  workspace: { id: string; name: string },
  clips: Record<string, unknown>[],
): Promise<string> {
  const headers = { Authorization: `Bearer ${token}`, Accept: 'application/json' };
  const exports = new URL('api/v1/suno/exports', base).toString();
  const created = await extension.post(exports, {
    headers,
    data: {
      format: 'n8tracks.suno-export',
      formatVersion: 1,
      extensionVersion: '0.1.0',
      adapterVersion: '3',
      capturedAt: new Date().toISOString(),
      scope: { kind: 'library', ids: [] },
      // Not read to the end: a whole-library sync would mark every other test's clip on the shared
      // containers Remote Missing at Confirm (#142), which this walk is not about.
      libraryComplete: false,
      trashedComplete: true,
      workspaces: [{ id: workspace.id, name: workspace.name, description: '' }],
      workspacesComplete: false,
      playlists: [],
    },
  });
  expect(created.status()).toBe(201);
  const { id } = (await created.json()) as { id: string };
  const part = await extension.post(`${exports}/${id}/parts`, {
    headers,
    data: { partNumber: 1, clips, trashedClips: [] },
  });
  expect(part.status()).toBe(200);
  const completed = await extension.post(`${exports}/${id}/complete`, { headers });
  expect(completed.status()).toBe(200);
  expect(((await completed.json()) as { state: string }).state).toBe('ready');
  return id;
}

/** Confirms the review open on the page and waits for the import to end. */
async function confirm(page: Page) {
  await page.getByRole('button', { name: 'Confirm import' }).click();
  await page
    .getByRole('dialog', { name: 'Confirm this import?' })
    .getByRole('button', { name: 'Confirm import' })
    .click();
  await expect(page.getByRole('heading', { name: 'This import was confirmed' })).toBeVisible({
    timeout: 30_000,
  });
}

/**
 * Walks #141's Demo on the shared containers with a Suno ID and titles of its own. 1. A clip is
 * imported from an export. 2. An export where its title and tags differ shows it as Changed; the diff
 * shows both fields side by side; the title is taken and the tags declined; Confirm. 3. The Generation
 * has the new title and its old tags. 4. An export where its lyrics differ shows it as Conflict; moving
 * it to a new Version and confirming puts the Generation on a frozen child Version, and its old
 * shortcode still resolves. Each visited state, the diff dialog included, is scanned with axe.
 * (Demo step 3's "the same export again is Already linked" needs the remembered decline, which waits
 * for its migration: see the story's completion comment.)
 */
test.describe('Import diff', () => {
  test('takes only the fields chosen from a Changed record and moves a Conflict to a new Version', async ({
    page,
    playwright,
  }, testInfo) => {
    const stamp = `${String(Date.now()).slice(-7)}${testInfo.project.name}`;
    const workspace = { id: `ws-diff-${stamp}`, name: `Diff ${stamp}` };
    const sunoId = randomUUID();
    const original = {
      id: sunoId,
      title: `Diff original ${stamp}`,
      tags: `dream pop ${stamp}`,
      workspace: workspace.id,
      lyrics: `[Verse]\nFirst words ${stamp}`,
    };
    const base = await appBase(page);
    const credential = await page.request.post(new URL('api/v1/credentials', base).toString(), {
      headers: ANTIFORGERY_HEADERS,
      data: { name: `Import diff ${stamp}`, kind: 'extension', scopes: ['suno.sync'] },
    });
    expect(credential.status()).toBe(201);
    const { token } = (await credential.json()) as { token: string };
    const extension = await playwright.request.newContext({ storageState: undefined });

    try {
      // 1. The clip is imported.
      const first = await uploadExport(extension, base, token, workspace, [clip(original)]);
      await page.goto(`./suno/imports/${first}`);
      await confirm(page);
      await expect(page.getByTestId('commit-outcomes')).toHaveText('1 record imported.');

      // 2. Its title and tags differ: Changed. The diff shows both; take the title, keep the tags.
      const retitled = { ...original, title: `Diff new ${stamp}`, tags: `shoegaze ${stamp}` };
      const second = await uploadExport(extension, base, token, workspace, [clip(retitled)]);
      await page.goto(`./suno/imports/${second}`);
      const row = page.locator(`tr[data-record="${sunoId}"]`);
      await expect(row.getByTestId('record-class')).toHaveText('Changed');
      await expect(row.getByTestId('record-choice')).toHaveText('Left as it is');
      await expectAccessibleInLightAndDark(page);
      await row
        .getByRole('button', { name: `Review the differences for ${retitled.title}` })
        .click();
      const dialog = page.getByRole('dialog', { name: `Differences for “${retitled.title}”` });
      const fields = dialog.getByRole('table', { name: 'Fields that differ' });
      await expect(fields.locator('tr[data-field="title"]')).toContainText(original.title);
      await expect(fields.locator('tr[data-field="title"]')).toContainText(retitled.title);
      await expect(fields.locator('tr[data-field="tags"]')).toContainText(original.tags);
      await expect(fields.locator('tr[data-field="tags"]')).toContainText(retitled.tags);
      await expectModalAccessibleInBothSchemes(page);
      await dialog.getByRole('checkbox', { name: 'Take Suno’s Title' }).check();
      await expect(dialog.getByTestId('diff-summary')).toContainText('Taken from Suno: Title.');
      await dialog.getByRole('button', { name: 'Save choice' }).click();
      await expect(dialog).toBeHidden();
      await expect(row.getByTestId('record-choice')).toHaveText('Take Title from Suno');
      const generationLink = row.getByRole('link', { name: /^Generation / });
      const shortcode = ((await generationLink.textContent()) ?? '').replace('Generation ', '');
      await confirm(page);
      await expect(page.getByTestId('commit-outcomes')).toHaveText('1 record updated from Suno.');
      await expectAccessibleInLightAndDark(page);

      // 3. The Generation shows the new title and the old tags.
      const read = async (reference: string) => {
        const response = await page.request.get(
          new URL(`api/v1/generations/${reference}`, base).toString(),
        );
        expect(response.status()).toBe(200);
        return (await response.json()) as Generation;
      };
      const updated = await read(shortcode);
      expect([updated.title, updated.styleTags]).toEqual([retitled.title, original.tags]);

      // 4. Its lyrics differ: Conflict. Move it to a new Version; its old shortcode still resolves.
      const remade = { ...retitled, tags: original.tags, lyrics: `[Verse]\nOther words ${stamp}` };
      const third = await uploadExport(extension, base, token, workspace, [clip(remade)]);
      await page.goto(`./suno/imports/${third}`);
      await expect(row.getByTestId('record-class')).toHaveText('Conflict');
      await row.getByRole('button', { name: `Review the differences for ${remade.title}` }).click();
      const conflict = page.getByRole('dialog', { name: `Differences for “${remade.title}”` });
      await expect(
        conflict.getByRole('table', { name: 'Creation inputs that differ' }),
      ).toContainText(`Other words ${stamp}`);
      await expectModalAccessibleInBothSchemes(page);
      await conflict
        .getByRole('radio', { name: 'Move it to a new Version holding Suno’s inputs' })
        .check();
      await conflict.getByRole('button', { name: 'Save choice' }).click();
      await expect(conflict).toBeHidden();
      await expect(row.getByTestId('record-choice')).toHaveText('Move to a new Version');
      await confirm(page);
      await expect(page.getByTestId('commit-outcomes')).toHaveText(
        '1 record moved to a new Version.',
      );

      const moved = await read(shortcode);
      expect(moved.shortcode).not.toBe(shortcode);
      const versionShortcode = moved.shortcode.slice(0, moved.shortcode.lastIndexOf('-g'));
      expect(versionShortcode).toBe(`${shortcode.slice(0, shortcode.lastIndexOf('-g'))}.1`);
      const version = (await (
        await page.request.get(new URL(`api/v1/versions/${versionShortcode}`, base).toString())
      ).json()) as { isFrozen: boolean };
      expect(version.isFrozen).toBe(true);
    } finally {
      await extension.dispose();
    }
  });
});
