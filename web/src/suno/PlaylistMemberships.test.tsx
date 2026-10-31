import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { PlaylistMembership } from '../api/sunoPlaylistMemberships';
import { renderApp } from '../test/helpers';
import { COMMIT_JOB_ID, EXPORT_ID, importServer, testCommitResult } from '../test/importServer';

const PAGE = `/suno/imports/${EXPORT_ID}`;
const PLAYLIST_SYNC = { scope: { kind: 'playlists', ids: ['pl-1', 'pl-2'] } };

function membership(sunoId: string, change: Partial<PlaylistMembership> = {}): PlaylistMembership {
  return {
    sunoId,
    name: 'Night Drive',
    songCount: 3,
    read: 3,
    complete: true,
    change: 'new',
    added: 3,
    dropped: 0,
    inCatalog: 1,
    inExport: 1,
    notImported: ['c-none'],
    record: true,
    chosen: false,
    ...change,
  };
}

function playlistRow(sunoId: string): HTMLElement {
  const found = document.querySelector<HTMLElement>(`tr[data-playlist="${sunoId}"]`);
  if (found === null) {
    throw new Error(`No row for ${sunoId}`);
  }
  return found;
}

describe('the playlists of a playlist sync in the import review (#449)', () => {
  it('lists each playlist’s songs against the catalog, naming those not imported, and confirms them', async () => {
    const server = importServer([], PLAYLIST_SYNC);
    server.memberships = [
      membership('pl-1'),
      membership('pl-2', {
        name: 'Long Mix',
        songCount: 24,
        read: 20,
        complete: false,
        change: 'changed',
        added: 2,
        dropped: 1,
        notImported: [],
        record: false,
      }),
    ];
    renderApp(PAGE);

    expect(await screen.findByRole('table', { name: 'Playlists read' })).toBeVisible();
    const first = playlistRow('pl-1');
    expect(within(first).getByTestId('playlist-read')).toHaveTextContent('Read all 3 songs.');
    expect(within(first).getByTestId('playlist-change')).toHaveTextContent(
      'n8Tracks has not recorded its songs before.',
    );
    expect(within(first).getByTestId('playlist-members')).toHaveTextContent(
      '1 song in n8Tracks, 1 song in this import, 1 song not imported.',
    );
    expect(within(first).getByTestId('playlist-not-imported')).toHaveTextContent(
      'Not imported: c-none',
    );
    expect(
      within(first).getByRole('checkbox', { name: 'Record the songs of Night Drive' }),
    ).toBeChecked();

    const partial = playlistRow('pl-2');
    expect(within(partial).getByTestId('playlist-read')).toHaveTextContent(
      'Read in part: 20 of 24 songs (Suno showed no more). Not recorded unless you tick it.',
    );
    expect(within(partial).getByTestId('playlist-change')).toHaveTextContent(
      'Changed since n8Tracks recorded it: 2 songs added, 1 song gone.',
    );
    expect(
      within(partial).getByRole('checkbox', { name: 'Record the songs of Long Mix' }),
    ).not.toBeChecked();

    // The summary counts them, so a playlist sync with no records can be confirmed.
    expect(await screen.findByTestId('summary-playlists')).toHaveTextContent(
      'Record the songs of 1 playlist of 2 this sync read',
    );
    expect(screen.getByRole('button', { name: 'Confirm import' })).toBeEnabled();
  });

  it('records a choice with the revision read, and the summary follows', async () => {
    const server = importServer([], PLAYLIST_SYNC);
    server.memberships = [membership('pl-1')];
    renderApp(PAGE);

    await userEvent.click(
      await screen.findByRole('checkbox', { name: 'Record the songs of Night Drive' }),
    );

    await waitFor(() => {
      expect(server.membershipPatches).toEqual([
        { ifMatch: '"1"', body: { sunoIds: ['pl-1'], record: false } },
      ]);
    });
    expect(await screen.findByTestId('playlist-membership-message')).toHaveTextContent(
      'Its songs are left as n8Tracks has them.',
    );
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Confirm import' })).toBeDisabled();
    });
  });

  it('is not shown for any other sync', async () => {
    const server = importServer([]);
    server.memberships = [membership('pl-1')];
    renderApp(PAGE);

    expect(await screen.findByRole('button', { name: 'Confirm import' })).toBeVisible();
    expect(screen.queryByRole('table', { name: 'Playlists read' })).not.toBeInTheDocument();
  });

  it('says what the commit did with the playlists', async () => {
    const server = importServer([], { ...PLAYLIST_SYNC, state: 'committed', jobId: COMMIT_JOB_ID });
    server.finishCommit(
      testCommitResult({
        playlists: [
          { sunoId: 'pl-1', outcome: 'applied' },
          { sunoId: 'pl-2', outcome: 'skipped' },
        ],
      }),
    );
    renderApp(PAGE);

    expect(await screen.findByTestId('commit-playlists')).toHaveTextContent(
      'Recorded the songs of 1 playlist; left 1 playlist as they were.',
    );
  });
});
