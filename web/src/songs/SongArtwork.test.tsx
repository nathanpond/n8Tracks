import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { jsonResponse, renderApp } from '../test/helpers';
import { baseSong, songServer, testArtwork, testAssetId } from '../test/songServer';

const TITLE = baseSong.title;
const ALT = `Artwork for ${TITLE}`;

/** Opens the Song page and its Details panel; resolves to the panel's artwork group. */
async function openArtwork(user: ReturnType<typeof userEvent.setup>) {
  const { container } = renderApp('/songs/n8-7');
  await screen.findByRole('heading', { level: 2, name: TITLE });
  await user.click(screen.getByRole('button', { name: 'Details' }));
  const group = await screen.findByRole('group', { name: 'Artwork' });
  return { group, container };
}

function fileInput(container: HTMLElement): HTMLInputElement {
  const input = container.ownerDocument.querySelector<HTMLInputElement>(
    '[data-testid="artwork-picker"] input[type="file"]',
  );
  if (!input) {
    throw new Error('No artwork file input.');
  }
  return input;
}

function png(name = 'cover.png'): File {
  return new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], name, { type: 'image/png' });
}

function header(): HTMLElement {
  return screen.getByTestId('song-header-artwork');
}

describe('a Song’s artwork', () => {
  it('shows a placeholder until an image is uploaded, then the image in the panel and the header', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    const { group, container } = await openArtwork(user);
    expect(within(group).getByRole('img', { name: 'No artwork' })).toBeInTheDocument();
    expect(within(header()).getByRole('img', { name: 'No artwork' })).toBeInTheDocument();
    expect(within(group).queryByRole('button', { name: 'Remove artwork' })).toBeNull();

    await user.upload(fileInput(container), png());

    await waitFor(() => {
      expect(within(group).getByTestId('artwork-status')).toHaveTextContent('Artwork saved.');
    });
    const id = testAssetId(1);
    expect(server.uploads.map((file) => file.name)).toEqual(['cover.png']);
    expect(server.edits).toEqual([{ ifMatch: '"1"', body: { artworkAssetId: id } }]);
    expect(server.song.revision).toBe(2);
    expect(within(group).getByRole('img', { name: ALT })).toHaveAttribute(
      'src',
      `/api/v1/artwork/${id}/320`,
    );
    expect(within(header()).getByRole('img', { name: ALT })).toHaveAttribute(
      'src',
      `/api/v1/artwork/${id}/320`,
    );
    expect(within(group).getByRole('button', { name: 'Replace artwork' })).toBeEnabled();
  });

  it('says why a file was refused and changes nothing', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    server.nextUpload = () =>
      jsonResponse(415, {
        code: 'artwork_type_not_supported',
        title: 'The file is not a JPEG, PNG, or WebP image. Its content decides, not its name.',
      });
    const { group, container } = await openArtwork(user);

    await user.upload(
      fileInput(container),
      new File(['just some text'], 'cover.jpg', { type: 'image/jpeg' }),
    );

    expect(await within(group).findByRole('alert')).toHaveTextContent(
      'The file is not a JPEG, PNG, or WebP image. Its content decides, not its name. The artwork was not changed.',
    );
    expect(server.edits).toEqual([]);
    expect(within(group).getByRole('img', { name: 'No artwork' })).toBeInTheDocument();
  });

  it('refuses a file over 25 MB without sending it', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    const { group, container } = await openArtwork(user);
    const huge = new File([new Uint8Array(25 * 1024 * 1024 + 1)], 'huge.png', {
      type: 'image/png',
    });

    await user.upload(fileInput(container), huge);

    expect(await within(group).findByRole('alert')).toHaveTextContent('larger than 25 MB');
    expect(server.uploads).toEqual([]);
  });

  it('replaces the artwork with a new upload', async () => {
    const user = userEvent.setup();
    const first = testArtwork(testAssetId(1));
    const { server } = songServer({ ...baseSong, artwork: first });
    server.uploads.push(png('first.png'));
    const { group, container } = await openArtwork(user);
    expect(within(group).getByRole('img', { name: ALT })).toHaveAttribute('src', first.urls['320']);

    await user.upload(fileInput(container), png('second.png'));

    await waitFor(() => {
      expect(within(group).getByTestId('artwork-status')).toHaveTextContent('Artwork replaced.');
    });
    const second = testAssetId(2);
    expect(server.edits.at(-1)?.body).toEqual({ artworkAssetId: second });
    expect(within(group).getByRole('img', { name: ALT })).toHaveAttribute(
      'src',
      `/api/v1/artwork/${second}/320`,
    );
    expect(within(header()).getByRole('img', { name: ALT })).toHaveAttribute(
      'src',
      `/api/v1/artwork/${second}/320`,
    );
  });

  it('asks before removing the artwork, and removes it only when confirmed', async () => {
    const user = userEvent.setup();
    const { server } = songServer({ ...baseSong, artwork: testArtwork(testAssetId(1)) });
    const { group } = await openArtwork(user);

    await user.click(within(group).getByRole('button', { name: 'Remove artwork' }));
    let dialog = await screen.findByRole('dialog', { name: 'Remove the artwork?' });
    expect(within(dialog).getByTestId('remove-artwork-summary')).toHaveTextContent(
      'The Song will show its Selected Generation’s image, or a placeholder when that has none, instead.',
    );
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Remove the artwork?' })).toBeNull();
    });
    expect(server.edits).toEqual([]);

    await user.click(within(group).getByRole('button', { name: 'Remove artwork' }));
    dialog = await screen.findByRole('dialog', { name: 'Remove the artwork?' });
    await user.click(within(dialog).getByRole('button', { name: 'Remove' }));

    await waitFor(() => {
      expect(within(group).getByTestId('artwork-status')).toHaveTextContent('Artwork removed.');
    });
    expect(server.edits).toEqual([{ ifMatch: '"1"', body: { artworkAssetId: null } }]);
    expect(within(group).getByRole('img', { name: 'No artwork' })).toBeInTheDocument();
    expect(within(header()).getByRole('img', { name: 'No artwork' })).toBeInTheDocument();
    expect(within(group).getByRole('button', { name: 'Upload artwork' })).toBeInTheDocument();
  });

  it('shows the 1,024-pixel image with a link to the original when the artwork is activated', async () => {
    const user = userEvent.setup();
    const artwork = testArtwork(testAssetId(1));
    songServer({ ...baseSong, artwork });
    const { group } = await openArtwork(user);

    await user.click(
      within(group).getByRole('button', { name: `Show the artwork for ${TITLE} larger` }),
    );

    const dialog = await screen.findByRole('dialog', { name: ALT });
    await waitFor(() => {
      expect(within(dialog).getByRole('img', { name: ALT })).toBeVisible();
    });
    expect(within(dialog).getByRole('img', { name: ALT })).toHaveAttribute(
      'src',
      artwork.urls['1024'],
    );
    expect(within(dialog).getByRole('link', { name: 'View original' })).toHaveAttribute(
      'href',
      artwork.urls.original,
    );
  });

  it('says so when n8Tracks refuses the upload’s asset, and keeps the artwork as it was', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    server.next = () =>
      jsonResponse(422, {
        code: 'validation_failed',
        errors: {
          artworkAssetId: ['There is no such artwork, or it was removed. Upload the image again.'],
        },
      });
    const { group, container } = await openArtwork(user);

    await user.upload(fileInput(container), png());

    expect(await within(group).findByRole('alert')).toHaveTextContent(
      'There is no such artwork, or it was removed. Upload the image again.',
    );
    expect(within(group).getByRole('img', { name: 'No artwork' })).toBeInTheDocument();
    expect(within(group).getByTestId('artwork-status')).toHaveTextContent('');
  });
});

describe('a Song’s artwork crop', () => {
  const ASSET = testAssetId(1);
  const SQUARE = `/api/v1/artwork/${ASSET}`;

  /** A Song whose artwork (the fake store's first upload, 1,200 × 600) has `crop`. */
  function cropped(crop: { x: number; y: number; size: number } | null) {
    const { server } = songServer({ ...baseSong, artwork: testArtwork(ASSET, { crop }) });
    server.uploads.push(png('first.png'));
    return server;
  }

  async function openCrop(user: ReturnType<typeof userEvent.setup>, group: HTMLElement) {
    await user.click(within(group).getByRole('button', { name: 'Crop artwork' }));
    const dialog = await screen.findByRole('dialog', { name: `Crop the artwork for ${TITLE}` });
    return { dialog, square: within(dialog).getByRole('application', { name: 'Crop selection' }) };
  }

  it('saves a crop moved with the keyboard, and shows the cropped square everywhere', async () => {
    const user = userEvent.setup();
    const server = cropped(null);
    const { group } = await openArtwork(user);
    expect(within(group).queryByRole('button', { name: 'Reset crop' })).toBeNull();
    expect(within(group).getByRole('img', { name: ALT })).toHaveAttribute('src', `${SQUARE}/320`);

    const { dialog, square } = await openCrop(user, group);
    square.focus();
    for (let press = 0; press < 5; press++) {
      await user.keyboard('{Shift>}{ArrowLeft}{/Shift}');
    }
    expect(within(dialog).getByTestId('crop-position')).toHaveTextContent(
      'Left 0 px, top 0 px, size 600 px.',
    );
    await user.click(within(dialog).getByRole('button', { name: 'Save crop' }));

    await waitFor(() => {
      expect(within(group).getByTestId('artwork-status')).toHaveTextContent('Crop saved.');
    });
    expect(server.edits).toEqual([
      { ifMatch: '"1"', body: { artworkCrop: { x: 0, y: 0, size: 600 } } },
    ]);
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: `Crop the artwork for ${TITLE}` })).toBeNull();
    });
    expect(within(group).getByRole('img', { name: ALT })).toHaveAttribute(
      'src',
      `${SQUARE}/crops/0-0-600/320`,
    );
    expect(within(header()).getByRole('img', { name: ALT })).toHaveAttribute(
      'src',
      `${SQUARE}/crops/0-0-600/320`,
    );
    expect(within(group).getByRole('button', { name: 'Reset crop' })).toBeEnabled();
  });

  it('opens on the saved crop, so it can be adjusted later', async () => {
    const user = userEvent.setup();
    const server = cropped({ x: 0, y: 0, size: 600 });
    const { group } = await openArtwork(user);

    const { dialog, square } = await openCrop(user, group);
    expect(square).toHaveAttribute('data-crop', '0,0,600');
    square.focus();
    await user.keyboard('-');
    await user.click(within(dialog).getByRole('button', { name: 'Save crop' }));

    await waitFor(() => {
      expect(server.edits).toEqual([
        { ifMatch: '"1"', body: { artworkCrop: { x: 3, y: 3, size: 594 } } },
      ]);
    });
  });

  it('keeps the dialog open with the reason when the crop is refused', async () => {
    const user = userEvent.setup();
    const server = cropped(null);
    server.next = () =>
      jsonResponse(422, {
        code: 'validation_failed',
        errors: { artworkCrop: ['The crop must be at least 64 pixels on a side.'] },
      });
    const { group } = await openArtwork(user);

    const { dialog } = await openCrop(user, group);
    await user.click(within(dialog).getByRole('button', { name: 'Save crop' }));

    expect(await within(dialog).findByTestId('crop-error')).toHaveTextContent(
      'The crop must be at least 64 pixels on a side.',
    );
    expect(within(group).getByRole('img', { name: ALT })).toHaveAttribute('src', `${SQUARE}/320`);
  });

  it('resets the crop to the centre', async () => {
    const user = userEvent.setup();
    const server = cropped({ x: 0, y: 0, size: 600 });
    const { group } = await openArtwork(user);

    await user.click(within(group).getByRole('button', { name: 'Reset crop' }));

    await waitFor(() => {
      expect(within(group).getByTestId('artwork-status')).toHaveTextContent(
        'Crop reset to the centre.',
      );
    });
    expect(server.edits).toEqual([{ ifMatch: '"1"', body: { artworkCrop: null } }]);
    expect(within(group).getByRole('img', { name: ALT })).toHaveAttribute('src', `${SQUARE}/320`);
    expect(within(group).queryByRole('button', { name: 'Reset crop' })).toBeNull();
  });

  it('resets the crop when the image is replaced, unless Keep crop is ticked', async () => {
    const user = userEvent.setup();
    const server = cropped({ x: 0, y: 0, size: 600 });
    const { group, container } = await openArtwork(user);
    const keep = within(group).getByRole('checkbox', { name: /Keep crop/ });
    expect(keep).not.toBeChecked();

    await user.upload(fileInput(container), png('second.png'));

    await waitFor(() => {
      expect(within(group).getByTestId('artwork-status')).toHaveTextContent('Artwork replaced.');
    });
    expect(server.edits.at(-1)?.body).toEqual({ artworkAssetId: testAssetId(2) });
    expect(within(group).queryByRole('checkbox', { name: /Keep crop/ })).toBeNull();
  });

  it('keeps the crop for the new image when Keep crop is ticked and it fits', async () => {
    const user = userEvent.setup();
    const server = cropped({ x: 0, y: 0, size: 600 });
    server.uploadSizes = [
      { width: 1200, height: 600 },
      { width: 800, height: 700 },
    ];
    const { group, container } = await openArtwork(user);

    await user.click(within(group).getByRole('checkbox', { name: /Keep crop/ }));
    await user.upload(fileInput(container), png('second.png'));

    await waitFor(() => {
      expect(within(group).getByTestId('artwork-status')).toHaveTextContent(/^Artwork replaced\.$/);
    });
    expect(server.edits.at(-1)?.body).toEqual({
      artworkAssetId: testAssetId(2),
      artworkCrop: { x: 0, y: 0, size: 600 },
    });
    expect(within(group).getByRole('img', { name: ALT })).toHaveAttribute(
      'src',
      `/api/v1/artwork/${testAssetId(2)}/crops/0-0-600/320`,
    );
    expect(within(group).getByRole('checkbox', { name: /Keep crop/ })).not.toBeChecked();
  });

  it('drops a kept crop that does not fit the new image to the centred default', async () => {
    const user = userEvent.setup();
    const server = cropped({ x: 0, y: 0, size: 600 });
    server.uploadSizes = [
      { width: 1200, height: 600 },
      { width: 500, height: 500 },
    ];
    const { group, container } = await openArtwork(user);

    await user.click(within(group).getByRole('checkbox', { name: /Keep crop/ }));
    await user.upload(fileInput(container), png('second.png'));

    await waitFor(() => {
      expect(within(group).getByTestId('artwork-status')).toHaveTextContent(
        'Artwork replaced. The crop does not fit the new image, so it shows the centre.',
      );
    });
    expect(server.edits.at(-1)?.body).toEqual({ artworkAssetId: testAssetId(2) });
  });
});
