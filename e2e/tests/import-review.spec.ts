import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { seedGeneration } from '../support/seeding.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  revision: number;
}

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

/** The fixture clip as Suno would list it with the fields given (a fresh Suno ID). */
function clip(fields: {
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
  copy.id = randomUUID();
  copy.title = fields.title;
  copy.created_at = fields.createdAt;
  copy.batch_index = fields.batchIndex;
  copy.project = { ...copy.project, id: fields.workspace };
  copy.metadata = { ...copy.metadata, prompt: fields.lyrics };
  return copy;
}

/** A clip with only an ID, a status, and a title, as a Generation can keep one. */
function minimalClip(title: string): Record<string, unknown> {
  return { id: randomUUID(), status: 'complete', title };
}

/** Creates a Song titled `title` as the signed-in user. */
async function createSong(page: Page, base: URL, title: string): Promise<Song> {
  const created = await page.request.post(new URL('api/v1/songs', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(created.status()).toBe(201);
  return (await created.json()) as Song;
}

/** Uploads one export of `clips` with the extension's token, as the extension does, and returns its ID once ready. */
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
      libraryComplete: true,
      trashedComplete: true,
      workspaces: [{ id: workspace.id, name: workspace.name, description: '' }],
      workspacesComplete: false,
      playlists: [],
      libraryFilters: { disliked: 'False', stem: { presence: 'False' } },
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

/** How many Songs are titled `title` exactly (ignoring case). */
async function songsTitled(page: Page, base: URL, title: string): Promise<number> {
  const response = await page.request.get(
    new URL(`api/v1/songs?title=${encodeURIComponent(title)}`, base).toString(),
  );
  expect(response.status()).toBe(200);
  return ((await response.json()) as { total: number }).total;
}

function row(page: Page, title: string) {
  return page
    .getByRole('table', { name: 'Records in this import' })
    .getByRole('row')
    .filter({ has: page.getByRole('rowheader', { name: title, exact: true }) });
}

/**
 * Walks #139's Demo on the shared containers with Suno IDs and titles of its own: an export built from
 * the TS-003 fixture clip is uploaded with an extension token, as `curl` would (three new clips in two
 * Create requests, one linked, and one deleted in n8Tracks; the ignore list has no writer before #143, so
 * a deleted record stands in for the ignored one); the review shows two proposals for new Songs (step 2);
 * the second group goes to a new Version of an existing Song and one clip is set to Don't copy, by
 * keyboard (step 3); the summary is read, the page reloaded, and the choices and summary are as left,
 * with no new Song in the catalog (step 4). The import is then discarded after a confirmation. Each
 * visited state is scanned with axe.
 */
test.describe('Import review', () => {
  test('changes what an import will do and keeps it across a reload, with no new Song', async ({
    page,
    playwright,
  }, testInfo) => {
    const stamp = `${String(Date.now()).slice(-7)}${testInfo.project.name}`;
    const workspace = { id: `ws-review-${stamp}`, name: `Review ${stamp}` };
    const morning = `Review morning ${stamp}`;
    const evening = `Review evening ${stamp}`;
    const base = await appBase(page);
    const credential = await page.request.post(new URL('api/v1/credentials', base).toString(), {
      headers: ANTIFORGERY_HEADERS,
      data: { name: `Import review ${stamp}`, kind: 'extension', scopes: ['suno.sync'] },
    });
    expect(credential.status()).toBe(201);
    const { token } = (await credential.json()) as { token: string };
    const extension = await playwright.request.newContext({ storageState: undefined });

    try {
      // 1. The catalog: an existing Song, a Generation the export holds again, and one deleted since.
      const existing = await createSong(page, base, `Existing ${stamp}`);
      const held = minimalClip(`Held ${stamp}`);
      const holder = await createSong(page, base, `Holder ${stamp}`);
      await seedGeneration(testInfo, `${holder.shortcode}-v1`, JSON.stringify(held));
      const gone = minimalClip(`Removed ${stamp}`);
      const remover = await createSong(page, base, `Remover ${stamp}`);
      const goneShortcode = await seedGeneration(
        testInfo,
        `${remover.shortcode}-v1`,
        JSON.stringify(gone),
      );
      const generationUrl = new URL(`api/v1/generations/${goneShortcode}`, base).toString();
      const generation = (await (await page.request.get(generationUrl)).json()) as {
        revision: number;
      };
      const deleted = await page.request.delete(generationUrl, {
        headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(generation.revision)}"` },
      });
      expect(deleted.status()).toBe(200);

      // Two Create requests: two clips of the same inputs a moment apart, and one a minute later.
      const at = Date.now() - 3_600_000;
      await uploadExport(extension, base, token, workspace, [
        clip({
          title: morning,
          workspace: workspace.id,
          createdAt: new Date(at).toISOString(),
          batchIndex: 0,
          lyrics: `[Verse]\nMorning ${stamp}`,
        }),
        clip({
          title: `${morning} (2)`,
          workspace: workspace.id,
          createdAt: new Date(at).toISOString(),
          batchIndex: 1,
          lyrics: `[Verse]\nMorning ${stamp}`,
        }),
        clip({
          title: evening,
          workspace: workspace.id,
          createdAt: new Date(at + 60_000).toISOString(),
          batchIndex: 0,
          lyrics: `[Verse]\nEvening ${stamp}`,
        }),
        held,
        gone,
      ]);

      // 2. The Suno import entry opens the review: two proposals for new Songs.
      await page.goto('./songs');
      await page
        .getByRole('navigation', { name: 'Main' })
        .getByRole('link', { name: 'Suno import', exact: true })
        .click();
      await expect(
        page.getByRole('heading', { level: 2, name: 'Review Suno import' }),
      ).toBeVisible();
      await expect(page.getByTestId('library-excluded')).toContainText('disliked songs, stems');
      const headings = page.getByTestId('group-heading');
      await expect(headings).toHaveText([
        `New Song “${evening}” (1 record)`,
        `New Song “${morning}” (2 records)`,
      ]);
      await expect(row(page, `Held ${stamp}`).getByRole('checkbox')).toHaveCount(0);
      await expect(
        row(page, `Held ${stamp}`).getByRole('link', { name: /^Generation / }),
      ).toBeVisible();
      await expect(row(page, `Removed ${stamp}`).getByTestId('record-class')).toHaveText(
        'Deleted in n8Tracks',
      );
      await expect(row(page, `Removed ${stamp}`).getByTestId('record-choice')).toHaveText(
        'Skip this time',
      );
      await expect(page.getByTestId('summary-creates')).toHaveText(
        'Create 2 Songs, 2 Versions, and 3 Generations.',
      );
      await expectAccessibleInLightAndDark(page);

      // 3. The second group goes to a new Version of the existing Song.
      await row(page, evening).getByRole('checkbox').check();
      await page.getByRole('radio', { name: 'An existing Song' }).check();
      await page.getByRole('textbox', { name: 'Find the Song' }).fill(`Existing ${stamp}`);
      await page.getByRole('option', { name: new RegExp(`Existing ${stamp}`) }).click();
      await page.getByRole('radio', { name: 'A new Version' }).check();
      await expect(page.getByRole('combobox', { name: 'Version number' })).toHaveValue('2');
      await expectAccessibleInLightAndDark(page);
      await page.getByRole('button', { name: 'Apply to 1 record' }).click();
      await expect(row(page, evening).getByTestId('record-choice')).toHaveText(
        `New Version 2 of ${existing.shortcode} “Existing ${stamp}”`,
      );

      // One clip set to Don't copy, without a pointer: the table and the bulk controls take the keyboard.
      await row(page, `${morning} (2)`).getByRole('checkbox').focus();
      await page.keyboard.press('Space');
      await expect(page.getByTestId('selection')).toHaveText(
        'Selected: 1 record. Records already in n8Tracks are never selected.',
      );
      await page.getByRole('radio', { name: 'Import', exact: true }).focus();
      await page.keyboard.press('ArrowRight');
      await expect(page.getByRole('radio', { name: 'Don’t copy' })).toBeChecked();
      await page.getByRole('button', { name: 'Apply to 1 record' }).focus();
      await page.keyboard.press('Enter');
      await expect(row(page, `${morning} (2)`).getByTestId('record-choice')).toHaveText(
        'Don’t copy',
      );

      // 4. The summary, then the same after a reload; and no new Song in the catalog.
      const expectAsLeft = async () => {
        await expect(page.getByTestId('summary-creates')).toHaveText(
          'Create 1 Song, 2 Versions, and 2 Generations.',
        );
        await expect(page.getByTestId('summary-ignored')).toHaveText(
          'Not copy 1 record (Don’t copy: on the ignore list).',
        );
        await expect(page.getByTestId('summary-skipped')).toHaveText(
          'Leave 2 records for a later sync (Skip this time).',
        );
        await expect(page.getByTestId('choices-valid')).toHaveText('Every choice is valid.');
        await expect(page.getByRole('button', { name: 'Confirm import' })).toBeEnabled();
        await expect(row(page, morning).getByTestId('record-choice')).toHaveText(
          `New Song “${morning}”`,
        );
        await expect(row(page, `${morning} (2)`).getByTestId('record-choice')).toHaveText(
          'Don’t copy',
        );
        await expect(row(page, evening).getByTestId('record-choice')).toHaveText(
          `New Version 2 of ${existing.shortcode} “Existing ${stamp}”`,
        );
      };
      await expectAsLeft();
      await page.reload();
      await expect(page.getByRole('table', { name: 'Records in this import' })).toBeVisible();
      await expectAsLeft();
      expect(await songsTitled(page, base, morning)).toBe(0);
      expect(await songsTitled(page, base, evening)).toBe(0);
      await expectAccessibleInLightAndDark(page);

      // Discarding asks first, and then the page shows only that the import was discarded.
      await page.getByRole('button', { name: 'Discard import' }).click();
      await expect(page.getByRole('dialog', { name: 'Discard this import?' })).toBeVisible();
      await expectModalAccessibleInBothSchemes(page);
      await page.getByRole('button', { name: 'Discard', exact: true }).click();
      await expect(page.getByRole('heading', { name: 'This import was discarded' })).toBeVisible();
      await expect(page.getByRole('table')).toHaveCount(0);
      await expectAccessibleInLightAndDark(page);
      expect(await songsTitled(page, base, morning)).toBe(0);
    } finally {
      await extension.dispose();
    }
  });
});
