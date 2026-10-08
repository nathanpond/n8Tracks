import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { NO_RELEASE, type Song, type SongPage } from '../api/songs';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';
import { directionLabel, sortsOffered } from './songSortRules';

const IDEA = { id: '01a10a6e-dc80-7000-8000-000000000001', name: 'Idea', colour: 'yellow' };
const STATES = [{ ...IDEA, order: 1, hidden: false }];

function song(number: number, overrides: Partial<Song> = {}): Song {
  const shortcode = `n8-${String(number)}`;
  return {
    id: `0199b1a0-0000-7000-8000-${String(number).padStart(12, '0')}`,
    shortcode,
    title: `Lantern ${String(number)}`,
    concept: null,
    state: IDEA,
    currentVersion: {
      id: `0199b1a0-0000-7000-9000-${String(number).padStart(12, '0')}`,
      number: '1',
      shortcode: `${shortcode}-v1`,
      kind: 'song',
    },
    versionCount: 1,
    createdAt: '2026-10-01T09:00:00Z',
    updatedAt: '2026-10-01T09:00:00Z',
    revision: 1,
    notes: null,
    genres: [],
    tags: [],
    credits: { primary: null, featured: [] },
    playlists: [],
    albums: [],
    relationships: [],
    release: NO_RELEASE,
    warnings: [],
    artwork: null,
    hasSelectedGeneration: false,
    selectedGeneration: null,
    sunoWorkspace: null,
    ...overrides,
  };
}

function page(items: Song[], extra: Partial<SongPage> = {}): SongPage {
  return { items, page: 1, pageSize: 50, total: items.length, ...extra };
}

/** The list answers every request with `answer`; the rest of the page's reads answer empty. */
function backend(answer: SongPage = page([song(1, { highestRating: 4 }), song(2)])) {
  const mock = stubFetch();
  mock.mockImplementation((input) => {
    const path = requestPath(input);
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (path.endsWith('/api/v1/workflow-states')) {
      return Promise.resolve(jsonResponse(200, { revision: 1, items: STATES }));
    }
    if (path.endsWith('/api/v1/genres') || path.endsWith('/api/v1/tags')) {
      return Promise.resolve(jsonResponse(200, { items: [] }));
    }
    if (path.endsWith('/api/v1/artists')) {
      return Promise.resolve(jsonResponse(200, { items: [], page: 1, pageSize: 20, total: 0 }));
    }
    if (path.endsWith('/api/v1/songs')) {
      return Promise.resolve(jsonResponse(200, answer));
    }
    return Promise.resolve(jsonResponse(404, { status: 404, code: 'not_found' }));
  });
  return mock;
}

function requestUrl(input: RequestInfo | URL): string {
  return input instanceof Request ? input.url : typeof input === 'string' ? input : input.href;
}

/** The query strings the list was asked with, in order. */
function listRequests(mock: ReturnType<typeof stubFetch>): string[] {
  return mock.mock.calls
    .filter(([input]) => requestPath(input).endsWith('/api/v1/songs'))
    .map(([input]) => new URL(requestUrl(input), document.baseURI).search);
}

function sortControl(): HTMLElement {
  return screen.getByRole('group', { name: 'Sort' });
}

function sortBy(): HTMLSelectElement {
  return within(sortControl()).getByRole('combobox', { name: 'Sort by' });
}

function header(name: string): HTMLElement {
  return within(screen.getByRole('table', { name: 'Songs' })).getByRole('columnheader', {
    name: new RegExp(`^${name}`),
  });
}

describe('sorting the Songs table (#226)', () => {
  it('offers every key in the Sort control, Relevance only while searching', () => {
    expect(sortsOffered({})).toEqual([
      'title',
      'created',
      'updated',
      'rating',
      'state',
      'lastGeneration',
      'audioFiles',
    ]);
    expect(sortsOffered({ search: 'lantern' })[0]).toBe('relevance');
    expect(directionLabel('rating', 'desc')).toBe('Highest first');
    expect(directionLabel('state', 'asc')).toBe('Workflow order');
    expect(directionLabel('relevance', 'desc')).toBeUndefined();
  });

  it('sorts by a key with no column from the Sort control, in its first direction, then reversed', async () => {
    const mock = backend();
    const user = userEvent.setup();

    renderApp('/songs?page=2');

    await screen.findByRole('table', { name: 'Songs' });
    expect(sortBy()).toHaveValue('updated');
    expect(within(sortControl()).getByRole('combobox', { name: 'Order' })).toHaveValue('desc');
    expect(
      within(sortBy())
        .getAllByRole('option')
        .map((option) => option.textContent),
    ).toEqual([
      'Title',
      'Created',
      'Updated',
      'Rating',
      'Workflow state',
      'Last Generation date',
      'Audio files',
    ]);

    // A new sort goes back to the first page.
    await user.selectOptions(sortBy(), 'Last Generation date');
    await waitFor(() => {
      expect(listRequests(mock).at(-1)).toBe('?sort=lastGeneration');
    });
    const order = within(sortControl()).getByRole('combobox', { name: 'Order' });
    expect(order).toHaveValue('desc');
    expect(
      within(order)
        .getAllByRole('option')
        .map((option) => option.textContent),
    ).toEqual(['Oldest first', 'Newest first']);
    // No column shows it: no header is sorted.
    expect(
      within(screen.getByRole('table', { name: 'Songs' }))
        .getAllByRole('columnheader')
        .filter((cell) => cell.hasAttribute('aria-sort')),
    ).toEqual([]);

    await user.selectOptions(order, 'Oldest first');
    await waitFor(() => {
      expect(listRequests(mock).at(-1)).toBe('?sort=lastGeneration&direction=asc');
    });
  });

  it('shows the sorted column and direction on its header, and sorts from the headers', async () => {
    const mock = backend();
    const user = userEvent.setup();

    renderApp('/songs');

    await screen.findByRole('table', { name: 'Songs' });
    expect(header('Updated')).toHaveAttribute('aria-sort', 'descending');

    // Rating starts highest first; a second press reverses it.
    await user.click(within(header('Rating')).getByRole('button'));
    await waitFor(() => {
      expect(listRequests(mock).at(-1)).toBe('?sort=rating');
    });
    expect(header('Rating')).toHaveAttribute('aria-sort', 'descending');
    expect(header('Updated')).not.toHaveAttribute('aria-sort');
    expect(sortBy()).toHaveValue('rating');
    await user.click(within(header('Rating')).getByRole('button'));
    await waitFor(() => {
      expect(listRequests(mock).at(-1)).toBe('?sort=rating&direction=asc');
    });
    expect(header('Rating')).toHaveAttribute('aria-sort', 'ascending');

    // State starts in the user's order; Created newest first.
    await user.click(within(header('State')).getByRole('button'));
    await waitFor(() => {
      expect(listRequests(mock).at(-1)).toBe('?sort=state');
    });
    expect(header('State')).toHaveAttribute('aria-sort', 'ascending');
    await user.click(within(header('Created')).getByRole('button'));
    await waitFor(() => {
      expect(listRequests(mock).at(-1)).toBe('?sort=created');
    });
    expect(header('Created')).toHaveAttribute('aria-sort', 'descending');
  });

  it('shows each Song’s highest rating, and a dash for none', async () => {
    backend();

    renderApp('/songs');

    const table = await screen.findByRole('table', { name: 'Songs' });
    const ratings = within(table).getAllByTestId('song-rating');
    expect(ratings.map((cell) => cell.textContent)).toEqual(['★ 4 of 5', '—None']);
  });

  it('orders a search by Relevance until another sort is chosen, and Relevance can be chosen again', async () => {
    const mock = backend();
    const user = userEvent.setup();

    renderApp('/songs?search=lantern');

    await screen.findByRole('table', { name: 'Songs' });
    expect(sortBy()).toHaveValue('relevance');
    expect(within(sortControl()).queryByRole('combobox', { name: 'Order' })).toBeNull();

    await user.selectOptions(sortBy(), 'Updated');
    await waitFor(() => {
      expect(listRequests(mock).at(-1)).toBe('?search=lantern&sort=updated');
    });
    expect(header('Updated')).toHaveAttribute('aria-sort', 'descending');

    await user.selectOptions(sortBy(), 'Relevance');
    await waitFor(() => {
      expect(listRequests(mock).at(-1)).toBe('?search=lantern');
    });
    expect(header('Updated')).not.toHaveAttribute('aria-sort');
  });

  it('keeps a chosen sort when a search starts, and returns to the first page', async () => {
    const mock = backend();
    const user = userEvent.setup();

    renderApp('/songs?sort=title&page=3');

    await screen.findByRole('table', { name: 'Songs' });
    const box = within(screen.getByRole('search', { name: 'Songs table search' })).getByRole(
      'searchbox',
      { name: /Search/ },
    );
    await user.type(box, 'lantern{Enter}');
    await waitFor(() => {
      expect(listRequests(mock).at(-1)).toBe('?search=lantern&sort=title');
    });
    expect(sortBy()).toHaveValue('title');
  });
});
