import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../test/helpers';
import { songServer, WRITING } from '../test/songServer';

/** Opens the Song, then edits its concept to `concept` without saving yet. */
async function editConcept(concept: string) {
  const user = userEvent.setup();
  renderApp('/songs/n8-7');
  await screen.findByRole('heading', { level: 2, name: 'Running in a Pack' });
  await user.click(screen.getByRole('button', { name: 'Edit concept' }));
  const area = screen.getByRole('textbox', { name: 'Concept' });
  await user.clear(area);
  await user.type(area, concept);
  return user;
}

function differences() {
  return within(screen.getByRole('table', { name: 'Differences' }));
}

/** Each row of the differences table: field, note, yours, current. */
function rows() {
  return differences()
    .getAllByRole('row')
    .slice(1)
    .map((row) =>
      Array.from(row.querySelectorAll('th, td')).map((cell) => cell.textContent.trim()),
    );
}

describe('the conflict dialog', () => {
  it('shows the fields changed elsewhere alongside the user’s own change', async () => {
    const { server } = songServer();
    const user = await editConcept('My concept');
    server.changeElsewhere({
      title: 'Changed in tab A',
      state: { id: WRITING.id, name: WRITING.name, colour: WRITING.colour },
    });

    await user.keyboard('{Control>}{Enter}{/Control}');

    const dialog = await screen.findByRole('dialog', { name: 'Changed since you loaded it' });
    expect(rows()).toEqual([
      ['TitleChanged elsewhere', 'Running in a Pack', 'Changed in tab A'],
      ['ConceptYour change', 'My concept', 'Fast and loud.'],
      ['StateChanged elsewhere', 'Idea', 'Writing'],
    ]);
    expect(within(dialog).getByRole('button', { name: 'Reapply my change' })).toBeInTheDocument();
    // Nothing typed is lost while the user decides.
    expect(screen.getByRole('textbox', { name: 'Concept', hidden: true })).toHaveValue(
      'My concept',
    );
  });

  it('reload discards the local edit and shows the current Song', async () => {
    const { server } = songServer();
    const user = await editConcept('My concept');
    server.changeElsewhere({ title: 'Changed in tab A' });
    await user.keyboard('{Control>}{Enter}{/Control}');

    await user.click(await screen.findByRole('button', { name: 'Reload' }));

    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    expect(screen.getByRole('heading', { level: 2, name: 'Changed in tab A' })).toBeVisible();
    expect(screen.getByText('Fast and loud.')).toBeVisible();
    expect(screen.queryByRole('textbox', { name: 'Concept' })).not.toBeInTheDocument();
    expect(server.edits).toHaveLength(1);
    expect(server.song.concept).toBe('Fast and loud.');
  });

  it('reapply sends the local edit with the fresh revision', async () => {
    const { server } = songServer();
    const user = await editConcept('My concept');
    server.changeElsewhere({ title: 'Changed in tab A' });
    await user.keyboard('{Control>}{Enter}{/Control}');

    await user.click(await screen.findByRole('button', { name: 'Reapply my change' }));

    expect(await screen.findByText('My concept', { selector: 'p' })).toBeVisible();
    expect(server.edits).toEqual([
      { ifMatch: '"1"', body: { concept: 'My concept' } },
      { ifMatch: '"2"', body: { concept: 'My concept' } },
    ]);
    expect(server.song).toMatchObject({ title: 'Changed in tab A', concept: 'My concept' });
    expect(screen.getByRole('heading', { level: 2, name: 'Changed in tab A' })).toBeVisible();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
  });

  it('appears again when reapplying meets another change', async () => {
    const { server } = songServer();
    const user = await editConcept('My concept');
    server.changeElsewhere({ title: 'Changed in tab A' });
    await user.keyboard('{Control>}{Enter}{/Control}');
    await screen.findByRole('dialog');

    server.changeElsewhere({ title: 'Changed again' });
    await user.click(screen.getByRole('button', { name: 'Reapply my change' }));

    await waitFor(() => {
      expect(rows()[0]).toEqual(['TitleChanged elsewhere', 'Changed in tab A', 'Changed again']);
    });
    await user.click(screen.getByRole('button', { name: 'Reapply my change' }));
    await waitFor(() => {
      expect(server.song.concept).toBe('My concept');
    });
    expect(server.edits.map((edit) => edit.ifMatch)).toEqual(['"1"', '"2"', '"3"']);
  });

  it('labels reapplying as replacing when both sides changed the same field', async () => {
    const { server } = songServer();
    const user = await editConcept('My concept');
    server.changeElsewhere({ concept: 'Their concept' });
    await user.keyboard('{Control>}{Enter}{/Control}');

    await screen.findByRole('dialog');
    expect(rows()).toEqual([
      ['ConceptChanged elsewhere and by you', 'My concept', 'Their concept'],
    ]);
    await user.click(
      screen.getByRole('button', { name: 'Reapply my change, replacing the current concept' }),
    );
    await waitFor(() => {
      expect(server.song.concept).toBe('My concept');
    });
  });

  it('keep editing (and Escape) returns to the field with the typed text and the fresh revision', async () => {
    const { server } = songServer();
    const user = await editConcept('My concept');
    server.changeElsewhere({ title: 'Changed in tab A' });
    await user.keyboard('{Control>}{Enter}{/Control}');

    await screen.findByRole('dialog');
    await user.keyboard('{Escape}');

    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    const area = screen.getByRole('textbox', { name: 'Concept' });
    expect(area).toHaveValue('My concept');
    expect(screen.getByRole('heading', { level: 2, name: 'Changed in tab A' })).toBeVisible();

    // The next save is based on the fresh revision, so it goes through.
    area.focus();
    await user.keyboard('{Control>}{Enter}{/Control}');
    expect(await screen.findByText('My concept', { selector: 'p' })).toBeVisible();
    expect(server.edits[1]).toEqual({ ifMatch: '"2"', body: { concept: 'My concept' } });

    // The close control is "Keep editing" too.
    server.changeElsewhere({ title: 'Changed once more' });
    await user.click(screen.getByRole('button', { name: 'Edit concept' }));
    await user.type(screen.getByRole('textbox', { name: 'Concept' }), '!');
    await user.keyboard('{Control>}{Enter}{/Control}');
    await user.click(await screen.findByRole('button', { name: 'Close' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    expect(screen.getByRole('textbox', { name: 'Concept' })).toHaveValue('My concept!');
  });

  it('retries silently when the revision moved but no compared field differs', async () => {
    const { server } = songServer();
    const user = await editConcept('My concept');
    server.changeElsewhere({});

    await user.keyboard('{Control>}{Enter}{/Control}');

    await waitFor(() => {
      expect(server.song.concept).toBe('My concept');
    });
    expect(server.edits.map((edit) => edit.ifMatch)).toEqual(['"1"', '"2"']);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('does not count a field both sides changed to the same value as a conflict', async () => {
    const { server } = songServer();
    const user = await editConcept('My concept');
    server.changeElsewhere({ concept: 'My concept' });

    await user.keyboard('{Control>}{Enter}{/Control}');

    expect(await screen.findByText('My concept', { selector: 'p' })).toBeVisible();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(server.song.revision).toBe(2);
  });
});
