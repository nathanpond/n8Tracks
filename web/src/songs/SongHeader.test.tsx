import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { jsonResponse, renderApp } from '../test/helpers';
import { baseSong, FINAL, SHELVED, songServer, WRITING } from '../test/songServer';

/** The state menu's button, once the states have loaded and it can be opened. */
async function stateButton(state: string) {
  const button = await screen.findByRole('button', { name: `State: ${state}` });
  await waitFor(() => {
    expect(button).toBeEnabled();
  });
  return button;
}

async function openSong() {
  renderApp('/songs/n8-7');
  return screen.findByRole('heading', { level: 2, name: 'Running in a Pack' });
}

describe('the Song header', () => {
  it('saves a title on Enter, trimmed, with the revision it was based on', async () => {
    const { server } = songServer();
    const user = userEvent.setup();
    await openSong();

    await user.click(screen.getByRole('button', { name: 'Edit title' }));
    const input = screen.getByRole('textbox', { name: 'Title' });
    expect(input).toHaveFocus();
    await user.clear(input);
    await user.type(input, '  Running Alone {Enter}');

    expect(await screen.findByRole('heading', { level: 2, name: 'Running Alone' })).toBeVisible();
    expect(server.edits).toEqual([{ ifMatch: '"1"', body: { title: 'Running Alone' } }]);

    // The next save is based on the revision the first one returned.
    await user.click(screen.getByRole('button', { name: 'Edit title' }));
    await user.type(screen.getByRole('textbox', { name: 'Title' }), ' Again');
    await user.tab();
    expect(
      await screen.findByRole('heading', { level: 2, name: 'Running Alone Again' }),
    ).toBeVisible();
    expect(server.edits[1]).toEqual({ ifMatch: '"2"', body: { title: 'Running Alone Again' } });
  });

  it('keeps an empty title open with an error and saves nothing', async () => {
    const { server } = songServer();
    const user = userEvent.setup();
    await openSong();

    await user.click(screen.getByRole('button', { name: 'Edit title' }));
    const input = screen.getByRole('textbox', { name: 'Title' });
    await user.clear(input);
    await user.type(input, '   {Enter}');

    expect(await screen.findByText('Enter a title.')).toBeVisible();
    expect(input).toHaveAttribute('aria-invalid', 'true');
    await user.tab();
    expect(screen.getByRole('textbox', { name: 'Title' })).toBeVisible();
    expect(server.edits).toEqual([]);
  });

  it('cancels on Escape and sends nothing for an edit that changes nothing', async () => {
    const { server } = songServer();
    const user = userEvent.setup();
    await openSong();

    await user.click(screen.getByRole('button', { name: 'Edit title' }));
    await user.type(screen.getByRole('textbox', { name: 'Title' }), ' changed{Escape}');
    expect(screen.getByRole('heading', { level: 2, name: 'Running in a Pack' })).toBeVisible();

    await user.click(screen.getByRole('button', { name: 'Edit title' }));
    await user.type(screen.getByRole('textbox', { name: 'Title' }), '  {Enter}');
    expect(
      await screen.findByRole('heading', { level: 2, name: 'Running in a Pack' }),
    ).toBeVisible();

    await user.click(screen.getByRole('button', { name: 'Edit concept' }));
    await user.keyboard('{Escape}');
    expect(screen.getByText('Fast and loud.')).toBeVisible();
    expect(server.edits).toEqual([]);
  });

  it('saves the concept on Ctrl+Enter or blur, keeps Enter as a new line, and clears it', async () => {
    const { server } = songServer();
    const user = userEvent.setup();
    await openSong();

    await user.click(screen.getByRole('button', { name: 'Edit concept' }));
    const area = screen.getByRole('textbox', { name: 'Concept' });
    expect(area).toHaveFocus();
    await user.type(area, '{Enter}With a quiet bridge.  {Control>}{Enter}{/Control}');

    await waitFor(() => {
      expect(screen.queryByRole('textbox', { name: 'Concept' })).not.toBeInTheDocument();
    });
    expect(server.edits).toEqual([
      { ifMatch: '"1"', body: { concept: 'Fast and loud.\nWith a quiet bridge.' } },
    ]);

    await user.click(screen.getByRole('button', { name: 'Edit concept' }));
    await user.clear(screen.getByRole('textbox', { name: 'Concept' }));
    await user.tab();
    expect(await screen.findByText('No concept yet.')).toBeVisible();
    expect(server.edits[1]).toEqual({ ifMatch: '"2"', body: { concept: null } });
  });

  it('keeps the field open with the typed text and an error when a save fails', async () => {
    const { server } = songServer();
    const user = userEvent.setup();
    await openSong();

    server.next = () => jsonResponse(500, { code: 'internal_error' });
    await user.click(screen.getByRole('button', { name: 'Edit title' }));
    const input = screen.getByRole('textbox', { name: 'Title' });
    await user.type(input, ' Live{Enter}');

    expect(await screen.findByText(/^Not saved: n8Tracks did not answer/)).toBeVisible();
    expect(screen.getByRole('textbox', { name: 'Title' })).toHaveValue('Running in a Pack Live');

    // The API's own field error is shown the same way; then a retry saves.
    server.next = () =>
      jsonResponse(422, { code: 'validation_failed', errors: { title: ['Use at most 300.'] } });
    await user.type(screen.getByRole('textbox', { name: 'Title' }), '{Enter}');
    expect(await screen.findByText('Use at most 300.')).toBeVisible();
    await user.type(screen.getByRole('textbox', { name: 'Title' }), '{Enter}');
    expect(
      await screen.findByRole('heading', { level: 2, name: 'Running in a Pack Live' }),
    ).toBeVisible();
  });

  it('moves the Song to any visible state from the menu', async () => {
    const { server } = songServer();
    const user = userEvent.setup();
    await openSong();

    await user.click(await stateButton('Idea'));
    const items = await screen.findAllByRole('menuitem');
    expect(items.map((item) => item.textContent)).toEqual(['Idea (current)✓', 'Writing', 'Final']);
    await user.click(await screen.findByRole('menuitem', { name: 'Final' }));

    expect(await screen.findByRole('button', { name: 'State: Final' })).toBeVisible();
    expect(server.edits).toEqual([{ ifMatch: '"1"', body: { stateId: FINAL.id } }]);

    // Backwards too.
    await user.click(await stateButton('Final'));
    await user.click(await screen.findByRole('menuitem', { name: 'Writing' }));
    expect(await screen.findByRole('button', { name: 'State: Writing' })).toBeVisible();
    expect(server.edits[1]).toEqual({ ifMatch: '"2"', body: { stateId: WRITING.id } });
  });

  it('offers a hidden current state alongside the visible ones until the Song leaves it', async () => {
    songServer({
      ...baseSong,
      state: { id: SHELVED.id, name: SHELVED.name, colour: SHELVED.colour },
    });
    const user = userEvent.setup();
    await openSong();

    await user.click(await stateButton('Shelved'));
    const items = await screen.findAllByRole('menuitem');
    expect(items.map((item) => item.textContent)).toEqual([
      'Idea',
      'Writing',
      'Final',
      'Shelved (current) (hidden)✓',
    ]);
    await user.click(await screen.findByRole('menuitem', { name: 'Writing' }));
    await user.click(await stateButton('Writing'));

    const after = await screen.findAllByRole('menuitem');
    expect(after.map((item) => item.textContent)).toEqual(['Idea', 'Writing (current)✓', 'Final']);
  });

  it('queues saves so a second one is based on the first one’s revision', async () => {
    const { server } = songServer();
    const user = userEvent.setup();
    let release: () => void = () => undefined;
    server.next = async () => {
      await new Promise<void>((resolve) => {
        release = resolve;
      });
      server.song = { ...server.song, title: 'Slow title', revision: 2 };
      return jsonResponse(200, server.song);
    };
    await openSong();

    await user.click(screen.getByRole('button', { name: 'Edit title' }));
    await user.type(screen.getByRole('textbox', { name: 'Title' }), ' x{Enter}');
    await user.click(screen.getByRole('button', { name: 'Edit concept' }));
    await user.type(screen.getByRole('textbox', { name: 'Concept' }), ' More.');
    await user.tab();
    expect(server.edits).toHaveLength(1);

    release();
    expect(await screen.findByText('Fast and loud. More.', { selector: 'p' })).toBeVisible();
    expect(server.edits.map((edit) => edit.ifMatch)).toEqual(['"1"', '"2"']);
    expect(screen.getByRole('heading', { level: 2, name: 'Slow title' })).toBeVisible();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});
