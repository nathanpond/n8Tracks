import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { artistServer, testArtist } from '../test/artistServer';
import { renderApp } from '../test/helpers';
import { baseSong } from '../test/songServer';

const N8 = testArtist('n8', {
  aliases: ['Nate'],
  notes: 'Plays everything.',
  links: [
    { label: 'Site', url: 'https://n8.example' },
    { label: null, url: 'https://other.example' },
  ],
});

async function openArtist(id = N8.id) {
  renderApp(`/artists/${id}`);
  return screen.findByRole('form', { name: 'Artist details' });
}

describe('an Artist page', () => {
  it('shows the name, aliases, notes, and links, and empty Songs and Albums sections', async () => {
    artistServer([N8]);

    const form = await openArtist();

    expect(screen.getByRole('heading', { level: 2, name: 'n8' })).toBeVisible();
    expect(within(form).getByRole('textbox', { name: 'Name' })).toHaveValue('n8');
    expect(within(form).getByRole('textbox', { name: 'Alias 1' })).toHaveValue('Nate');
    expect(within(form).getByRole('textbox', { name: 'Notes' })).toHaveValue('Plays everything.');
    expect(within(form).getByRole('textbox', { name: 'Link 1 label' })).toHaveValue('Site');
    expect(within(form).getByRole('textbox', { name: 'Link 2 URL' })).toHaveValue(
      'https://other.example',
    );
    expect(within(form).getByRole('link', { name: 'Site' })).toHaveAttribute(
      'href',
      'https://n8.example',
    );
    expect(within(form).getByRole('button', { name: 'Save' })).toBeDisabled();

    const songs = screen.getByRole('region', { name: 'Songs' });
    expect(
      await within(songs).findByText('No Songs are credited to this Artist yet.'),
    ).toBeVisible();
    expect(screen.getByRole('region', { name: 'Albums' })).toHaveTextContent(
      'No Albums are credited to this Artist yet.',
    );
  });

  it('lists each Song crediting the Artist with its role, primary or featured', async () => {
    const other = { id: '01a10e00-0000-7000-9000-000000000099', name: 'Other' };
    const n8 = { id: N8.id, name: N8.name };
    artistServer(
      [N8],
      [
        {
          ...baseSong,
          id: '0199b1a0-0000-7000-8000-000000000001',
          shortcode: 'n8-1',
          title: 'Zebra Crossing',
          credits: { primary: n8, featured: [] },
        },
        {
          ...baseSong,
          id: '0199b1a0-0000-7000-8000-000000000002',
          shortcode: 'n8-2',
          title: 'All Together',
          credits: { primary: other, featured: [n8] },
        },
        {
          ...baseSong,
          id: '0199b1a0-0000-7000-8000-000000000003',
          shortcode: 'n8-3',
          title: 'Not Theirs',
          credits: { primary: other, featured: [] },
        },
      ],
    );

    await openArtist();

    const table = await screen.findByRole('table', { name: 'Songs credited to n8' });
    const rows = within(table)
      .getAllByRole('row')
      .slice(1)
      .map((row) =>
        within(row)
          .getAllByRole('cell')
          .map((cell) => cell.textContent),
      );
    expect(rows).toEqual([
      ['All Together', 'n8-2', 'Featured'],
      ['Zebra Crossing', 'n8-1', 'Primary'],
    ]);
    expect(within(table).getByRole('link', { name: 'Zebra Crossing' })).toHaveAttribute(
      'href',
      '/songs/n8-1',
    );
  });

  it('adds an alias and a link and saves the whole lists under the revision', async () => {
    const server = artistServer([N8]);
    const user = userEvent.setup();
    const form = await openArtist();

    await user.click(within(form).getByRole('button', { name: 'Add alias' }));
    await user.type(within(form).getByRole('textbox', { name: 'Alias 2' }), '  N.  Pond ');
    await user.click(within(form).getByRole('button', { name: 'Add link' }));
    await user.type(
      within(form).getByRole('textbox', { name: 'Link 3 URL' }),
      'https://new.example',
    );
    await user.click(within(form).getByRole('button', { name: 'Move link 3 up' }));
    await user.click(within(form).getByRole('button', { name: 'Save' }));

    expect(await within(form).findByText('Saved.')).toBeVisible();
    expect(server.writes).toEqual([
      {
        method: 'PATCH',
        path: `/api/v1/artists/${N8.id}`,
        ifMatch: '"1"',
        body: {
          aliases: ['Nate', 'N. Pond'],
          links: [
            { label: 'Site', url: 'https://n8.example' },
            { label: null, url: 'https://new.example' },
            { label: null, url: 'https://other.example' },
          ],
        },
      },
    ]);
    expect(within(form).getByRole('textbox', { name: 'Alias 2' })).toHaveValue('N. Pond');
    expect(within(form).getByRole('button', { name: 'Save' })).toBeDisabled();

    // Removing one saves the shorter list on the new revision.
    await user.click(within(form).getByRole('button', { name: 'Remove alias 1' }));
    await user.click(within(form).getByRole('button', { name: 'Save' }));
    await waitFor(() => {
      expect(server.writes).toHaveLength(2);
    });
    expect(server.writes[1]).toMatchObject({ ifMatch: '"2"', body: { aliases: ['N. Pond'] } });
  });

  it('refuses a link that is not a web address, an alias equal to the name, and a blank name, sending nothing', async () => {
    const server = artistServer([N8]);
    const user = userEvent.setup();
    const form = await openArtist();

    await user.clear(within(form).getByRole('textbox', { name: 'Link 2 URL' }));
    await user.type(
      within(form).getByRole('textbox', { name: 'Link 2 URL' }),
      'ftp://files.example',
    );
    await user.clear(within(form).getByRole('textbox', { name: 'Alias 1' }));
    await user.type(within(form).getByRole('textbox', { name: 'Alias 1' }), 'N8');
    await user.click(within(form).getByRole('button', { name: 'Save' }));

    expect(
      within(form).getByText('Enter a web address starting with http:// or https://.'),
    ).toBeVisible();
    expect(within(form).getByText("An alias cannot be the Artist's own name.")).toBeVisible();
    expect(within(form).getByRole('textbox', { name: 'Link 2 URL' })).toHaveAttribute(
      'aria-invalid',
      'true',
    );

    await user.clear(within(form).getByRole('textbox', { name: 'Name' }));
    await user.click(within(form).getByRole('button', { name: 'Save' }));
    expect(within(form).getByText('Enter a name.')).toBeVisible();
    expect(server.writes).toHaveLength(0);

    await user.click(within(form).getByRole('button', { name: 'Discard changes' }));
    expect(within(form).getByRole('textbox', { name: 'Name' })).toHaveValue('n8');
    expect(within(form).queryByText('Enter a name.')).not.toBeInTheDocument();
  });

  it('asks before giving the Artist a name another has, and saves it once confirmed', async () => {
    const other = testArtist('Other', { aliases: ['Twin'] });
    const server = artistServer([N8, other]);
    const user = userEvent.setup();
    const form = await openArtist();

    await user.clear(within(form).getByRole('textbox', { name: 'Name' }));
    await user.type(within(form).getByRole('textbox', { name: 'Name' }), 'twin');
    await user.click(within(form).getByRole('button', { name: 'Save' }));

    const dialog = await screen.findByRole('dialog', { name: 'Another Artist has this name' });
    expect(within(dialog).getByTestId('duplicate-matches')).toHaveTextContent(
      'Other (its alias “Twin”)',
    );

    // Cancelled: nothing is saved, and the typed name stays.
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    expect(within(form).getByRole('textbox', { name: 'Name' })).toHaveValue('twin');
    expect(within(form).queryByText('Artist not saved')).not.toBeInTheDocument();
    expect(server.artists[0]?.name).toBe('n8');

    await user.click(within(form).getByRole('button', { name: 'Save' }));
    const again = await screen.findByRole('dialog', { name: 'Another Artist has this name' });
    await user.click(within(again).getByRole('button', { name: 'Save anyway' }));

    expect(await screen.findByRole('heading', { level: 2, name: 'twin' })).toBeVisible();
    expect(server.writes.map((write) => write.body)).toEqual([
      { name: 'twin' },
      { name: 'twin' },
      { name: 'twin', confirmDuplicate: true },
    ]);

    // An edit that changes neither name nor aliases never asks.
    await user.type(within(form).getByRole('textbox', { name: 'Notes' }), ' More.');
    await user.click(within(form).getByRole('button', { name: 'Save' }));
    expect(await within(form).findByText('Saved.')).toBeVisible();
    expect(server.writes.at(-1)?.body).toEqual({ notes: 'Plays everything. More.' });
  });

  it('shows the conflict dialog when the Artist changed elsewhere', async () => {
    const server = artistServer([N8]);
    const user = userEvent.setup();
    const form = await openArtist();
    server.changeElsewhere(N8.id, { notes: 'Changed elsewhere.' });

    await user.clear(within(form).getByRole('textbox', { name: 'Notes' }));
    await user.type(within(form).getByRole('textbox', { name: 'Notes' }), 'Mine.');
    await user.click(within(form).getByRole('button', { name: 'Save' }));

    const dialog = await screen.findByRole('dialog', { name: 'Changed since you loaded it' });
    await user.click(within(dialog).getByRole('button', { name: /Reapply|Replace/ }));

    await waitFor(() => {
      expect(server.artists[0]?.notes).toBe('Mine.');
    });
    expect(server.writes.map((write) => write.ifMatch)).toEqual(['"1"', '"2"']);
  });

  it('says when there is no such Artist', async () => {
    artistServer([]);

    renderApp('/artists/01a10e00-0000-7000-8000-999999999999');

    expect(await screen.findByRole('heading', { level: 2, name: 'No such Artist' })).toBeVisible();
    expect(screen.getByRole('link', { name: '← Artists' })).toHaveAttribute('href', '/artists');
  });

  it('says when a save fails', async () => {
    const server = artistServer([N8]);
    const user = userEvent.setup();
    const form = await openArtist();
    server.next = () => new Response('{}', { status: 500 });

    await user.type(within(form).getByRole('textbox', { name: 'Notes' }), '!');
    await user.click(within(form).getByRole('button', { name: 'Save' }));

    expect(await within(form).findByText('Artist not saved')).toBeVisible();
    expect(within(form).getByRole('textbox', { name: 'Notes' })).toHaveValue('Plays everything.!');
  });
});
