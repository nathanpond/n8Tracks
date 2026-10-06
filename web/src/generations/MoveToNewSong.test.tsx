import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../test/helpers';
import { STATES } from '../test/songServer';
import { testGeneration, testVersion, versionServer } from '../test/versionServer';

const ONE = testVersion('1', { isFrozen: true, current: true });
const G2 = 'n8-7-v1-g2';

function songWithTwoGenerations(selected = false) {
  const { server } = versionServer([ONE]);
  server.generations = [testGeneration('1', 1), testGeneration('1', 2, { isSelected: selected })];
  if (selected) {
    const chosen = server.generations[1];
    server.song = {
      ...server.song,
      hasSelectedGeneration: true,
      selectedGeneration:
        chosen === undefined
          ? null
          : { id: chosen.id, shortcode: chosen.shortcode, state: 'active', remoteState: 'present' },
    };
  }
  return server;
}

/** The Song page with a Generation's panel open, then "Create new Song from Generation" chosen. */
async function openMove(user: ReturnType<typeof userEvent.setup>, shortcode: string) {
  const rendered = renderApp(`/songs/n8-7/generations/${shortcode}`);
  const panel = await screen.findByRole('dialog', { name: `Generation ${shortcode}` });
  await waitFor(() => {
    expect(panel).toBeVisible();
  });
  await user.click(within(panel).getByRole('button', { name: 'Create new Song from Generation' }));
  const dialog = await screen.findByRole('dialog', { name: 'Create new Song from Generation' });
  await waitFor(() => {
    expect(dialog).toBeVisible();
  });
  return { ...rendered, dialog };
}

describe('Create new Song from Generation', () => {
  it('warns first that the Generation is moved, not copied, and names what moves with it', async () => {
    const user = userEvent.setup();
    const server = songWithTwoGenerations();
    const { dialog } = await openMove(user, G2);

    expect(within(dialog).getByText(`${G2} will be moved, not copied`)).toBeVisible();
    expect(within(dialog).getByTestId('move-warning')).toHaveTextContent(`It leaves n8-7`);
    const moving = within(within(dialog).getByTestId('move-what-moves')).getAllByRole('listitem');
    expect(moving.map((item) => item.textContent)).toEqual([
      'its rating',
      'its comments',
      'its image',
      'its Suno data',
    ]);
    // Pre-filled with the Generation's Suno title; no selection choice while it is not selected.
    expect(within(dialog).getByRole('textbox', { name: 'New Song title' })).toHaveValue('Take 2');
    expect(within(dialog).queryByRole('radiogroup')).toBeNull();

    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Create new Song from Generation' })).toBeNull();
    });
    expect(server.moves).toEqual([]);
  });

  it('moves it on confirming and opens it in the new Song, saying where it came from', async () => {
    const user = userEvent.setup();
    const server = songWithTwoGenerations();
    const { dialog, router } = await openMove(user, G2);
    const title = within(dialog).getByRole('textbox', { name: 'New Song title' });
    await user.clear(title);
    await user.type(title, 'Split out');

    await user.click(within(dialog).getByRole('button', { name: 'Move to a new Song' }));

    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/songs/n8-8/generations/n8-8-v1-g1');
    });
    expect(router.state.location.state).toEqual({ movedFrom: G2 });
    expect(server.moves).toEqual([
      { reference: testGeneration('1', 2).id, ifMatch: '"1"', body: { title: 'Split out' } },
    ]);
  });

  it('refuses a blank title without asking n8Tracks, and shows a title n8Tracks refuses', async () => {
    const user = userEvent.setup();
    const server = songWithTwoGenerations();
    const { dialog } = await openMove(user, G2);
    const title = within(dialog).getByRole('textbox', { name: 'New Song title' });
    await user.clear(title);

    await user.click(within(dialog).getByRole('button', { name: 'Move to a new Song' }));

    expect(await within(dialog).findByText('Enter a title.')).toBeVisible();
    expect(server.moves).toEqual([]);
  });

  it('asks what the Song selects instead when the Generation is its Selected Generation', async () => {
    const user = userEvent.setup();
    const server = songWithTwoGenerations(true);
    const { dialog } = await openMove(user, G2);
    const choice = within(dialog).getByRole('radiogroup', {
      name: `${G2} is the Song’s Selected Generation. What should n8-7 select instead?`,
    });
    expect(within(choice).getByRole('radio', { name: 'Another Generation' })).toBeChecked();
    expect(
      within(choice).getByRole('combobox', { name: 'Generation to select instead' }),
    ).toHaveValue(testGeneration('1', 1).id);

    // A workflow state instead: the Song is left with none selected, in the state chosen.
    await user.click(
      within(choice).getByRole('radio', { name: 'No Selected Generation, and a workflow state' }),
    );
    const state = STATES[2];
    if (state === undefined) {
      throw new Error('The fixture has no third state.');
    }
    await user.selectOptions(
      within(choice).getByRole('combobox', { name: 'Workflow state for the Song' }),
      state.id,
    );
    await user.click(within(dialog).getByRole('button', { name: 'Move to a new Song' }));

    await waitFor(() => {
      expect(server.moves).toHaveLength(1);
    });
    expect(server.moves[0]?.body).toEqual({ title: 'Take 2', workflowState: state.id });
  });

  it('sends the replacement Generation chosen', async () => {
    const user = userEvent.setup();
    const server = songWithTwoGenerations(true);
    const { dialog, router } = await openMove(user, G2);

    await user.click(within(dialog).getByRole('button', { name: 'Move to a new Song' }));

    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/songs/n8-8/generations/n8-8-v1-g1');
    });
    expect(server.moves[0]?.body).toEqual({
      title: 'Take 2',
      replacementGeneration: testGeneration('1', 1).id,
    });
  });

  it('says when the Generation changed elsewhere, and moves nothing', async () => {
    const user = userEvent.setup();
    const server = songWithTwoGenerations();
    const { dialog } = await openMove(user, G2);
    server.rateElsewhere(testGeneration('1', 2).id, 3);

    await user.click(within(dialog).getByRole('button', { name: 'Move to a new Song' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      `${G2} was changed somewhere else at the same time, so it was not moved.`,
    );
    expect(server.generations).toHaveLength(2);
  });
});

describe('a moved Generation’s old shortcode', () => {
  it('opens the Generation where it is now from its old address, saying it moved', async () => {
    const { server } = versionServer([ONE, testVersion('2', { isFrozen: true })]);
    server.generations = [testGeneration('1', 1), testGeneration('2', 1)];
    server.moved.set('n8-7-v1-g9', { song: 'n8-7', shortcode: 'n8-7-v2-g1' });
    const { router } = renderApp('/songs/n8-7/generations/n8-7-v1-g9');

    const panel = await screen.findByRole('dialog', { name: 'Generation n8-7-v2-g1' });
    expect(router.state.location.pathname).toBe('/songs/n8-7/generations/n8-7-v2-g1');
    expect(within(panel).getByTestId('generation-moved')).toHaveTextContent(
      'n8-7-v1-g9 moved: this Generation is now n8-7-v2-g1.',
    );
  });

  it('opens it from the Go to box, in any letter case, saying it moved', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE, testVersion('2', { isFrozen: true })]);
    server.generations = [testGeneration('1', 1), testGeneration('2', 1)];
    server.moved.set('n8-7-v1-g9', { song: 'n8-7', shortcode: 'n8-7-v2-g1' });
    renderApp('/songs/n8-7');
    await screen.findByRole('heading', { level: 2 });

    await user.type(screen.getByRole('textbox', { name: 'Go to' }), 'N8-7-V1-G9{Enter}');

    const panel = await screen.findByRole('dialog', { name: 'Generation n8-7-v2-g1' });
    expect(within(panel).getByTestId('generation-moved')).toHaveTextContent(
      'n8-7-v1-g9 moved: this Generation is now n8-7-v2-g1.',
    );
  });
});
