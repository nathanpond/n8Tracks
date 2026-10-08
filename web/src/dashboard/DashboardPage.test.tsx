import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DASHBOARD_REFRESH_MS, type Dashboard, type DashboardSong } from '../api/dashboard';
import {
  healthyReport,
  jsonResponse,
  neverAnswers,
  renderApp,
  requestPath,
  stubFetch,
} from '../test/helpers';

const WRITING = { id: '01a10a6e-dc81-7001-8000-000000000002', name: 'Writing', colour: 'blue' };
const ARCHIVED = { id: '01a10a6e-dc86-7006-8000-000000000007', name: 'Archived', colour: 'gray' };
const FINAL = { id: '01a10a6e-dc84-7004-8000-000000000005', name: 'Final', colour: 'green' };

function song(number: number, change: Partial<DashboardSong> = {}): DashboardSong {
  return {
    id: `0199b1a0-0000-7000-8000-${String(number).padStart(12, '0')}`,
    shortcode: `n8-${String(number)}`,
    title: `Song ${String(number)}`,
    state: WRITING,
    updatedAt: '2026-10-08T09:00:00Z',
    ...change,
  };
}

function dashboard(change: Partial<Dashboard> = {}): Dashboard {
  return {
    recentlyEdited: { data: { songs: [song(2), song(1)], total: 2 } },
    workflowStates: {
      data: {
        states: [
          { ...WRITING, hidden: false, songCount: 2 },
          { ...FINAL, hidden: true, songCount: 0 },
          { ...ARCHIVED, hidden: false, songCount: 1 },
        ],
      },
    },
    withoutSelection: { data: { count: 1, songs: [song(1)] } },
    ...change,
  };
}

const FAILED = { error: { code: 'section_failed' } };

/** A fake n8Tracks answering the dashboard with each of `answers` in turn (the last one again after). */
function server(...answers: (() => Promise<Response>)[]) {
  const reads: number[] = [];
  stubFetch().mockImplementation((input, init) => {
    const path = requestPath(input);
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (path.endsWith('/api/v1/dashboard')) {
      reads.push(reads.length);
      const answer = answers[Math.min(reads.length - 1, answers.length - 1)];
      return answer === undefined ? neverAnswers(input, init) : answer();
    }
    return Promise.resolve(jsonResponse(404, { code: 'not_found' }));
  });
  return reads;
}

const ok = (body: Dashboard) => () => Promise.resolve(jsonResponse(200, body));

function section(name: string) {
  return screen.getByRole('region', { name });
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe('the dashboard', () => {
  it('shows Recently edited, By workflow state, and Without a Selected Generation, each linking to its Songs', async () => {
    server(ok(dashboard()));

    renderApp('/');

    expect(await screen.findByRole('heading', { level: 2, name: 'Dashboard' })).toBeVisible();
    const recent = await waitFor(() => section('Recently edited'));
    const rows = await within(recent).findAllByTestId('dashboard-song');
    expect(rows.map((row) => row.dataset.song)).toEqual(['n8-2', 'n8-1']);
    const [first] = rows;
    if (first === undefined) {
      throw new Error('Recently edited has no rows.');
    }
    expect(within(first).getByRole('link', { name: 'Song 2' })).toHaveAttribute(
      'href',
      '/songs/n8-2',
    );
    expect(within(first).getByText('n8-2')).toBeVisible();
    expect(within(first).getByText('Writing')).toBeVisible();
    expect(within(first).getByRole('time')).toHaveAttribute('datetime', '2026-10-08T09:00:00Z');
    expect(within(recent).getByRole('link', { name: /^See all/ })).toHaveAttribute(
      'href',
      '/songs?archived=active',
    );

    // Each state in the order given, a hidden one with no Song included as the API sends it.
    const states = within(section('By workflow state')).getAllByTestId('state-count');
    expect(states.map((state) => state.dataset.state)).toEqual([WRITING.id, FINAL.id, ARCHIVED.id]);
    expect(
      within(section('By workflow state')).getByRole('link', { name: '2 Songs in Writing' }),
    ).toHaveAttribute('href', `/songs?state=${WRITING.id}`);
    expect(
      within(section('By workflow state')).getByRole('link', { name: '1 Song in Archived' }),
    ).toHaveAttribute('href', `/songs?state=${ARCHIVED.id}`);

    const without = section('Without a Selected Generation');
    expect(within(without).getByTestId('without-selection-count')).toHaveTextContent(
      '1 active Song has Generations but no Selected Generation.',
    );
    expect(within(without).getByRole('link', { name: /^Show in Songs/ })).toHaveAttribute(
      'href',
      '/songs?archived=active&selected=no&generations=some',
    );
    expect(
      within(without)
        .getAllByTestId('dashboard-song')
        .map((row) => row.dataset.song),
    ).toEqual(['n8-1']);
    expect(screen.queryByTestId('dashboard-welcome')).not.toBeInTheDocument();
  });

  it('opens the Songs table filtered to a state from its count', async () => {
    server(ok(dashboard()));
    const user = userEvent.setup();

    const { router } = renderApp('/');
    await user.click(await screen.findByRole('link', { name: '2 Songs in Writing' }));

    expect(await screen.findByRole('heading', { level: 2, name: 'Songs' })).toBeVisible();
    expect(router.state.location.pathname).toBe('/songs');
    expect(router.state.location.search).toBe(`?state=${WRITING.id}`);
  });

  it('shows each section loading while the dashboard is read', async () => {
    server();

    renderApp('/');

    expect(await screen.findByRole('heading', { level: 2, name: 'Dashboard' })).toBeVisible();
    expect(screen.getByLabelText('Loading Recently edited')).toBeInTheDocument();
    expect(screen.getByLabelText('Loading By workflow state')).toBeInTheDocument();
    expect(screen.getByLabelText('Loading Without a Selected Generation')).toBeInTheDocument();
  });

  it('shows each section empty in its own words', async () => {
    server(
      ok(
        dashboard({
          recentlyEdited: { data: { songs: [], total: 0 } },
          workflowStates: { data: { states: [{ ...ARCHIVED, hidden: false, songCount: 3 }] } },
          withoutSelection: { data: { count: 0, songs: [] } },
        }),
      ),
    );

    renderApp('/');

    expect(
      await within(await waitFor(() => section('Recently edited'))).findByText(
        'No active Songs to show. Archived Songs are left out.',
      ),
    ).toBeVisible();
    expect(
      within(section('Without a Selected Generation')).getByText(
        'Every active Song with Generations has a Selected Generation.',
      ),
    ).toBeVisible();
    expect(
      within(section('Without a Selected Generation')).queryByRole('link'),
    ).not.toBeInTheDocument();
    // Archived Songs are still Songs: no welcome.
    expect(screen.queryByTestId('dashboard-welcome')).not.toBeInTheDocument();
  });

  it('shows a failed section with Retry while the others show their data, and Retry reads again', async () => {
    const reads = server(ok(dashboard({ workflowStates: FAILED })), ok(dashboard()));
    const user = userEvent.setup();

    renderApp('/');

    const states = await waitFor(() => section('By workflow state'));
    expect(await within(states).findByText('By workflow state could not be loaded.')).toBeVisible();
    expect(within(section('Recently edited')).getAllByTestId('dashboard-song')).toHaveLength(2);
    expect(
      within(section('Without a Selected Generation')).getByTestId('without-selection-count'),
    ).toBeVisible();

    await user.click(within(states).getByRole('button', { name: 'Retry By workflow state' }));
    expect(await within(states).findAllByTestId('state-count')).toHaveLength(3);
    expect(reads).toHaveLength(2);
    // The others kept their rows throughout.
    expect(within(section('Recently edited')).getAllByTestId('dashboard-song')).toHaveLength(2);
  });

  it('shows every section failed when the dashboard cannot be read at all', async () => {
    server(() => Promise.resolve(jsonResponse(500, { code: 'section_failed' })));

    renderApp('/');

    for (const name of ['Recently edited', 'By workflow state', 'Without a Selected Generation']) {
      const region = await waitFor(() => section(name));
      expect(await within(region).findByText(`${name} could not be loaded.`)).toBeVisible();
      expect(within(region).getByRole('button', { name: `Retry ${name}` })).toBeVisible();
    }
  });

  it('welcomes a new, empty instance with New Song in place of the sections', async () => {
    server(
      ok(
        dashboard({
          recentlyEdited: { data: { songs: [], total: 0 } },
          workflowStates: {
            data: {
              states: [
                { ...WRITING, hidden: false, songCount: 0 },
                { ...ARCHIVED, hidden: false, songCount: 0 },
              ],
            },
          },
          withoutSelection: { data: { count: 0, songs: [] } },
        }),
      ),
    );
    const user = userEvent.setup();

    renderApp('/');

    const welcome = await screen.findByTestId('dashboard-welcome');
    expect(within(welcome).getByRole('heading', { name: 'Welcome to n8Tracks' })).toBeVisible();
    expect(screen.queryByRole('region', { name: 'Recently edited' })).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'By workflow state' })).not.toBeInTheDocument();

    await user.click(within(welcome).getByRole('button', { name: 'New Song' }));
    expect(await screen.findByRole('dialog', { name: 'New Song' })).toBeInTheDocument();
  });

  it('reads again when the window regains focus, at most every 30 seconds, keeping what it shows', async () => {
    let now = 1_000_000;
    vi.spyOn(Date, 'now').mockImplementation(() => now);
    const reads = server(
      ok(dashboard()),
      ok(
        dashboard({
          recentlyEdited: { data: { songs: [song(3), song(2), song(1)], total: 3 } },
        }),
      ),
      () => Promise.resolve(jsonResponse(500, { code: 'section_failed' })),
    );

    renderApp('/');
    const recent = await waitFor(() => section('Recently edited'));
    expect(await within(recent).findAllByTestId('dashboard-song')).toHaveLength(2);

    // Too soon: nothing is read.
    now += DASHBOARD_REFRESH_MS - 1;
    act(() => {
      fireEvent.focus(window);
    });
    expect(reads).toHaveLength(1);

    now += 1;
    act(() => {
      fireEvent.focus(window);
    });
    await waitFor(() => {
      expect(within(recent).getAllByTestId('dashboard-song')).toHaveLength(3);
    });
    expect(reads).toHaveLength(2);

    // A refresh that fails keeps what was shown, with a small notice.
    now += DASHBOARD_REFRESH_MS;
    act(() => {
      fireEvent.focus(window);
    });
    expect(await screen.findByTestId('dashboard-stale')).toHaveTextContent(
      'The dashboard could not be refreshed, so it shows what was read before.',
    );
    expect(reads).toHaveLength(3);
    expect(within(recent).getAllByTestId('dashboard-song')).toHaveLength(3);
  });
});
