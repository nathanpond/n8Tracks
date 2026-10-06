import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { albumServer, testAlbum } from '../test/albumServer';
import { artistServer, testArtist } from '../test/artistServer';
import { testArtwork, testAssetId } from '../test/artworkFake';
import { renderApp } from '../test/helpers';
import { playlistServer, testPlaylist } from '../test/playlistServer';

/** The page's artwork picker and its file input. */
async function openArtwork() {
  const group = await screen.findByRole('group', { name: 'Artwork' });
  const input = group.querySelector<HTMLInputElement>('input[type="file"]');
  if (input === null) {
    throw new Error('No artwork file input.');
  }
  return { group, input };
}

function png(name = 'cover.png'): File {
  return new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], name, { type: 'image/png' });
}

/** Each owner's page, its fake, and how its record reads back from the fake. */
const OWNERS = [
  {
    noun: 'Album',
    open: (artwork = false) => {
      const album = testAlbum('Night Drive', {
        artwork: artwork ? testArtwork(testAssetId(1)) : null,
      });
      const server = albumServer([album]);
      if (artwork) {
        server.artwork.uploads.push(png('first.png'));
      }
      renderApp(`/albums/${album.id}`);
      return {
        writes: () => server.writes,
        artwork: () => server.albums[0]?.artwork,
        revision: () => server.albums[0]?.revision,
      };
    },
  },
  {
    noun: 'Playlist',
    open: (artwork = false) => {
      const playlist = testPlaylist('Night Drive', [], {
        artwork: artwork ? testArtwork(testAssetId(1)) : null,
      });
      const server = playlistServer([playlist]);
      if (artwork) {
        server.artwork.uploads.push(png('first.png'));
      }
      renderApp(`/playlists/${playlist.id}`);
      return {
        writes: () => server.writes,
        artwork: () => server.playlists[0]?.artwork,
        revision: () => server.playlists[0]?.revision,
      };
    },
  },
  {
    noun: 'Artist',
    open: (artwork = false) => {
      const artist = testArtist('Night Drive', {
        artwork: artwork ? testArtwork(testAssetId(1)) : null,
      });
      const server = artistServer([artist]);
      if (artwork) {
        server.artwork.uploads.push(png('first.png'));
      }
      renderApp(`/artists/${artist.id}`);
      return {
        writes: () => server.writes,
        artwork: () => server.artists[0]?.artwork,
        revision: () => server.artists[0]?.revision,
      };
    },
  },
];

describe.each(OWNERS)('an $noun’s own artwork', ({ noun, open }) => {
  it('shows the neutral placeholder until an image is uploaded, then saves it under the revision', async () => {
    const user = userEvent.setup();
    const server = open();
    await screen.findByRole('heading', { level: 2, name: 'Night Drive' });
    const { group, input } = await openArtwork();
    expect(within(group).getByRole('img', { name: 'No artwork' })).toBeInTheDocument();
    expect(within(group).queryByRole('button', { name: 'Remove artwork' })).toBeNull();

    await user.upload(input, png());

    await waitFor(() => {
      expect(within(group).getByTestId('artwork-status')).toHaveTextContent('Artwork saved.');
    });
    const id = testAssetId(1);
    expect(server.writes()).toEqual([
      expect.objectContaining({ method: 'PATCH', ifMatch: '"1"', body: { artworkAssetId: id } }),
    ]);
    expect(server.revision()).toBe(2);
    expect(within(group).getByRole('img', { name: 'Artwork for Night Drive' })).toHaveAttribute(
      'src',
      `/api/v1/artwork/${id}/320`,
    );
  });

  it('replaces the artwork, resetting the crop', async () => {
    const user = userEvent.setup();
    const server = open(true);
    const { group, input } = await openArtwork();
    expect(within(group).getByRole('img', { name: 'Artwork for Night Drive' })).toHaveAttribute(
      'src',
      `/api/v1/artwork/${testAssetId(1)}/320`,
    );

    await user.upload(input, png('second.png'));

    await waitFor(() => {
      expect(within(group).getByTestId('artwork-status')).toHaveTextContent('Artwork replaced.');
    });
    expect(server.writes().at(-1)?.body).toEqual({ artworkAssetId: testAssetId(2) });
    expect(server.artwork()?.assetId).toBe(testAssetId(2));
  });

  it(`asks before removing the artwork, saying the ${noun} shows a placeholder`, async () => {
    const user = userEvent.setup();
    const server = open(true);
    const { group } = await openArtwork();

    await user.click(within(group).getByRole('button', { name: 'Remove artwork' }));
    let dialog = await screen.findByRole('dialog', { name: 'Remove the artwork?' });
    expect(within(dialog).getByTestId('remove-artwork-summary')).toHaveTextContent(
      `The ${noun} will show a placeholder instead.`,
    );
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Remove the artwork?' })).toBeNull();
    });
    expect(server.writes()).toEqual([]);

    await user.click(within(group).getByRole('button', { name: 'Remove artwork' }));
    dialog = await screen.findByRole('dialog', { name: 'Remove the artwork?' });
    await user.click(within(dialog).getByRole('button', { name: 'Remove' }));

    await waitFor(() => {
      expect(within(group).getByTestId('artwork-status')).toHaveTextContent('Artwork removed.');
    });
    expect(server.writes().at(-1)?.body).toEqual({ artworkAssetId: null });
    expect(server.artwork()).toBeNull();
    expect(within(group).getByRole('img', { name: 'No artwork' })).toBeInTheDocument();
  });

  it('resets a crop to the centre as an edit of the owner', async () => {
    const user = userEvent.setup();
    const server = open(true);
    await openArtwork();
    // Give the artwork a crop first, through the dialog's keyboard controls.
    await user.click(await screen.findByRole('button', { name: 'Crop artwork' }));
    const dialog = await screen.findByRole('dialog', { name: /Crop/ });
    const selection = within(dialog).getByRole('application', { name: 'Crop selection' });
    selection.focus();
    await user.keyboard('{ArrowRight}');
    await user.click(within(dialog).getByRole('button', { name: 'Save crop' }));
    await waitFor(() => {
      expect(server.artwork()?.crop).not.toBeNull();
    });
    expect(server.writes().at(-1)?.body).toEqual({ artworkCrop: server.artwork()?.crop });

    await user.click(await screen.findByRole('button', { name: 'Reset crop' }));

    await waitFor(() => {
      expect(server.artwork()?.crop).toBeNull();
    });
    expect(server.writes().at(-1)?.body).toEqual({ artworkCrop: null });
  });
});

describe('the lists', () => {
  const artwork = testArtwork(testAssetId(3), { crop: { x: 100, y: 0, size: 600 } });

  it.each([
    {
      name: 'Albums',
      open: () => {
        albumServer([testAlbum('With', { artwork }), testAlbum('Without')]);
        return '/albums';
      },
    },
    {
      name: 'Playlists',
      open: () => {
        playlistServer([testPlaylist('With', [], { artwork }), testPlaylist('Without')]);
        return '/playlists';
      },
    },
    {
      name: 'Artists',
      open: () => {
        artistServer([testArtist('With', { artwork }), testArtist('Without')]);
        return '/artists';
      },
    },
  ])(
    '$name show each one’s own artwork as a square thumbnail, or the neutral placeholder',
    async ({ name, open }) => {
      renderApp(open());

      const table = await screen.findByRole('table', { name });
      expect(within(table).getByRole('columnheader', { name: 'Artwork' })).toBeInTheDocument();
      const withArtwork = within(table).getByRole('row', { name: /^Artwork for With/ });
      expect(within(withArtwork).getByRole('img', { name: 'Artwork for With' })).toHaveAttribute(
        'src',
        artwork.squareUrls['96'],
      );
      const without = within(table).getByRole('row', { name: /^No artwork Without/ });
      expect(within(without).getByRole('img', { name: 'No artwork' })).toBeInTheDocument();
    },
  );
});
