import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { albumServer, testAlbum, trackOf } from '../test/albumServer';
import { renderApp } from '../test/helpers';
import { HIGHWAY, SONGS, SUNRISE, TOLL } from '../test/playlistServer';

const PACK = testAlbum('Pack EP', {
  tracks: [trackOf(HIGHWAY, 1, 1), trackOf(SUNRISE, 1, 2), trackOf(TOLL, 1, 3)],
  songCount: 3,
});

async function openAlbum(id: string) {
  renderApp(`/albums/${id}`);
  await screen.findByRole('textbox', { name: 'Title' });
}

const disc = (number: number) => screen.getByRole('list', { name: `Disc ${String(number)}` });

/** Each disc's tracks as `title track`, disc by disc, in order. */
const discs = () =>
  [...document.querySelectorAll<HTMLElement>('ol[aria-labelledby^="album-disc-"]')].map((list) =>
    within(list)
      .getAllByRole('listitem')
      .map(
        (row) =>
          `${row.getAttribute('data-song-title') ?? ''} ${row.getAttribute('data-track') ?? ''}`,
      ),
  );

const rowOf = (title: string) => {
  const row = document.querySelector(`li[data-song-title="${title}"]`);
  if (!(row instanceof HTMLElement)) {
    throw new Error(`No row for ${title}`);
  }
  return row;
};

const status = () => screen.getByTestId('album-track-status');

describe("an Album's tracks", () => {
  it('shows each disc in track-number order, each track marked incomplete without a Selected Generation', async () => {
    const server = albumServer([
      testAlbum('Two discs', {
        tracks: [trackOf(TOLL, 2, 1), trackOf(SUNRISE, 1, 5), trackOf(HIGHWAY, 1, 2)],
      }),
    ]);
    await openAlbum(server.albums[0]?.id ?? '');

    expect(screen.getByRole('heading', { level: 3, name: 'Tracks' })).toBeVisible();
    expect(disc(1)).toBeVisible();
    expect(disc(2)).toBeVisible();
    expect(discs()).toEqual([['Highway Lights 2', 'Sunrise Exit 5'], ['Toll Booth Blues 1']]);
    expect(
      within(rowOf('Highway Lights')).getByRole('textbox', {
        name: 'Track number of Highway Lights',
      }),
    ).toHaveValue('2');
    expect(within(rowOf('Highway Lights')).getByRole('link', { name: 'n8-1' })).toHaveAttribute(
      'href',
      '/songs/n8-1',
    );
    expect(rowOf('Highway Lights')).toHaveTextContent('n8');
    expect(within(rowOf('Sunrise Exit')).getByText('Writing')).toBeVisible();
    expect(screen.getAllByTestId('track-incomplete')).toHaveLength(3);
  });

  it('does not mark a track whose Song has a Selected Generation', async () => {
    const server = albumServer([
      testAlbum('Chosen', { tracks: [{ ...trackOf(HIGHWAY, 1, 1), hasSelectedGeneration: true }] }),
    ]);
    await openAlbum(server.albums[0]?.id ?? '');

    expect(discs()).toEqual([['Highway Lights 1']]);
    expect(screen.queryByTestId('track-incomplete')).not.toBeInTheDocument();
  });

  it('adds a Song found by title at the end of the last disc, showing Songs already on it disabled', async () => {
    const server = albumServer(
      [testAlbum('Two', { tracks: [trackOf(HIGHWAY, 1, 1), trackOf(SUNRISE, 1, 2)] })],
      [],
      SONGS,
    );
    const user = userEvent.setup();
    await openAlbum(server.albums[0]?.id ?? '');

    await user.type(screen.getByRole('textbox', { name: 'Add a Song' }), 'i');
    const results = await screen.findByRole('listbox', { name: 'Add a Song results' });
    await waitFor(() => {
      expect(within(results).getAllByRole('option')).toHaveLength(2);
    });
    const onIt = within(results).getByRole('option', { name: /Highway Lights/ });
    expect(onIt).toHaveAttribute('aria-disabled', 'true');
    expect(onIt).toHaveTextContent('on this Album');

    await user.clear(screen.getByRole('textbox', { name: 'Add a Song' }));
    await user.type(screen.getByRole('textbox', { name: 'Add a Song' }), 'n8-3');
    await user.click(await screen.findByRole('option', { name: /Toll Booth Blues/ }));

    await waitFor(() => {
      expect(discs()).toEqual([['Highway Lights 1', 'Sunrise Exit 2', 'Toll Booth Blues 3']]);
    });
    expect(server.writes.at(-1)).toMatchObject({
      method: 'POST',
      ifMatch: '"1"',
      body: { songId: TOLL.id },
    });
    expect(status()).toHaveTextContent('Added Toll Booth Blues as track 3 on disc 1.');
  });

  it('reorders by keyboard with Move up and Move down, renumbering the disc and keeping the focus', async () => {
    const server = albumServer([PACK], [], SONGS);
    const user = userEvent.setup();
    await openAlbum(PACK.id);

    const up = within(rowOf('Toll Booth Blues')).getByRole('button', {
      name: 'Move Toll Booth Blues up',
    });
    up.focus();
    await user.keyboard('{Enter}');
    await waitFor(() => {
      expect(discs()).toEqual([['Highway Lights 1', 'Toll Booth Blues 2', 'Sunrise Exit 3']]);
    });
    await waitFor(() => {
      expect(
        within(rowOf('Toll Booth Blues')).getByRole('button', { name: 'Move Toll Booth Blues up' }),
      ).toHaveFocus();
    });
    await user.keyboard('{Enter}');
    await waitFor(() => {
      expect(discs()).toEqual([['Toll Booth Blues 1', 'Highway Lights 2', 'Sunrise Exit 3']]);
    });

    // At the top, Move up is disabled and the focus moves to Move down.
    await waitFor(() => {
      expect(
        within(rowOf('Toll Booth Blues')).getByRole('button', {
          name: 'Move Toll Booth Blues down',
        }),
      ).toHaveFocus();
    });
    expect(server.writes.map((write) => [write.method, write.ifMatch])).toEqual([
      ['PUT', '"1"'],
      ['PUT', '"2"'],
    ]);
    expect(server.writes.at(-1)?.body).toEqual({
      tracks: [
        { songId: TOLL.id, disc: 1, track: 1 },
        { songId: HIGHWAY.id, disc: 1, track: 2 },
        { songId: SUNRISE.id, disc: 1, track: 3 },
      ],
    });
    expect(status()).toHaveTextContent('Moved Toll Booth Blues to track 1 of 3 on disc 1.');
  });

  it('reorders by dragging a track onto another on its disc', async () => {
    const server = albumServer([PACK], [], SONGS);
    await openAlbum(PACK.id);

    fireEvent.dragStart(rowOf('Toll Booth Blues'), { dataTransfer: { setData: () => undefined } });
    fireEvent.dragOver(rowOf('Highway Lights'));
    fireEvent.drop(rowOf('Highway Lights'));

    await waitFor(() => {
      expect(discs()).toEqual([['Toll Booth Blues 1', 'Highway Lights 2', 'Sunrise Exit 3']]);
    });
    expect(server.writes.at(-1)).toMatchObject({ method: 'PUT' });
  });

  it('moves a track to a new disc and back, closing up the discs', async () => {
    const server = albumServer([PACK], [], SONGS);
    const user = userEvent.setup();
    await openAlbum(PACK.id);

    await user.click(screen.getByRole('button', { name: 'Move Highway Lights to disc' }));
    expect((await screen.findAllByRole('menuitem')).map((item) => item.textContent)).toEqual([
      'New disc 2',
    ]);
    await user.click(screen.getByRole('menuitem', { name: 'New disc 2' }));

    await waitFor(() => {
      expect(discs()).toEqual([['Sunrise Exit 1', 'Toll Booth Blues 2'], ['Highway Lights 1']]);
    });
    expect(server.writes.at(-1)?.body).toEqual({
      tracks: [
        { songId: SUNRISE.id, disc: 1, track: 1 },
        { songId: TOLL.id, disc: 1, track: 2 },
        { songId: HIGHWAY.id, disc: 2, track: 1 },
      ],
    });
    expect(status()).toHaveTextContent('Moved Highway Lights to disc 2, track 1.');

    // Alone on the last disc, it is offered only the other disc; disc 2 then disappears.
    await user.click(screen.getByRole('button', { name: 'Move Highway Lights to disc' }));
    expect((await screen.findAllByRole('menuitem')).map((item) => item.textContent)).toEqual([
      'Disc 1',
    ]);
    await user.click(screen.getByRole('menuitem', { name: 'Disc 1' }));
    await waitFor(() => {
      expect(discs()).toEqual([['Sunrise Exit 1', 'Toll Booth Blues 2', 'Highway Lights 3']]);
    });
    expect(disc(1)).toBeVisible();
  });

  it('moves a row when a number is typed, and Renumber closes the gap', async () => {
    const server = albumServer([PACK], [], SONGS);
    const user = userEvent.setup();
    await openAlbum(PACK.id);
    expect(screen.getByRole('button', { name: 'Renumber' })).toBeDisabled();

    const number = screen.getByRole('textbox', { name: 'Track number of Highway Lights' });
    await user.clear(number);
    await user.type(number, '9{Enter}');

    await waitFor(() => {
      expect(discs()).toEqual([['Sunrise Exit 2', 'Toll Booth Blues 3', 'Highway Lights 9']]);
    });
    expect(server.writes.at(-1)?.body).toEqual({
      tracks: [
        { songId: SUNRISE.id, disc: 1, track: 2 },
        { songId: TOLL.id, disc: 1, track: 3 },
        { songId: HIGHWAY.id, disc: 1, track: 9 },
      ],
    });

    await user.click(screen.getByRole('button', { name: 'Renumber' }));
    await waitFor(() => {
      expect(discs()).toEqual([['Sunrise Exit 1', 'Toll Booth Blues 2', 'Highway Lights 3']]);
    });
    expect(status()).toHaveTextContent('Renumbered every disc 1, 2, 3….');
  });

  it('refuses a number another track on the disc holds, naming it, and a number out of range', async () => {
    const server = albumServer([PACK], [], SONGS);
    const user = userEvent.setup();
    await openAlbum(PACK.id);

    const number = screen.getByRole('textbox', { name: 'Track number of Toll Booth Blues' });
    await user.clear(number);
    await user.type(number, '1');
    await user.tab();

    expect(
      await screen.findByText('Track 1 on disc 1 is held by Highway Lights (n8-1).'),
    ).toBeVisible();
    expect(screen.getByRole('textbox', { name: 'Track number of Toll Booth Blues' })).toHaveValue(
      '3',
    );
    expect(discs()).toEqual([['Highway Lights 1', 'Sunrise Exit 2', 'Toll Booth Blues 3']]);

    // Out of range is refused before anything is sent.
    const writes = server.writes.length;
    await user.clear(number);
    await user.type(number, '1000{Enter}');
    expect(await screen.findByText('Use a whole number from 1 to 999.')).toBeVisible();
    expect(server.writes).toHaveLength(writes);
  });

  it('removes a track, renumbering the rest of its disc', async () => {
    const server = albumServer([PACK], [], SONGS);
    const user = userEvent.setup();
    await openAlbum(PACK.id);

    await user.click(screen.getByRole('button', { name: 'Remove Highway Lights' }));

    await waitFor(() => {
      expect(discs()).toEqual([['Sunrise Exit 1', 'Toll Booth Blues 2']]);
    });
    expect(server.writes.at(-1)).toMatchObject({ method: 'DELETE', ifMatch: '"1"' });
    expect(server.writes.at(-1)?.path).toContain(`/tracks/${HIGHWAY.id}`);
    expect(status()).toHaveTextContent('Removed Highway Lights.');
  });

  it('shows the Album as it is, with a notice, when it changed elsewhere, and does not apply the change', async () => {
    const server = albumServer([PACK], [], SONGS);
    const user = userEvent.setup();
    await openAlbum(PACK.id);
    server.changeElsewhere(PACK.id, { tracks: [trackOf(SUNRISE, 1, 1), trackOf(HIGHWAY, 1, 2)] });

    await user.click(screen.getByRole('button', { name: 'Move Toll Booth Blues up' }));

    await waitFor(() => {
      expect(discs()).toEqual([['Sunrise Exit 1', 'Highway Lights 2']]);
    });
    expect(status()).toHaveTextContent(
      'This Album was changed elsewhere, so your change was not applied.',
    );

    // The next change is based on the Album as it is now.
    await user.click(screen.getByRole('button', { name: 'Move Highway Lights up' }));
    await waitFor(() => {
      expect(discs()).toEqual([['Highway Lights 1', 'Sunrise Exit 2']]);
    });
    expect(server.writes.at(-1)?.ifMatch).toBe('"2"');
  });

  it('says so when a Song is on the Album already', async () => {
    const server = albumServer([testAlbum('One', { tracks: [trackOf(HIGHWAY, 1, 1)] })], [], SONGS);
    const user = userEvent.setup();
    await openAlbum(server.albums[0]?.id ?? '');
    // Added elsewhere after the search answered: the API refuses it.
    const current = server.albums[0];
    server.next = () =>
      new Response(
        JSON.stringify({
          status: 409,
          code: 'song_already_on_album',
          current: {
            ...current,
            tracks: [trackOf(HIGHWAY, 1, 1), trackOf(SUNRISE, 1, 2)],
            revision: 2,
          },
        }),
        { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
      );

    await user.type(screen.getByRole('textbox', { name: 'Add a Song' }), 'sunrise');
    await user.click(await screen.findByRole('option', { name: /Sunrise Exit/ }));

    expect(await screen.findByText('Sunrise Exit is on this Album already.')).toBeVisible();
    expect(discs()).toEqual([['Highway Lights 1', 'Sunrise Exit 2']]);
  });

  it('says there are no tracks yet', async () => {
    const server = albumServer([testAlbum('Empty')], [], SONGS);
    await openAlbum(server.albums[0]?.id ?? '');

    expect(screen.getByText('No tracks yet. Find a Song above to add it.')).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Renumber' })).not.toBeInTheDocument();
  });
});
