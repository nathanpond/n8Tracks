import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  revision: number;
  sunoWorkspace: { id: string } | null;
}

/** The app's base (the root or the sub-path), as the Songs page resolves it. */
async function appBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

/** Reports `workspaces` (Suno's raw projects) as the extension does, with its token and no cookies. */
async function report(
  request: APIRequestContext,
  base: URL,
  token: string,
  workspaces: Record<string, unknown>[],
): Promise<void> {
  const response = await request.put(
    new URL('api/v1/suno/workspaces/discovered', base).toString(),
    {
      headers: { Authorization: `Bearer ${token}`, Accept: 'application/json' },
      data: { complete: false, workspaces },
    },
  );
  expect(response.status()).toBe(200);
}

/** Creates a Song titled `title` and puts it in the workspace with Suno ID `workspace`. */
async function songIn(page: Page, base: URL, title: string, workspace: string): Promise<Song> {
  const created = await page.request.post(new URL('api/v1/songs', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(created.status()).toBe(201);
  const song = (await created.json()) as Song;
  const patched = await page.request.patch(
    new URL(`api/v1/songs/${song.shortcode}`, base).toString(),
    {
      headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(song.revision)}"` },
      data: { sunoWorkspaceId: workspace },
    },
  );
  expect(patched.status()).toBe(200);
  return (await patched.json()) as Song;
}

async function workspaceOf(page: Page, base: URL, shortcode: string): Promise<string | undefined> {
  const response = await page.request.get(new URL(`api/v1/songs/${shortcode}`, base).toString());
  expect(response.status()).toBe(200);
  return ((await response.json()) as Song).sunoWorkspace?.id;
}

/**
 * Walks #151's Demo on the shared containers, with workspaces of its own (stamped Suno IDs): an
 * extension token reports two workspaces and Settings → Suno workspaces lists them (step 1); the
 * first is opened, all its Songs selected and moved to the second after a confirmation stating the
 * count (step 2); the first then shows no Songs and the second shows them (step 3). Each visited
 * state is scanned with axe.
 */
test.describe('Suno workspaces page', () => {
  test('moves all of a workspace’s Songs to another after confirming how many', async ({
    page,
    playwright,
  }) => {
    const stamp = String(Date.now()).slice(-7);
    const first = {
      id: `00000000-0000-4000-8000-3${stamp.padStart(11, '0')}`,
      name: `Moving out ${stamp}`,
    };
    const second = {
      id: `00000000-0000-4000-8000-4${stamp.padStart(11, '0')}`,
      name: `Moving in ${stamp}`,
    };
    const base = await appBase(page);
    const credential = await page.request.post(new URL('api/v1/credentials', base).toString(), {
      headers: ANTIFORGERY_HEADERS,
      data: { name: `Workspace page ${stamp}`, kind: 'extension', scopes: ['suno.sync'] },
    });
    expect(credential.status()).toBe(201);
    const { token } = (await credential.json()) as { token: string };
    const extension = await playwright.request.newContext({ storageState: undefined });

    try {
      // 1. Two workspaces reported with an extension token; Settings → Suno workspaces lists them.
      await report(extension, base, token, [
        { id: first.id, name: first.name, description: '' },
        { id: second.id, name: second.name, description: '' },
      ]);
      const songs = [
        await songIn(page, base, `Workspace song A ${stamp}`, first.id),
        await songIn(page, base, `Workspace song B ${stamp}`, first.id),
      ];

      await page.goto('./songs');
      await page
        .getByRole('navigation', { name: 'Main' })
        .getByRole('link', { name: 'Suno workspaces' })
        .click();
      await expect(page.getByRole('heading', { level: 2, name: 'Suno workspaces' })).toBeVisible();
      const list = page.getByRole('table', { name: 'Suno workspaces' });
      const firstRow = list.getByTestId('workspace-row').filter({ hasText: first.name });
      await expect(firstRow).toContainText('Available');
      await expect(firstRow.getByRole('cell').last()).toHaveText('2');
      await expect(
        list.getByTestId('workspace-row').filter({ hasText: second.name }).getByRole('cell').last(),
      ).toHaveText('0');
      await expectAccessibleInLightAndDark(page);

      // 2. Open the first, select all its Songs, move them to the second, and confirm the count.
      await list.getByRole('link', { name: first.name }).click();
      await expect(page.getByRole('heading', { level: 2, name: first.name })).toBeVisible();
      await expect(page.getByTestId('workspace-song')).toHaveCount(2);
      await page.getByRole('checkbox', { name: 'Select all 2 Songs in this workspace' }).check();
      await page.getByRole('combobox', { name: 'Move to' }).selectOption({ label: second.name });
      await expectAccessibleInLightAndDark(page);
      await page.getByRole('button', { name: 'Move 2 Songs' }).click();
      const dialog = page.getByRole('dialog', { name: 'Move 2 Songs?' });
      await expect(dialog.getByTestId('move-count')).toContainText(
        `2 Songs will move from ${first.name} to ${second.name}.`,
      );
      await expectModalAccessibleInBothSchemes(page);
      await dialog.getByRole('button', { name: 'Move 2 Songs' }).click();
      await expect(page.getByTestId('songs-moved')).toHaveText(`Moved 2 Songs to ${second.name}.`);
      for (const song of songs) {
        expect(await workspaceOf(page, base, song.shortcode)).toBe(second.id);
      }

      // 3. The first shows no Songs; the second shows them.
      await expect(page.getByTestId('no-workspace-songs')).toBeVisible();
      await expect(page.getByTestId('workspace-summary')).toContainText('0 Songs');
      await expectAccessibleInLightAndDark(page);
      await page.getByTestId('songs-moved').getByRole('link', { name: second.name }).click();
      await expect(page.getByRole('heading', { level: 2, name: second.name })).toBeVisible();
      await expect(page.getByTestId('workspace-song')).toHaveCount(2);
      await expect(page.getByTestId('workspace-summary')).toContainText('2 Songs');
      await expectAccessibleInLightAndDark(page);
    } finally {
      await extension.dispose();
    }
  });
});
