import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { Song } from '../api/songs';
import { renderApp } from '../test/helpers';
import { baseSong, credited, FOLK, INDIE_POP, N8, ROCK, songServer } from '../test/songServer';

/** Midnight UTC `days` days after 1 September 2026. */
function dayAfter(days: number): string {
  return new Date(Date.UTC(2026, 8, 1 + days)).toISOString();
}

/** Another Song titled like the base Song (ignoring case and spacing). */
function namesake(number: number, change: Partial<Song> = {}): Song {
  return {
    ...baseSong,
    id: `0199b1a0-0000-7000-8000-0000000001${String(number).padStart(2, '0')}`,
    shortcode: `n8-${String(number)}`,
    title: '  running IN a   pack ',
    concept: null,
    createdAt: dayAfter(number),
    updatedAt: dayAfter(number),
    ...change,
  };
}

/** The indicator, once it shows (by test ID: a role query over the whole page is slow in jsdom). */
async function indicator(name: string) {
  const button = await screen.findByTestId('same-title');
  expect(button).toHaveAccessibleName(name);
  return button;
}

async function openSong() {
  renderApp('/songs/n8-7');
  return screen.findByRole('heading', { level: 2, name: 'Running in a Pack' });
}

describe('the same-title indicator', () => {
  it('counts the other Songs with the title and lists them, newest first, each linking to it', async () => {
    const { server } = songServer();
    server.others = [
      namesake(11, {
        concept: 'A long concept that goes on.\nAnd on.',
        credits: { primary: credited(N8), featured: [] },
        genres: [FOLK, INDIE_POP, ROCK, { id: 'g4', name: 'Swing' }].map(({ id, name }) => ({
          id,
          name,
        })),
      }),
      namesake(12),
      { ...namesake(13), title: 'Running in a Pack Again' },
    ];
    const user = userEvent.setup();
    await openSong();

    const button = await indicator('2 others with this title');
    expect(server.titleQueries).toEqual([
      `title=Running+in+a+Pack&excludeId=${baseSong.id}&pageSize=20`,
    ]);

    await user.click(button);
    const list = await screen.findByRole('dialog', { name: '2 other Songs have this title' });
    await waitFor(() => {
      expect(list).toBeVisible();
    });
    const [, newest, older] = within(list).getAllByRole('row');
    if (newest === undefined || older === undefined) {
      throw new Error('Expected two rows.');
    }
    expect([newest, older].map((row) => row.getAttribute('data-song'))).toEqual(['n8-12', 'n8-11']);

    // A missing concept, Artist, and Genres are dashes.
    expect(
      within(newest)
        .getAllByRole('cell')
        .map((cell) => cell.textContent),
    ).toEqual(['—', '—', '—', 'Idea', expect.stringContaining('2026')]);
    const full = within(older).getAllByRole('cell');
    expect(full[0]).toHaveTextContent('A long concept that goes on. And on.');
    expect(full[1]).toHaveTextContent('n8');
    expect(full[2]).toHaveTextContent('Folk, Indie Pop, Rock +1');
    expect(full[3]).toHaveTextContent('Idea');
    expect(full[4]?.querySelector('time')).toHaveAttribute('datetime', dayAfter(11));

    expect(
      within(list).getByRole('link', {
        name: 'Show every Song with this title in the Songs table',
      }),
    ).toHaveAttribute('href', '/songs?title=Running+in+a+Pack');

    await user.click(within(list).getByRole('link', { name: 'n8-12' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
  });

  it('says "1 other" for one', async () => {
    const { server } = songServer();
    server.others = [namesake(11)];
    await openSong();
    expect(await indicator('1 other with this title')).toBeVisible();
  });

  it('lists the twenty most recently updated and links to the table for the rest', async () => {
    const { server } = songServer();
    server.others = Array.from({ length: 23 }, (_, index) => namesake(index + 10));
    const user = userEvent.setup();
    await openSong();

    await user.click(await indicator('23 others with this title'));
    const list = await screen.findByRole('dialog', { name: '23 other Songs have this title' });
    expect(within(list).getAllByRole('row')).toHaveLength(21);
    await waitFor(() => {
      expect(within(list).getByText('Showing the 20 most recently updated of 23.')).toBeVisible();
    });
  });

  it('shows nothing for a unique title', async () => {
    const { server } = songServer();
    await openSong();

    await waitFor(() => {
      expect(server.titleQueries).toHaveLength(1);
    });
    expect(screen.queryByRole('button', { name: /with this title/ })).not.toBeInTheDocument();
  });

  it('asks again when the title is saved, and goes away when no other Song has the new one', async () => {
    const { server } = songServer();
    server.others = [namesake(11), namesake(12)];
    const user = userEvent.setup();
    await openSong();
    expect(await indicator('2 others with this title')).toBeVisible();

    await user.click(screen.getByRole('button', { name: 'Edit title' }));
    const input = screen.getByRole('textbox', { name: 'Title' });
    await user.clear(input);
    await user.type(input, 'Running Alone{Enter}');

    expect(await screen.findByRole('heading', { level: 2, name: 'Running Alone' })).toBeVisible();
    await waitFor(() => {
      expect(server.titleQueries.at(-1)).toBe(
        `title=Running+Alone&excludeId=${baseSong.id}&pageSize=20`,
      );
    });
    expect(screen.queryByRole('button', { name: /with this title/ })).not.toBeInTheDocument();

    // Back to a shared title: the indicator returns.
    await user.click(screen.getByRole('button', { name: 'Edit title' }));
    const again = screen.getByRole('textbox', { name: 'Title' });
    await user.clear(again);
    await user.type(again, 'running in a pack{Enter}');
    expect(await indicator('2 others with this title')).toBeVisible();
  });
});
