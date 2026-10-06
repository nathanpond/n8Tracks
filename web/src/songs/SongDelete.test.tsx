import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { deletedSongOf, type SongDeletionImpact } from '../api/songDeletion';
import { jsonResponse, renderApp } from '../test/helpers';
import { baseSong } from '../test/songServer';
import { testVersion, versionServer } from '../test/versionServer';
import { songDeletionCounts, titleConfirms } from './songDeletion';

const ONE = [testVersion('1', { current: true })];
const THREE = [testVersion('1'), testVersion('2'), testVersion('3', { current: true })];
const ON_AN_ALBUM = {
  ...baseSong,
  albums: [{ id: '0199b1a0-0000-7000-8000-0000000000a1', title: 'Pack EP', disc: 1, track: 2 }],
};

async function openSong(path = '/songs/n8-7') {
  renderApp(path);
  await screen.findByRole('tree', { name: 'Versions' });
}

async function openDialog(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('button', { name: 'Delete Song' }));
  const dialog = await screen.findByRole('dialog', { name: 'Delete “Running in a Pack”?' });
  await within(dialog).findByTestId('delete-song-counts');
  return dialog;
}

function confirmButton(dialog: HTMLElement) {
  return within(dialog).getByRole('button', { name: 'Delete Song' });
}

describe('deleting a Song', () => {
  it('confirms a Song with one empty Version plainly, then shows the Songs table with a notice', async () => {
    const user = userEvent.setup();
    const { server } = versionServer(ONE);
    await openSong();

    const dialog = await openDialog(user);
    expect(within(dialog).getByTestId('delete-song-summary')).toHaveTextContent(
      'Deleting n8-7 “Running in a Pack” is permanent.',
    );
    expect(
      within(within(dialog).getByTestId('delete-song-counts'))
        .getAllByRole('listitem')
        .map((item) => item.textContent),
    ).toEqual([
      '1 Version',
      '0 Generations',
      '0 managed artwork images',
      '0 Album memberships',
      '0 Playlist memberships',
      '0 relationships',
      '0 local audio files',
    ]);
    expect(within(dialog).queryByRole('textbox')).toBeNull();
    expect(confirmButton(dialog)).toBeEnabled();

    await user.click(confirmButton(dialog));

    expect(await screen.findByRole('heading', { level: 2, name: 'Songs' })).toBeVisible();
    const notice = screen.getByTestId('song-deleted-notice');
    expect(within(notice).getByRole('status')).toHaveTextContent(
      'Deleted n8-7 “Running in a Pack”.',
    );
    expect(server.songDeletes).toEqual([{ ifMatch: '"1"', body: {} }]);

    await user.click(within(notice).getByRole('button', { name: 'Dismiss' }));
    expect(screen.queryByTestId('song-deleted-notice')).toBeNull();
  });

  it('asks for the title when the Song has more than one Version or a membership, and enables Delete only on a match', async () => {
    const user = userEvent.setup();
    const { server } = versionServer(THREE, ON_AN_ALBUM);
    await openSong();

    const dialog = await openDialog(user);
    expect(within(dialog).getByText('3 Versions')).toBeInTheDocument();
    expect(within(dialog).getByText('1 Album membership')).toBeInTheDocument();
    const typed = within(dialog).getByRole('textbox', { name: 'Type the Song’s title to confirm' });
    expect(confirmButton(dialog)).toBeDisabled();

    await user.type(typed, 'running in a pack');
    expect(confirmButton(dialog)).toBeDisabled();
    await user.clear(typed);
    await user.type(typed, '  Running in a Pack ');
    expect(confirmButton(dialog)).toBeEnabled();

    await user.click(confirmButton(dialog));

    expect(await screen.findByTestId('song-deleted-notice')).toHaveTextContent(
      'Deleted n8-7 “Running in a Pack”.',
    );
    expect(server.songDeletes).toEqual([
      { ifMatch: '"1"', body: { confirmTitle: '  Running in a Pack ' } },
    ]);
  });

  it('refreshes the counts and asks for the title when the server requires it at delete time', async () => {
    const user = userEvent.setup();
    const { server } = versionServer(ONE);
    await openSong();
    const dialog = await openDialog(user);
    expect(within(dialog).queryByRole('textbox')).toBeNull();

    // Another client adds a Version while the plain confirmation is open.
    server.addElsewhere('2');
    await user.click(confirmButton(dialog));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'Not deleted: the Song changed since this opened, and deleting it now needs its title typed.',
    );
    expect(within(dialog).getByText('2 Versions')).toBeInTheDocument();
    expect(
      within(dialog).getByRole('textbox', { name: 'Type the Song’s title to confirm' }),
    ).toBeVisible();
    expect(confirmButton(dialog)).toBeDisabled();
    expect(server.songDeletedAt).toBeUndefined();
    expect(screen.getByRole('heading', { level: 2, name: 'Running in a Pack' })).toBeVisible();
  });

  it('reads the counts again after a conflict, and deletes nothing', async () => {
    const user = userEvent.setup();
    const { server } = versionServer(ONE);
    await openSong();
    const dialog = await openDialog(user);
    server.nextSongDelete = () =>
      jsonResponse(409, {
        code: 'revision_conflict',
        current: { ...server.song, revision: server.song.revision + 1 },
      });

    await user.click(confirmButton(dialog));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'Not deleted: the Song was changed elsewhere since this opened.',
    );
    await within(dialog).findByTestId('delete-song-counts');
    expect(server.songDeletedAt).toBeUndefined();
  });

  it('says so when the Song is no longer there, and closes on Cancel without deleting', async () => {
    const user = userEvent.setup();
    const { server } = versionServer(ONE);
    await openSong();
    const dialog = await openDialog(user);
    server.nextSongDelete = () => jsonResponse(404, { code: 'not_found' });

    await user.click(confirmButton(dialog));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'This Song is no longer there',
    );

    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(server.songDeletes).toHaveLength(1);
  });

  it('shows a deleted Song’s page as deleted, naming it, not as not found', async () => {
    const { server } = versionServer(ONE);
    server.songDeletedAt = '2026-10-01T10:00:00Z';

    renderApp('/songs/n8-7');

    expect(
      await screen.findByRole('heading', { level: 2, name: 'This Song was deleted' }),
    ).toBeVisible();
    expect(screen.getByTestId('song-deleted')).toHaveTextContent(
      /^n8-7 “Running in a Pack” was deleted on .+\. Its shortcode is never used for another Song\.$/,
    );
    expect(screen.queryByRole('heading', { name: 'Song not found' })).toBeNull();
  });
});

describe('the deletion rules the dialog uses', () => {
  const impact: SongDeletionImpact = {
    id: baseSong.id,
    shortcode: 'n8-7',
    title: 'Night Drive',
    versionCount: 2,
    generationCount: 1,
    artworkCount: 1,
    albumCount: 0,
    playlistCount: 3,
    relationshipCount: 1,
    audioFileCount: 0,
    titleRequired: true,
    revision: 4,
  };

  it('lists every count, singular or plural', () => {
    expect(songDeletionCounts(impact)).toEqual([
      '2 Versions',
      '1 Generation',
      '1 managed artwork image',
      '0 Album memberships',
      '3 Playlist memberships',
      '1 relationship',
      '0 local audio files',
    ]);
  });

  it('matches the title after trimming and exactly otherwise', () => {
    expect(titleConfirms('Night Drive', 'Night Drive')).toBe(true);
    expect(titleConfirms('  Night Drive\t', 'Night Drive')).toBe(true);
    expect(titleConfirms('night drive', 'Night Drive')).toBe(false);
    expect(titleConfirms('Night  Drive', 'Night Drive')).toBe(false);
    expect(titleConfirms('', 'Night Drive')).toBe(false);
  });

  it('reads a deleted Song from a 404 answer, and nothing from a plain one', () => {
    expect(
      deletedSongOf({
        code: 'song_deleted',
        songId: baseSong.id,
        shortcode: 'n8-7',
        title: 'Night Drive',
        deletedAt: '2026-10-01T10:00:00Z',
      }),
    ).toEqual({
      songId: baseSong.id,
      shortcode: 'n8-7',
      title: 'Night Drive',
      deletedAt: '2026-10-01T10:00:00Z',
    });
    expect(deletedSongOf({ code: 'not_found' })).toBeUndefined();
    expect(deletedSongOf(undefined)).toBeUndefined();
  });
});
