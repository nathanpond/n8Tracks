import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { renderApp } from '../test/helpers';
import { baseSong, FOLK, INDIE_POP, ROCK, songServer } from '../test/songServer';
import { DETAILS_OPEN_STORAGE_KEY, DETAILS_OVERLAY_QUERY } from './detailsPanelState';

/** Plays a window narrower than the overlay breakpoint. */
function narrowWindow() {
  vi.stubGlobal('matchMedia', (query: string): MediaQueryList => ({
    matches: query === DETAILS_OVERLAY_QUERY,
    media: query,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn(() => false),
  }));
}

async function openSong() {
  const view = renderApp('/songs/n8-7');
  await screen.findByRole('heading', { level: 2, name: 'Running in a Pack' });
  return view;
}

function detailsButton() {
  return screen.getByRole('button', { name: 'Details' });
}

async function openDetails(user: ReturnType<typeof userEvent.setup>) {
  await openSong();
  await user.click(detailsButton());
  return screen.findByRole('combobox', { name: 'Genres' });
}

/** The Genres on the Song, by their remove buttons. */
function chosenGenres() {
  return screen
    .queryAllByRole('button', { name: /^Remove Genre / })
    .map((button) => button.getAttribute('aria-label')?.replace('Remove Genre ', ''));
}

describe('the Details panel', () => {
  it('starts closed, opens beside the editor, and is remembered per browser', async () => {
    const user = userEvent.setup();
    songServer();
    const view = await openSong();

    expect(detailsButton()).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('complementary', { name: 'Details' })).not.toBeInTheDocument();

    await user.click(detailsButton());

    const panel = screen.getByRole('complementary', { name: 'Details' });
    expect(detailsButton()).toHaveAttribute('aria-expanded', 'true');
    expect(detailsButton()).toHaveAttribute('aria-controls', panel.id);
    expect(within(panel).getByRole('combobox', { name: 'Genres' })).toBeVisible();
    expect(within(panel).getByText('No notes yet.')).toBeVisible();
    expect(window.localStorage.getItem(DETAILS_OPEN_STORAGE_KEY)).toBe('true');

    // Opened again later, it is still open.
    view.unmount();
    await openSong();
    expect(screen.getByRole('complementary', { name: 'Details' })).toBeVisible();

    // Its close control closes it and gives focus back to the control that opens it.
    await user.click(screen.getByRole('button', { name: 'Close details' }));
    expect(screen.queryByRole('complementary', { name: 'Details' })).not.toBeInTheDocument();
    expect(detailsButton()).toHaveFocus();
    expect(window.localStorage.getItem(DETAILS_OPEN_STORAGE_KEY)).toBe('false');
  });

  it('overlays the editor on a narrow window, always starting closed, and closes with Escape', async () => {
    const user = userEvent.setup();
    window.localStorage.setItem(DETAILS_OPEN_STORAGE_KEY, 'true');
    narrowWindow();
    songServer();
    await openSong();

    expect(detailsButton()).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('dialog', { name: 'Details' })).not.toBeInTheDocument();

    await user.click(detailsButton());
    const dialog = await screen.findByRole('dialog', { name: 'Details' });
    expect(screen.queryByRole('complementary', { name: 'Details' })).not.toBeInTheDocument();
    expect(within(dialog).getByRole('combobox', { name: 'Genres' })).toBeInTheDocument();

    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Details' })).not.toBeInTheDocument();
    });
    await waitFor(() => {
      expect(detailsButton()).toHaveFocus();
    });
    // The overlay's state is not remembered.
    expect(window.localStorage.getItem(DETAILS_OPEN_STORAGE_KEY)).toBe('true');

    // Its close control closes it too.
    await user.click(detailsButton());
    await user.click(await screen.findByRole('button', { name: 'Close details' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Details' })).not.toBeInTheDocument();
    });
  });

  it('adds an existing Genre by typing, on the Song’s revision', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    const field = await openDetails(user);

    await user.type(field, 'pop');
    await screen.findByRole('option', { name: 'Indie Pop' });
    await user.keyboard('{Enter}');

    await waitFor(() => {
      expect(chosenGenres()).toEqual(['Indie Pop']);
    });
    expect(server.edits).toEqual([{ ifMatch: '"1"', body: { genreIds: [INDIE_POP.id] } }]);
    expect(server.song.revision).toBe(2);
  });

  it('creates a Genre on the spot and adds it', async () => {
    const user = userEvent.setup();
    const { server } = songServer({ ...baseSong, genres: [{ id: FOLK.id, name: FOLK.name }] });
    const field = await openDetails(user);

    await user.type(field, 'Indie Rock');
    await user.click(await screen.findByRole('option', { name: 'Create Genre “Indie Rock”' }));

    await waitFor(() => {
      expect(chosenGenres()).toEqual(['Folk', 'Indie Rock']);
    });
    expect(server.created).toEqual(['Indie Rock']);
    const created = server.genres.find((genre) => genre.name === 'Indie Rock');
    expect(server.edits.at(-1)?.body).toEqual({ genreIds: [FOLK.id, created?.id] });

    // It is suggested from then on.
    await user.type(field, 'indie');
    expect(await screen.findByRole('option', { name: 'Indie Pop' })).toBeVisible();
    expect(screen.queryByRole('option', { name: 'Indie Rock' })).not.toBeInTheDocument();
  });

  it('removes a Genre from the Song, keeping the others', async () => {
    const user = userEvent.setup();
    const { server } = songServer({
      ...baseSong,
      genres: [
        { id: FOLK.id, name: FOLK.name },
        { id: ROCK.id, name: ROCK.name },
      ],
    });
    await openDetails(user);

    await user.click(screen.getByRole('button', { name: 'Remove Genre Rock' }));

    await waitFor(() => {
      expect(chosenGenres()).toEqual(['Folk']);
    });
    expect(server.edits.at(-1)?.body).toEqual({ genreIds: [FOLK.id] });
  });

  it('merges its change onto Genres changed elsewhere without asking', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    const field = await openDetails(user);
    server.changeElsewhere({ genres: [{ id: FOLK.id, name: FOLK.name }] });

    await user.type(field, 'rock');
    await user.click(await screen.findByRole('option', { name: 'Rock' }));

    await waitFor(() => {
      expect(chosenGenres()).toEqual(['Folk', 'Rock']);
    });
    expect(server.edits.map((edit) => edit.ifMatch)).toEqual(['"1"', '"2"']);
    expect(server.edits[1]?.body).toEqual({ genreIds: [FOLK.id, ROCK.id] });
    expect(screen.queryByRole('dialog', { name: /changed/ })).not.toBeInTheDocument();
    expect(server.song.revision).toBe(3);
  });

  it('shows the Genres in the conflict dialog when another field changed too', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    const field = await openDetails(user);
    server.changeElsewhere({
      title: 'Renamed elsewhere',
      genres: [{ id: FOLK.id, name: FOLK.name }],
    });

    await user.type(field, 'rock');
    await user.click(await screen.findByRole('option', { name: 'Rock' }));

    const differences = await screen.findByRole('table', { name: 'Differences' });
    const genresRow = within(differences).getByRole('row', { name: /Genres/ });
    expect(genresRow).toHaveTextContent('Folk, Rock');
    expect(within(differences).getByRole('row', { name: /Title/ })).toHaveTextContent(
      'Renamed elsewhere',
    );
  });

  it('reads the Genres again when one chosen no longer exists', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    const field = await openDetails(user);
    await user.type(field, 'rock');
    await screen.findByRole('option', { name: 'Rock' });
    // Rock goes away (merged or deleted elsewhere) while the list on the page still has it.
    server.genres = server.genres.filter((genre) => genre.id !== ROCK.id);
    await user.keyboard('{Enter}');

    expect(
      await screen.findByText(
        'A Genre chosen no longer exists. Choose again. The Genres have been read again.',
      ),
    ).toBeVisible();
    expect(chosenGenres()).toEqual([]);
    await user.type(field, 'rock');
    expect(await screen.findByRole('option', { name: 'Create Genre “rock”' })).toBeVisible();
  });

  it('saves the Song’s notes with its revision', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    await openDetails(user);

    await user.click(screen.getByRole('button', { name: 'Edit notes' }));
    const area = screen.getByRole('textbox', { name: 'Notes' });
    await user.type(area, '  Bridge needs work.  ');
    await user.tab();

    expect(await screen.findByText('Bridge needs work.')).toBeVisible();
    expect(server.edits).toEqual([{ ifMatch: '"1"', body: { notes: 'Bridge needs work.' } }]);

    // Over the limit is refused before sending.
    await user.click(screen.getByRole('button', { name: 'Edit notes' }));
    const again = screen.getByRole('textbox', { name: 'Notes' });
    await user.clear(again);
    await user.click(again);
    await user.paste('n'.repeat(10_001));
    await user.keyboard('{Control>}{Enter}{/Control}');
    expect(await screen.findByText('Use at most 10,000 characters.')).toBeVisible();
    expect(server.edits).toHaveLength(1);
  });

  it('lists the Playlists the Song is on, by title, each linking to the Playlist', async () => {
    const user = userEvent.setup();
    songServer({
      ...baseSong,
      playlists: [
        { id: '01a1c000-0000-7000-8000-000000000101', title: 'Anthems' },
        { id: '01a1c000-0000-7000-8000-000000000102', title: 'Road trip' },
      ],
    });
    await openDetails(user);

    const section = screen.getByRole('group', { name: 'Playlists' });
    expect(
      within(section)
        .getAllByRole('link')
        .map((link) => link.textContent),
    ).toEqual(['Anthems', 'Road trip']);
    expect(within(section).getByRole('link', { name: 'Road trip' })).toHaveAttribute(
      'href',
      '/playlists/01a1c000-0000-7000-8000-000000000102',
    );
  });

  it('lists the Albums the Song is on, with its disc and track, each linking to the Album', async () => {
    const user = userEvent.setup();
    songServer({
      ...baseSong,
      albums: [
        { id: '01a1b000-0000-7000-8000-000000000101', title: 'Anthology', disc: 2, track: 7 },
        { id: '01a1b000-0000-7000-8000-000000000102', title: 'Pack EP', disc: 1, track: 2 },
      ],
    });
    await openDetails(user);

    const section = screen.getByRole('group', { name: 'Albums' });
    expect(
      within(section)
        .getAllByRole('listitem')
        .map((item) => item.textContent),
    ).toEqual(['Anthology, disc 2, track 7', 'Pack EP, disc 1, track 2']);
    expect(within(section).getByRole('link', { name: 'Pack EP' })).toHaveAttribute(
      'href',
      '/albums/01a1b000-0000-7000-8000-000000000102',
    );
  });

  it('says when the Song is on no Album', async () => {
    const user = userEvent.setup();
    songServer();
    await openDetails(user);

    expect(
      within(screen.getByRole('group', { name: 'Albums' })).getByText('Not on any Album.'),
    ).toBeVisible();
  });

  it('says when the Song is on no Playlist', async () => {
    const user = userEvent.setup();
    songServer();
    await openDetails(user);

    expect(
      within(screen.getByRole('group', { name: 'Playlists' })).getByText('Not on any Playlist.'),
    ).toBeVisible();
  });
});
