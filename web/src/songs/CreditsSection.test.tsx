import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../test/helpers';
import { baseSong, CHOIR, credited, GUEST, N8, songServer } from '../test/songServer';

async function openCredits(user: ReturnType<typeof userEvent.setup>) {
  renderApp('/songs/n8-7');
  await screen.findByRole('heading', { level: 2, name: 'Running in a Pack' });
  await user.click(screen.getByRole('button', { name: 'Details' }));
  return screen.findByTestId('song-credits');
}

/** The primary Artist as shown, and the featured Artists in order. */
function shown() {
  const section = screen.getByTestId('song-credits');
  return {
    primary: within(section)
      .getByTestId('primary-artist')
      .textContent.replace(/Remove$/, ''),
    featured: [...section.querySelectorAll('[data-featured-artist]')].map((item) =>
      item.getAttribute('data-featured-artist'),
    ),
  };
}

async function choose(
  user: ReturnType<typeof userEvent.setup>,
  label: string | RegExp,
  typed: string,
  option: string,
) {
  await user.type(screen.getByRole('textbox', { name: label }), typed);
  await user.click(await screen.findByRole('option', { name: option }));
}

describe('the credits section of the Details panel', () => {
  it('sets the primary Artist by typing part of a name, on the Song’s revision', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    await openCredits(user);
    expect(shown()).toEqual({ primary: 'None', featured: [] });

    await choose(user, 'Choose the primary Artist', 'n', 'n8');

    await waitFor(() => {
      expect(shown().primary).toBe('n8');
    });
    expect(server.credits).toEqual([
      { ifMatch: '"1"', body: { primaryArtistId: N8.id, featuredArtistIds: [] } },
    ]);
    expect(server.edits).toEqual([]);
    expect(screen.getByRole('link', { name: 'n8' })).toHaveAttribute('href', `/artists/${N8.id}`);

    // Removing it leaves the Song with no primary Artist.
    await user.click(screen.getByRole('button', { name: 'Remove primary Artist n8' }));
    await waitFor(() => {
      expect(shown().primary).toBe('None');
    });
    expect(server.credits.at(-1)).toEqual({
      ifMatch: '"2"',
      body: { primaryArtistId: null, featuredArtistIds: [] },
    });
  });

  it('adds featured Artists in order, moves them, and makes one primary', async () => {
    const user = userEvent.setup();
    const { server } = songServer({
      ...baseSong,
      credits: { primary: credited(N8), featured: [] },
    });
    await openCredits(user);

    await choose(user, 'Add a featured Artist', 'gue', 'Guest Singer');
    await waitFor(() => {
      expect(shown().featured).toEqual(['Guest Singer']);
    });
    await choose(user, 'Add a featured Artist', 'cho', 'Choir');
    await waitFor(() => {
      expect(shown().featured).toEqual(['Guest Singer', 'Choir']);
    });
    expect(server.credits.at(-1)?.body).toEqual({
      primaryArtistId: N8.id,
      featuredArtistIds: [GUEST.id, CHOIR.id],
    });

    // The first can only move down, the last only up.
    expect(screen.getByRole('button', { name: 'Move Guest Singer up' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Move Choir down' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Move Choir up' }));
    await waitFor(() => {
      expect(shown().featured).toEqual(['Choir', 'Guest Singer']);
    });
    expect(server.credits.at(-1)?.body.featuredArtistIds).toEqual([CHOIR.id, GUEST.id]);

    // Make primary: the previous primary becomes the first featured Artist.
    await user.click(screen.getByRole('button', { name: 'Make Guest Singer primary' }));
    await waitFor(() => {
      expect(shown()).toEqual({ primary: 'Guest Singer', featured: ['n8', 'Choir'] });
    });
    expect(server.credits.at(-1)?.body).toEqual({
      primaryArtistId: GUEST.id,
      featuredArtistIds: [N8.id, CHOIR.id],
    });

    // Removing a featured Artist keeps the others in order.
    await user.click(screen.getByRole('button', { name: 'Remove featured Artist n8' }));
    await waitFor(() => {
      expect(shown().featured).toEqual(['Choir']);
    });
    expect(server.song.revision).toBe(6);
  });

  it('does not suggest an Artist already credited, and choosing a featured one as primary makes it primary', async () => {
    const user = userEvent.setup();
    const { server } = songServer({
      ...baseSong,
      credits: { primary: credited(N8), featured: [credited(GUEST), credited(CHOIR)] },
    });
    await openCredits(user);

    await user.click(screen.getByRole('textbox', { name: 'Add a featured Artist' }));
    const suggestions = await screen.findByRole('listbox', {
      name: 'Add a featured Artist suggestions',
    });
    await waitFor(() => {
      expect(within(suggestions).queryAllByRole('option')).toEqual([]);
    });
    await user.keyboard('{Escape}');

    await choose(user, 'Change the primary Artist', 'ch', 'Choir');
    await waitFor(() => {
      expect(shown()).toEqual({ primary: 'Choir', featured: ['n8', 'Guest Singer'] });
    });
    expect(server.credits.at(-1)?.body).toEqual({
      primaryArtistId: CHOIR.id,
      featuredArtistIds: [N8.id, GUEST.id],
    });
  });

  it('creates an Artist on the spot and credits it', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    await openCredits(user);

    await choose(user, 'Add a featured Artist', 'Strings', 'Create Artist “Strings”');

    await waitFor(() => {
      expect(shown().featured).toEqual(['Strings']);
    });
    expect(server.createdArtists).toEqual([{ name: 'Strings', confirmDuplicate: false }]);
    const created = server.artists.find((artist) => artist.name === 'Strings');
    expect(server.credits.at(-1)?.body).toEqual({
      primaryArtistId: null,
      featuredArtistIds: [created?.id],
    });
  });

  it('asks in the picker when another Artist already has the new name, then uses it or creates another', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    await openCredits(user);

    // "Nate" is n8's alias: suggested as n8, so creating "Nate" asks first.
    await choose(user, 'Choose the primary Artist', 'Nate', 'Create Artist “Nate”');
    const asked = await screen.findByTestId('artist-duplicate');
    expect(asked).toHaveTextContent('An Artist already has the name “Nate”.');
    expect(server.credits).toEqual([]);

    await user.click(within(asked).getByRole('button', { name: 'Use n8 (alias Nate)' }));
    await waitFor(() => {
      expect(shown().primary).toBe('n8');
    });
    expect(server.createdArtists).toEqual([{ name: 'Nate', confirmDuplicate: false }]);

    // Asked again, the user creates another Artist with the same name.
    await choose(user, 'Add a featured Artist', 'Nate', 'Create Artist “Nate”');
    await user.click(
      within(await screen.findByTestId('artist-duplicate')).getByRole('button', {
        name: 'Create another “Nate”',
      }),
    );
    await waitFor(() => {
      expect(shown().featured).toEqual(['Nate']);
    });
    expect(server.createdArtists.at(-1)).toEqual({ name: 'Nate', confirmDuplicate: true });
    expect(screen.queryByTestId('artist-duplicate')).not.toBeInTheDocument();
  });

  it('offers to create an Artist even when one has the typed name, asking first', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    await openCredits(user);

    await user.type(screen.getByRole('textbox', { name: 'Add a featured Artist' }), 'choir');
    expect(await screen.findByRole('option', { name: 'Choir' })).toBeVisible();
    await user.click(screen.getByRole('option', { name: 'Create Artist “choir”' }));

    const asked = await screen.findByTestId('artist-duplicate');
    expect(asked).toHaveTextContent('An Artist already has the name “choir”.');
    await user.click(within(asked).getByRole('button', { name: 'Use Choir' }));
    await waitFor(() => {
      expect(shown().featured).toEqual(['Choir']);
    });
    expect(server.createdArtists).toEqual([{ name: 'choir', confirmDuplicate: false }]);
    expect(server.artists).toHaveLength(3);
  });

  it('reads the credits again when an Artist chosen no longer exists', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    await openCredits(user);
    await user.type(screen.getByRole('textbox', { name: 'Choose the primary Artist' }), 'cho');
    const option = await screen.findByRole('option', { name: 'Choir' });
    server.artists = server.artists.filter((artist) => artist.id !== CHOIR.id);

    await user.click(option);

    expect(
      await screen.findByText(
        'The primary Artist chosen no longer exists. Choose again. The credits have been read again.',
      ),
    ).toBeVisible();
    expect(shown().primary).toBe('None');
  });
});
