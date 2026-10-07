import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { jsonResponse, renderApp } from '../test/helpers';
import { FINAL, SHELVED, WRITING } from '../test/songServer';
import { testComment, testGeneration, testVersion, versionServer } from '../test/versionServer';

const ONE = testVersion('1', { isFrozen: true, current: true });
const G1 = 'n8-7-v1-g1';
const G2 = 'n8-7-v1-g2';

/** The Song n8-7 with two Generations of its frozen Version 1, the second selected when `selected`. */
function songWithGenerations(selected: boolean, count = 2) {
  const { server } = versionServer([ONE]);
  server.generations = Array.from({ length: count }, (_, index) =>
    testGeneration('1', index + 1, { isSelected: selected && index === count - 1 }),
  );
  const chosen = server.generations[count - 1];
  if (selected && chosen !== undefined) {
    server.song = {
      ...server.song,
      hasSelectedGeneration: true,
      selectedGeneration: {
        id: chosen.id,
        shortcode: chosen.shortcode,
        state: 'active',
        remoteState: 'present',
      },
    };
  }
  return server;
}

/** The Song page with a Generation's panel open, then "Delete Generation" chosen. */
async function openDelete(user: ReturnType<typeof userEvent.setup>, shortcode: string) {
  const rendered = renderApp(`/songs/n8-7/generations/${shortcode}`);
  const panel = await screen.findByRole('dialog', { name: `Generation ${shortcode}` });
  await waitFor(() => {
    expect(panel).toBeVisible();
  });
  await user.click(within(panel).getByRole('button', { name: 'Delete Generation' }));
  const dialog = await screen.findByRole('dialog', { name: `Delete Generation ${shortcode}?` });
  await waitFor(() => {
    expect(dialog).toBeVisible();
  });
  return { ...rendered, dialog };
}

describe('Delete a Generation', () => {
  it('states what goes by shortcode, who uses it, and that Suno is not changed, then deletes it', async () => {
    const user = userEvent.setup();
    const server = songWithGenerations(false);
    const first = server.generations[0];
    if (first !== undefined) {
      server.generations[0] = { ...first, comments: [testComment(1), testComment(2)] };
      server.sourceVersionCounts.set(first.id, 2);
    }
    const { dialog, router } = await openDelete(user, G1);

    const summary = await within(dialog).findByTestId('delete-generation-summary');
    expect(
      within(summary)
        .getAllByText(/./)
        .map((line) => line.textContent),
    ).toEqual([
      `Generation ${G1} will be deleted from n8Tracks with its rating, its comments (2), and its Suno artwork (none).`,
      '2 Versions use it as a source; they will show it as Deleted.',
      'Nothing in Suno is changed.',
      'It can be restored for 30 days.',
    ]);
    // Not selected: no choice is asked for.
    expect(within(dialog).queryByRole('radiogroup')).toBeNull();

    await user.click(within(dialog).getByRole('button', { name: `Delete ${G1}` }));

    expect(await screen.findByTestId('generation-deleted')).toHaveTextContent(
      `Generation ${G1} deleted. Nothing in Suno was changed; it can be restored for 30 days.`,
    );
    expect(server.generationDeletes).toEqual([
      { reference: testGeneration('1', 1).id.toLowerCase(), ifMatch: '"1"', body: null },
    ]);
    // The panel closes onto the Version, and the table no longer lists it.
    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/songs/n8-7/v/1');
    });
    await waitFor(() => {
      expect(document.querySelector(`tr[data-generation="${G1}"]`)).toBeNull();
    });
    expect(document.querySelector(`tr[data-generation="${G2}"]`)).not.toBeNull();
  });

  it('requires a replacement or a workflow state for the Selected Generation, nothing pre-selected', async () => {
    const user = userEvent.setup();
    const server = songWithGenerations(true);
    const { dialog } = await openDelete(user, G2);

    expect(await within(dialog).findByTestId('delete-generation-selection')).toHaveTextContent(
      `${G2} is the Song’s Selected Generation. Choose what n8-7 selects instead.`,
    );
    const instead = within(dialog).getByRole('radiogroup', { name: 'Instead' });
    for (const radio of within(instead).getAllByRole('radio')) {
      expect(radio).not.toBeChecked();
    }
    const remove = within(dialog).getByRole('button', { name: `Delete ${G2}` });
    expect(remove).toBeDisabled();

    // The workflow state: every visible state, none chosen yet.
    await user.click(
      within(instead).getByRole('radio', { name: 'Clear the selection and set a workflow state' }),
    );
    const states = within(dialog).getByRole('radiogroup', {
      name: 'Workflow state once the selection is cleared',
    });
    await waitFor(() => {
      expect(within(states).getAllByRole('radio')).toHaveLength(3);
    });
    expect(within(states).queryByRole('radio', { name: SHELVED.name })).toBeNull();
    for (const radio of within(states).getAllByRole('radio')) {
      expect(radio).not.toBeChecked();
    }
    expect(remove).toBeDisabled();

    await user.click(within(states).getByRole('radio', { name: FINAL.name }));
    expect(remove).toBeEnabled();
    await user.click(remove);

    expect(await screen.findByTestId('generation-deleted')).toBeVisible();
    expect(server.generationDeletes.at(-1)?.body).toEqual({ workflowState: FINAL.id });
    expect(server.song.state.id).toBe(FINAL.id);
    expect(server.song.selectedGeneration).toBeNull();
    await waitFor(() => {
      expect(screen.getByTestId('song-selected-generation')).not.toHaveTextContent(G2);
    });
  });

  it('with a replacement, selects it and leaves the workflow state as it was', async () => {
    const user = userEvent.setup();
    const server = songWithGenerations(true, 3);
    const state = server.song.state.id;
    const { dialog } = await openDelete(user, 'n8-7-v1-g3');

    await user.click(
      await within(dialog).findByRole('radio', {
        name: 'Select another Generation (the workflow state stays)',
      }),
    );
    const replacements = within(dialog).getByRole('radiogroup', {
      name: 'Generation to select instead',
    });
    expect(
      within(replacements)
        .getAllByRole('radio')
        .map((radio) => radio.getAttribute('value')),
    ).toEqual([testGeneration('1', 1).id, testGeneration('1', 2).id]);
    expect(within(dialog).getByRole('button', { name: 'Delete n8-7-v1-g3' })).toBeDisabled();
    await user.click(within(replacements).getByRole('radio', { name: `${G2} (Take 2)` }));
    await user.click(within(dialog).getByRole('button', { name: 'Delete n8-7-v1-g3' }));

    expect(await screen.findByTestId('generation-deleted')).toBeVisible();
    expect(server.generationDeletes.at(-1)?.body).toEqual({
      replacementGeneration: testGeneration('1', 2).id,
    });
    expect(server.song.selectedGeneration?.shortcode).toBe(G2);
    expect(server.song.state.id).toBe(state);
  });

  it('asks only for a workflow state when the Song has no other Generation', async () => {
    const user = userEvent.setup();
    const server = songWithGenerations(true, 1);
    const { dialog } = await openDelete(user, G1);

    expect(await within(dialog).findByTestId('delete-generation-selection')).toHaveTextContent(
      'n8-7 has no other Generation, so choose the workflow state it moves to once the selection is cleared.',
    );
    expect(within(dialog).queryByRole('radiogroup', { name: 'Instead' })).toBeNull();
    const states = within(dialog).getByRole('radiogroup', {
      name: 'Workflow state once the selection is cleared',
    });
    await user.click(await within(states).findByRole('radio', { name: WRITING.name }));
    await user.click(within(dialog).getByRole('button', { name: `Delete ${G1}` }));

    expect(await screen.findByTestId('generation-deleted')).toBeVisible();
    expect(server.generationDeletes.at(-1)?.body).toEqual({ workflowState: WRITING.id });
  });

  it('reads the impact again after a conflict, and says so; Cancel deletes nothing', async () => {
    const user = userEvent.setup();
    const server = songWithGenerations(false);
    server.nextGenerationDelete = () =>
      jsonResponse(409, { code: 'revision_conflict', current: testGeneration('1', 1) });
    const { dialog } = await openDelete(user, G1);
    await within(dialog).findByTestId('delete-generation-summary');

    await user.click(within(dialog).getByRole('button', { name: `Delete ${G1}` }));

    expect(
      await within(dialog).findByText(
        `${G1} was changed elsewhere since this opened, so it was not deleted. Check what deleting it does now, then choose Delete again.`,
      ),
    ).toBeVisible();
    expect(await within(dialog).findByTestId('delete-generation-summary')).toBeVisible();
    expect(server.generations).toHaveLength(2);

    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: `Delete Generation ${G1}?` })).toBeNull();
    });
    expect(server.generationDeletes).toHaveLength(1);
    expect(screen.queryByTestId('generation-deleted')).toBeNull();
  });
});
