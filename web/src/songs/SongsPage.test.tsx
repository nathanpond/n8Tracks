import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { Song, SongPage } from '../api/songs';
import { formatDateTime } from '../api/timeZone';
import {
  healthyReport,
  jsonResponse,
  neverAnswers,
  renderApp,
  requestPath,
  stubFetch,
} from '../test/helpers';

const IDEA = { id: '01a10a6e-dc80-7000-8000-000000000001', name: 'Idea', colour: 'yellow' };
const WRITING = { id: '01a10a6e-dc81-7001-8000-000000000002', name: 'Writing', colour: 'blue' };
const ARCHIVED = { id: '01a10a6e-dc86-7006-8000-000000000007', name: 'Archived', colour: 'gray' };

const STATES = [IDEA, WRITING, ARCHIVED].map((state, index) => ({
  ...state,
  order: index + 1,
  hidden: false,
}));

const LONG_CONCEPT =
  'A fast-paced song about running in a pack through the night, ' +
  'with a chorus that keeps coming back and a bridge that slows everything down.\nSecond line.';

function song(number: number, overrides: Partial<Song> = {}): Song {
  const shortcode = `n8-${String(number)}`;
  return {
    id: `0199b1a0-0000-7000-8000-${String(number).padStart(12, '0')}`,
    shortcode,
    title: 'Running in a Pack',
    concept: null,
    state: IDEA,
    currentVersion: {
      id: `0199b1a0-0000-7000-9000-${String(number).padStart(12, '0')}`,
      number: '1',
      shortcode: `${shortcode}-v1`,
    },
    versionCount: 1,
    createdAt: '2026-10-01T09:00:00Z',
    updatedAt: '2026-10-01T09:00:00Z',
    revision: 1,
    ...overrides,
  };
}

function page(items: Song[], extra: Partial<SongPage> = {}): SongPage {
  return { items, page: 1, pageSize: 50, total: items.length, ...extra };
}

function problem(status: number, code: string, extra: Record<string, unknown> = {}): Response {
  return new Response(JSON.stringify({ status, code, title: 'refused', ...extra }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

interface Backend {
  /** The answer to a GET of the list, given its query string. */
  list?: (search: URLSearchParams) => Response | Promise<Response>;
  /** The answer to a GET of one Song, given its reference. */
  song?: (reference: string) => Response;
  /** The answer to POST songs. */
  create?: (body: unknown) => Response;
  /** The workflow states, when not Idea, Writing, and Archived. */
  states?: unknown[];
}

/** The query string a request was made with, whatever form `fetch` was given it in. */
function searchOf(input: RequestInfo | URL): string {
  const url = input instanceof Request ? input.url : typeof input === 'string' ? input : input.href;
  return new URL(url, document.baseURI).search;
}

/** The JSON a request sent. */
function sentBody(init: RequestInit | undefined): unknown {
  return typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
}

/** A backend: the health report names UTC, the states are Idea, Writing, and Archived. */
function backend({ list, song: one, create, states = STATES }: Backend) {
  const mock = stubFetch();
  mock.mockImplementation((input, init) => {
    const path = requestPath(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (path.endsWith('/api/v1/workflow-states')) {
      return Promise.resolve(jsonResponse(200, { revision: 1, items: states }));
    }
    if (path.endsWith('/api/v1/songs') && method === 'GET' && list) {
      const url = new URL(
        input instanceof Request ? input.url : input.toString(),
        document.baseURI,
      );
      return Promise.resolve(list(url.searchParams));
    }
    if (path.endsWith('/api/v1/songs') && method === 'POST' && create) {
      return Promise.resolve(create(sentBody(init)));
    }
    const match = /\/api\/v1\/songs\/([^/]+)$/.exec(path);
    if (match?.[1] !== undefined && method === 'GET' && one) {
      return Promise.resolve(one(decodeURIComponent(match[1])));
    }
    return Promise.resolve(problem(404, 'not_found'));
  });
  return mock;
}

/** The query strings the list was asked with, in order. */
function listRequests(mock: ReturnType<typeof stubFetch>): string[] {
  return mock.mock.calls
    .filter(
      ([input, init]) =>
        requestPath(input).endsWith('/api/v1/songs') &&
        (init?.method ?? 'GET').toUpperCase() === 'GET',
    )
    .map(([input]) => searchOf(input));
}

function posts(mock: ReturnType<typeof stubFetch>) {
  return mock.mock.calls.filter(
    ([input, init]) => requestPath(input).endsWith('/api/v1/songs') && init?.method === 'POST',
  );
}

function row(shortcode: string): HTMLElement {
  const found = screen.getByRole('rowheader', { name: shortcode }).closest('tr');
  if (!found) {
    throw new Error(`No row for ${shortcode}`);
  }
  return found;
}

async function openDialog(user: ReturnType<typeof userEvent.setup>) {
  await screen.findByText('There are no Songs yet.');
  await user.click(screen.getByRole('button', { name: 'New Song' }));
  return screen.findByRole('form', { name: 'New Song' });
}

describe('Songs', () => {
  it('shows a loading indicator until the list answers', async () => {
    const mock = stubFetch();
    mock.mockImplementation(neverAnswers);

    renderApp('/songs');

    expect(await screen.findByLabelText('Loading Songs')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('shows the empty state with the New Song action when there are no Songs', async () => {
    backend({ list: () => jsonResponse(200, page([])) });

    renderApp('/songs');

    expect(await screen.findByText('There are no Songs yet.')).toBeVisible();
    expect(screen.getByRole('button', { name: 'New Song' })).toBeVisible();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('lists each Song with its shortcode, title, concept, state, Versions, and updated time', async () => {
    const updated = new Date(Date.now() - 5 * 60 * 1000).toISOString();
    backend({
      list: () =>
        jsonResponse(
          200,
          page([
            song(3, { concept: LONG_CONCEPT, updatedAt: updated }),
            song(2, { state: ARCHIVED, versionCount: 4, title: 'Older one' }),
          ]),
        ),
    });

    renderApp('/songs');

    const table = await screen.findByRole('table', { name: 'Songs' });
    expect(
      within(table)
        .getAllByRole('columnheader')
        .map((cell) => cell.textContent),
    ).toEqual(['Shortcode', 'Title', 'Concept', 'State', 'Versions', 'Updated ▼']);
    const first = row('n8-3');
    expect(within(first).getByRole('link', { name: 'Running in a Pack' })).toHaveAttribute(
      'href',
      '/songs/n8-3',
    );
    expect(within(first).getByText('Idea')).toBeVisible();
    expect(within(first).getByText('1')).toBeVisible();
    expect(within(first).getByText('5 minutes ago')).toBeVisible();
    // Archived Songs are listed like any other.
    const second = row('n8-2');
    expect(within(second).getByText('Archived').closest('[data-state-colour]')).toHaveAttribute(
      'data-state-colour',
      'gray',
    );
    expect(within(second).getByText('4')).toBeVisible();
    expect(screen.getByText('2 Songs')).toBeVisible();
  });

  it('cuts a long concept to one line and shows all of it on focus', async () => {
    backend({ list: () => jsonResponse(200, page([song(1, { concept: LONG_CONCEPT })])) });
    const user = userEvent.setup();

    renderApp('/songs');

    await screen.findByRole('table', { name: 'Songs' });
    const concept = within(row('n8-1')).getByText(/^A fast-paced song/);
    expect(concept).toHaveStyle({
      whiteSpace: 'nowrap',
      overflow: 'hidden',
      textOverflow: 'ellipsis',
    });
    expect(concept).toHaveTextContent(LONG_CONCEPT.replace('\n', ' '));

    await user.tab();
    while (document.activeElement !== concept) {
      await user.tab();
    }
    const tooltip = await screen.findByRole('tooltip');
    expect(tooltip.textContent).toBe(LONG_CONCEPT);
  });

  it('shows the updated time in the configured zone on focus', async () => {
    backend({ list: () => jsonResponse(200, page([song(1)])) });

    renderApp('/songs');

    await screen.findByRole('table', { name: 'Songs' });
    const absolute = formatDateTime('2026-10-01T09:00:00Z', 'UTC');
    const time = within(row('n8-1')).getByText((_, element) => element?.tagName === 'TIME');
    expect(time).toHaveAttribute('dateTime', '2026-10-01T09:00:00Z');
    const target = time.parentElement;
    if (!target) {
      throw new Error('The time has no target.');
    }
    act(() => {
      target.focus();
    });
    expect(await screen.findByRole('tooltip')).toHaveTextContent(absolute);
  });

  it('says the list could not be loaded, and tries again', async () => {
    let fail = true;
    backend({
      list: () => (fail ? problem(500, 'internal_error') : jsonResponse(200, page([song(1)]))),
    });
    const user = userEvent.setup();

    renderApp('/songs');

    expect(await screen.findByText('Songs could not be loaded')).toBeVisible();
    fail = false;
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('table', { name: 'Songs' })).toBeVisible();
  });

  it('opens newest first and sorts by title, A to Z then Z to A, in the URL', async () => {
    const mock = backend({ list: () => jsonResponse(200, page([song(1)])) });
    const user = userEvent.setup();

    renderApp('/songs');

    await screen.findByRole('table', { name: 'Songs' });
    expect(listRequests(mock)).toEqual(['']);
    expect(screen.getByRole('columnheader', { name: /Updated/ })).toHaveAttribute(
      'aria-sort',
      'descending',
    );

    await user.click(screen.getByRole('button', { name: /Title/ }));
    await waitFor(() => {
      expect(listRequests(mock).at(-1)).toBe('?sort=title');
    });
    expect(await screen.findByRole('columnheader', { name: /Title/ })).toHaveAttribute(
      'aria-sort',
      'ascending',
    );
    expect(screen.getByRole('columnheader', { name: /Updated/ })).not.toHaveAttribute('aria-sort');

    await user.click(screen.getByRole('button', { name: /Title/ }));
    await waitFor(() => {
      expect(listRequests(mock).at(-1)).toBe('?sort=title&direction=desc');
    });

    await user.click(screen.getByRole('button', { name: /Updated/ }));
    await waitFor(() => {
      expect(listRequests(mock).at(-1)).toBe('');
    });
  });

  it('filters by any number of states and back to every state', async () => {
    const mock = backend({
      list: (search) =>
        jsonResponse(200, search.getAll('state').length > 1 ? page([]) : page([song(1)])),
    });
    const user = userEvent.setup();

    renderApp('/songs');

    const filter = await screen.findByRole('group', { name: 'Workflow state' });
    await user.click(within(filter).getByRole('checkbox', { name: 'Idea' }));
    await waitFor(() => {
      expect(listRequests(mock).at(-1)).toBe(`?state=${IDEA.id}`);
    });
    await user.click(within(filter).getByRole('checkbox', { name: 'Archived' }));
    await waitFor(() => {
      expect(listRequests(mock).at(-1)).toBe(`?state=${IDEA.id}&state=${ARCHIVED.id}`);
    });
    expect(await screen.findByText('No Songs are in the chosen states.')).toBeVisible();

    await user.click(within(filter).getByRole('button', { name: 'Show every state' }));
    await waitFor(() => {
      expect(listRequests(mock).at(-1)).toBe('');
    });
    expect(await screen.findByRole('table', { name: 'Songs' })).toBeVisible();
  });

  it('offers a hidden state in the filter only while Songs are in it', async () => {
    backend({
      list: () => jsonResponse(200, page([song(1)])),
      states: [
        { ...IDEA, order: 1, hidden: false, songCount: 1 },
        { ...WRITING, order: 2, hidden: false, songCount: 0 },
        { ...ARCHIVED, order: 3, hidden: true, songCount: 2 },
        {
          id: '01a10a6e-dc87-7007-8000-000000000008',
          name: 'Shelved',
          colour: 'red',
          order: 4,
          hidden: true,
          songCount: 0,
        },
      ],
    });

    renderApp('/songs');

    const filter = await screen.findByRole('group', { name: 'Workflow state' });
    expect(
      within(filter)
        .getAllByRole('checkbox')
        .map((box) => box.getAttribute('value')),
    ).toEqual([IDEA.id, WRITING.id, ARCHIVED.id]);
    expect(within(filter).queryByRole('checkbox', { name: 'Shelved' })).not.toBeInTheDocument();
  });

  it('restores the view from the URL, as after a reload', async () => {
    const mock = backend({ list: () => jsonResponse(200, page([song(1)])) });

    renderApp(`/songs?sort=title&state=${WRITING.id}&page=2`);

    await screen.findByRole('table', { name: 'Songs' });
    expect(listRequests(mock)).toEqual([`?sort=title&state=${WRITING.id}&page=2`]);
    const filter = screen.getByRole('group', { name: 'Workflow state' });
    expect(within(filter).getByRole('checkbox', { name: 'Writing' })).toBeChecked();
    expect(within(filter).getByRole('checkbox', { name: 'Idea' })).not.toBeChecked();
    expect(screen.getByRole('columnheader', { name: /Title/ })).toHaveAttribute(
      'aria-sort',
      'ascending',
    );
  });

  it('pages through more than fifty Songs', async () => {
    const mock = backend({
      list: (search) => {
        const number = Number(search.get('page') ?? '1');
        return jsonResponse(
          200,
          page([song(number === 1 ? 120 : 70)], { page: number, total: 120 }),
        );
      },
    });
    const user = userEvent.setup();

    renderApp('/songs');

    await screen.findByRole('table', { name: 'Songs' });
    expect(screen.getByText('Songs 1–1 of 120')).toBeVisible();
    const pages = screen.getByRole('navigation', { name: 'Pages' });
    await user.click(within(pages).getByRole('button', { name: 'Page 2' }));
    await waitFor(() => {
      expect(listRequests(mock).at(-1)).toBe('?page=2');
    });
    expect(await screen.findByRole('rowheader', { name: 'n8-70' })).toBeVisible();
    expect(
      within(screen.getByRole('navigation', { name: 'Pages' })).getByRole('button', {
        name: 'Page 2',
      }),
    ).toHaveAttribute('aria-current', 'page');
  });

  it('offers the first page when the page in the URL is past the end', async () => {
    backend({
      list: (search) =>
        jsonResponse(
          200,
          search.get('page') === '9' ? page([], { page: 9, total: 3 }) : page([song(1)]),
        ),
    });
    const user = userEvent.setup();

    renderApp('/songs?page=9');

    expect(await screen.findByText('There are no Songs on this page.')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Go to the first page' }));
    expect(await screen.findByRole('table', { name: 'Songs' })).toBeVisible();
  });
});

describe('New Song', () => {
  it('refuses a blank title without sending anything', async () => {
    const mock = backend({ list: () => jsonResponse(200, page([])) });
    const user = userEvent.setup();

    renderApp('/songs');
    const form = await openDialog(user);
    await user.type(within(form).getByLabelText(/Title/), '   ');
    await user.click(within(form).getByRole('button', { name: 'Create Song' }));

    expect(await within(form).findByText('Enter a title.')).toBeVisible();
    expect(within(form).getByLabelText(/Title/)).toHaveAttribute('aria-invalid', 'true');
    expect(posts(mock)).toHaveLength(0);
  });

  it('refuses a title over 300 characters and a concept over 2,000 without sending anything', async () => {
    const mock = backend({ list: () => jsonResponse(200, page([])) });
    const user = userEvent.setup();

    renderApp('/songs');
    const form = await openDialog(user);
    fireEvent.change(within(form).getByLabelText(/Title/), { target: { value: 'x'.repeat(301) } });
    fireEvent.change(within(form).getByLabelText(/Concept/), {
      target: { value: 'y'.repeat(2001) },
    });
    await user.click(within(form).getByRole('button', { name: 'Create Song' }));

    expect(await within(form).findByText('Use at most 300 characters.')).toBeVisible();
    expect(within(form).getByText('Use at most 2,000 characters.')).toBeVisible();
    expect(posts(mock)).toHaveLength(0);

    // Exactly 300 after trimming is fine.
    fireEvent.change(within(form).getByLabelText(/Title/), {
      target: { value: ` ${'x'.repeat(300)} ` },
    });
    fireEvent.change(within(form).getByLabelText(/Concept/), { target: { value: '' } });
    await user.click(within(form).getByRole('button', { name: 'Create Song' }));
    await waitFor(() => {
      expect(posts(mock)).toHaveLength(1);
    });
  });

  it("shows the API's field errors", async () => {
    backend({
      list: () => jsonResponse(200, page([])),
      create: () =>
        problem(422, 'validation_failed', {
          errors: { title: ['A title is one line, with no control characters.'] },
        }),
    });
    const user = userEvent.setup();

    renderApp('/songs');
    const form = await openDialog(user);
    await user.type(within(form).getByLabelText(/Title/), 'Odd title');
    await user.click(within(form).getByRole('button', { name: 'Create Song' }));

    expect(
      await within(form).findByText('A title is one line, with no control characters.'),
    ).toBeVisible();
  });

  it('keeps the dialog open with the typed text when the create fails, and adds nothing', async () => {
    const mock = backend({
      list: () => jsonResponse(200, page([])),
      create: () => problem(500, 'internal_error'),
    });
    const user = userEvent.setup();

    renderApp('/songs');
    const form = await openDialog(user);
    await user.type(within(form).getByLabelText(/Title/), 'Running in a Pack');
    await user.type(within(form).getByLabelText(/Concept/), 'Fast and loud.');
    const listsBefore = listRequests(mock).length;
    await user.click(within(form).getByRole('button', { name: 'Create Song' }));

    expect(await within(form).findByText('Song not created')).toBeVisible();
    expect(within(form).getByLabelText(/Title/)).toHaveValue('Running in a Pack');
    expect(within(form).getByLabelText(/Concept/)).toHaveValue('Fast and loud.');
    expect(screen.getByRole('dialog', { name: 'New Song' })).toBeVisible();
    expect(listRequests(mock)).toHaveLength(listsBefore);
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('turns line breaks in a pasted title into spaces', async () => {
    const mock = backend({
      list: () => jsonResponse(200, page([])),
      create: () => problem(500, 'internal_error'),
    });
    const user = userEvent.setup();

    renderApp('/songs');
    const form = await openDialog(user);
    const title = within(form).getByLabelText(/Title/);
    await user.click(title);
    await user.paste('Running\nin a\r\nPack');

    expect(title).toHaveValue('Running in a Pack');
    await user.click(within(form).getByRole('button', { name: 'Create Song' }));
    await waitFor(() => {
      expect(posts(mock)).toHaveLength(1);
    });
    expect(sentBody(posts(mock)[0]?.[1])).toEqual({
      title: 'Running in a Pack',
      concept: '',
    });
  });

  it('creates the Song and opens its page', async () => {
    const created = song(1, { concept: 'Fast and loud.' });
    const mock = backend({
      list: () => jsonResponse(200, page([])),
      create: () => jsonResponse(201, created),
      song: (reference) => (reference === 'n8-1' ? jsonResponse(200, created) : problem(404, 'x')),
    });
    const user = userEvent.setup();

    renderApp('/songs');
    const form = await openDialog(user);
    await user.type(within(form).getByLabelText(/Title/), 'Running in a Pack');
    await user.type(within(form).getByLabelText(/Concept/), 'Fast and loud.');
    await user.click(within(form).getByRole('button', { name: 'Create Song' }));

    expect(
      await screen.findByRole('heading', { level: 2, name: 'Running in a Pack' }),
    ).toBeVisible();
    expect(screen.getByTestId('shortcode')).toHaveTextContent('n8-1');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(sentBody(posts(mock)[0]?.[1])).toEqual({
      title: 'Running in a Pack',
      concept: 'Fast and loud.',
    });
  });
});
