import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { Song } from '../api/songs';
import type { SunoWorkspace } from '../api/sunoWorkspaces';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';
import { ARCHIVE, baseSong, DEMOS, STUDIO } from '../test/songServer';

/** A Song for the fake, numbered `n`, in the workspace with Suno ID `workspace`. */
function songIn(n: number, title: string, workspace: SunoWorkspace): Song {
  return {
    ...baseSong,
    id: `0199b1a0-0000-7000-8000-${String(100 + n).padStart(12, '0')}`,
    shortcode: `n8-${String(n)}`,
    title,
    sunoWorkspace: { id: workspace.id, name: workspace.name, state: workspace.state },
  };
}

interface ReceivedMove {
  from: string;
  body: Record<string, unknown>;
}

/**
 * A fake n8Tracks with the workspaces Archive (Unavailable), Demos, and Studio, and Songs in them:
 * it lists the workspaces with live counts, lists a workspace's Songs by title, and moves them as
 * the API does (all or nothing, the target Available and another). `server.moves` holds each POST;
 * `server.next` answers the next one instead.
 */
function workspaceServer(songs: Song[]) {
  const server = {
    workspaces: [ARCHIVE, DEMOS, STUDIO].map((workspace) => ({ ...workspace })),
    songs: songs.map((song) => ({ ...song })),
    moves: [] as ReceivedMove[],
    /** The query string of every Song list request, in order. */
    lists: [] as string[],
    next: undefined as (() => Response) | undefined,
  };
  const listed = () =>
    server.workspaces.map((workspace) => ({
      ...workspace,
      songCount: server.songs.filter((song) => song.sunoWorkspace?.id === workspace.id).length,
    }));

  stubFetch().mockImplementation((input, init) => {
    const path = requestPath(input);
    const url = new URL(input instanceof Request ? input.url : input.toString(), document.baseURI);
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (path.endsWith('/api/v1/suno/workspaces')) {
      return Promise.resolve(jsonResponse(200, { items: listed() }));
    }
    const move = /\/api\/v1\/suno\/workspaces\/([^/]+)\/move-songs$/.exec(path);
    if (move !== null) {
      const from = decodeURIComponent(move[1] ?? '');
      const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as Record<
        string,
        unknown
      >;
      server.moves.push({ from, body });
      const next = server.next;
      if (next) {
        server.next = undefined;
        return Promise.resolve(next());
      }
      const to = server.workspaces.find((workspace) => workspace.id === body.targetWorkspaceId);
      if (to?.state !== 'available' || to.id === from) {
        return Promise.resolve(
          jsonResponse(422, {
            code: 'validation_failed',
            errors: { targetWorkspaceId: ['Choose an available other workspace.'] },
          }),
        );
      }
      const inFrom = server.songs.filter((song) => song.sunoWorkspace?.id === from);
      const named = Array.isArray(body.songIds) ? (body.songIds as string[]) : undefined;
      const missing = (named ?? []).filter((id) => !inFrom.some((song) => song.id === id));
      if (missing.length > 0) {
        return Promise.resolve(
          jsonResponse(422, { code: 'song_not_in_workspace', songs: missing }),
        );
      }
      const moving = body.all === true ? inFrom : inFrom.filter((song) => named?.includes(song.id));
      for (const song of moving) {
        song.sunoWorkspace = { id: to.id, name: to.name, state: to.state };
      }
      return Promise.resolve(jsonResponse(200, { moved: moving.length }));
    }
    if (path.endsWith('/api/v1/songs')) {
      server.lists.push(url.searchParams.toString());
      const workspace = url.searchParams.get('workspace');
      const pageSize = Number(url.searchParams.get('pageSize') ?? '50');
      const page = Number(url.searchParams.get('page') ?? '1');
      const matching = server.songs
        .filter((song) => song.sunoWorkspace?.id === workspace)
        .sort((a, b) => a.title.localeCompare(b.title));
      return Promise.resolve(
        jsonResponse(200, {
          items: matching.slice((page - 1) * pageSize, page * pageSize),
          page,
          pageSize,
          total: matching.length,
        }),
      );
    }
    return Promise.resolve(jsonResponse(404, { code: 'not_found' }));
  });
  return server;
}

const FIRST = songIn(1, 'Alpha', DEMOS);
const SECOND = songIn(2, 'Bravo', DEMOS);
const THIRD = songIn(3, 'Charlie', DEMOS);
const OLD = songIn(4, 'Old tune', ARCHIVE);

async function openWorkspace(name: string) {
  const user = userEvent.setup();
  renderApp('/settings/suno-workspaces');
  const table = await screen.findByRole('table', { name: 'Suno workspaces' });
  await user.click(within(table).getByRole('link', { name }));
  await screen.findByRole('heading', { level: 2, name });
  await screen.findByRole('table', { name: 'Songs in this workspace' });
  return user;
}

describe('Settings → Suno workspaces', () => {
  it('lists every workspace by name with its state and Song count, an Unavailable one with when it was last seen', async () => {
    workspaceServer([FIRST, SECOND, THIRD, OLD]);
    renderApp('/settings/suno-workspaces');

    expect(await screen.findByRole('heading', { level: 2, name: 'Suno workspaces' })).toBeVisible();
    const rows = within(await screen.findByRole('table', { name: 'Suno workspaces' }))
      .getAllByTestId('workspace-row')
      .map((row) =>
        within(row)
          .getAllByRole('cell')
          .map((cell) => cell.textContent),
      );
    expect(rows).toEqual([
      [expect.stringMatching(/^Unavailablelast seen .*2026/), '1'],
      ['Available', '3'],
      ['Available', '0'],
    ]);
    expect(screen.getByRole('link', { name: 'Archive' })).toHaveAttribute(
      'href',
      `/settings/suno-workspaces/${ARCHIVE.id}`,
    );
    expect(screen.getByRole('link', { name: 'Demos' })).toHaveAttribute(
      'href',
      '/settings/suno-workspaces/default',
    );
    expect(screen.getAllByTestId('workspace-state-unavailable')).toHaveLength(1);
  });

  it('says so when no workspace is known yet', async () => {
    const server = workspaceServer([]);
    server.workspaces = [];
    renderApp('/settings/suno-workspaces');

    expect(await screen.findByTestId('no-workspaces')).toHaveTextContent(
      'No Suno workspaces are known yet',
    );
  });

  it('opens a workspace to show its Songs, by title', async () => {
    const server = workspaceServer([THIRD, FIRST, SECOND, OLD]);
    await openWorkspace('Demos');

    expect(
      screen
        .getAllByTestId('workspace-song')
        .map((row) => within(row).getByRole('link').textContent),
    ).toEqual(['Alpha', 'Bravo', 'Charlie']);
    expect(screen.getByRole('link', { name: 'Alpha' })).toHaveAttribute('href', '/songs/n8-1');
    expect(screen.getByTestId('workspace-summary')).toHaveTextContent('Available3 Songs');
    expect(server.lists).toContain('workspace=default&sort=title&pageSize=100');
  });

  it('moves the selected Songs to another workspace after a confirmation stating how many', async () => {
    const server = workspaceServer([FIRST, SECOND, THIRD]);
    const user = await openWorkspace('Demos');
    const move = screen.getByRole('button', { name: 'Move selected Songs' });
    expect(move).toBeDisabled();

    await user.click(screen.getByRole('checkbox', { name: 'Select Alpha' }));
    await user.click(screen.getByRole('checkbox', { name: 'Select Charlie' }));
    // Only the Available other workspaces are offered.
    const target = screen.getByRole('combobox', { name: 'Move to' });
    expect(
      within(target)
        .getAllByRole('option')
        .map((option) => option.textContent),
    ).toEqual(['Choose…', 'Studio']);
    expect(screen.getByRole('button', { name: 'Move 2 Songs' })).toBeDisabled();
    await user.selectOptions(target, 'Studio');
    await user.click(screen.getByRole('button', { name: 'Move 2 Songs' }));

    const dialog = await screen.findByRole('dialog', { name: 'Move 2 Songs?' });
    expect(within(dialog).getByTestId('move-count')).toHaveTextContent(
      '2 Songs will move from Demos to Studio.',
    );
    expect(server.moves).toEqual([]);
    await user.click(within(dialog).getByRole('button', { name: 'Move 2 Songs' }));

    expect(await screen.findByTestId('songs-moved')).toHaveTextContent('Moved 2 Songs to Studio.');
    expect(server.moves).toEqual([
      { from: 'default', body: { songIds: [FIRST.id, THIRD.id], targetWorkspaceId: STUDIO.id } },
    ]);
    await waitFor(() => {
      expect(
        screen
          .getAllByTestId('workspace-song')
          .map((row) => within(row).getByRole('link').textContent),
      ).toEqual(['Bravo']);
    });
    await waitFor(() => {
      expect(screen.getByTestId('workspace-summary')).toHaveTextContent('1 Song');
    });
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
  });

  it('moves every Song with one "all" command, and cancelling the confirmation moves nothing', async () => {
    const server = workspaceServer([FIRST, SECOND, THIRD]);
    const user = await openWorkspace('Demos');

    await user.click(
      screen.getByRole('checkbox', { name: 'Select all 3 Songs in this workspace' }),
    );
    expect(screen.getByRole('checkbox', { name: 'Select Bravo' })).toBeChecked();
    expect(screen.getByTestId('selected-count')).toHaveTextContent('3 Songs selected of 3 Songs');
    await user.selectOptions(screen.getByRole('combobox', { name: 'Move to' }), 'Studio');

    await user.click(screen.getByRole('button', { name: 'Move 3 Songs' }));
    await user.click(
      within(await screen.findByRole('dialog', { name: 'Move 3 Songs?' })).getByRole('button', {
        name: 'Cancel',
      }),
    );
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    expect(server.moves).toEqual([]);

    await user.click(screen.getByRole('button', { name: 'Move 3 Songs' }));
    const dialog = await screen.findByRole('dialog', { name: 'Move 3 Songs?' });
    await user.click(within(dialog).getByRole('button', { name: 'Move 3 Songs' }));

    expect(await screen.findByTestId('songs-moved')).toHaveTextContent('Moved 3 Songs to Studio.');
    expect(server.moves).toEqual([
      { from: 'default', body: { all: true, targetWorkspaceId: STUDIO.id } },
    ]);
    expect(await screen.findByTestId('no-workspace-songs')).toHaveTextContent(
      'No Songs are in this workspace.',
    );

    // The workspace moved to now shows them.
    await user.click(screen.getByRole('link', { name: 'Studio' }));
    await screen.findByRole('heading', { level: 2, name: 'Studio' });
    await waitFor(() => {
      expect(screen.getAllByTestId('workspace-song')).toHaveLength(3);
    });
  });

  it('shows an Unavailable workspace with when it was last seen, and moves its Songs to an Available one', async () => {
    const server = workspaceServer([OLD, FIRST]);
    const user = await openWorkspace('Archive');

    expect(screen.getByTestId('workspace-summary')).toHaveTextContent(
      /Unavailablelast seen .*2026/,
    );
    expect(screen.getByText('Suno no longer offers this workspace')).toBeVisible();
    const target = screen.getByRole('combobox', { name: 'Move to' });
    expect(
      within(target)
        .getAllByRole('option')
        .map((option) => option.textContent),
    ).toEqual(['Choose…', 'Demos', 'Studio']);

    await user.click(screen.getByRole('checkbox', { name: 'Select Old tune' }));
    await user.selectOptions(target, 'Demos');
    await user.click(screen.getByRole('button', { name: 'Move 1 Song' }));
    const dialog = await screen.findByRole('dialog', { name: 'Move 1 Song?' });
    expect(within(dialog).getByTestId('move-count')).toHaveTextContent(
      '1 Song will move from Archive to Demos.',
    );
    await user.click(within(dialog).getByRole('button', { name: 'Move 1 Song' }));

    expect(await screen.findByTestId('songs-moved')).toHaveTextContent('Moved 1 Song to Demos.');
    expect(server.songs.find((song) => song.id === OLD.id)?.sunoWorkspace?.id).toBe(DEMOS.id);
  });

  it('keeps the dialog open and says nothing moved when the move is refused, reading the Songs again', async () => {
    const server = workspaceServer([FIRST, SECOND]);
    const user = await openWorkspace('Demos');
    await user.click(screen.getByRole('checkbox', { name: 'Select Alpha' }));
    await user.selectOptions(screen.getByRole('combobox', { name: 'Move to' }), 'Studio');
    server.next = () => jsonResponse(422, { code: 'song_not_in_workspace', songs: [FIRST.id] });
    const listsBefore = server.lists.length;

    await user.click(screen.getByRole('button', { name: 'Move 1 Song' }));
    const dialog = await screen.findByRole('dialog', { name: 'Move 1 Song?' });
    await user.click(within(dialog).getByRole('button', { name: 'Move 1 Song' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'Nothing moved: some of the selected Songs are no longer in this workspace.',
    );
    expect(screen.queryByTestId('songs-moved')).not.toBeInTheDocument();
    await waitFor(() => {
      expect(server.lists.length).toBeGreaterThan(listsBefore);
    });
    expect(server.songs.every((song) => song.sunoWorkspace?.id === DEMOS.id)).toBe(true);
  });

  it('says why a move failed when n8Tracks does not answer as expected', async () => {
    const server = workspaceServer([FIRST]);
    const user = await openWorkspace('Demos');
    await user.click(screen.getByRole('checkbox', { name: 'Select Alpha' }));
    await user.selectOptions(screen.getByRole('combobox', { name: 'Move to' }), 'Studio');
    server.next = () => jsonResponse(500, { code: 'unexpected' });

    await user.click(screen.getByRole('button', { name: 'Move 1 Song' }));
    const dialog = await screen.findByRole('dialog', { name: 'Move 1 Song?' });
    await user.click(within(dialog).getByRole('button', { name: 'Move 1 Song' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'Nothing moved: n8Tracks did not answer as expected.',
    );
  });

  it('offers no move when there is no other Available workspace', async () => {
    const server = workspaceServer([FIRST]);
    server.workspaces = server.workspaces.filter((workspace) => workspace.id !== STUDIO.id);
    await openWorkspace('Demos');

    expect(screen.getByTestId('no-move-target')).toBeVisible();
    expect(screen.getByRole('combobox', { name: 'Move to' })).toBeDisabled();
  });

  it('says a workspace it does not know is not found', async () => {
    workspaceServer([]);
    renderApp('/settings/suno-workspaces/no-such-workspace');

    expect(
      await screen.findByRole('heading', { level: 2, name: 'Workspace not found' }),
    ).toBeVisible();
    expect(screen.getByRole('link', { name: 'All Suno workspaces' })).toHaveAttribute(
      'href',
      '/settings/suno-workspaces',
    );
  });
});
