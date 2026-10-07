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

interface GenerationStates {
  state: string;
  remoteState: string;
  archivedBy: string | null;
}

interface RemoteStatePage {
  items: { sunoId: string }[];
  total: number;
  revision: number;
}

/** The app's base (the root or the sub-path), as the Songs page resolves it. */
async function appBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

/** The fixture clip as Suno would list it with these fields: one Create request's take `batchIndex`. */
function clip(id: string, title: string, workspace: string, stamp: string, batchIndex: number) {
  const copy = structuredClone(LIBRARY_CLIP) as Record<string, unknown> & {
    metadata: Record<string, unknown>;
    project: Record<string, unknown>;
  };
  copy.id = id;
  copy.title = title;
  copy.created_at = new Date(Date.now() - 3_600_000).toISOString();
  copy.batch_index = batchIndex;
  copy.project = { ...copy.project, id: workspace };
  copy.metadata = { ...copy.metadata, prompt: `[Verse]\nFollowed ${stamp}` };
  return copy;
}

/**
 * Uploads an export of `clips` and `trashedClips` with the extension's token, as the extension does;
 * `whole` says the sync read the whole library and the Trash to the end. Returns its ID once ready.
 */
async function uploadExport(
  extension: APIRequestContext,
  base: URL,
  token: string,
  workspace: { id: string; name: string },
  clips: Record<string, unknown>[],
  trashedClips: Record<string, unknown>[],
  whole: boolean,
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
      libraryComplete: whole,
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
    data: { partNumber: 1, clips, trashedClips },
  });
  expect(part.status()).toBe(200);
  const completed = await extension.post(`${exports}/${id}/complete`, { headers });
  expect(completed.status()).toBe(200);
  expect(((await completed.json()) as { state: string }).state).toBe('ready');
  return id;
}

/**
 * Sets to Skip every Suno state change of the export but those of `kept`: the shared containers hold
 * other tests' clips, which a whole-library sync that does not list them finds Remote Missing.
 */
async function skipOthers(page: Page, base: URL, id: string, kept: string[]): Promise<number> {
  const path = new URL(`api/v1/suno/exports/${id}/remote-states`, base).toString();
  const others: string[] = [];
  for (let number = 1; ; number++) {
    const listed = (await (
      await page.request.get(`${path}?pageSize=200&page=${String(number)}`)
    ).json()) as RemoteStatePage;
    others.push(
      ...listed.items.map((row) => row.sunoId).filter((sunoId) => !kept.includes(sunoId)),
    );
    if (number * 200 >= listed.total) {
      break;
    }
  }
  for (let start = 0; start < others.length; start += 1000) {
    const { revision } = (await (await page.request.get(path)).json()) as RemoteStatePage;
    const skipped = await page.request.patch(path, {
      headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(revision)}"` },
      data: { sunoIds: others.slice(start, start + 1000), apply: false },
    });
    expect(skipped.status()).toBe(200);
  }
  return others.length;
}

/** Confirms the export on its review page and waits for the result. */
async function confirmImport(page: Page) {
  await page.getByRole('button', { name: 'Confirm import' }).click();
  const asked = page.getByRole('dialog', { name: 'Confirm this import?' });
  await expect(asked).toBeVisible();
  await expectModalAccessibleInBothSchemes(page);
  await asked.getByRole('button', { name: 'Confirm import' }).click();
  await expect(page.getByRole('heading', { name: 'This import was confirmed' })).toBeVisible({
    timeout: 30_000,
  });
}

/** Lists the export's Suno state changes on its review page, those whose title holds `search`. */
async function openStateChanges(page: Page, id: string, search: string) {
  await page.goto(`./suno/imports/${id}`);
  await page.getByRole('combobox', { name: 'Class' }).selectOption('remote-state');
  await page.getByRole('textbox', { name: 'Search titles' }).fill(search);
  await expect(page.getByRole('table', { name: 'Suno state changes' })).toBeVisible();
}

function changeOf(page: Page, sunoId: string) {
  return page.locator(`tr[data-remote-state="${sunoId}"]`).getByTestId('remote-state-change');
}

/**
 * Walks #142's Demo on the shared containers with Suno IDs and titles of its own. 1. Two clips of one
 * Create request are imported. 2. A whole-library sync finds the first in Suno's Trash and the second
 * nowhere: the review's "Suno state changes" list one "In Suno Trash" and one Remote Missing (other
 * tests' clips, also missing, are set to Skip), and Confirm applies them. 3. The first Generation is
 * Archived; the second is Active with a Remote Missing badge. 4. A sync with both listed again brings
 * both back to present, and the first is Active again. Each visited state is scanned with axe.
 */
test.describe('Following Suno', () => {
  test('archives a clip trashed in Suno, marks a missing one, and brings both back', async ({
    page,
    playwright,
  }, testInfo) => {
    // Three syncs, each reviewed and confirmed, and every state scanned in both schemes.
    test.setTimeout(180_000);
    const stamp = `${String(Date.now()).slice(-7)}${testInfo.project.name}`;
    const workspace = { id: `ws-follow-${stamp}`, name: `Follow ${stamp}` };
    const base = await appBase(page);
    const credential = await page.request.post(new URL('api/v1/credentials', base).toString(), {
      headers: ANTIFORGERY_HEADERS,
      data: { name: `Following Suno ${stamp}`, kind: 'extension', scopes: ['suno.sync'] },
    });
    expect(credential.status()).toBe(201);
    const { token } = (await credential.json()) as { token: string };
    const extension = await playwright.request.newContext({ storageState: undefined });
    const generation = async (shortcode: string) =>
      (await (
        await page.request.get(new URL(`api/v1/generations/${shortcode}`, base).toString())
      ).json()) as GenerationStates;

    try {
      const trashedId = randomUUID();
      const missingId = randomUUID();
      const trashed = clip(trashedId, `Follow trashed ${stamp}`, workspace.id, stamp, 0);
      const missing = clip(missingId, `Follow missing ${stamp}`, workspace.id, stamp, 1);

      // 1. Two clips imported (a sync that did not read the whole library marks nothing missing).
      const first = await uploadExport(
        extension,
        base,
        token,
        workspace,
        [trashed, missing],
        [],
        false,
      );
      await page.goto(`./suno/imports/${first}`);
      await confirmImport(page);
      await expect(page.getByTestId('commit-outcomes')).toHaveText('2 records imported.');
      await page
        .getByTestId('commit-songs')
        .getByRole('link', { name: new RegExp(`Follow trashed ${stamp}`) })
        .click();
      const song = (await page.getByTestId('shortcode').first().textContent())?.trim() ?? '';
      const trashedGeneration = `${song}-v1-g1`;
      const missingGeneration = `${song}-v1-g2`;

      // 2. A whole-library sync: the first clip in Suno's Trash, the second in neither list.
      const second = await uploadExport(extension, base, token, workspace, [], [trashed], true);
      const others = await skipOthers(page, base, second, [trashedId, missingId]);
      await openStateChanges(page, second, stamp);
      await expect(changeOf(page, trashedId)).toHaveText('In Suno Trash: will be archived');
      await expect(changeOf(page, missingId)).toHaveText('Remote Missing');
      await expect(
        page.getByRole('checkbox', { name: `Apply: Follow trashed ${stamp}` }),
      ).toBeChecked();
      await expect(
        page.getByRole('checkbox', { name: `Apply: Follow missing ${stamp}` }),
      ).toBeChecked();
      await expectAccessibleInLightAndDark(page);
      await confirmImport(page);
      await expect(page.getByTestId('commit-remote-states')).toHaveText(
        others === 0
          ? 'Followed 2 Suno state changes.'
          : `Followed 2 Suno state changes; skipped ${String(others)}.`,
      );
      await expectAccessibleInLightAndDark(page);

      // 3. The first Generation is Archived (by sync); the second is Active with a Remote Missing badge.
      expect(await generation(trashedGeneration)).toEqual(
        expect.objectContaining({ state: 'archived', remoteState: 'trashed', archivedBy: 'sync' }),
      );
      expect(await generation(missingGeneration)).toEqual(
        expect.objectContaining({ state: 'active', remoteState: 'missing', archivedBy: null }),
      );
      await page.goto(`./songs/${song}/generations/${missingGeneration}`);
      const missingPanel = page.getByRole('dialog', { name: `Generation ${missingGeneration}` });
      await expect(missingPanel.getByTestId('generation-state')).toContainText('Active');
      await expect(missingPanel.getByTestId('generation-state')).toContainText('Remote Missing');
      await expectModalAccessibleInBothSchemes(page);
      await page.goto(`./songs/${song}/generations/${trashedGeneration}`);
      const trashedPanel = page.getByRole('dialog', { name: `Generation ${trashedGeneration}` });
      await expect(trashedPanel.getByTestId('generation-state')).toContainText('Archived');
      await expect(trashedPanel.getByTestId('in-suno-trash')).toHaveText('In Suno Trash');
      await expectModalAccessibleInBothSchemes(page);

      // 4. Both listed again: both are present, and the first is Active again.
      const third = await uploadExport(
        extension,
        base,
        token,
        workspace,
        [trashed, missing],
        [],
        false,
      );
      await openStateChanges(page, third, stamp);
      await expect(changeOf(page, trashedId)).toHaveText('Restored in Suno: will be reactivated');
      await expect(changeOf(page, missingId)).toHaveText('Restored in Suno');
      await expectAccessibleInLightAndDark(page);
      await confirmImport(page);
      expect(await generation(trashedGeneration)).toEqual(
        expect.objectContaining({ state: 'active', remoteState: 'present', archivedBy: null }),
      );
      expect(await generation(missingGeneration)).toEqual(
        expect.objectContaining({ state: 'active', remoteState: 'present', archivedBy: null }),
      );
    } finally {
      await extension.dispose();
    }
  });
});
