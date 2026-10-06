import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { albumServer, testAlbum, trackOf } from '../test/albumServer';
import { jsonResponse, renderApp } from '../test/helpers';
import { HIGHWAY, playlistServer, SUNRISE, testPlaylist, TOLL } from '../test/playlistServer';

const ROAD_TRIP = testPlaylist('Road trip', [HIGHWAY, SUNRISE, TOLL]);
const OTHER_MIX = testPlaylist('Other mix', [HIGHWAY]);
const PACK = testAlbum('Pack EP', {
  songCount: 2,
  tracks: [trackOf(HIGHWAY, 1, 1), trackOf(SUNRISE, 1, 2)],
});
const EMPTY = testAlbum('Empty EP');

/** Opens the record's page and its delete confirmation; the dialog. */
async function openDialog(path: string, noun: 'Album' | 'Playlist', title: string) {
  const user = userEvent.setup();
  renderApp(path);
  await screen.findByRole('textbox', { name: 'Title' });
  await user.click(screen.getByRole('button', { name: `Delete ${noun}` }));
  const dialog = await screen.findByRole('dialog', { name: `Delete “${title}”?` });
  await waitFor(() => {
    expect(dialog).toBeVisible();
  });
  return { user, dialog };
}

describe('deleting a Playlist', () => {
  it('states the Song count, that the Songs stay, and that it is permanent, then opens the list with a notice', async () => {
    const server = playlistServer([ROAD_TRIP, OTHER_MIX]);
    const { user, dialog } = await openDialog(
      `/playlists/${ROAD_TRIP.id}`,
      'Playlist',
      'Road trip',
    );

    const summary = within(dialog).getByTestId('delete-collection-summary');
    expect(summary).toHaveTextContent('Deleting the Playlist “Road trip” is permanent.');
    expect(within(dialog).getByTestId('delete-collection-songs')).toHaveTextContent(
      'It holds 3 Songs. The Songs themselves are not deleted: they stay in the catalog and are only taken off this Playlist.',
    );

    await user.click(within(dialog).getByRole('button', { name: 'Delete Playlist' }));

    const notice = await screen.findByTestId('collection-deleted-notice');
    expect(notice).toHaveTextContent(
      'Deleted the Playlist “Road trip”. Its Songs were not deleted.',
    );
    expect(screen.getByRole('heading', { level: 2, name: 'Playlists' })).toBeVisible();
    expect(await screen.findByRole('link', { name: 'Other mix' })).toBeVisible();
    expect(screen.queryByRole('link', { name: 'Road trip' })).not.toBeInTheDocument();
    expect(server.deleted).toEqual([ROAD_TRIP.id]);
    expect(server.writes).toEqual([expect.objectContaining({ method: 'DELETE', ifMatch: '"1"' })]);
    // No Song was written.
    expect(server.writes.filter((write) => write.path.includes('/songs'))).toEqual([]);

    await user.click(within(notice).getByRole('button', { name: 'Dismiss' }));
    expect(screen.queryByTestId('collection-deleted-notice')).not.toBeInTheDocument();
  });

  it('changes nothing when cancelled', async () => {
    const server = playlistServer([ROAD_TRIP]);
    const { user, dialog } = await openDialog(
      `/playlists/${ROAD_TRIP.id}`,
      'Playlist',
      'Road trip',
    );

    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    expect(screen.getByRole('heading', { level: 2, name: 'Road trip' })).toBeVisible();
    expect(server.writes).toEqual([]);
  });

  it('says so when it is no longer there, or when n8Tracks does not answer', async () => {
    const server = playlistServer([ROAD_TRIP]);
    const { user, dialog } = await openDialog(
      `/playlists/${ROAD_TRIP.id}`,
      'Playlist',
      'Road trip',
    );

    server.next = () => jsonResponse(500, { code: 'internal' });
    await user.click(within(dialog).getByRole('button', { name: 'Delete Playlist' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'Not deleted: n8Tracks did not answer as expected.',
    );

    server.next = () => jsonResponse(404, { code: 'not_found' });
    await user.click(within(dialog).getByRole('button', { name: 'Delete Playlist' }));
    expect(await within(dialog).findByText(/no longer there/)).toBeVisible();
    expect(server.deleted).toEqual([]);
  });
});

describe('deleting an Album', () => {
  it('refreshes the count when the Album changed elsewhere, and deletes it at its new revision', async () => {
    const server = albumServer([PACK]);
    const { user, dialog } = await openDialog(`/albums/${PACK.id}`, 'Album', 'Pack EP');
    expect(within(dialog).getByTestId('delete-collection-summary')).toHaveTextContent(
      'Deleting the Album “Pack EP” is permanent. Its track order, links, and artwork are deleted with it.',
    );
    expect(within(dialog).getByTestId('delete-collection-songs')).toHaveTextContent(
      'It holds 2 Songs. The Songs themselves are not deleted',
    );

    server.changeElsewhere(PACK.id, {
      songCount: 3,
      tracks: [...PACK.tracks, trackOf(TOLL, 1, 3)],
    });
    await user.click(within(dialog).getByRole('button', { name: 'Delete Album' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'Not deleted: the Album was changed elsewhere since this opened. It now holds 3 Songs. Check, then choose Delete again.',
    );
    expect(within(dialog).getByTestId('delete-collection-songs')).toHaveTextContent(
      'It holds 3 Songs.',
    );
    expect(server.deleted).toEqual([]);

    await user.click(within(dialog).getByRole('button', { name: 'Delete Album' }));

    expect(await screen.findByTestId('collection-deleted-notice')).toHaveTextContent(
      'Deleted the Album “Pack EP”. Its Songs were not deleted.',
    );
    expect(screen.getByRole('heading', { level: 2, name: 'Albums' })).toBeVisible();
    expect(server.deleted).toEqual([PACK.id]);
    expect(server.writes.map((write) => [write.method, write.ifMatch])).toEqual([
      ['DELETE', '"1"'],
      ['DELETE', '"2"'],
    ]);
  });

  it('says that no Song is affected when it holds none', async () => {
    albumServer([EMPTY]);
    const { dialog } = await openDialog(`/albums/${EMPTY.id}`, 'Album', 'Empty EP');

    expect(within(dialog).getByTestId('delete-collection-songs')).toHaveTextContent(
      'It holds no Songs. No Song is affected.',
    );
  });
});
