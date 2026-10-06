import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { SongRelationship } from '../api/songs';
import { renderApp } from '../test/helpers';
import { baseSong, SEQUEL, SIBLING, SONG_B, SONG_C, songServer } from '../test/songServer';

async function openRelated(user: ReturnType<typeof userEvent.setup>) {
  renderApp('/songs/n8-7');
  await screen.findByRole('heading', { level: 2, name: 'Running in a Pack' });
  await user.click(screen.getByRole('button', { name: 'Details' }));
  const group = await screen.findByRole('group', { name: 'Related' });
  // The type picker is enabled once the types have loaded.
  await waitFor(() => {
    expect(within(group).getByRole('combobox', { name: /Relationship/ })).toBeEnabled();
  });
  return group;
}

/** "Name: title" for each relationship shown, in order. */
function shown(): string[] {
  return [...document.querySelectorAll('[data-relationship-name]')].map(
    (item) =>
      `${item.getAttribute('data-relationship-name') ?? ''}: ${item.getAttribute('data-song-title') ?? ''}`,
  );
}

const relation = (
  id: string,
  type: typeof SEQUEL,
  direction: 'forward' | 'reverse',
  other: typeof SONG_B,
): SongRelationship => ({
  id,
  typeId: type.id,
  name: direction === 'forward' ? type.name : type.reverseName,
  direction,
  song: { id: other.id, shortcode: other.shortcode, title: other.title },
});

describe('the Related section of the Details panel', () => {
  it('relates the Song to another by choosing the type in a direction, then the Song', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    const group = await openRelated(user);
    expect(within(group).getByText('Not related to any Song.')).toBeVisible();
    const search = within(group).getByRole('textbox', { name: /Related Song/ });
    expect(search).toBeDisabled();

    // Both directions of a type are offered by name; a symmetric type once.
    const picker = within(group).getByRole('combobox', { name: /Relationship/ });
    const options = within(picker)
      .getAllByRole('option')
      .map((option) => option.textContent);
    expect(options).toContain('Sequel to');
    expect(options).toContain('Has sequel');
    expect(options.filter((option) => option === 'Sibling of')).toHaveLength(1);
    expect(options).toContain('Covered by');

    await user.selectOptions(picker, `${SEQUEL.id}:forward`);
    expect(search).toBeEnabled();
    await user.type(search, 'song');
    // The Song itself is never offered.
    const results = await screen.findByRole('listbox', { name: 'Related Song results' });
    await within(results).findByRole('option', { name: /Song B/ });
    expect(within(results).queryByRole('option', { name: /Running in a Pack/ })).toBeNull();
    await user.click(within(results).getByRole('option', { name: /Song B/ }));

    await waitFor(() => {
      expect(shown()).toEqual(['Sequel to: Song B']);
    });
    expect(server.relationships).toEqual([
      {
        method: 'POST',
        path: `/api/v1/songs/${baseSong.id}/relationships`,
        body: { typeId: SEQUEL.id, direction: 'forward', otherSong: SONG_B.id },
      },
    ]);
    expect(within(group).getByRole('link', { name: 'Song B' })).toHaveAttribute(
      'href',
      '/songs/n8-8',
    );
    // No edit of the Song was sent: a relationship never moves its revision.
    expect(server.edits).toEqual([]);
  });

  it('shows a Song already related under the chosen type as disabled, and only under that type', async () => {
    const user = userEvent.setup();
    songServer({
      ...baseSong,
      relationships: [relation('r1', SEQUEL, 'reverse', SONG_B)],
    });
    const group = await openRelated(user);
    expect(shown()).toEqual(['Has sequel: Song B']);

    const picker = within(group).getByRole('combobox', { name: /Relationship/ });
    await user.selectOptions(picker, `${SEQUEL.id}:forward`);
    await user.type(within(group).getByRole('textbox', { name: /Related Song/ }), 'song');
    const results = await screen.findByRole('listbox', { name: 'Related Song results' });
    expect(await within(results).findByRole('option', { name: /Song B/ })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
    expect(within(results).getByRole('option', { name: /Song C/ })).not.toHaveAttribute(
      'aria-disabled',
      'true',
    );

    await user.selectOptions(picker, `${SIBLING.id}:forward`);
    await user.click(within(group).getByRole('textbox', { name: /Related Song/ }));
    const reopened = await screen.findByRole('listbox', { name: 'Related Song results' });
    expect(await within(reopened).findByRole('option', { name: /Song B/ })).not.toHaveAttribute(
      'aria-disabled',
      'true',
    );
  });

  it('groups the relationships by name as seen from this Song', async () => {
    const user = userEvent.setup();
    songServer({
      ...baseSong,
      relationships: [
        relation('r1', SEQUEL, 'reverse', SONG_B),
        relation('r2', SEQUEL, 'reverse', SONG_C),
        relation('r3', SIBLING, 'forward', SONG_C),
      ],
    });
    const group = await openRelated(user);

    expect(shown()).toEqual(['Has sequel: Song B', 'Has sequel: Song C', 'Sibling of: Song C']);
    const hasSequel = within(group).getByRole('list', { name: 'Has sequel:' });
    expect(
      within(hasSequel)
        .getAllByRole('link')
        .map((link) => link.textContent),
    ).toEqual(['Song B', 'Song C']);
  });

  it('removes a relationship after a confirmation', async () => {
    const user = userEvent.setup();
    const { server } = songServer({
      ...baseSong,
      relationships: [relation('r1', SEQUEL, 'forward', SONG_B)],
    });
    const group = await openRelated(user);

    await user.click(within(group).getByRole('button', { name: 'Remove Sequel to: Song B' }));
    const dialog = await screen.findByRole('dialog', { name: 'Remove relationship' });
    await waitFor(() => {
      expect(within(dialog).getByTestId('remove-relationship-summary')).toBeVisible();
    });
    expect(within(dialog).getByTestId('remove-relationship-summary')).toHaveTextContent(
      'Remove Sequel to: Song B? It is removed from Song B as well.',
    );

    // Cancelling keeps it.
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(server.relationships).toEqual([]);

    await user.click(within(group).getByRole('button', { name: 'Remove Sequel to: Song B' }));
    const again = await screen.findByRole('dialog', { name: 'Remove relationship' });
    await user.click(within(again).getByRole('button', { name: 'Remove' }));

    await waitFor(() => {
      expect(shown()).toEqual([]);
    });
    expect(server.relationships).toEqual([
      {
        method: 'DELETE',
        path: `/api/v1/songs/${baseSong.id}/relationships/r1`,
        body: undefined,
      },
    ]);
    expect(within(group).getByText('Not related to any Song.')).toBeVisible();
  });
});
