import { expect, test, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark, expectNoA11yViolations } from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface SongRelease {
  releaseDate: string | null;
  originalReleaseDate: string | null;
  explicit: 'explicit' | 'clean' | null;
  copyright: string | null;
  publishing: string | null;
  isrc: string | null;
  language: string | null;
  links: { label: string | null; url: string }[];
}

interface Song {
  id: string;
  shortcode: string;
  title: string;
  revision: number;
  release: SongRelease;
}

/** The Demo's ISRC, as typed and as stored. */
const TYPED_ISRC = 'US-S1Z-99-00001';
const STORED_ISRC = 'USS1Z9900001';

/** The app's base (the root or the sub-path), as the Songs page resolves it. */
async function appBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

async function createSong(page: Page, base: URL, title: string): Promise<Song> {
  const response = await page.request.post(new URL('api/v1/songs', base).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: { title },
  });
  expect(response.status()).toBe(201);
  return (await response.json()) as Song;
}

async function readSong(page: Page, base: URL, song: Song): Promise<Song> {
  const answer = await page.request.get(new URL(`api/v1/songs/${song.id}`, base).toString());
  expect(answer.ok()).toBe(true);
  return (await answer.json()) as Song;
}

/** Gives a Song an ISRC through the API, at its current revision. */
async function setIsrc(page: Page, base: URL, song: Song, isrc: string): Promise<void> {
  const current = await readSong(page, base, song);
  const response = await page.request.patch(new URL(`api/v1/songs/${song.id}`, base).toString(), {
    headers: { ...ANTIFORGERY_HEADERS, 'If-Match': `"${String(current.revision)}"` },
    data: { release: { isrc } },
  });
  expect(response.ok()).toBe(true);
}

async function openDetails(page: Page, song: Song): Promise<void> {
  await page.goto(`./songs/${song.shortcode}`);
  await expect(page.getByRole('heading', { level: 2, name: song.title })).toBeVisible();
  const details = page.getByRole('button', { name: 'Details', exact: true });
  if ((await details.getAttribute('aria-expanded')) === 'false') {
    await details.click();
  }
  await expect(page.getByRole('group', { name: 'Release' })).toBeVisible();
}

/**
 * Walks the story's Demo on the project's shared container, on Songs of its own: in a Song's
 * Details, a release date, the explicit flag, the ISRC "US-S1Z-99-00001", and a language are set;
 * after a reload the ISRC shows as "USS1Z9900001" and the rest is kept; an ISRC of eleven
 * characters is refused with a field error. Also: another Song given the same ISRC is named in a
 * warning, and the ISRC is kept. Each state is scanned with axe in both colour schemes. The Demo's
 * ISRC is fixed, so earlier runs on the same container may share it too; the warning is checked
 * to name this run's other Song, not to name it alone.
 */
test.describe('Song release details', () => {
  test('records a Song’s release details, normalises its ISRC, and refuses a short one', async ({
    page,
  }) => {
    const stamp = String(Date.now()).slice(-7);
    const base = await appBase(page);
    const song = await createSong(page, base, `Release ${stamp}`);
    const other = await createSong(page, base, `Release twin ${stamp}`);
    const release = page.getByRole('group', { name: 'Release' });

    // 1. In a Song's Details, set a release date, mark it explicit, and enter the ISRC.
    await openDetails(page, song);
    await expectAccessibleInLightAndDark(page);
    const releaseDate = release.getByRole('textbox', { name: 'Release date', exact: true });
    await releaseDate.fill('2026-03-01');
    await releaseDate.press('Enter');
    await expect(release.getByTestId('releaseDate-shown')).toContainText('2026');
    await release.getByRole('radio', { name: 'Explicit', exact: true }).check();
    await expect(release.getByRole('radio', { name: 'Explicit', exact: true })).toBeChecked();
    const isrc = release.getByRole('textbox', { name: 'ISRC' });
    await isrc.fill(TYPED_ISRC);
    await isrc.press('Enter');
    await expect(isrc).toHaveValue(STORED_ISRC);
    await release.getByRole('combobox', { name: 'Language' }).selectOption({ label: 'English' });
    await expect(release.getByRole('combobox', { name: 'Language' })).toHaveValue('en');
    await expectAccessibleInLightAndDark(page);

    // 2. Reload: the ISRC shows as "USS1Z9900001" and the rest is kept.
    await page.reload();
    await expect(page.getByRole('heading', { level: 2, name: song.title })).toBeVisible();
    await expect(isrc).toHaveValue(STORED_ISRC);
    await expect(releaseDate).toHaveValue('2026-03-01');
    await expect(release.getByRole('radio', { name: 'Explicit', exact: true })).toBeChecked();
    await expect(release.getByRole('combobox', { name: 'Language' })).toHaveValue('en');
    const saved = (await readSong(page, base, song)).release;
    expect(saved).toMatchObject({
      releaseDate: '2026-03-01',
      explicit: 'explicit',
      isrc: STORED_ISRC,
      language: 'en',
    });

    // Another Song with the same ISRC: allowed, and named in a warning while it applies.
    await setIsrc(page, base, other, TYPED_ISRC);
    await page.reload();
    const warning = release.getByTestId('isrc-warning');
    await expect(warning.getByRole('link', { name: other.title })).toBeVisible();
    await expect(isrc).toHaveValue(STORED_ISRC);
    await expectAccessibleInLightAndDark(page);

    // 3. An ISRC of eleven characters is refused with a field error; nothing is saved.
    await isrc.fill('US-S1Z-99-0000');
    await isrc.press('Enter');
    await expect(
      release.getByText(
        'An ISRC has 12 characters once spaces and hyphens are left out; this has 11.',
      ),
    ).toBeVisible();
    await expect(isrc).toHaveAttribute('aria-invalid', 'true');
    // The field is still being edited: a scan that clicks the colour control would blur it.
    await expectNoA11yViolations(page);
    expect((await readSong(page, base, song)).release.isrc).toBe(STORED_ISRC);
  });
});
