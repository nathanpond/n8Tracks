import { expect, test, type Locator, type Page } from '@playwright/test';
import {
  expectAccessibleInLightAndDark,
  expectModalAccessibleInBothSchemes,
} from '../support/a11y.ts';
import { halvesPng } from '../support/images.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface Song {
  id: string;
  shortcode: string;
  title: string;
  artwork: {
    assetId: string;
    urls: Record<string, string>;
    crop: { x: number; y: number; size: number } | null;
    squareUrls: Record<string, string>;
  } | null;
}

type Colour = 'red' | 'blue' | 'other';

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

/**
 * The colours the image shows near its left and right edges, as the page shows it: a square cut
 * from the middle of its natural pixels, which is what `object-fit: cover` centred draws (and the
 * whole image, when it is already square).
 */
async function shownEdges(image: Locator): Promise<[Colour, Colour]> {
  await expect(image).toBeVisible();
  await expect
    .poll(() => image.evaluate((element) => (element as HTMLImageElement).naturalWidth))
    .toBeGreaterThan(0);
  return image.evaluate((element): [Colour, Colour] => {
    const img = element as HTMLImageElement;
    const style = getComputedStyle(img);
    if (style.objectFit !== 'cover' || style.objectPosition !== '50% 50%') {
      throw new Error(
        `Not shown as its centred square: ${style.objectFit} ${style.objectPosition}`,
      );
    }
    const side = Math.min(img.naturalWidth, img.naturalHeight);
    const canvas = document.createElement('canvas');
    canvas.width = 100;
    canvas.height = 100;
    const context = canvas.getContext('2d');
    if (context === null) {
      throw new Error('No canvas.');
    }
    context.drawImage(
      img,
      (img.naturalWidth - side) / 2,
      (img.naturalHeight - side) / 2,
      side,
      side,
      0,
      0,
      100,
      100,
    );
    const colour = (x: number): Colour => {
      const [r = 0, , b = 0] = context.getImageData(x, 50, 1, 1).data;
      return r > 200 && b < 60 ? 'red' : b > 200 && r < 60 ? 'blue' : 'other';
    };
    return [colour(8), colour(92)];
  });
}

/**
 * Walks the story's Demo on the project's shared container, on a Song of its own (title and image
 * bytes stamped with this run): a wide image, red on the left and blue on the right, shows its
 * centre as the square (half red, half blue); the crop control, moved to the left edge with the
 * keyboard and saved, makes the Song's artwork the red left part in the panel, the header, and the
 * Songs table; and Reset brings the centre back. Each state is scanned with axe in both schemes.
 */
test.describe('Artwork crop', () => {
  test('crops a Song’s artwork to its left edge and resets it to the centre', async ({
    page,
  }, testInfo) => {
    const stamp = `${testInfo.project.name} ${String(Date.now())} ${String(testInfo.retry)}`;
    const base = await appBase(page);
    const song = await createSong(page, base, `Crop ${String(Date.now()).slice(-7)}`);
    const alt = `Artwork for ${song.title}`;

    await page.goto(`./songs/${song.shortcode}`);
    await expect(page.getByRole('heading', { level: 2, name: song.title })).toBeVisible();
    await page.getByRole('button', { name: 'Details', exact: true }).click();
    const picker = page.getByRole('group', { name: 'Artwork' });
    const status = picker.getByTestId('artwork-status');
    const panelImage = picker.getByRole('img', { name: alt });
    const headerImage = page.getByTestId('song-header-artwork').getByRole('img', { name: alt });

    // 1. A wide image: the square view shows its centre, half red and half blue.
    await picker.locator('input[type="file"]').setInputFiles({
      name: 'wide.png',
      mimeType: 'image/png',
      buffer: halvesPng(1200, 600, stamp),
    });
    await expect(status).toHaveText('Artwork saved.');
    expect(await shownEdges(panelImage)).toEqual(['red', 'blue']);
    expect(await shownEdges(headerImage)).toEqual(['red', 'blue']);
    await expect(picker.getByRole('button', { name: 'Reset crop' })).toHaveCount(0);
    await expectAccessibleInLightAndDark(page);

    // 2. Move the square to the left edge with the keyboard, and save.
    await picker.getByRole('button', { name: 'Crop artwork' }).click();
    const dialog = page.getByRole('dialog', { name: `Crop the artwork for ${song.title}` });
    const selection = dialog.getByRole('application', { name: 'Crop selection' });
    const position = dialog.getByTestId('crop-position');
    await expect(position).toHaveText('Left 300 px, top 0 px, size 600 px.');
    await expectModalAccessibleInBothSchemes(page);
    await selection.focus();
    for (let press = 0; press < 5; press++) {
      await page.keyboard.press('Shift+ArrowLeft');
    }
    await expect(position).toHaveText('Left 0 px, top 0 px, size 600 px.');
    await expectModalAccessibleInBothSchemes(page);
    await dialog.getByRole('button', { name: 'Save crop' }).click();
    await expect(status).toHaveText('Crop saved.');
    await expect(dialog).toBeHidden();

    const croppedArtwork = (await readSong(page, base, song)).artwork;
    expect(croppedArtwork?.crop).toEqual({ x: 0, y: 0, size: 600 });
    await expect(panelImage).toHaveAttribute('src', croppedArtwork?.squareUrls['320'] ?? '');
    expect(await shownEdges(panelImage)).toEqual(['red', 'red']);
    expect(await shownEdges(headerImage)).toEqual(['red', 'red']);
    await expectAccessibleInLightAndDark(page);

    await page.goto('./songs');
    const tableImage = page
      .getByRole('table', { name: 'Songs' })
      .getByRole('row')
      .filter({ has: page.getByRole('rowheader', { name: song.shortcode, exact: true }) })
      .getByRole('img', { name: alt });
    await expect(tableImage).toHaveAttribute('src', croppedArtwork?.squareUrls['96'] ?? '');
    expect(await shownEdges(tableImage)).toEqual(['red', 'red']);
    await expectAccessibleInLightAndDark(page);

    // 3. Reset: the centre returns.
    await page.goto(`./songs/${song.shortcode}`);
    await picker.getByRole('button', { name: 'Reset crop' }).click();
    await expect(status).toHaveText('Crop reset to the centre.');
    expect((await readSong(page, base, song)).artwork?.crop).toBeNull();
    expect(await shownEdges(panelImage)).toEqual(['red', 'blue']);
    expect(await shownEdges(headerImage)).toEqual(['red', 'blue']);
    await expectAccessibleInLightAndDark(page);
  });
});
