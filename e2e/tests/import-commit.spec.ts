import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { solidPng } from '../support/images.ts';
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
  sunoId: string | null;
  title: string | null;
  artwork: unknown;
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
  workspace: string;
  createdAt: string;
  batchIndex: number;
  lyrics: string;
}): Record<string, unknown> {
  const copy = structuredClone(LIBRARY_CLIP) as Record<string, unknown> & {
    metadata: Record<string, unknown>;
    project: Record<string, unknown>;
  };
  copy.id = fields.id;
  copy.title = fields.title;
  copy.created_at = fields.createdAt;
  copy.batch_index = fields.batchIndex;
  copy.project = { ...copy.project, id: fields.workspace };
  copy.metadata = { ...copy.metadata, prompt: fields.lyrics };
  return copy;
}

/**
 * Uploads one export of `clips` with the extension's token, as the extension does, then a cover image
 * for each; returns the export's ID once it is ready.
 */
async function uploadExport(
  extension: APIRequestContext,
  base: URL,
  token: string,
  workspace: { id: string; name: string },
  clips: Record<string, unknown>[],
  stamp: string,
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
      // containers Remote Missing (#142), which this walk is not about.
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
  for (const [index, each] of clips.entries()) {
    const image = await extension.put(`${exports}/${id}/artwork/${String(each.id)}`, {
      headers,
      multipart: {
        file: {
          name: 'cover.png',
          mimeType: 'image/png',
          buffer: solidPng(64, 64, `${stamp} ${String(index)}`, [40 * index, 80, 160]),
        },
      },
    });
    expect([200, 409]).toContain(image.status());
  }
  return id;
}

/**
 * Walks #140's Demo on the shared containers with Suno IDs and titles of its own. An export of two clips
 * of one Create request (the same inputs, each with a cover image) is uploaded with an extension token,
 * as `curl` would. 1. Confirm on the review page asks once more with the same numbers, then the page
 * follows the import to its end and links to what was created. 2. The new Song has Version 1, frozen,
 * with two Generations showing Suno's titles and artwork. 3. The same export uploaded again is all
 * Already linked, and the summary says there is nothing to import. Each visited state is scanned with axe.
 */
test.describe('Import commit', () => {
  test('confirms an import, follows it to the end, and finds it linked the next time', async ({
    page,
    playwright,
  }, testInfo) => {
    const stamp = `${String(Date.now()).slice(-7)}${testInfo.project.name}`;
    const workspace = { id: `ws-commit-${stamp}`, name: `Commit ${stamp}` };
    const title = `Commit morning ${stamp}`;
    const base = await appBase(page);
    const credential = await page.request.post(new URL('api/v1/credentials', base).toString(), {
      headers: ANTIFORGERY_HEADERS,
      data: { name: `Import commit ${stamp}`, kind: 'extension', scopes: ['suno.sync'] },
    });
    expect(credential.status()).toBe(201);
    const { token } = (await credential.json()) as { token: string };
    const extension = await playwright.request.newContext({ storageState: undefined });

    try {
      const at = new Date(Date.now() - 3_600_000).toISOString();
      const clips = [
        clip({
          id: randomUUID(),
          title,
          workspace: workspace.id,
          createdAt: at,
          batchIndex: 0,
          lyrics: `[Verse]\nCommitted ${stamp}`,
        }),
        clip({
          id: randomUUID(),
          title: `${title} (2)`,
          workspace: workspace.id,
          createdAt: at,
          batchIndex: 1,
          lyrics: `[Verse]\nCommitted ${stamp}`,
        }),
      ];
      const id = await uploadExport(extension, base, token, workspace, clips, stamp);

      // 1. Confirm asks once more with the same numbers, then the page follows the import.
      await page.goto(`./suno/imports/${id}`);
      await expect(page.getByTestId('summary-creates')).toHaveText(
        'Create 1 Song, 1 Version, and 2 Generations.',
      );
      await expectAccessibleInLightAndDark(page);
      await page.getByRole('button', { name: 'Confirm import' }).click();
      const asked = page.getByRole('dialog', { name: 'Confirm this import?' });
      await expect(asked).toBeVisible();
      await expect(asked.getByTestId('confirm-summary')).toContainText(
        'Create 1 Song, 1 Version, and 2 Generations.',
      );
      await expectModalAccessibleInBothSchemes(page);
      await asked.getByRole('button', { name: 'Confirm import' }).click();
      await expect(page.getByRole('heading', { name: 'This import was confirmed' })).toBeVisible({
        timeout: 30_000,
      });
      await expect(page.getByTestId('commit-created')).toHaveText(
        'Created 1 Song, 1 Version, and 2 Generations.',
      );
      await expect(page.getByTestId('commit-outcomes')).toHaveText('2 records imported.');
      await expectAccessibleInLightAndDark(page);

      // Leaving and coming back shows the same result.
      await page.goto('./songs');
      await page.goto(`./suno/imports/${id}`);
      await expect(page.getByTestId('commit-created')).toHaveText(
        'Created 1 Song, 1 Version, and 2 Generations.',
      );

      // 2. The new Song: Version 1, frozen, with two Generations showing Suno's titles and artwork.
      const songLink = page
        .getByTestId('commit-songs')
        .getByRole('link', { name: new RegExp(title) });
      await songLink.click();
      await expect(page.getByRole('heading', { level: 2, name: title })).toBeVisible();
      const shortcode = (await page.getByTestId('shortcode').first().textContent())?.trim() ?? '';
      const version = (await (
        await page.request.get(new URL(`api/v1/versions/${shortcode}-v1`, base).toString())
      ).json()) as { isFrozen: boolean };
      expect(version.isFrozen).toBe(true);
      const generations = (
        (await (
          await page.request.get(new URL(`api/v1/songs/${shortcode}/generations`, base).toString())
        ).json()) as { items: Generation[] }
      ).items;
      expect(generations.map((generation) => generation.title)).toEqual([title, `${title} (2)`]);
      expect(generations.every((generation) => generation.artwork !== null)).toBe(true);
      await expectAccessibleInLightAndDark(page);

      // 3. The same export again: every record is already linked, and there is nothing to import.
      const again = await uploadExport(extension, base, token, workspace, clips, `${stamp} again`);
      await page.goto(`./suno/imports/${again}`);
      await expect(page.getByTestId('record-class')).toHaveText([
        'Already linked',
        'Already linked',
      ]);
      await expect(
        page.getByRole('button', { name: 'Confirm import' }),
      ).toHaveAccessibleDescription(
        'There is nothing to do: no record is set to be imported or added to the ignore list.',
      );
      await expectAccessibleInLightAndDark(page);
      await page.getByRole('button', { name: 'Discard import' }).click();
      await page.getByRole('button', { name: 'Discard', exact: true }).click();
      await expect(page.getByRole('heading', { name: 'This import was discarded' })).toBeVisible();
    } finally {
      await extension.dispose();
    }
  });
});
