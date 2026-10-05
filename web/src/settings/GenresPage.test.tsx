import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { ManagedGenre } from '../api/genres';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';

const INDIE_ROCK: ManagedGenre = {
  id: '01a10a6e-dc80-7000-8000-000000000001',
  name: 'Indie Rock',
  songCount: 3,
  revision: 1,
};
const DASHED: ManagedGenre = {
  id: '01a10a6e-dc80-7000-8000-000000000002',
  name: 'indie-rock',
  songCount: 1,
  revision: 1,
};
const FOLK: ManagedGenre = {
  id: '01a10a6e-dc80-7000-8000-000000000003',
  name: 'Folk',
  songCount: 5,
  revision: 2,
};
const AMBIENT: ManagedGenre = {
  id: '01a10a6e-dc80-7000-8000-000000000004',
  name: 'ambient',
  songCount: 0,
  revision: 1,
};

function problem(status: number, code: string, extra: Record<string, unknown> = {}): Response {
  return new Response(JSON.stringify({ status, code, title: 'refused', ...extra }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

/** One write the fake server received. */
interface Write {
  method: string;
  path: string;
  ifMatch: string | null;
  body: unknown;
}

/**
 * A fake n8Tracks holding Genres, answering rename, merge, and delete as the API does: each write is
 * checked against the Genre's own revision (409 `revision_conflict` when stale). The Songs list
 * answers `total` with `server.songsWithAny` for any Genre filter. `server.next` answers the next
 * write some other way.
 */
function genreServer(genres: ManagedGenre[] = [INDIE_ROCK, DASHED, FOLK, AMBIENT]) {
  const server = {
    genres: genres.map((genre) => ({ ...genre })),
    writes: [] as Write[],
    countQueries: [] as string[],
    songsWithAny: 0,
    next: undefined as (() => Response) | undefined,
  };

  stubFetch().mockImplementation((input, init) => {
    const path = requestPath(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    const url = new URL(input instanceof Request ? input.url : input.toString(), document.baseURI);
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (url.pathname.endsWith('/api/v1/songs')) {
      server.countQueries.push(url.search);
      return Promise.resolve(
        jsonResponse(200, { items: [], page: 1, pageSize: 1, total: server.songsWithAny }),
      );
    }
    if (!url.pathname.includes('/api/v1/genres')) {
      return Promise.resolve(problem(404, 'not_found'));
    }
    if (method === 'GET') {
      return Promise.resolve(jsonResponse(200, { items: server.genres }));
    }

    const headers = new Headers(init?.headers);
    const body: unknown = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
    server.writes.push({
      method,
      path: url.pathname.replace(/^.*\/api\/v1\//, '') + url.search,
      ifMatch: headers.get('If-Match'),
      body,
    });
    const next = server.next;
    if (next) {
      server.next = undefined;
      return Promise.resolve(next());
    }
    const id = /\/genres\/([^/?]+)/.exec(url.pathname)?.[1];
    const genre = server.genres.find((each) => each.id === id);
    if (genre === undefined) {
      return Promise.resolve(problem(404, 'not_found'));
    }
    if (headers.get('If-Match') !== `"${String(genre.revision)}"`) {
      return Promise.resolve(problem(409, 'revision_conflict', { current: genre }));
    }
    const fields = (body ?? {}) as Record<string, unknown>;
    if (method === 'PATCH') {
      const name = String(fields.name).trim().replace(/\s+/g, ' ');
      const holder = server.genres.find(
        (each) => each.id !== id && each.name.toUpperCase() === name.toUpperCase(),
      );
      if (holder) {
        return Promise.resolve(
          problem(409, 'genre_name_taken', { genreId: holder.id, genre: holder }),
        );
      }
      genre.name = name;
      genre.revision += 1;
      return Promise.resolve(jsonResponse(200, genre));
    }
    if (method === 'POST') {
      const sources = fields.sourceIds as string[];
      genre.songCount = server.songsWithAny || genre.songCount;
      genre.revision += 1;
      server.genres = server.genres.filter((each) => !sources.includes(each.id));
      return Promise.resolve(jsonResponse(200, genre));
    }
    const reassignTo = url.searchParams.get('reassignTo');
    const remove = url.searchParams.get('removeFromSongs');
    if (genre.songCount > 0 && reassignTo === null && remove === null) {
      return Promise.resolve(problem(409, 'genre_in_use', { songCount: genre.songCount }));
    }
    server.genres = server.genres.filter((each) => each.id !== id);
    return Promise.resolve(new Response(null, { status: 204 }));
  });
  return server;
}

async function openPage() {
  renderApp('/settings/genres');
  return screen.findByRole('table', { name: 'Genres' });
}

/** The Genre names in the table's order. */
function names(): string[] {
  return [...document.querySelectorAll('tr[data-genre-name]')].map(
    (row) => row.getAttribute('data-genre-name') ?? '',
  );
}

function rowOf(name: string): HTMLElement {
  const row = document.querySelector<HTMLElement>(`tr[data-genre-name="${name}"]`);
  if (!row) {
    throw new Error(`No row for ${name}`);
  }
  return row;
}

function status(): HTMLElement {
  const region = document.querySelector<HTMLElement>('[role="status"][aria-live="polite"]');
  if (!region) {
    throw new Error('No status region');
  }
  return region;
}

async function dialogNamed(name: string): Promise<HTMLElement> {
  const dialog = await screen.findByRole('dialog', { name });
  await waitFor(() => {
    expect(dialog).toBeVisible();
  });
  return dialog;
}

describe('Settings → Genres', () => {
  it('is in the Settings group of the sidebar', async () => {
    genreServer();
    const user = userEvent.setup();
    renderApp('/settings/account');

    await user.click(await screen.findByRole('link', { name: 'Genres' }));

    expect(await screen.findByRole('heading', { level: 2, name: 'Genres' })).toBeVisible();
  });

  it('lists every Genre with its Song count, by name and sortable by count', async () => {
    genreServer();
    const user = userEvent.setup();

    const table = await openPage();

    expect(names()).toEqual(['ambient', 'Folk', 'Indie Rock', 'indie-rock']);
    expect(within(rowOf('Folk')).getAllByRole('cell')[1]).toHaveTextContent('5');
    const nameHeader = within(table).getByRole('columnheader', { name: /Name/ });
    expect(nameHeader).toHaveAttribute('aria-sort', 'ascending');

    await user.click(within(table).getByRole('button', { name: /Songs/ }));
    expect(names()).toEqual(['Folk', 'Indie Rock', 'indie-rock', 'ambient']);
    expect(within(table).getByRole('columnheader', { name: /Songs/ })).toHaveAttribute(
      'aria-sort',
      'descending',
    );
    await user.click(within(table).getByRole('button', { name: /Songs/ }));
    expect(names()).toEqual(['ambient', 'indie-rock', 'Indie Rock', 'Folk']);

    await user.click(within(table).getByRole('button', { name: /Name/ }));
    expect(names()).toEqual(['ambient', 'Folk', 'Indie Rock', 'indie-rock']);
    await user.click(within(table).getByRole('button', { name: /Name/ }));
    expect(names()).toEqual(['indie-rock', 'Indie Rock', 'Folk', 'ambient']);
  });

  it('says so when there are no Genres yet', async () => {
    genreServer([]);
    renderApp('/settings/genres');

    expect(await screen.findByText(/No Genres yet/)).toBeVisible();
  });

  it('renames a Genre under its revision and shows the new name', async () => {
    const server = genreServer();
    const user = userEvent.setup();
    await openPage();

    await user.click(screen.getByRole('button', { name: 'Rename Folk' }));
    const dialog = await dialogNamed('Rename Folk');
    const input = within(dialog).getByRole('textbox', { name: 'Name' });
    await user.clear(input);
    await user.type(input, '  Folk   Pop ');
    await user.click(within(dialog).getByRole('button', { name: 'Rename' }));

    await waitFor(() => {
      expect(names()).toContain('Folk Pop');
    });
    expect(server.writes).toEqual([
      {
        method: 'PATCH',
        path: `genres/${FOLK.id}`,
        ifMatch: '"2"',
        body: { name: '  Folk   Pop ' },
      },
    ]);
    expect(status()).toHaveTextContent('Folk is renamed to Folk Pop.');
  });

  it('offers to merge instead when the new name is another Genre’s, stating the Song count', async () => {
    const server = genreServer();
    const user = userEvent.setup();
    await openPage();

    await user.click(screen.getByRole('button', { name: 'Rename indie-rock' }));
    const rename = await dialogNamed('Rename indie-rock');
    const input = within(rename).getByRole('textbox', { name: 'Name' });
    await user.clear(input);
    await user.type(input, 'INDIE ROCK');
    await user.click(within(rename).getByRole('button', { name: 'Rename' }));

    expect(await within(rename).findByText(/Another Genre is already called/)).toBeVisible();
    expect(server.writes).toEqual([]);
    await user.click(within(rename).getByRole('button', { name: 'Merge into Indie Rock' }));

    const merge = await dialogNamed('Merge indie-rock');
    expect(within(merge).getByRole('combobox', { name: 'Merge into' })).toHaveValue(INDIE_ROCK.id);
    expect(within(merge).getByTestId('merge-summary')).toHaveTextContent(/^1 Song changes\./);
    await user.click(within(merge).getByRole('button', { name: 'Merge' }));

    await waitFor(() => {
      expect(names()).not.toContain('indie-rock');
    });
    expect(server.writes).toEqual([
      {
        method: 'POST',
        path: `genres/${INDIE_ROCK.id}/merge`,
        ifMatch: '"1"',
        body: { sourceIds: [DASHED.id] },
      },
    ]);
    expect(status()).toHaveTextContent('indie-rock merged into Indie Rock.');
  });

  it('offers the merge when the API says the name is taken although the list did not show it', async () => {
    const server = genreServer();
    const user = userEvent.setup();
    await openPage();
    // Another tab renamed Folk to "Rock" after this page loaded.
    server.genres = server.genres.map((genre) =>
      genre.id === FOLK.id ? { ...genre, name: 'Rock', revision: 3 } : genre,
    );

    await user.click(screen.getByRole('button', { name: 'Rename ambient' }));
    const rename = await dialogNamed('Rename ambient');
    const input = within(rename).getByRole('textbox', { name: 'Name' });
    await user.clear(input);
    await user.type(input, 'rock');
    await user.click(within(rename).getByRole('button', { name: 'Rename' }));

    expect(await within(rename).findByRole('button', { name: 'Merge into Rock' })).toBeVisible();
  });

  it('merges several selected Genres into another after stating how many Songs change', async () => {
    const server = genreServer();
    server.songsWithAny = 4;
    const user = userEvent.setup();
    await openPage();
    const merge = screen.getByRole('button', { name: 'Merge into…' });
    expect(merge).toBeDisabled();

    await user.click(screen.getByRole('checkbox', { name: 'Select indie-rock' }));
    await user.click(screen.getByRole('checkbox', { name: 'Select Indie Rock' }));
    await user.click(merge);

    const dialog = await dialogNamed('Merge Indie Rock and indie-rock');
    expect(await within(dialog).findByText(/^4 Songs change\./)).toBeVisible();
    expect(server.countQueries).toEqual([`?pageSize=1&genre=${INDIE_ROCK.id}&genre=${DASHED.id}`]);
    await user.selectOptions(within(dialog).getByRole('combobox', { name: 'Merge into' }), 'Folk');
    expect(within(dialog).getByTestId('merge-summary')).toHaveTextContent('gets Folk instead');
    await user.click(within(dialog).getByRole('button', { name: 'Merge' }));

    await waitFor(() => {
      expect(names()).toEqual(['ambient', 'Folk']);
    });
    expect(server.writes[0]).toEqual({
      method: 'POST',
      path: `genres/${FOLK.id}/merge`,
      ifMatch: '"2"',
      body: { sourceIds: [INDIE_ROCK.id, DASHED.id] },
    });
    expect(screen.getByRole('button', { name: 'Merge into…' })).toBeDisabled();
  });

  it('deletes an unused Genre directly', async () => {
    const server = genreServer();
    const user = userEvent.setup();
    await openPage();

    await user.click(screen.getByRole('button', { name: 'Delete ambient' }));

    await waitFor(() => {
      expect(names()).not.toContain('ambient');
    });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(server.writes).toEqual([
      { method: 'DELETE', path: `genres/${AMBIENT.id}`, ifMatch: '"1"', body: undefined },
    ]);
    expect(status()).toHaveTextContent('ambient is deleted.');
  });

  it('asks what happens to the Songs of a Genre in use, stating the count, and reassigns them', async () => {
    const server = genreServer();
    const user = userEvent.setup();
    await openPage();

    await user.click(screen.getByRole('button', { name: 'Delete Indie Rock' }));
    const dialog = await dialogNamed('Delete Indie Rock');
    expect(within(dialog).getByTestId('delete-summary')).toHaveTextContent(
      /^3 Songs have Indie Rock\./,
    );
    await user.click(within(dialog).getByRole('radio', { name: 'Reassign them to another Genre' }));
    await user.selectOptions(within(dialog).getByRole('combobox', { name: 'Reassign to' }), 'Folk');
    await user.click(within(dialog).getByRole('button', { name: 'Delete Indie Rock' }));

    await waitFor(() => {
      expect(names()).not.toContain('Indie Rock');
    });
    expect(server.writes).toEqual([
      {
        method: 'DELETE',
        path: `genres/${INDIE_ROCK.id}?reassignTo=${FOLK.id}`,
        ifMatch: '"1"',
        body: undefined,
      },
    ]);
    expect(status()).toHaveTextContent('Indie Rock is deleted; its Songs now have Folk.');
  });

  it('can remove a Genre in use from its Songs instead', async () => {
    const server = genreServer();
    const user = userEvent.setup();
    await openPage();

    await user.click(screen.getByRole('button', { name: 'Delete indie-rock' }));
    const dialog = await dialogNamed('Delete indie-rock');
    expect(within(dialog).getByTestId('delete-summary')).toHaveTextContent(
      /^1 Song has indie-rock\./,
    );
    expect(
      within(dialog).getByRole('radio', { name: 'Remove indie-rock from them' }),
    ).toBeChecked();
    await user.click(within(dialog).getByRole('button', { name: 'Delete indie-rock' }));

    await waitFor(() => {
      expect(names()).not.toContain('indie-rock');
    });
    expect(server.writes[0]?.path).toBe(`genres/${DASHED.id}?removeFromSongs=true`);
  });

  it('asks about the Songs when a Genre thought unused turns out to be in use', async () => {
    const server = genreServer();
    const user = userEvent.setup();
    await openPage();
    server.genres = server.genres.map((genre) =>
      genre.id === AMBIENT.id ? { ...genre, songCount: 2 } : genre,
    );

    await user.click(screen.getByRole('button', { name: 'Delete ambient' }));

    const dialog = await dialogNamed('Delete ambient');
    expect(within(dialog).getByTestId('delete-summary')).toHaveTextContent(
      /^2 Songs have ambient\./,
    );
  });

  it('reloads the list and changes nothing when the Genre was changed elsewhere', async () => {
    const server = genreServer();
    const user = userEvent.setup();
    await openPage();
    server.genres = server.genres.map((genre) =>
      genre.id === FOLK.id ? { ...genre, name: 'Folk Music', revision: 3 } : genre,
    );

    await user.click(screen.getByRole('button', { name: 'Rename Folk' }));
    const dialog = await dialogNamed('Rename Folk');
    const input = within(dialog).getByRole('textbox', { name: 'Name' });
    await user.clear(input);
    await user.type(input, 'Folk Pop');
    await user.click(within(dialog).getByRole('button', { name: 'Rename' }));

    await waitFor(() => {
      expect(status()).toHaveTextContent(/changed somewhere else/);
    });
    expect(names()).toContain('Folk Music');
    expect(names()).not.toContain('Folk Pop');
  });
});
