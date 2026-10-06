import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../test/helpers';
import {
  entryOf,
  HIGHWAY,
  playlistServer,
  SUNRISE,
  testPlaylist,
  TOLL,
} from '../test/playlistServer';

const ROAD_TRIP = testPlaylist('Road trip', [HIGHWAY, SUNRISE, TOLL], {
  description: 'For the drive.',
});

async function openPlaylist(id = ROAD_TRIP.id) {
  renderApp(`/playlists/${id}`);
  return screen.findByRole('textbox', { name: 'Title' });
}

const songList = () => screen.getByRole('list', { name: 'Songs' });

/** The titles of the Songs on the page, in order. */
const titles = () =>
  within(songList())
    .getAllByRole('listitem')
    .map((row) => row.getAttribute('data-song-title'));

const rowOf = (title: string) => {
  const row = songList().querySelector(`[data-song-title="${title}"]`);
  if (!(row instanceof HTMLElement)) {
    throw new Error(`No row for ${title}`);
  }
  return row;
};

describe('a Playlist page', () => {
  it('shows the title, description, and Songs in order, each marked without a Selected Generation', async () => {
    playlistServer([ROAD_TRIP]);

    await openPlaylist();

    expect(screen.getByRole('heading', { level: 2, name: 'Road trip' })).toBeVisible();
    expect(screen.getByRole('textbox', { name: 'Description' })).toHaveValue('For the drive.');
    expect(titles()).toEqual(['Highway Lights', 'Sunrise Exit', 'Toll Booth Blues']);
    const first = rowOf('Highway Lights');
    expect(within(first).getByTestId('playlist-position')).toHaveTextContent('1.');
    expect(within(first).getByRole('link', { name: 'n8-1' })).toHaveAttribute(
      'href',
      '/songs/n8-1',
    );
    expect(first).toHaveTextContent('n8');
    expect(within(rowOf('Sunrise Exit')).getByText('Writing')).toBeVisible();
    expect(within(rowOf('Toll Booth Blues')).getByText('No Artist')).toBeVisible();
    expect(screen.getAllByTestId('no-selected-generation')).toHaveLength(3);
    expect(screen.getAllByText('No Selected Generation')[0]).toBeVisible();
  });

  it('does not mark a Song that has a Selected Generation', async () => {
    playlistServer([
      testPlaylist('Chosen', [], { songs: [{ ...entryOf(HIGHWAY), hasSelectedGeneration: true }] }),
    ]);
    renderApp('/playlists/01a1c000-0000-7000-8000-000000000002');
    await screen.findByRole('textbox', { name: 'Title' });

    expect(titles()).toEqual(['Highway Lights']);
    expect(screen.queryByTestId('no-selected-generation')).not.toBeInTheDocument();
  });

  it('adds a Song found by title at the end, showing Songs already on it disabled', async () => {
    const server = playlistServer([testPlaylist('Two', [HIGHWAY, SUNRISE])]);
    const user = userEvent.setup();
    renderApp(`/playlists/${server.playlists[0]?.id ?? ''}`);
    await screen.findByRole('textbox', { name: 'Title' });

    await user.type(screen.getByRole('textbox', { name: 'Add a Song' }), 'i');
    const results = await screen.findByRole('listbox', { name: 'Add a Song results' });
    await waitFor(() => {
      expect(within(results).getAllByRole('option')).toHaveLength(2);
    });
    expect(within(results).getByRole('option', { name: /Highway Lights/ })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
    expect(within(results).getByRole('option', { name: /Highway Lights/ })).toHaveTextContent(
      'on this Playlist',
    );
    expect(server.searches.at(-1)).toBe('i');

    await user.clear(screen.getByRole('textbox', { name: 'Add a Song' }));
    await user.type(screen.getByRole('textbox', { name: 'Add a Song' }), 'n8-3');
    await user.click(await screen.findByRole('option', { name: /Toll Booth Blues/ }));

    await waitFor(() => {
      expect(titles()).toEqual(['Highway Lights', 'Sunrise Exit', 'Toll Booth Blues']);
    });
    expect(server.writes.at(-1)).toMatchObject({
      method: 'POST',
      ifMatch: '"1"',
      body: { songId: TOLL.id },
    });
    expect(screen.getByTestId('playlist-status')).toHaveTextContent(
      'Added Toll Booth Blues at position 3.',
    );
  });

  it('reorders by keyboard with Move up and Move down, keeping the focus on the moved Song', async () => {
    const server = playlistServer([ROAD_TRIP]);
    const user = userEvent.setup();
    await openPlaylist();

    const up = within(rowOf('Toll Booth Blues')).getByRole('button', {
      name: 'Move Toll Booth Blues up',
    });
    up.focus();
    await user.keyboard('{Enter}');
    await waitFor(() => {
      expect(titles()).toEqual(['Highway Lights', 'Toll Booth Blues', 'Sunrise Exit']);
    });
    await waitFor(() => {
      expect(
        within(rowOf('Toll Booth Blues')).getByRole('button', { name: 'Move Toll Booth Blues up' }),
      ).toHaveFocus();
    });
    await user.keyboard('{Enter}');
    await waitFor(() => {
      expect(titles()).toEqual(['Toll Booth Blues', 'Highway Lights', 'Sunrise Exit']);
    });

    // At the top, Move up is disabled and the focus moves to Move down.
    await waitFor(() => {
      expect(
        within(rowOf('Toll Booth Blues')).getByRole('button', {
          name: 'Move Toll Booth Blues down',
        }),
      ).toHaveFocus();
    });
    expect(
      within(rowOf('Toll Booth Blues')).getByRole('button', { name: 'Move Toll Booth Blues up' }),
    ).toBeDisabled();
    expect(server.writes.map((write) => [write.method, write.ifMatch])).toEqual([
      ['PUT', '"1"'],
      ['PUT', '"2"'],
    ]);
    expect(server.writes.at(-1)?.body).toEqual({ songIds: [TOLL.id, HIGHWAY.id, SUNRISE.id] });
    expect(screen.getByTestId('playlist-status')).toHaveTextContent(
      'Moved Toll Booth Blues to position 1 of 3.',
    );
  });

  it('reorders by dragging a Song onto another', async () => {
    const server = playlistServer([ROAD_TRIP]);
    await openPlaylist();

    fireEvent.dragStart(rowOf('Toll Booth Blues'), { dataTransfer: { setData: () => undefined } });
    fireEvent.dragOver(rowOf('Highway Lights'));
    fireEvent.drop(rowOf('Highway Lights'));

    await waitFor(() => {
      expect(titles()).toEqual(['Toll Booth Blues', 'Highway Lights', 'Sunrise Exit']);
    });
    expect(server.writes.at(-1)).toMatchObject({
      method: 'PUT',
      body: { songIds: [TOLL.id, HIGHWAY.id, SUNRISE.id] },
    });
  });

  it('removes a Song, keeping the others in order', async () => {
    const server = playlistServer([ROAD_TRIP]);
    const user = userEvent.setup();
    await openPlaylist();

    await user.click(screen.getByRole('button', { name: 'Remove Sunrise Exit' }));

    await waitFor(() => {
      expect(titles()).toEqual(['Highway Lights', 'Toll Booth Blues']);
    });
    expect(server.writes.at(-1)).toMatchObject({ method: 'DELETE', ifMatch: '"1"' });
    expect(server.writes.at(-1)?.path).toContain(`/songs/${SUNRISE.id}`);
    expect(screen.getByTestId('playlist-status')).toHaveTextContent('Removed Sunrise Exit.');
  });

  it('shows the Playlist as it is, with a notice, when it changed elsewhere, and does not apply the change', async () => {
    const server = playlistServer([ROAD_TRIP]);
    const user = userEvent.setup();
    await openPlaylist();
    server.changeElsewhere(ROAD_TRIP.id, { songs: [entryOf(SUNRISE), entryOf(HIGHWAY)] });

    await user.click(screen.getByRole('button', { name: 'Move Toll Booth Blues up' }));

    await waitFor(() => {
      expect(titles()).toEqual(['Sunrise Exit', 'Highway Lights']);
    });
    expect(screen.getByTestId('playlist-status')).toHaveTextContent(
      'This Playlist was changed elsewhere, so your change was not applied.',
    );
    expect(server.playlists[0]?.songs.map((song) => song.title)).toEqual([
      'Sunrise Exit',
      'Highway Lights',
    ]);

    // The next change is based on the Playlist as it is now.
    await user.click(screen.getByRole('button', { name: 'Move Highway Lights up' }));
    await waitFor(() => {
      expect(titles()).toEqual(['Highway Lights', 'Sunrise Exit']);
    });
    expect(server.writes.at(-1)?.ifMatch).toBe('"2"');
  });

  it('says so when a Song is on the Playlist already', async () => {
    const server = playlistServer([testPlaylist('One', [HIGHWAY])]);
    const user = userEvent.setup();
    renderApp(`/playlists/${server.playlists[0]?.id ?? ''}`);
    await screen.findByRole('textbox', { name: 'Title' });
    // Added elsewhere after the search answered: the API refuses it.
    server.next = () =>
      new Response(
        JSON.stringify({
          status: 409,
          code: 'song_already_on_playlist',
          current: {
            ...server.playlists[0],
            songs: [entryOf(HIGHWAY), entryOf(SUNRISE)],
            songCount: 2,
            revision: 2,
          },
        }),
        { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
      );

    await user.type(screen.getByRole('textbox', { name: 'Add a Song' }), 'sunrise');
    await user.click(await screen.findByRole('option', { name: /Sunrise Exit/ }));

    expect(await screen.findByText('Sunrise Exit is on this Playlist already.')).toBeVisible();
    expect(titles()).toEqual(['Highway Lights', 'Sunrise Exit']);
  });

  it('saves the title on its own when it loses focus, under the Playlist revision', async () => {
    const server = playlistServer([ROAD_TRIP]);
    const user = userEvent.setup();
    const title = await openPlaylist();

    await user.clear(title);
    await user.type(title, '  Road trip 2026 ');
    await user.tab();

    await waitFor(() => {
      expect(server.writes).toHaveLength(1);
    });
    expect(server.writes[0]).toMatchObject({
      method: 'PATCH',
      ifMatch: '"1"',
      body: { title: 'Road trip 2026' },
    });
    expect(await screen.findByRole('heading', { level: 2, name: 'Road trip 2026' })).toBeVisible();
    expect(screen.getByTestId('playlist-save-status')).toHaveTextContent('Saved.');

    // A blank title is refused before anything is sent.
    await user.clear(screen.getByRole('textbox', { name: 'Title' }));
    await user.tab();
    expect(await screen.findByText('Enter a title.')).toBeVisible();
    expect(server.writes).toHaveLength(1);
  });

  it('says when there is no such Playlist', async () => {
    playlistServer([]);
    renderApp('/playlists/01a1c000-0000-7000-8000-999999999999');

    expect(
      await screen.findByRole('heading', { level: 2, name: 'No such Playlist' }),
    ).toBeVisible();
  });
});
