import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { albumServer, testAlbum } from '../test/albumServer';
import { testArtist } from '../test/artistServer';
import { renderApp } from '../test/helpers';

const N8 = testArtist('n8');
const PACK = testAlbum('Pack EP', {
  description: 'About the EP.',
  copyright: '© 2026 n8',
  links: [{ label: 'Shop', url: 'https://shop.example/pack' }],
});

async function openAlbum(id = PACK.id) {
  renderApp(`/albums/${id}`);
  return screen.findByRole('textbox', { name: 'Title' });
}

const field = (name: string) => screen.getByRole('textbox', { name });

describe('an Album page', () => {
  it('shows the title, description, release details, and links', async () => {
    albumServer([PACK]);

    await openAlbum();

    expect(screen.getByRole('heading', { level: 2, name: 'Pack EP' })).toBeVisible();
    expect(field('Title')).toHaveValue('Pack EP');
    expect(field('Description')).toHaveValue('About the EP.');
    expect(field('Copyright')).toHaveValue('© 2026 n8');
    expect(field('Release date')).toHaveValue('');
    expect(field('UPC/EAN')).toHaveValue('');
    expect(screen.getByTestId('album-artist')).toHaveTextContent('No Album Artist.');
    expect(field('Link 1 URL')).toHaveValue('https://shop.example/pack');
    expect(screen.getByRole('link', { name: 'Shop' })).toHaveAttribute(
      'href',
      'https://shop.example/pack',
    );
  });

  it('saves a field on its own when it loses focus, under the Album revision, and shows the date', async () => {
    const server = albumServer([PACK]);
    const user = userEvent.setup();
    await openAlbum();

    await user.type(field('Release date'), ' 2026-03 ');
    await user.tab();

    await waitFor(() => {
      expect(server.writes).toHaveLength(1);
    });
    expect(server.writes[0]).toMatchObject({
      method: 'PATCH',
      ifMatch: '"1"',
      body: { releaseDate: '2026-03' },
    });
    expect(await screen.findByTestId('releaseDate-shown')).toHaveTextContent('Shown as March 2026');
    expect(screen.getByTestId('album-save-status')).toHaveTextContent('Saved.');

    // Unchanged text is not sent again; the next field saves on the new revision.
    await user.click(field('Release date'));
    await user.tab();
    await user.type(field('Original release date'), '1999');
    await user.tab();
    await waitFor(() => {
      expect(server.writes).toHaveLength(2);
    });
    expect(server.writes[1]).toMatchObject({
      ifMatch: '"2"',
      body: { originalReleaseDate: '1999' },
    });
  });

  it('refuses a wrong date or UPC/EAN with a field error before sending it', async () => {
    const server = albumServer([PACK]);
    const user = userEvent.setup();
    await openAlbum();

    await user.type(field('Release date'), '2025-02-29');
    await user.tab();
    expect(field('Release date')).toHaveAccessibleDescription(
      /That day does not exist in that month\./,
    );

    await user.type(field('UPC/EAN'), '036000291453');
    await user.tab();
    expect(field('UPC/EAN')).toHaveAccessibleDescription(
      /The check digit is wrong: check the code for a typing mistake\./,
    );
    expect(field('UPC/EAN')).toHaveAttribute('aria-invalid', 'true');
    expect(server.writes).toHaveLength(0);
  });

  it('stores a UPC/EAN as its digits and warns while another Album has the same code', async () => {
    const other = testAlbum('Another', { upc: '0036000291452' });
    const server = albumServer([PACK, other]);
    const user = userEvent.setup();
    await openAlbum();

    await user.type(field('UPC/EAN'), '0 36000-29145 2');
    await user.tab();

    await waitFor(() => {
      expect(server.writes[0]?.body).toEqual({ upc: '0 36000-29145 2'.replace(/[\s-]/g, '') });
    });
    const warning = await screen.findByTestId('upc-warning');
    expect(warning).toHaveTextContent('Another Album has this UPC/EAN.');
    expect(within(warning).getByRole('link', { name: 'Another' })).toHaveAttribute(
      'href',
      `/albums/${other.id}`,
    );
    expect(field('UPC/EAN')).toHaveValue('036000291452');
  });

  it('chooses, creates, and clears the Album Artist, each saved at once', async () => {
    const server = albumServer([PACK], [N8]);
    const user = userEvent.setup();
    await openAlbum();

    await user.type(screen.getByRole('textbox', { name: 'Choose an Album Artist' }), 'n');
    await user.click(await screen.findByRole('option', { name: 'n8' }));

    await waitFor(() => {
      expect(screen.getByTestId('album-artist')).toHaveTextContent('n8');
    });
    expect(server.writes[0]?.body).toEqual({ albumArtistId: N8.id });
    expect(screen.getByRole('link', { name: 'n8' })).toHaveAttribute('href', `/artists/${N8.id}`);

    await user.click(screen.getByRole('button', { name: 'Clear the Album Artist' }));
    await waitFor(() => {
      expect(screen.getByTestId('album-artist')).toHaveTextContent('No Album Artist.');
    });
    expect(server.writes[1]).toMatchObject({ ifMatch: '"2"', body: { albumArtistId: null } });

    await user.type(screen.getByRole('textbox', { name: 'Choose an Album Artist' }), 'New Act');
    await user.click(await screen.findByRole('option', { name: /Create Artist/ }));
    await waitFor(() => {
      expect(screen.getByTestId('album-artist')).toHaveTextContent('New Act');
    });
    expect(server.artists.map((artist) => artist.name)).toEqual(['n8', 'New Act']);
  });

  it('edits the links as a list and saves them together', async () => {
    const server = albumServer([PACK]);
    const user = userEvent.setup();
    await openAlbum();

    await user.click(screen.getByRole('button', { name: 'Add link' }));
    await user.type(field('Link 2 URL'), 'notaurl');
    await user.click(screen.getByRole('button', { name: 'Save links' }));
    expect(field('Link 2 URL')).toHaveAccessibleDescription(
      /Enter a web address starting with http:\/\/ or https:\/\//,
    );
    expect(server.writes).toHaveLength(0);

    await user.clear(field('Link 2 URL'));
    await user.type(field('Link 2 URL'), 'https://stream.example/pack');
    await user.type(field('Link 2 label'), ' Stream ');
    await user.click(screen.getByRole('button', { name: 'Move link 2 up' }));
    await user.click(screen.getByRole('button', { name: 'Save links' }));

    await waitFor(() => {
      expect(server.writes).toHaveLength(1);
    });
    expect(server.writes[0]?.body).toEqual({
      links: [
        { label: 'Stream', url: 'https://stream.example/pack' },
        { label: 'Shop', url: 'https://shop.example/pack' },
      ],
    });
    expect(await screen.findByRole('link', { name: 'Stream' })).toBeVisible();
  });

  it('shows the conflict dialog when the same field changed elsewhere', async () => {
    const server = albumServer([PACK]);
    const user = userEvent.setup();
    await openAlbum();
    server.changeElsewhere(PACK.id, { copyright: '© 2025 someone' });

    await user.clear(field('Copyright'));
    await user.type(field('Copyright'), '© 2026 mine');
    await user.tab();

    const dialog = await screen.findByRole('dialog', { name: 'Changed since you loaded it' });
    await user.click(within(dialog).getByRole('button', { name: /Reapply|Replace/ }));

    await waitFor(() => {
      expect(server.albums[0]?.copyright).toBe('© 2026 mine');
    });
    expect(server.writes.map((write) => write.ifMatch)).toEqual(['"1"', '"2"']);
  });

  it('says when a save fails and keeps what was typed', async () => {
    const server = albumServer([PACK]);
    const user = userEvent.setup();
    await openAlbum();
    server.next = () => new Response('{}', { status: 500 });

    await user.type(field('Publishing'), '℗ 2026 n8');
    await user.tab();

    expect(await screen.findByText('Not saved')).toBeVisible();
    expect(field('Publishing')).toHaveValue('℗ 2026 n8');
  });

  it('says when there is no such Album', async () => {
    albumServer([]);

    renderApp('/albums/01a1b000-0000-7000-8000-999999999999');

    expect(await screen.findByRole('heading', { level: 2, name: 'No such Album' })).toBeVisible();
    expect(screen.getByRole('link', { name: '← Albums' })).toHaveAttribute('href', '/albums');
  });
});
