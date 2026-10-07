import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { jsonResponse, renderApp } from '../test/helpers';
import { baseSong } from '../test/songServer';
import { testGeneration, testVersion, versionServer } from '../test/versionServer';

const ONE = testVersion('1', { isFrozen: true });
const TWO = testVersion('2', { current: true, isFrozen: true });
const V1G1 = 'n8-7-v1-g1';
const V2G1 = 'n8-7-v2-g1';

/** The Song page with a Generation's panel open. */
async function openPanel(shortcode: string) {
  const rendered = renderApp(`/songs/n8-7/generations/${shortcode}`);
  const panel = await screen.findByRole('dialog', { name: `Generation ${shortcode}` });
  await waitFor(() => {
    expect(panel).toBeVisible();
  });
  return { ...rendered, panel };
}

function header(): HTMLElement {
  return screen.getByTestId('song-selected-generation');
}

function generationRow(shortcode: string): HTMLElement {
  const row = document.querySelector(`tr[data-generation="${shortcode}"]`);
  if (!(row instanceof HTMLElement)) {
    throw new Error(`Generation ${shortcode} is not listed.`);
  }
  return row;
}

/** Opens a Generation row's actions menu and chooses `item`. */
async function chooseFromRow(
  user: ReturnType<typeof userEvent.setup>,
  shortcode: string,
  item: string,
) {
  await user.click(
    within(generationRow(shortcode)).getByRole('button', { name: `Actions for ${shortcode}` }),
  );
  await user.click(await screen.findByRole('menuitem', { name: item }));
}

function twoVersionSong() {
  const { server } = versionServer([ONE, TWO]);
  server.generations = [testGeneration('1', 1), testGeneration('1', 2), testGeneration('2', 1)];
  return server;
}

describe('the Song’s Selected Generation', () => {
  it('is chosen in the panel: the header names it with a link, and the row and panel mark it', async () => {
    const user = userEvent.setup();
    const server = twoVersionSong();
    const { panel } = await openPanel(V1G1);
    expect(header()).toHaveTextContent('Selected Generation:None');
    expect(within(panel).getByTestId('generation-panel-selection')).toHaveTextContent(
      'Not selected.',
    );

    await user.click(within(panel).getByRole('button', { name: 'Select for the Song' }));

    await waitFor(() => {
      expect(header()).toHaveTextContent(`Selected Generation:${V1G1}`);
    });
    expect(
      within(header()).getByRole('link', { name: `Selected Generation ${V1G1}` }),
    ).toHaveAttribute('href', `/songs/n8-7/generations/${V1G1}`);
    expect(within(panel).getByTestId('generation-panel-selection')).toHaveTextContent(
      'This is the Song’s chosen output.',
    );
    expect(within(panel).getByRole('button', { name: 'Clear the selection' })).toBeVisible();
    expect(within(generationRow(V1G1)).getByTestId('selected-generation')).toBeVisible();
    expect(server.selectionWrites).toEqual([
      { method: 'PUT', ifMatch: '"1"', body: { generation: testGeneration('1', 1).id } },
    ]);
    expect(server.song.revision).toBe(2);
  });

  it('is replaced by one of another Version from its row, and cleared there, changing nothing else', async () => {
    const user = userEvent.setup();
    const server = twoVersionSong();
    renderApp('/songs/n8-7');
    await user.click(await screen.findByRole('button', { name: 'Generations of Version 1' }));
    await user.click(screen.getByRole('button', { name: 'Generations of Version 2' }));

    await chooseFromRow(user, V1G1, 'Select for the Song');
    await waitFor(() => {
      expect(header()).toHaveTextContent(V1G1);
    });
    await chooseFromRow(user, V2G1, 'Select for the Song');
    await waitFor(() => {
      expect(header()).toHaveTextContent(V2G1);
    });
    expect(within(generationRow(V1G1)).queryByTestId('selected-generation')).toBeNull();
    expect(within(generationRow(V2G1)).getByTestId('selected-generation')).toBeVisible();

    await chooseFromRow(user, V2G1, 'Clear the selection');
    await waitFor(() => {
      expect(header()).toHaveTextContent('Selected Generation:None');
    });
    expect(document.querySelectorAll('[data-testid="selected-generation"]')).toHaveLength(0);
    expect(server.selectionWrites.map((write) => write.method)).toEqual(['PUT', 'PUT', 'DELETE']);
    // No Generation was written: their states and revisions are as they were.
    expect(server.generationWrites).toEqual([]);
    expect(server.generations.map((generation) => generation.revision)).toEqual([1, 1, 1]);
  });

  it('is sent again once based on the Song as it is now when the Song changed elsewhere', async () => {
    const user = userEvent.setup();
    const server = twoVersionSong();
    const { panel } = await openPanel(V2G1);
    server.touchSongElsewhere();

    await user.click(within(panel).getByRole('button', { name: 'Select for the Song' }));

    await waitFor(() => {
      expect(header()).toHaveTextContent(V2G1);
    });
    expect(server.selectionWrites.map((write) => write.ifMatch)).toEqual(['"1"', '"2"']);
  });

  it('is not cleared over a selection made elsewhere: the conflict dialog shows it, and Reload keeps it', async () => {
    const user = userEvent.setup();
    const server = twoVersionSong();
    const { panel } = await openPanel(V1G1);
    await user.click(within(panel).getByRole('button', { name: 'Select for the Song' }));
    await waitFor(() => {
      expect(header()).toHaveTextContent(V1G1);
    });
    server.selectElsewhere(testGeneration('2', 1).id);

    await user.click(within(panel).getByRole('button', { name: 'Clear the selection' }));

    const conflict = await screen.findByRole('dialog', { name: 'Changed since you loaded it' });
    const row = within(conflict).getByRole('row', { name: /Selected Generation/ });
    expect(row).toHaveTextContent('Changed elsewhere and by you');
    expect(row).toHaveTextContent(`None${V2G1}`);
    // Not sent again: the other client's selection stands, and the page shows it.
    expect(server.selectionWrites.map((write) => write.method)).toEqual(['PUT', 'DELETE']);
    expect(header()).toHaveTextContent(`Selected Generation:${V2G1}`);

    await user.click(within(conflict).getByRole('button', { name: 'Reload' }));

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Changed since you loaded it' })).toBeNull();
    });
    expect(server.selectionWrites).toHaveLength(2);
    expect(server.song.selectedGeneration?.shortcode).toBe(V2G1);
    expect(header()).toHaveTextContent(`Selected Generation:${V2G1}`);
    expect(within(generationRow(V1G1)).queryByTestId('selected-generation')).toBeNull();
  });

  it('replaces a selection made elsewhere only when the user reapplies theirs in the conflict dialog', async () => {
    const user = userEvent.setup();
    const server = twoVersionSong();
    const { panel } = await openPanel(V1G1);
    server.selectElsewhere(testGeneration('2', 1).id);

    await user.click(within(panel).getByRole('button', { name: 'Select for the Song' }));

    const conflict = await screen.findByRole('dialog', { name: 'Changed since you loaded it' });
    expect(within(conflict).getByRole('row', { name: /Selected Generation/ })).toHaveTextContent(
      `${V1G1}${V2G1}`,
    );
    expect(server.selectionWrites).toHaveLength(1);

    await user.click(
      within(conflict).getByRole('button', {
        name: 'Reapply my change, replacing the current selected generation',
      }),
    );

    await waitFor(() => {
      expect(header()).toHaveTextContent(`Selected Generation:${V1G1}`);
    });
    expect(server.selectionWrites.map((write) => write.ifMatch)).toEqual(['"1"', '"2"']);
    expect(server.song.selectedGeneration?.shortcode).toBe(V1G1);
  });

  it('says so when n8Tracks does not take the choice, and keeps the Song as it was', async () => {
    const user = userEvent.setup();
    const server = twoVersionSong();
    const { panel } = await openPanel(V1G1);
    server.nextSelectionWrite = () => jsonResponse(500, { code: 'internal_error' });

    await user.click(within(panel).getByRole('button', { name: 'Select for the Song' }));

    expect(await within(panel).findByRole('alert')).toHaveTextContent(
      `${V1G1} was not selected: n8Tracks did not answer as expected.`,
    );
    expect(header()).toHaveTextContent('Selected Generation:None');
  });

  it('shows the selected Generation’s Archived, In Suno Trash, and Remote Missing states in the header', async () => {
    versionServer([ONE], {
      ...baseSong,
      hasSelectedGeneration: true,
      selectedGeneration: {
        id: testGeneration('1', 1).id,
        shortcode: V1G1,
        state: 'archived',
        remoteState: 'trashed',
      },
    });
    renderApp('/songs/n8-7');

    const shown = await screen.findByTestId('song-selected-generation');
    expect(within(shown).getByText('Archived')).toBeVisible();
    expect(within(shown).getByText('In Suno Trash')).toBeVisible();
    expect(within(shown).queryByText('Remote Missing')).toBeNull();
  });
});

describe('archiving a Generation', () => {
  it('archives the selected Generation from its panel: it stays selected and shows Archived', async () => {
    const user = userEvent.setup();
    const server = twoVersionSong();
    const { panel } = await openPanel(V1G1);
    await user.click(within(panel).getByRole('button', { name: 'Select for the Song' }));
    await waitFor(() => {
      expect(header()).toHaveTextContent(V1G1);
    });

    await user.click(within(panel).getByRole('button', { name: 'Archive' }));

    await waitFor(() => {
      expect(within(header()).getByText('Archived')).toBeVisible();
    });
    expect(within(panel).getByTestId('generation-state')).toHaveTextContent('Archived');
    expect(within(panel).getByTestId('generation-state')).toHaveTextContent('Selected');
    expect(within(panel).getByRole('button', { name: 'Reactivate' })).toBeVisible();
    expect(server.generationWrites.at(-1)).toMatchObject({
      method: 'PATCH',
      ifMatch: '"1"',
      body: { state: 'archived' },
    });
    // Selected, it stays listed while archived Generations are hidden.
    expect(generationRow(V1G1)).toBeVisible();
    // Its Version was not touched.
    expect(server.versions.find((version) => version.number === '1')?.archived).toBe(false);
  });

  it('reactivates an archived Generation from its row, sending its revision as it is now', async () => {
    const user = userEvent.setup();
    const server = twoVersionSong();
    server.generations = [testGeneration('1', 1, { state: 'archived', revision: 3 })];
    renderApp('/songs/n8-7?archivedGenerations=1');
    await user.click(await screen.findByRole('button', { name: 'Generations of Version 1' }));

    await chooseFromRow(user, V1G1, 'Reactivate');

    await waitFor(() => {
      expect(within(generationRow(V1G1)).getByTestId('generation-state')).toHaveTextContent(
        'Active',
      );
    });
    expect(server.generationWrites).toEqual([
      expect.objectContaining({ method: 'PATCH', ifMatch: '"3"', body: { state: 'active' } }),
    ]);
  });
});
