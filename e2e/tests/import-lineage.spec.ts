import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark } from '../support/a11y.ts';
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

/** The app's base (the root or the sub-path), as the Songs page resolves it. */
async function appBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

/** The fixture clip with these fields, and (for a cover) the clip it covers, as Suno names it. */
function clip(fields: {
  id: string;
  title: string;
  createdAt: string;
  lyrics: string;
  coverOf?: { id: string; title: string };
}): Record<string, unknown> {
  const copy = structuredClone(LIBRARY_CLIP) as Record<string, unknown> & {
    metadata: Record<string, unknown>;
  };
  copy.id = fields.id;
  copy.title = fields.title;
  copy.created_at = fields.createdAt;
  copy.batch_index = 0;
  delete copy.project;
  copy.metadata = { ...copy.metadata, prompt: fields.lyrics };
  if (fields.coverOf !== undefined) {
    copy.metadata = {
      ...copy.metadata,
      task: 'cover',
      cover_clip_id: fields.coverOf.id,
      edited_clip_id: fields.coverOf.id,
    };
    copy.clip_roots = { clips: [{ id: fields.coverOf.id, title: fields.coverOf.title }] };
  }
  return copy;
}

/** Uploads one export of `clips` with the extension's token, as the extension does; returns its ID once ready. */
async function uploadExport(
  extension: APIRequestContext,
  base: URL,
  token: string,
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
      libraryComplete: true,
      trashedComplete: true,
      workspaces: [],
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

/** Confirms the import open on the page and waits for it to end. */
async function confirm(page: Page) {
  await page.getByRole('button', { name: 'Confirm import' }).click();
  const asked = page.getByRole('dialog', { name: 'Confirm this import?' });
  await asked.getByRole('button', { name: 'Confirm import' }).click();
  await expect(page.getByRole('heading', { name: 'This import was confirmed' })).toBeVisible({
    timeout: 30_000,
  });
}

/**
 * Walks #153's Demo on the shared containers with Suno IDs and titles of its own: 1. an export holding
 * a cover whose source it lacks shows "Cover of <title>: not in this sync" in the review, and is
 * confirmed; 2. the new Version's Sources show the source Not imported, with its Suno title; 3. an export
 * holding the source too lists the cover Already linked, and is confirmed; 4. the cover's source now
 * links to the imported Generation, and the two Songs show the Cover relationship. Each visited state is
 * scanned with axe.
 */
test.describe('Lineage in the import review and on Versions', () => {
  test('shows a cover’s source Not imported, then links it once the source is imported', async ({
    page,
    playwright,
  }, testInfo) => {
    // Two imports, each followed to its end.
    test.setTimeout(120_000);
    const stamp = `${String(Date.now()).slice(-7)}${testInfo.project.name}`;
    const coverTitle = `Lineage cover ${stamp}`;
    const sourceTitle = `Lineage original ${stamp}`;
    const source = { id: randomUUID(), title: sourceTitle };
    const base = await appBase(page);
    const credential = await page.request.post(new URL('api/v1/credentials', base).toString(), {
      headers: ANTIFORGERY_HEADERS,
      data: { name: `Import lineage ${stamp}`, kind: 'extension', scopes: ['suno.sync'] },
    });
    expect(credential.status()).toBe(201);
    const { token } = (await credential.json()) as { token: string };
    const extension = await playwright.request.newContext({ storageState: undefined });

    try {
      const at = Date.now() - 3_600_000;
      const cover = clip({
        id: randomUUID(),
        title: coverTitle,
        createdAt: new Date(at).toISOString(),
        lyrics: `[Verse]\nCovered ${stamp}`,
        coverOf: source,
      });

      // 1. The review shows the cover's source, not in this sync; Confirm.
      const first = await uploadExport(extension, base, token, [cover]);
      await page.goto(`./suno/imports/${first}`);
      const coverRow = page.locator(`tr[data-record="${String(cover.id)}"]`);
      await expect(coverRow.getByTestId('lineage-line')).toHaveText(
        `Cover of ${sourceTitle}: not in this sync`,
      );
      await expectAccessibleInLightAndDark(page);
      await confirm(page);

      // 2. The new Version's Sources show the source Not imported, with its Suno title.
      await page
        .getByTestId('commit-songs')
        .getByRole('link', { name: new RegExp(coverTitle) })
        .click();
      await expect(page.getByRole('heading', { level: 2, name: coverTitle })).toBeVisible();
      const sources = page.getByTestId('sources-section');
      await expect(sources.getByTestId('source-title')).toHaveText(sourceTitle);
      await expect(sources.getByTestId('source-availability')).toHaveText('Not imported');
      await expect(
        sources.getByRole('button', { name: `Add to the ignore list: ${sourceTitle}` }),
      ).toBeVisible();
      await expectAccessibleInLightAndDark(page);
      const coverSong = page.url();

      // 3. An export holding the source too: the cover is Already linked; Confirm the source.
      const second = await uploadExport(extension, base, token, [
        cover,
        clip({
          id: source.id,
          title: sourceTitle,
          createdAt: new Date(at - 60_000).toISOString(),
          lyrics: `[Verse]\nOriginal ${stamp}`,
        }),
      ]);
      await page.goto(`./suno/imports/${second}`);
      await expect(coverRow.getByTestId('record-class')).toHaveText('Already linked');
      await expect(coverRow.getByTestId('lineage-line')).toHaveText(
        `Cover of ${sourceTitle}: in this sync`,
      );
      await expectAccessibleInLightAndDark(page);
      await confirm(page);

      // 4. The cover's source links to the imported Generation; the Songs show the Cover relationship.
      await page.goto(coverSong);
      await expect(page.getByRole('heading', { level: 2, name: coverTitle })).toBeVisible();
      const link = page.getByTestId('sources-section').getByRole('link', { name: sourceTitle });
      await expect(link).toHaveAttribute('href', /\/generations\//);
      await expect(
        page.getByTestId('sources-section').getByTestId('source-availability'),
      ).toHaveCount(0);
      await page.getByRole('button', { name: 'Details', exact: true }).click();
      const related = page
        .getByRole('group', { name: 'Related' })
        .locator('[data-relationship-group="Cover"]');
      await expect(related.getByRole('link', { name: sourceTitle })).toBeVisible();
      await expectAccessibleInLightAndDark(page);

      // The link opens the source's Generation; the open Details drawer (kept open across Songs)
      // covers the page, so the link is followed directly.
      const href = (await link.getAttribute('href')) ?? '';
      await page.goto(new URL(href, page.url()).toString());
      await expect(page.getByRole('heading', { level: 2, name: sourceTitle })).toBeVisible();
      await expect(
        page
          .getByRole('group', { name: 'Related' })
          .locator('[data-relationship-group="Covered by"]')
          .getByRole('link', { name: coverTitle }),
      ).toBeVisible();
    } finally {
      await extension.dispose();
    }
  });
});
