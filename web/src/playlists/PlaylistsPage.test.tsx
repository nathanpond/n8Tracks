import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../test/helpers';
import { HIGHWAY, playlistServer, SUNRISE, testPlaylist } from '../test/playlistServer';

describe('the Playlists page', () => {
  it('lists Playlists by title with their Song counts, each linking to its page', async () => {
    const zebra = testPlaylist('zebra');
    const road = testPlaylist('Road trip', [HIGHWAY, SUNRISE]);
    playlistServer([zebra, road]);

    renderApp('/playlists');

    const table = await screen.findByRole('table', { name: 'Playlists' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows.map((row) => row.getAttribute('data-playlist-title'))).toEqual([
      'Road trip',
      'zebra',
    ]);
    expect(rows[0]).toHaveTextContent('2');
    expect(within(table).getByRole('link', { name: 'Road trip' })).toHaveAttribute(
      'href',
      `/playlists/${road.id}`,
    );
    expect(screen.getByText('2 Playlists')).toBeVisible();
  });

  it('explains Playlists when there are none, and creates one from a title, opening it', async () => {
    const server = playlistServer([]);
    const user = userEvent.setup();
    renderApp('/playlists');

    expect(await screen.findByText('There are no Playlists yet.')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'New Playlist' }));
    const dialog = await screen.findByRole('dialog', { name: 'New Playlist' });

    // A blank title is refused before anything is sent.
    await user.click(within(dialog).getByRole('button', { name: 'Create Playlist' }));
    expect(await within(dialog).findByText('Enter a title.')).toBeVisible();
    expect(server.writes).toHaveLength(0);

    await user.type(within(dialog).getByRole('textbox', { name: 'Title' }), 'Road trip');
    await user.click(within(dialog).getByRole('button', { name: 'Create Playlist' }));

    expect(await screen.findByRole('heading', { level: 2, name: 'Road trip' })).toBeVisible();
    expect(screen.getByText('No Songs yet. Find one above to add it.')).toBeVisible();
    await waitFor(() => {
      expect(server.writes).toEqual([
        expect.objectContaining({ method: 'POST', body: { title: 'Road trip' } }),
      ]);
    });
  });
});
