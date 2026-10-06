import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { albumServer, testAlbum } from '../test/albumServer';
import { renderApp } from '../test/helpers';

const N8 = { id: '01a10e00-0000-7000-8000-0000000000aa', name: 'n8' };
const PACK = testAlbum('Pack EP', { albumArtist: N8, releaseDate: '2026-03' });
const ALPHA = testAlbum('alpha', { originalReleaseDate: '1999' });
const UNDATED = testAlbum('Zed');

function rows() {
  return within(screen.getByRole('table', { name: 'Albums' }))
    .getAllByRole('row')
    .slice(1)
    .map((row) => row.getAttribute('data-album-title'));
}

describe('the Albums page', () => {
  it('lists Albums by title with the Album Artist, Song count, and date, a dash for what is missing', async () => {
    albumServer([PACK, UNDATED, ALPHA]);

    renderApp('/albums');

    expect(await screen.findByRole('heading', { level: 2, name: 'Albums' })).toBeVisible();
    await screen.findByRole('table', { name: 'Albums' });
    expect(rows()).toEqual(['alpha', 'Pack EP', 'Zed']);

    const cells = (title: string) =>
      within(screen.getByRole('row', { name: new RegExp(title) }))
        .getAllByRole('cell')
        .map((cell) => cell.textContent);
    expect(cells('Pack EP')).toEqual(['n8', '0', 'March 2026']);
    // The original release date stands in for a missing release date.
    expect(cells('alpha')).toEqual(['—', '0', '1999']);
    expect(cells('Zed')).toEqual(['—', '0', '—']);
    expect(
      within(screen.getByRole('row', { name: /Pack EP/ })).getByRole('link', { name: 'Pack EP' }),
    ).toHaveAttribute('href', `/albums/${PACK.id}`);
    expect(screen.getByText('3 Albums')).toBeVisible();
    expect(
      within(screen.getByRole('navigation', { name: 'Main' })).getByRole('link', {
        name: 'Albums',
      }),
    ).toHaveAttribute('aria-current', 'page');
  });

  it('sorts by a column header, again reversed, keeping the view in the URL', async () => {
    const server = albumServer([PACK, UNDATED, ALPHA]);
    const user = userEvent.setup();

    const { router } = renderApp('/albums');
    await screen.findByRole('table', { name: 'Albums' });
    expect(screen.getByRole('columnheader', { name: /Title/ })).toHaveAttribute(
      'aria-sort',
      'ascending',
    );

    await user.click(screen.getByRole('button', { name: /Release date/ }));
    await waitFor(() => {
      expect(rows()).toEqual(['alpha', 'Pack EP', 'Zed']);
    });
    expect(router.state.location.search).toBe('?sort=releaseDate');
    expect(server.queries.at(-1)).toBe('?sort=releaseDate');
    expect(screen.getByRole('columnheader', { name: /Release date/ })).toHaveAttribute(
      'aria-sort',
      'ascending',
    );

    await user.click(screen.getByRole('button', { name: /Release date/ }));
    await waitFor(() => {
      expect(router.state.location.search).toBe('?sort=releaseDate&direction=desc');
    });
    // Undated Albums stay last in both directions.
    await waitFor(() => {
      expect(rows()).toEqual(['Pack EP', 'alpha', 'Zed']);
    });

    await user.click(screen.getByRole('button', { name: /Album Artist/ }));
    await waitFor(() => {
      expect(router.state.location.search).toBe('?sort=artist');
    });
  });

  it('says when there are no Albums, and creates one from a title, opening it', async () => {
    const server = albumServer([]);
    const user = userEvent.setup();

    const { router } = renderApp('/albums');
    expect(await screen.findByText('There are no Albums yet.')).toBeVisible();

    await user.click(screen.getByRole('button', { name: 'New Album' }));
    const dialog = await screen.findByRole('dialog', { name: 'New Album' });
    await user.click(within(dialog).getByRole('button', { name: 'Create Album' }));
    expect(within(dialog).getByRole('textbox', { name: 'Title' })).toHaveAccessibleDescription(
      /Enter a title\./,
    );
    expect(server.writes).toHaveLength(0);

    await user.type(within(dialog).getByRole('textbox', { name: 'Title' }), '  Pack EP ');
    await user.click(within(dialog).getByRole('button', { name: 'Create Album' }));

    await waitFor(() => {
      expect(router.state.location.pathname).toMatch(/^\/albums\/01a1b000-/);
    });
    expect(server.writes).toEqual([
      { method: 'POST', path: '/api/v1/albums', ifMatch: null, body: { title: '  Pack EP ' } },
    ]);
    expect(await screen.findByRole('heading', { level: 2, name: 'Pack EP' })).toBeVisible();
  });

  it('refuses an over-long title before sending it', async () => {
    const server = albumServer([PACK]);
    const user = userEvent.setup();

    renderApp('/albums');
    await screen.findByRole('table', { name: 'Albums' });
    await user.click(screen.getByRole('button', { name: 'New Album' }));
    const dialog = await screen.findByRole('dialog', { name: 'New Album' });
    await user.click(within(dialog).getByRole('textbox', { name: 'Title' }));
    await user.paste('a'.repeat(301));
    await user.click(within(dialog).getByRole('button', { name: 'Create Album' }));

    expect(within(dialog).getByRole('textbox', { name: 'Title' })).toHaveAccessibleDescription(
      /Use at most 300 characters\./,
    );
    expect(server.writes).toHaveLength(0);
  });
});
