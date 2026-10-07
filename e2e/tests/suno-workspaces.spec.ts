import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark } from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  currentVersion: { id: string };
  sunoWorkspace: { id: string; name: string; state: string } | null;
}

interface Workspace {
  id: string;
  name: string;
  state: 'available' | 'unavailable';
  songCount: number;
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
  complete: boolean,
  workspaces: Record<string, unknown>[],
): Promise<void> {
  const response = await request.put(
    new URL('api/v1/suno/workspaces/discovered', base).toString(),
    {
      headers: { Authorization: `Bearer ${token}`, Accept: 'application/json' },
      data: { complete, workspaces },
    },
  );
  expect(response.status()).toBe(200);
}

async function listed(page: Page, base: URL): Promise<Workspace[]> {
  const response = await page.request.get(new URL('api/v1/suno/workspaces', base).toString());
  expect(response.status()).toBe(200);
  return ((await response.json()) as { items: Workspace[] }).items;
}

async function readSong(page: Page, base: URL, shortcode: string): Promise<Song> {
  const response = await page.request.get(new URL(`api/v1/songs/${shortcode}`, base).toString());
  expect(response.status()).toBe(200);
  return (await response.json()) as Song;
}

/** Opens the Song's Details panel unless it is open already (the page remembers it). */
async function openDetails(page: Page): Promise<void> {
  const control = page.getByRole('button', { name: 'Details', exact: true });
  if ((await control.getAttribute('aria-expanded')) !== 'true') {
    await control.click();
  }
  await expect(page.getByTestId('song-workspace')).toBeVisible();
}

/**
 * Walks #129's Demo on the shared containers, with workspaces of its own (stamped Suno IDs): an
 * extension token reports two workspaces, which the list shows (step 1); a Song is put in the first
 * from its Details (step 2); a complete list with only the second makes the first Unavailable and
 * the Song shows a warning, keeping it (step 3); both reported again, the first is Available by
 * itself and the warning goes (step 4). Each visited state is scanned with axe.
 */
test.describe('Suno workspaces', () => {
  test('a Song keeps its workspace when it disappears from Suno, and the warning goes when it is back', async ({
    page,
    playwright,
  }) => {
    const stamp = String(Date.now()).slice(-7);
    const first = {
      id: `00000000-0000-4000-8000-1${stamp.padStart(11, '0')}`,
      name: `Studio ${stamp}`,
    };
    const second = {
      id: `00000000-0000-4000-8000-2${stamp.padStart(11, '0')}`,
      name: `Demos ${stamp}`,
    };
    const base = await appBase(page);
    const credential = await page.request.post(new URL('api/v1/credentials', base).toString(), {
      headers: ANTIFORGERY_HEADERS,
      data: { name: `Workspaces ${stamp}`, kind: 'extension', scopes: ['suno.sync'] },
    });
    expect(credential.status()).toBe(201);
    const { token } = (await credential.json()) as { token: string };
    const extension = await playwright.request.newContext({ storageState: undefined });

    try {
      // 1. The extension reports two workspaces; the list shows them, Available.
      await report(extension, base, token, false, [
        { id: first.id, name: first.name, description: '', clip_count: 0 },
        { id: second.id, name: second.name, description: '', clip_count: 3 },
      ]);
      const reported = (await listed(page, base)).filter((workspace) =>
        [first.id, second.id].includes(workspace.id),
      );
      expect(reported.map((workspace) => [workspace.name, workspace.state])).toEqual([
        [second.name, 'available'],
        [first.name, 'available'],
      ]);

      // 2. A Song is put in the first from its Details.
      const created = await page.request.post(new URL('api/v1/songs', base).toString(), {
        headers: ANTIFORGERY_HEADERS,
        data: { title: `In a workspace ${stamp}` },
      });
      expect(created.status()).toBe(201);
      const song = (await created.json()) as Song;
      await page.goto(`./songs/${song.shortcode}`);
      await openDetails(page);
      const select = page
        .getByTestId('song-workspace')
        .getByRole('combobox', { name: 'Workspace' });
      await expect(select).toBeEnabled();
      await select.selectOption({ label: first.name });
      await expect
        .poll(async () => (await readSong(page, base, song.shortcode)).sunoWorkspace?.id)
        .toBe(first.id);
      await expect(select).toHaveValue(first.id);
      await expect(page.getByTestId('workspace-unavailable')).toHaveCount(0);
      await expectAccessibleInLightAndDark(page);

      // 3. A complete list with only the second: the first is Unavailable, the Song keeps it and warns.
      await report(extension, base, token, true, [
        { id: second.id, name: second.name, description: '' },
      ]);
      await page.reload();
      await openDetails(page);
      await expect(page.getByTestId('workspace-unavailable')).toHaveCount(2);
      await expect(page.getByTestId('workspace-warning')).toContainText(
        `${first.name} is unavailable`,
      );
      await expect(select).toHaveValue(first.id);
      const kept = await readSong(page, base, song.shortcode);
      expect(kept.sunoWorkspace).toEqual({ id: first.id, name: first.name, state: 'unavailable' });
      expect(kept.currentVersion.id).toBe(song.currentVersion.id);
      expect(
        (await listed(page, base)).find((workspace) => workspace.id === first.id),
      ).toMatchObject({
        state: 'unavailable',
        songCount: 1,
      });
      await expectAccessibleInLightAndDark(page);

      // 4. Both reported again: the first is Available by itself, and the warning goes.
      await report(extension, base, token, true, [
        { id: first.id, name: first.name, description: '' },
        { id: second.id, name: second.name, description: '' },
      ]);
      await page.reload();
      await openDetails(page);
      await expect(select).toHaveValue(first.id);
      await expect(page.getByTestId('workspace-unavailable')).toHaveCount(0);
      await expect(page.getByTestId('workspace-warning')).toHaveCount(0);
      expect((await listed(page, base)).find((workspace) => workspace.id === first.id)?.state).toBe(
        'available',
      );
      await expectAccessibleInLightAndDark(page);

      // The workspace can be cleared from the Details too.
      await select.selectOption({ label: 'None' });
      await expect
        .poll(async () => (await readSong(page, base, song.shortcode)).sunoWorkspace)
        .toBeNull();
    } finally {
      await extension.dispose();
    }
  });
});
