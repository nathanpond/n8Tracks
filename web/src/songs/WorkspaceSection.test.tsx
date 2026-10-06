import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { workspaceChoices, workspaceName } from '../api/sunoWorkspaces';
import { renderApp } from '../test/helpers';
import {
  ARCHIVE,
  baseSong,
  DEMOS,
  songServer,
  STUDIO,
  testWorkspace,
  WORKSPACES,
} from '../test/songServer';

async function openWorkspace(user: ReturnType<typeof userEvent.setup>) {
  renderApp('/songs/n8-7');
  await screen.findByRole('heading', { level: 2, name: 'Running in a Pack' });
  await user.click(screen.getByRole('button', { name: 'Details' }));
  const select = await screen.findByRole('combobox', { name: 'Workspace' });
  await waitFor(() => {
    expect(select).toBeEnabled();
  });
  return select;
}

/** The options the workspace select offers, by label. */
function options(select: HTMLElement) {
  return within(select)
    .getAllByRole('option')
    .map((option) => option.textContent);
}

describe('the Suno workspace section of the Details panel', () => {
  it('offers the Available workspaces and saves the one chosen by its Suno ID', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    const select = await openWorkspace(user);

    // The one that has gone from Suno is not offered.
    expect(options(select)).toEqual(['None', 'Demos', 'Studio']);
    expect(select).toHaveValue('');

    await user.selectOptions(select, 'Studio');

    await waitFor(() => {
      expect(server.song.sunoWorkspace).toEqual({
        id: STUDIO.id,
        name: 'Studio',
        state: 'available',
      });
    });
    expect(server.edits).toEqual([{ ifMatch: '"1"', body: { sunoWorkspaceId: STUDIO.id } }]);
    expect(select).toHaveValue(STUDIO.id);
    expect(screen.queryByTestId('workspace-unavailable')).not.toBeInTheDocument();
  });

  it('moves the Song to another workspace, then clears it', async () => {
    const user = userEvent.setup();
    const { server } = songServer({
      ...baseSong,
      sunoWorkspace: { id: STUDIO.id, name: 'Studio', state: 'available' },
    });
    const select = await openWorkspace(user);
    expect(select).toHaveValue(STUDIO.id);

    await user.selectOptions(select, 'Demos');
    await waitFor(() => {
      expect(server.song.sunoWorkspace?.id).toBe(DEMOS.id);
    });
    await user.selectOptions(select, 'None');

    await waitFor(() => {
      expect(server.song.sunoWorkspace).toBeNull();
    });
    expect(server.edits.map((edit) => edit.body)).toEqual([
      { sunoWorkspaceId: DEMOS.id },
      { sunoWorkspaceId: null },
    ]);
    expect(server.song.revision).toBe(3);
    expect(select).toHaveValue('');
  });

  it('warns in the header and the Details while the Song’s workspace is unavailable, and keeps it', async () => {
    const user = userEvent.setup();
    const { server } = songServer({
      ...baseSong,
      sunoWorkspace: { id: ARCHIVE.id, name: 'Archive', state: 'unavailable' },
    });
    const select = await openWorkspace(user);

    // Two badges: the header's and the Details'.
    expect(screen.getAllByTestId('workspace-unavailable')).toHaveLength(2);
    expect(screen.getByTestId('workspace-warning')).toHaveTextContent('Archive is unavailable');
    expect(options(select)).toEqual(['None', 'Archive (unavailable)', 'Demos', 'Studio']);
    expect(select).toHaveValue(ARCHIVE.id);

    // Leaving it for an Available one clears the warnings.
    await user.selectOptions(select, 'Demos');
    await waitFor(() => {
      expect(screen.queryByTestId('workspace-unavailable')).not.toBeInTheDocument();
    });
    expect(server.song.sunoWorkspace?.id).toBe(DEMOS.id);
    expect(screen.queryByTestId('workspace-warning')).not.toBeInTheDocument();
  });

  it('shows a refusal when the workspace went from Suno meanwhile, and reads the list again', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    const select = await openWorkspace(user);
    server.workspaces = [ARCHIVE, DEMOS, { ...STUDIO, state: 'unavailable' }];

    await user.selectOptions(select, 'Studio');

    expect(
      await screen.findByText('This Suno workspace is unavailable: choose an available one.'),
    ).toBeVisible();
    expect(server.song.sunoWorkspace).toBeNull();
    await waitFor(() => {
      expect(options(select)).toEqual(['None', 'Demos']);
    });
  });

  it('says when no workspace is known yet', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    server.workspaces = [];
    await openWorkspace(user);
    expect(screen.getByTestId('no-workspaces')).toHaveTextContent(
      'No Suno workspaces are known yet',
    );
  });

  it('offers to try again when the list fails to load', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    server.workspaces = undefined;
    renderApp('/songs/n8-7');
    await screen.findByRole('heading', { level: 2, name: 'Running in a Pack' });
    await user.click(screen.getByRole('button', { name: 'Details' }));

    expect(await screen.findByText('The workspace list could not be loaded.')).toBeVisible();
    expect(screen.getByRole('combobox', { name: 'Workspace' })).toBeDisabled();
    server.workspaces = WORKSPACES;
    await user.click(
      within(screen.getByTestId('song-workspace')).getByRole('button', { name: 'Try again' }),
    );
    await waitFor(() => {
      expect(screen.getByRole('combobox', { name: 'Workspace' })).toBeEnabled();
    });
  });
});

describe('workspace choices', () => {
  it('are the Available workspaces, plus the Song’s own even when unavailable or unlisted', () => {
    expect(workspaceChoices(WORKSPACES, null).map((choice) => choice.id)).toEqual([
      DEMOS.id,
      STUDIO.id,
    ]);
    expect(
      workspaceChoices(WORKSPACES, { id: ARCHIVE.id, name: 'Archive', state: 'unavailable' }).map(
        (choice) => choice.id,
      ),
    ).toEqual([ARCHIVE.id, DEMOS.id, STUDIO.id]);
    const gone = { id: 'gone', name: 'Gone', state: 'unavailable' as const };
    expect(workspaceChoices(WORKSPACES, gone)[0]).toEqual(gone);
  });

  it('name a blank workspace (unnamed)', () => {
    expect(workspaceName(testWorkspace('w', '  '))).toBe('(unnamed)');
    expect(workspaceName(testWorkspace('w', 'Named'))).toBe('Named');
  });
});
