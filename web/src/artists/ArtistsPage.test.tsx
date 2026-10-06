import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { artistServer, testArtist } from '../test/artistServer';
import { renderApp } from '../test/helpers';

const N8 = testArtist('n8', { aliases: ['Nate', 'N. Pond'], songCount: 3, albumCount: 1 });
const ALPHA = testArtist('Alpha');
const BETA = testArtist('beta', { aliases: ['The Gamma Ones'] });

function rows() {
  return within(screen.getByRole('table', { name: 'Artists' }))
    .getAllByRole('row')
    .slice(1)
    .map((row) => row.getAttribute('data-artist-name'));
}

describe('the Artists page', () => {
  it('lists every Artist by name, ignoring case, with aliases and Song and Album counts', async () => {
    artistServer([N8, BETA, ALPHA]);

    renderApp('/artists');

    expect(await screen.findByRole('heading', { level: 2, name: 'Artists' })).toBeVisible();
    await screen.findByRole('table', { name: 'Artists' });
    expect(rows()).toEqual(['Alpha', 'beta', 'n8']);
    const row = screen.getByRole('row', { name: /n8/ });
    expect(
      within(row)
        .getAllByRole('cell')
        .map((cell) => cell.textContent),
    ).toEqual(['', 'Nate, N. Pond', '3', '1']);
    expect(within(row).getByRole('link', { name: 'n8' })).toHaveAttribute(
      'href',
      `/artists/${N8.id}`,
    );
    expect(screen.getByText('3 Artists')).toBeVisible();
    expect(
      within(screen.getByRole('navigation', { name: 'Main' })).getByRole('link', {
        name: 'Artists',
      }),
    ).toHaveAttribute('aria-current', 'page');
  });

  it('searches names and aliases, keeping the search in the URL', async () => {
    const server = artistServer([N8, BETA, ALPHA]);
    const user = userEvent.setup();

    const { router } = renderApp('/artists');
    await screen.findByRole('table', { name: 'Artists' });

    await user.type(screen.getByRole('searchbox', { name: 'Search Artists' }), 'gamma');

    await waitFor(() => {
      expect(rows()).toEqual(['beta']);
    });
    expect(router.state.location.search).toBe('?search=gamma');
    expect(server.queries.at(-1)).toBe('?search=gamma');

    await user.clear(screen.getByRole('searchbox', { name: 'Search Artists' }));
    await user.type(screen.getByRole('searchbox', { name: 'Search Artists' }), 'zzz');
    expect(
      await screen.findByText('No Artist has a name or alias containing “zzz”.'),
    ).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Show every Artist' }));
    await waitFor(() => {
      expect(rows()).toEqual(['Alpha', 'beta', 'n8']);
    });
    expect(router.state.location.search).toBe('');
  });

  it('opens on the view in the URL and pages through the list', async () => {
    const server = artistServer([N8, BETA, ALPHA]);
    server.pageSize = 2;
    const user = userEvent.setup();

    const { router } = renderApp('/artists?search=a');
    await screen.findByRole('table', { name: 'Artists' });
    expect(screen.getByRole('searchbox', { name: 'Search Artists' })).toHaveValue('a');
    expect(rows()).toEqual(['Alpha', 'beta']);
    expect(screen.getByText('Artists 1–2 of 3')).toBeVisible();

    await user.click(screen.getByRole('button', { name: 'Next page' }));

    await waitFor(() => {
      expect(rows()).toEqual(['n8']);
    });
    expect(router.state.location.search).toBe('?search=a&page=2');
  });

  it('invites the first Artist when there are none', async () => {
    artistServer([]);

    renderApp('/artists');

    expect(await screen.findByText('There are no Artists yet.')).toBeVisible();
    expect(screen.queryByRole('searchbox')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'New Artist' })).toBeVisible();
  });

  it('creates an Artist and opens it', async () => {
    const server = artistServer([ALPHA]);
    const user = userEvent.setup();

    const { router } = renderApp('/artists');
    await screen.findByRole('table', { name: 'Artists' });
    await user.click(screen.getByRole('button', { name: 'New Artist' }));
    const dialog = await screen.findByRole('dialog', { name: 'New Artist' });

    // A blank name is refused before anything is sent.
    await user.click(within(dialog).getByRole('button', { name: 'Create Artist' }));
    expect(within(dialog).getByText('Enter a name.')).toBeVisible();
    expect(server.writes).toHaveLength(0);

    await user.type(within(dialog).getByRole('textbox', { name: 'Name' }), 'n8');
    await user.click(within(dialog).getByRole('button', { name: 'Create Artist' }));

    expect(await screen.findByRole('heading', { level: 2, name: 'n8' })).toBeVisible();
    expect(server.writes).toEqual([
      { method: 'POST', path: '/api/v1/artists', ifMatch: null, body: { name: 'n8' } },
    ]);
    expect(router.state.location.pathname).toBe(`/artists/${server.artists[1]?.id ?? ''}`);
    await user.click(screen.getByRole('link', { name: '← Artists' }));
    expect(await screen.findByRole('table', { name: 'Artists' })).toBeVisible();
  });

  it('asks before creating an Artist with a name another has, and creates it once confirmed', async () => {
    const server = artistServer([N8]);
    const user = userEvent.setup();

    renderApp('/artists');
    await screen.findByRole('table', { name: 'Artists' });
    await user.click(screen.getByRole('button', { name: 'New Artist' }));
    const dialog = await screen.findByRole('dialog', { name: 'New Artist' });
    await user.type(within(dialog).getByRole('textbox', { name: 'Name' }), 'NATE');
    await user.click(within(dialog).getByRole('button', { name: 'Create Artist' }));

    expect(await within(dialog).findByText('Another Artist has this name')).toBeVisible();
    expect(within(dialog).getByTestId('duplicate-matches')).toHaveTextContent(
      'n8 (its alias “Nate”)',
    );
    expect(server.artists).toHaveLength(1);

    await user.click(within(dialog).getByRole('button', { name: 'Create anyway' }));

    expect(await screen.findByRole('heading', { level: 2, name: 'NATE' })).toBeVisible();
    expect(server.writes.map((write) => write.body)).toEqual([
      { name: 'NATE' },
      { name: 'NATE', confirmDuplicate: true },
    ]);
  });

  it('asks again when the name changes after a duplicate was shown', async () => {
    const server = artistServer([N8]);
    const user = userEvent.setup();

    renderApp('/artists');
    await screen.findByRole('table', { name: 'Artists' });
    await user.click(screen.getByRole('button', { name: 'New Artist' }));
    const dialog = await screen.findByRole('dialog', { name: 'New Artist' });
    await user.type(within(dialog).getByRole('textbox', { name: 'Name' }), 'N8');
    await user.click(within(dialog).getByRole('button', { name: 'Create Artist' }));
    await within(dialog).findByText('Another Artist has this name');

    await user.type(within(dialog).getByRole('textbox', { name: 'Name' }), ' two');

    expect(within(dialog).queryByText('Another Artist has this name')).not.toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Create Artist' }));
    expect(await screen.findByRole('heading', { level: 2, name: 'N8 two' })).toBeVisible();
    expect(server.writes.at(-1)?.body).toEqual({ name: 'N8 two' });
  });
});
