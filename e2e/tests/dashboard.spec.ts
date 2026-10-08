import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark } from '../support/a11y.ts';
import {
  removeContainers,
  startContainer,
  waitForHealth,
  type Target,
} from '../support/containers.ts';
import { seedGenerationIn } from '../support/seeding.ts';
import { ANTIFORGERY_HEADERS, signInWithTheForm, userMenu } from '../support/session.ts';
import { completeSetup, TEST_ADMIN } from '../support/setup.ts';
import { FRESH_NAME, FRESH_PORT, FRESH_URL } from '../support/targets.ts';

const fresh: Target = {
  name: FRESH_NAME,
  port: FRESH_PORT,
  url: FRESH_URL,
  media: true,
  expectedStatus: 'healthy',
};

/** The seeded Archived workflow state. */
const ARCHIVED_STATE = '01a10a6e-dc86-7006-8000-000000000007';

interface Song {
  id: string;
  shortcode: string;
  title: string;
  revision: number;
  state: { id: string; name: string };
  currentVersion: { shortcode: string };
}

async function createSong(page: Page, title: string): Promise<Song> {
  const created = await page.request.post(`${FRESH_URL}api/v1/songs`, {
    data: { title },
    headers: ANTIFORGERY_HEADERS,
  });
  expect(created.status(), await created.text()).toBe(201);
  return (await created.json()) as Song;
}

async function readSong(page: Page, shortcode: string): Promise<Song> {
  const response = await page.request.get(`${FRESH_URL}api/v1/songs/${shortcode}`);
  expect(response.status()).toBe(200);
  return (await response.json()) as Song;
}

async function editSong(page: Page, shortcode: string, change: object): Promise<void> {
  const song = await readSong(page, shortcode);
  const edited = await page.request.patch(`${FRESH_URL}api/v1/songs/${song.id}`, {
    data: change,
    headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(song.revision)}"` },
  });
  expect(edited.status(), await edited.text()).toBe(200);
}

function section(page: Page, name: string) {
  return page.getByRole('region', { name });
}

/** The number at the front of a count link's text ("2 Songs"). */
async function countOf(link: Locator): Promise<number> {
  return Number.parseInt((await link.textContent()) ?? '', 10);
}

/**
 * Walks #228's Demo on a container of its own, so every count is known: a new instance opens on the
 * welcome; once there are Songs, signing in opens the dashboard with the Song changed last at the
 * top of Recently edited (an archived one left out); a workflow state's count opens the Songs table
 * filtered to it with the same number, and so does Without a Selected Generation. Each state is
 * scanned with axe in light and dark.
 */
test.describe('the dashboard', { tag: '@root-only' }, () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  let work: string | undefined;

  test.beforeEach(async () => {
    test.setTimeout(180_000);
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-dashboard-'));
    await mkdir(join(work, 'media'));
    await startContainer(fresh, work);
    await waitForHealth(fresh);
    await completeSetup(FRESH_URL);
  });

  test.afterEach(async () => {
    await removeContainers(FRESH_NAME);
    if (work !== undefined) {
      await rm(work, { recursive: true, force: true });
    }
  });

  test('opens after signing in and leads to the Songs behind each count', async ({ page }) => {
    test.setTimeout(240_000);

    // A new instance: signing in lands on the dashboard, which welcomes with New Song.
    await page.goto(FRESH_URL);
    await signInWithTheForm(page, TEST_ADMIN.username, TEST_ADMIN.password);
    await expect(userMenu(page)).toHaveText(TEST_ADMIN.username);
    await expect(page.getByRole('heading', { level: 2, name: 'Dashboard' })).toBeVisible();
    await expect(page).toHaveURL(FRESH_URL);
    const welcome = page.getByTestId('dashboard-welcome');
    await expect(welcome.getByRole('heading', { name: 'Welcome to n8Tracks' })).toBeVisible();
    await expect(welcome.getByRole('button', { name: 'New Song' })).toBeVisible();
    await expect(
      page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Home' }),
    ).toHaveAttribute('aria-current', 'page');
    await expectAccessibleInLightAndDark(page);

    // Songs: two with Generations and none selected, one archived, and one changed last.
    const harbour = await createSong(page, 'Harbour Lights');
    const lantern = await createSong(page, 'Lantern Song');
    const shelved = await createSong(page, 'Shelved Idea');
    await seedGenerationIn(FRESH_NAME, harbour.currentVersion.shortcode);
    await seedGenerationIn(FRESH_NAME, lantern.currentVersion.shortcode);
    await seedGenerationIn(FRESH_NAME, shelved.currentVersion.shortcode);
    await editSong(page, shelved.shortcode, { stateId: ARCHIVED_STATE });
    await editSong(page, harbour.shortcode, { concept: 'Edited last' });

    // 1. Sign in again: the dashboard opens with the Song changed last at the top.
    await userMenu(page).click();
    await page.getByRole('menuitem', { name: 'Sign out', exact: true }).click();
    await signInWithTheForm(page, TEST_ADMIN.username, TEST_ADMIN.password);
    await expect(page.getByRole('heading', { level: 2, name: 'Dashboard' })).toBeVisible();
    await expect(page).toHaveURL(FRESH_URL);
    const recent = section(page, 'Recently edited');
    const rows = recent.getByTestId('dashboard-song');
    await expect(rows).toHaveCount(2);
    await expect(rows.first()).toHaveAttribute('data-song', harbour.shortcode);
    await expect(rows.first().getByRole('link', { name: 'Harbour Lights' })).toBeVisible();
    await expect(rows.first()).toContainText(harbour.shortcode);
    await expect(rows.first()).toContainText(harbour.state.name);
    await expect(recent).not.toContainText('Shelved Idea');
    await expectAccessibleInLightAndDark(page);

    // 2. A workflow state's count opens the Songs table filtered to it, with the same number.
    const states = section(page, 'By workflow state');
    const initial = states.getByRole('link', { name: `2 Songs in ${harbour.state.name}` });
    await expect(initial).toBeVisible();
    await expect(states.getByRole('link', { name: '1 Song in Archived' })).toBeVisible();
    const stateCount = await countOf(initial);
    await initial.click();
    await expect(page.getByRole('heading', { level: 2, name: 'Songs' })).toBeVisible();
    expect(new URL(page.url()).searchParams.getAll('state')).toEqual([harbour.state.id]);
    await expect(page.getByTestId('songs-total')).toHaveText(`${String(stateCount)} Songs`);
    await expectAccessibleInLightAndDark(page);

    // 3. Without a Selected Generation likewise: the archived Song is left out of both.
    await page
      .getByRole('navigation', { name: 'Main' })
      .getByRole('link', { name: 'Home' })
      .click();
    const without = section(page, 'Without a Selected Generation');
    await expect(without.getByTestId('without-selection-count')).toHaveText(
      '2 active Songs have Generations but no Selected Generation.',
    );
    await without
      .getByRole('link', { name: 'Show in Songs without a Selected Generation' })
      .click();
    await expect(page.getByRole('heading', { level: 2, name: 'Songs' })).toBeVisible();
    await expect(page.getByTestId('songs-total')).toHaveText('2 Songs');
    const chips = page.getByTestId('active-filters');
    await expect(chips).toContainText('Has Generations');
    await expect(chips).toContainText('Has no Selected Generation');
    await expect(chips).toContainText('Active only');
    await expectAccessibleInLightAndDark(page);

    // A Recently edited row opens its Song.
    await page.goBack();
    await section(page, 'Recently edited').getByRole('link', { name: 'Harbour Lights' }).click();
    await expect(page).toHaveURL(`${FRESH_URL}songs/${harbour.shortcode}`);
  });
});
