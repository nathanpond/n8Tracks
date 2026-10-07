import { expect, test, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark } from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  currentVersion: { id: string; shortcode: string };
}

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

function row(page: Page, name: string) {
  return page.locator(`tr[data-type-name="${name}"]`);
}

/** The page's status line, where each change is reported. */
function status(page: Page) {
  return page.getByRole('status').filter({ hasText: /./ }).first();
}

/**
 * Walks #126's Demo on the shared containers, with a type and a Song of its own (stamped, because
 * types are instance-wide): "Reimagining of" / "Reimagined as" is added in Settings →
 * Relationships and mapped to Cover. On a mutable Version, an audio source of that type is added
 * (an unimported Suno clip pasted by ID): it is accepted and behaves as a Cover, hiding Inspiration.
 * Back in Settings, clearing the mapping is refused, naming one Version. Each state is scanned with
 * axe.
 */
test.describe('mapping a relationship type to a Suno action', () => {
  test('a type of the user’s own stands for Cover on a Version, and keeps it while in use', async ({
    page,
  }) => {
    const stamp = String(Date.now()).slice(-7);
    const name = `Reimagining of ${stamp}`;
    const reverse = `Reimagined as ${stamp}`;
    const base = await appBase(page);
    const song = await createSong(page, base, `Reimagined ${stamp}`);

    // 1. Add the type, then map it to Cover.
    await page.goto('./settings/relationships');
    await expect(page.getByRole('heading', { level: 2, name: 'Relationships' })).toBeVisible();
    const form = page.getByRole('form', { name: 'Add a type' });
    await form.getByRole('textbox', { name: 'Name', exact: true }).fill(name);
    await form.getByRole('textbox', { name: 'Reverse name' }).fill(reverse);
    await form.getByRole('button', { name: 'Add type' }).click();
    await expect(status(page)).toContainText(`${name} / ${reverse} is added.`);
    const mapping = row(page, name).getByRole('combobox', { name: `Suno action for ${name}` });
    await expect(mapping).toHaveValue('');
    await expect(row(page, 'Cover').getByTestId('suno-action')).toHaveText('Cover');
    await mapping.selectOption({ label: 'Cover' });
    await expect(status(page)).toContainText(`${name} now stands for Cover.`);
    await expect(mapping).toHaveValue('cover');
    await expectAccessibleInLightAndDark(page);

    // 2. On a mutable Version, add an audio source of that type: it is accepted as a Cover.
    await page.goto(`./songs/${song.shortcode}`);
    const sources = page.getByTestId('sources-section');
    const action = sources.getByRole('combobox', { name: 'Action' });
    await expect(action).toBeEnabled();
    await action.selectOption({ label: `${name} (Cover)` });
    await expect(page.getByRole('heading', { name: 'Inspiration' })).toBeHidden();
    await sources.getByRole('button', { name: `Choose the ${name} source` }).click();
    const picker = page.getByRole('dialog', { name: `Choose the ${name} source` });
    await expect(picker).toBeVisible();
    const sunoId = `0199b1a0-${stamp.slice(0, 4)}-7000-8000-${stamp.padStart(12, '0')}`;
    await picker.getByRole('textbox', { name: 'Suno song address or clip ID' }).fill(sunoId);
    await picker.getByRole('button', { name: 'Use this clip' }).click();
    await expect(picker).toBeHidden();
    const audio = sources.getByRole('list', { name: 'Audio sources' });
    await expect(audio.getByRole('listitem')).toHaveCount(1);
    await expect(page.getByTestId('autosave').getByRole('status')).toHaveText('Saved');
    const version = await page.request.get(
      new URL(`api/v1/versions/${song.currentVersion.id}`, base).toString(),
    );
    const stored = (await version.json()) as {
      inputs: { sources: { typeId: string; sunoAction: string }[] };
    };
    expect(stored.inputs.sources).toHaveLength(1);
    expect(stored.inputs.sources[0]?.sunoAction).toBe('cover');
    // A Cover holds one source: no second is asked for.
    await expect(sources.getByRole('button', { name: 'Add the second source' })).toHaveCount(0);
    await expectAccessibleInLightAndDark(page);

    // 3. Clearing the mapping is refused, naming one Version; the type keeps Cover.
    await page.goto('./settings/relationships');
    const held = row(page, name).getByRole('combobox', { name: `Suno action for ${name}` });
    await expect(held).toHaveValue('cover');
    await held.selectOption({ label: 'Not mapped' });
    await expect(page.getByRole('status')).toContainText(
      `1 Version has a source of the type ${name}, so the Suno action it stands for cannot change.`,
    );
    await expect(held).toHaveValue('cover');
    await expectAccessibleInLightAndDark(page);
  });
});
