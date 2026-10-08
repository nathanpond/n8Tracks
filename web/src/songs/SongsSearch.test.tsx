import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { NO_RELEASE, type Song, type SongMatch, type SongPage } from '../api/songs';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';

const IDEA = { id: '01a10a6e-dc80-7000-8000-000000000001', name: 'Idea', colour: 'yellow' };
const STATES = [{ ...IDEA, order: 1, hidden: false }];

function song(number: number, overrides: Partial<Song> = {}): Song {
  const shortcode = `n8-${String(number)}`;
  return {
    id: `0199b1a0-0000-7000-8000-${String(number).padStart(12, '0')}`,
    shortcode,
    title: 'Lantern',
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

/** A match on `field` of `owner` (none for the Song's own text), the word `lantern` marked. */
function match(
  field: string,
  owner: SongMatch['owner'] = null,
  text = 'a lantern swinging',
): SongMatch {
  const start = text.indexOf('lantern');
  return {
    field,
    owner,
    excerpt: { text, highlights: start < 0 ? [] : [{ start, length: 7 }] },
  };
}

const version = (song: string, number: string, state: 'active' | 'archived' = 'active') =>
  ({ kind: 'version', reference: `${song}-v${number}`, label: `v${number}`, state }) as const;

const generation = (song: string, state: 'active' | 'archived' | 'trashed' = 'active') =>
  ({ kind: 'generation', reference: `${song}-v1-g1`, label: 'v1-g1', state }) as const;

function page(items: Song[], extra: Partial<SongPage> = {}): SongPage {
  return { items, page: 1, pageSize: 50, total: items.length, ...extra };
}

/** Searched Songs answer with matches; the list without `search` answers `plain`. */
function backend({
  searched,
  plain = page([song(9, { title: 'Plain' })]),
  more,
}: {
  searched: (search: string) => SongPage;
  plain?: SongPage;
  more?: (reference: string, search: string) => Response;
}) {
  const mock = stubFetch();
  mock.mockImplementation((input) => {
    const path = requestPath(input);
    const url = new URL(requestUrl(input), document.baseURI);
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
    const matches = /\/api\/v1\/songs\/([^/]+)\/matches$/.exec(path);
    if (matches?.[1] !== undefined && more) {
      return Promise.resolve(
        more(decodeURIComponent(matches[1]), url.searchParams.get('search') ?? ''),
      );
    }
    if (path.endsWith('/api/v1/songs')) {
      const search = url.searchParams.get('search');
      return Promise.resolve(jsonResponse(200, search === null ? plain : searched(search)));
    }
    return Promise.resolve(jsonResponse(404, { status: 404, code: 'not_found' }));
  });
  return mock;
}

/** The URL a request was made to, whatever form `fetch` was given it in. */
function requestUrl(input: RequestInfo | URL): string {
  return input instanceof Request ? input.url : typeof input === 'string' ? input : input.href;
}

/** The query strings the list was asked with, in order. */
function listRequests(mock: ReturnType<typeof stubFetch>): string[] {
  return mock.mock.calls
    .filter(([input]) => requestPath(input).endsWith('/api/v1/songs'))
    .map(([input]) => new URL(requestUrl(input), document.baseURI).search);
}

/** The `index`th of `elements`, failing the test when there is none. */
function nth(elements: HTMLElement[], index: number): HTMLElement {
  const element = elements[index];
  if (element === undefined) {
    throw new Error(`There is no element ${String(index)}.`);
  }
  return element;
}

function matchRows(shortcode: string): HTMLElement {
  const list = screen.getByRole('list', { name: `Where ${shortcode} matched` });
  return list;
}

function tableSearch(): HTMLElement {
  return within(screen.getByRole('search', { name: 'Songs table search' })).getByRole('searchbox', {
    name: /Search/,
  });
}

/** Waits for the signed-in shell's header (it renders once the session is known). */
async function headerReady(): Promise<void> {
  await screen.findAllByRole('search', { name: 'Search' });
}

function headerSearch(): HTMLElement {
  return within(nth(screen.getAllByRole('search', { name: 'Search' }), 0)).getByRole('textbox', {
    name: 'Search Songs',
  });
}

describe('searching the Songs table', () => {
  it('shows one match beneath a Song: the field, its Version, and the excerpt with the word marked', async () => {
    backend({
      searched: () =>
        page([song(1, { matches: [match('lyrics', version('n8-1', '2.1'))], matchCount: 1 })]),
    });

    renderApp('/songs?search=lantern');

    const list = await screen.findByRole('list', { name: 'Where n8-1 matched' });
    const items = within(list).getAllByRole('listitem');
    expect(items).toHaveLength(1);
    const link = within(nth(items, 0)).getByRole('link', { name: 'Lyrics v2.1' });
    // A Version's match opens the Song on that Version.
    expect(link).toHaveAttribute('href', '/songs/n8-1/v/2.1');
    const marked = within(nth(items, 0)).getByTestId('match-highlight');
    expect(marked.tagName).toBe('MARK');
    expect(marked).toHaveTextContent(/^lantern$/);
    // Bold as well as coloured, and said in words to a screen reader.
    expect(marked).toHaveStyle({ fontWeight: '700' });
    expect(within(nth(items, 0)).getByTestId('match-excerpt')).toHaveTextContent(
      'a lantern swinging (matched: “lantern”)',
    );
    expect(screen.queryByRole('button', { name: /more match/ })).not.toBeInTheDocument();
    expect(screen.getByTestId('songs-total')).toHaveTextContent('1 Song matches “lantern”');
  });

  it('shows two matches: a Song-level field by name alone, and a trashed Generation’s with its mark', async () => {
    backend({
      searched: () =>
        page([
          song(2, {
            matches: [
              match('title', null, 'Lantern'),
              match('sunoTitle', generation('n8-2', 'trashed')),
            ],
            matchCount: 2,
          }),
        ]),
    });

    renderApp('/songs?search=lantern');

    const items = within(
      await screen.findByRole('list', { name: 'Where n8-2 matched' }),
    ).getAllByRole('listitem');
    expect(items).toHaveLength(2);
    // The Song's own field opens the Song.
    expect(within(nth(items, 0)).getByRole('link', { name: 'Title' })).toHaveAttribute(
      'href',
      '/songs/n8-2',
    );
    expect(within(nth(items, 0)).queryByTestId('match-owner')).not.toBeInTheDocument();
    // A Generation's opens the Song with its panel open, and names its shortcode.
    expect(
      within(nth(items, 1)).getByRole('link', { name: 'Suno title n8-2-v1-g1 (In Suno’s Trash)' }),
    ).toHaveAttribute('href', '/songs/n8-2/generations/n8-2-v1-g1');
  });

  it('shows three matches of more and fetches the rest in place on "n more"', async () => {
    const owner = version('n8-3', '1');
    const all = ['lyrics', 'styles', 'prompt', 'versionName', 'versionNotes'].map((field) =>
      match(field, owner),
    );
    const mock = backend({
      searched: () => page([song(3, { matches: all.slice(0, 3), matchCount: 5 })]),
      more: (reference, search) =>
        reference === song(3).id && search === 'lantern'
          ? jsonResponse(200, { matches: all, matchCount: 5 })
          : jsonResponse(404, {}),
    });
    const user = userEvent.setup();

    renderApp('/songs?search=lantern');

    const list = await screen.findByRole('list', { name: 'Where n8-3 matched' });
    expect(within(list).getAllByRole('listitem')).toHaveLength(3);
    await user.click(screen.getByRole('button', { name: '2 more matches in n8-3' }));

    await waitFor(() => {
      expect(within(matchRows('n8-3')).getAllByRole('listitem')).toHaveLength(5);
    });
    expect(
      within(matchRows('n8-3'))
        .getAllByTestId('song-match')
        .map((item) => item.getAttribute('data-field')),
    ).toEqual(['lyrics', 'styles', 'prompt', 'versionName', 'versionNotes']);
    expect(screen.queryByRole('button', { name: /more match/ })).not.toBeInTheDocument();
    expect(
      mock.mock.calls.filter(([input]) => requestPath(input).endsWith('/matches')),
    ).toHaveLength(1);
  });

  it('says when nothing matches and offers to clear the search', async () => {
    const mock = backend({ searched: () => page([]) });
    const user = userEvent.setup();

    const { router } = renderApp('/songs?search=nothing');

    expect(await screen.findByTestId('no-search-results')).toHaveTextContent(
      'No Songs match “nothing”.',
    );
    expect(screen.queryByText('There are no Songs yet.')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Clear the search' }));

    expect(await screen.findByRole('rowheader', { name: 'n8-9' })).toBeVisible();
    expect(router.state.location.search).toBe('');
    expect(listRequests(mock).at(-1)).toBe('');
    expect(tableSearch()).toHaveValue('');
  });

  it('returns to the ordinary order and drops the match rows when the search is cleared', async () => {
    const mock = backend({
      searched: () =>
        page([song(1, { matches: [match('lyrics', version('n8-1', '1'))], matchCount: 1 })]),
    });
    const user = userEvent.setup();

    const { router } = renderApp('/songs?search=lantern');

    await screen.findByRole('list', { name: 'Where n8-1 matched' });
    // Relevance is the order while searching: no sort is sent.
    expect(listRequests(mock).at(-1)).toBe('?search=lantern');
    await user.click(screen.getByRole('button', { name: 'Clear the search text' }));

    expect(await screen.findByRole('rowheader', { name: 'n8-9' })).toBeVisible();
    expect(screen.queryByTestId('song-matches')).not.toBeInTheDocument();
    expect(listRequests(mock).at(-1)).toBe('');
    expect(router.state.location.search).toBe('');
    expect(screen.getByRole('columnheader', { name: /Updated/ })).toHaveAttribute(
      'aria-sort',
      'descending',
    );
  });

  it('keeps the search in the address: typing replaces the entry, Enter adds one, and Back restores it', async () => {
    const mock = backend({
      searched: (search) =>
        page([song(1, { title: search, matches: [match('title', null, search)], matchCount: 1 })]),
    });
    const user = userEvent.setup();

    const { router } = renderApp('/songs?search=lantern&sort=title&page=2');

    // State from the address: the box, the sort, and the page are what it says.
    expect(await screen.findByRole('list', { name: 'Where n8-1 matched' })).toBeVisible();
    expect(tableSearch()).toHaveValue('lantern');
    expect(listRequests(mock).at(-1)).toBe('?search=lantern&sort=title&page=2');
    // The header box shows the active search on the Songs page.
    expect(headerSearch()).toHaveValue('lantern');

    // Typing searches once it stops, back on page 1, in place of the current entry.
    await user.clear(tableSearch());
    await user.type(tableSearch(), 'harbour');
    await waitFor(() => {
      expect(router.state.location.search).toBe('?search=harbour&sort=title');
    });
    expect(router.state.historyAction).toBe('REPLACE');
    expect(listRequests(mock)).not.toContain('?search=h&sort=title');

    // Enter adds an entry; Back goes to the one before it.
    fireEvent.change(tableSearch(), { target: { value: 'quay' } });
    fireEvent.submit(screen.getByRole('search', { name: 'Songs table search' }));
    await waitFor(() => {
      expect(router.state.location.search).toBe('?search=quay&sort=title');
    });
    expect(router.state.location.search).toBe('?search=quay&sort=title');
    expect(router.state.historyAction).toBe('PUSH');
    await waitFor(() => {
      expect(tableSearch()).toHaveValue('quay');
    });

    await router.navigate(-1);
    await waitFor(() => {
      expect(tableSearch()).toHaveValue('harbour');
    });
    expect(headerSearch()).toHaveValue('harbour');
    expect(listRequests(mock).at(-1)).toBe('?search=harbour&sort=title');
  });

  it('shows no match rows without a search', async () => {
    backend({
      searched: () => page([]),
      // Even an answer carrying matches is shown as a plain table when nothing is searched.
      plain: page([song(4, { matches: [match('lyrics', version('n8-4', '1'))], matchCount: 1 })]),
    });

    renderApp('/songs');

    expect(await screen.findByRole('rowheader', { name: 'n8-4' })).toBeVisible();
    expect(screen.queryByTestId('song-matches')).not.toBeInTheDocument();
    expect(screen.queryByRole('list', { name: /matched/ })).not.toBeInTheDocument();
    expect(screen.getByTestId('songs-total')).toHaveTextContent('1 Song');
  });

  it('shows an excerpt holding markup as text', async () => {
    const text = '<script>window.injected = true</script> <b>lantern</b>';
    backend({
      searched: () =>
        page([song(5, { matches: [match('lyrics', version('n8-5', '1'), text)], matchCount: 1 })]),
    });

    renderApp('/songs?search=lantern');

    const excerpt = within(
      await screen.findByRole('list', { name: 'Where n8-5 matched' }),
    ).getByTestId('match-excerpt');
    expect(excerpt.textContent).toContain('<script>window.injected = true</script> <b>');
    expect(excerpt.querySelector('script')).toBeNull();
    expect(excerpt.querySelector('b')).toBeNull();
    expect(within(excerpt).getByTestId('match-highlight')).toHaveTextContent(/^lantern$/);
    expect('injected' in window).toBe(false);
  });

  it('says when the index is being rebuilt', async () => {
    backend({
      searched: () => page([song(1, { matches: [], matchCount: 0 })], { indexRebuilding: true }),
    });

    renderApp('/songs?search=lantern');

    expect(await screen.findByTestId('index-rebuilding')).toHaveTextContent(
      'The search index is being rebuilt',
    );
  });
});

describe('the header search', () => {
  it('opens the Songs table with the results, starting afresh, from any page', async () => {
    const mock = backend({
      searched: () =>
        page([song(1, { matches: [match('lyrics', version('n8-1', '1'))], matchCount: 1 })]),
    });
    const user = userEvent.setup();

    const { router } = renderApp('/settings/system');
    await headerReady();

    await user.type(headerSearch(), 'lantern{Enter}');

    expect(await screen.findByRole('list', { name: 'Where n8-1 matched' })).toBeVisible();
    expect(router.state.location.pathname).toBe('/songs');
    expect(router.state.location.search).toBe('?search=lantern');
    expect(listRequests(mock)).toEqual(['?search=lantern']);
  });

  it('drops the table’s filters, sort, and page, and opens the unsearched table for no text', async () => {
    backend({ searched: () => page([]) });
    const user = userEvent.setup();

    const { router } = renderApp(
      '/songs?search=old&sort=title&page=3&state=01a10a6e-dc80-7000-8000-000000000001',
    );

    await screen.findByTestId('no-search-results');
    await user.clear(headerSearch());
    await user.type(headerSearch(), 'new{Enter}');
    expect(router.state.location.search).toBe('?search=new');

    await user.clear(headerSearch());
    await user.type(headerSearch(), '   {Enter}');
    expect(router.state.location.search).toBe('');
    expect(router.state.location.pathname).toBe('/songs');
  });

  it('takes at most 200 characters', async () => {
    backend({ searched: () => page([]) });

    renderApp('/settings/system');
    await headerReady();

    expect(headerSearch()).toHaveAttribute('maxlength', '200');
    expect(await screen.findByRole('heading', { name: /System/ })).toBeVisible();
  });

  it('is focused by / and Ctrl+K, or on the Songs page the table’s box, but not while typing in a field', async () => {
    backend({ searched: () => page([]) });

    renderApp('/songs');
    await screen.findByRole('rowheader', { name: 'n8-9' });

    // On the Songs page the shortcut goes to the table's own box.
    fireEvent.keyDown(document.body, { key: '/' });
    expect(tableSearch()).toHaveFocus();
    tableSearch().blur();
    fireEvent.keyDown(document.body, { key: 'k', ctrlKey: true });
    expect(tableSearch()).toHaveFocus();

    // Typing a slash into a field is typing, not the shortcut.
    const other = headerSearch();
    other.focus();
    const typed = fireEvent.keyDown(other, { key: '/' });
    expect(typed).toBe(true);
    expect(other).toHaveFocus();
    const control = fireEvent.keyDown(other, { key: 'k', metaKey: true });
    expect(control).toBe(true);
    expect(other).toHaveFocus();
  });

  it('opens and focuses its box from the shortcut away from the Songs page', async () => {
    backend({ searched: () => page([]) });

    renderApp('/settings/system');
    await screen.findByRole('heading', { name: /System/ });

    fireEvent.keyDown(document.body, { key: 'k', metaKey: true });

    // jsdom lays nothing out, so the header is narrow here: the icon's box opens with focus in it.
    await waitFor(() => {
      expect(document.activeElement).toHaveAccessibleName('Search Songs');
    });
    expect(document.activeElement?.tagName).toBe('INPUT');
  });
});
