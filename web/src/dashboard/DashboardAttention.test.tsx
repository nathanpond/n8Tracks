import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Dashboard, SunoProblem } from '../api/dashboard';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';

const WRITING = { id: '01a10a6e-dc81-7001-8000-000000000002', name: 'Writing', colour: 'blue' };
const EXPORT = '0199c0de-0000-7000-8000-000000000001';
const SYNC = '0199c0de-0000-7000-8000-000000000002';
const REQUEST = '0199c0de-0000-7000-8000-000000000003';

function problem(change: Partial<SunoProblem>): SunoProblem {
  return {
    kind: 'unavailableWorkspace',
    subject: 'ws-1',
    occurredAt: null,
    reason: null,
    step: null,
    message: null,
    workspaceName: 'Demos',
    songCount: 2,
    versionShortcode: null,
    songShortcode: null,
    versionNumber: null,
    dismissible: false,
    ...change,
  };
}

const FAILED_SYNC = problem({
  kind: 'failedSync',
  subject: SYNC,
  occurredAt: '2026-10-08T09:00:00Z',
  reason: 'failed',
  step: 'Read the library',
  workspaceName: null,
  songCount: null,
  dismissible: true,
});

const FAILED_GENERATE = problem({
  kind: 'failedGenerate',
  subject: REQUEST,
  occurredAt: '2026-10-08T08:00:00Z',
  reason: 'stopped',
  step: 'Fill the form',
  message: 'The Create page did not load.',
  workspaceName: null,
  songCount: null,
  versionShortcode: 'n8-12-v2',
  songShortcode: 'n8-12',
  versionNumber: '2',
  dismissible: true,
});

function dashboard(change: Partial<Dashboard> = {}): Dashboard {
  return {
    recentlyEdited: {
      data: {
        songs: [
          {
            id: '0199b1a0-0000-7000-8000-000000000001',
            shortcode: 'n8-1',
            title: 'Song 1',
            state: WRITING,
            updatedAt: '2026-10-08T09:00:00Z',
          },
        ],
        total: 1,
      },
    },
    workflowStates: { data: { states: [{ ...WRITING, hidden: false, songCount: 1 }] } },
    withoutSelection: { data: { count: 0, songs: [] } },
    unmatchedFiles: { data: { count: 0, mediaUnavailable: false } },
    sunoReviews: { data: { count: 0, exports: [] } },
    sunoProblems: { data: { count: 0, problems: [] } },
    ...change,
  };
}

/** A fake n8Tracks answering the dashboard with each of `answers` in turn, and dismissals with `dismissal`. */
function server(answers: Dashboard[], dismissal = 204) {
  const dismissed: unknown[] = [];
  let reads = 0;
  stubFetch().mockImplementation((input, init) => {
    const path = requestPath(input);
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (path.endsWith('/api/v1/dashboard')) {
      const answer = answers[Math.min(reads, answers.length - 1)];
      reads += 1;
      return Promise.resolve(jsonResponse(200, answer));
    }
    if (path.endsWith('/api/v1/attention/dismissals') && init?.method === 'POST') {
      dismissed.push(JSON.parse(typeof init.body === 'string' ? init.body : 'null'));
      return Promise.resolve(
        dismissal === 204 ? new Response(null, { status: 204 }) : jsonResponse(dismissal, {}),
      );
    }
    return Promise.resolve(jsonResponse(404, { code: 'not_found' }));
  });
  return { dismissed, reads: () => reads };
}

/** The `index`th of `elements`, which must be there. */
function nth(elements: HTMLElement[], index: number): HTMLElement {
  const element = elements[index];
  if (element === undefined) {
    throw new Error(`There is no element ${String(index)}.`);
  }
  return element;
}

function section(name: string) {
  return screen.getByRole('region', { name });
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe('what needs attention on the dashboard (#229)', () => {
  it('shows the unmatched files, the export waiting for review, and the Suno problems, each linking to where it is resolved', async () => {
    server([
      dashboard({
        unmatchedFiles: { data: { count: 2, mediaUnavailable: false } },
        sunoReviews: {
          data: {
            count: 1,
            exports: [
              {
                exportId: EXPORT,
                arrivedAt: '2026-10-08T09:30:00Z',
                recordCount: 12,
                changedCount: 3,
                conflictCount: 1,
              },
            ],
          },
        },
        sunoProblems: {
          data: { count: 3, problems: [FAILED_SYNC, FAILED_GENERATE, problem({})] },
        },
      }),
    ]);

    renderApp('/');

    const unmatched = await screen.findByRole('region', { name: 'Unmatched Files' });
    expect(
      await within(unmatched).findByRole('link', {
        name: '2 audio files unmatched: open Unmatched Files',
      }),
    ).toHaveAttribute('href', '/library/unmatched');

    const reviews = section('Suno reviews');
    const review = within(reviews).getByTestId('suno-review');
    expect(review).toHaveAttribute('data-export', EXPORT);
    expect(within(review).getByRole('link', { name: 'Review the import' })).toHaveAttribute(
      'href',
      `/suno/imports/${EXPORT}`,
    );
    expect(within(review).getByTestId('suno-review-counts')).toHaveTextContent(
      '12 records; 3 changed and 1 Conflict to resolve',
    );

    const problems = within(section('Suno problems')).getAllByTestId('suno-problem');
    expect(problems.map((row) => row.getAttribute('data-kind'))).toEqual([
      'failedSync',
      'failedGenerate',
      'unavailableWorkspace',
    ]);
    expect(problems[0]).toHaveTextContent(
      'Your last Suno sync failed at the step “Read the library”.',
    );
    expect(
      within(nth(problems, 0)).getByRole('link', { name: 'Open Suno import' }),
    ).toHaveAttribute('href', '/suno/imports');
    expect(problems[1]).toHaveTextContent(
      'Generate on Suno for n8-12-v2 stopped at “Fill the form”: The Create page did not load.',
    );
    expect(within(nth(problems, 1)).getByRole('link', { name: 'Open n8-12-v2' })).toHaveAttribute(
      'href',
      '/songs/n8-12/v/2',
    );
    expect(problems[2]).toHaveTextContent(
      'The Suno workspace Demos is unavailable, and 2 Songs are in it.',
    );
    expect(
      within(nth(problems, 2)).getByRole('link', { name: 'Open the workspace' }),
    ).toHaveAttribute('href', '/settings/suno-workspaces/ws-1');
    // A standing state cannot be dismissed; a failure can.
    expect(within(nth(problems, 2)).queryByRole('button', { name: /^Dismiss/ })).toBeNull();
    expect(within(nth(problems, 0)).getByRole('button', { name: /^Dismiss/ })).toBeVisible();
  });

  it('says in one line that a section has nothing to report, and keeps the section', async () => {
    server([dashboard()]);

    renderApp('/');

    expect(
      await within(await screen.findByRole('region', { name: 'Unmatched Files' })).findByText(
        'No audio files are waiting to be placed.',
      ),
    ).toBeVisible();
    expect(
      within(section('Suno reviews')).getByText('No Suno import is waiting for review.'),
    ).toBeVisible();
    expect(within(section('Suno problems')).getByText('No Suno problems to report.')).toBeVisible();
  });

  it('says the media folder is unavailable instead of counting', async () => {
    server([dashboard({ unmatchedFiles: { data: { count: 4, mediaUnavailable: true } } })]);

    renderApp('/');

    const unmatched = await screen.findByRole('region', { name: 'Unmatched Files' });
    expect(await within(unmatched).findByTestId('media-unavailable')).toHaveTextContent(
      'The media folder is unavailable',
    );
    expect(within(unmatched).queryByTestId('unmatched-count')).toBeNull();
    expect(within(unmatched).getByRole('link', { name: 'Open Unmatched Files' })).toBeVisible();
  });

  it('lists five and links the rest as "n more"', async () => {
    const workspaces = [1, 2, 3, 4, 5].map((n) =>
      problem({ subject: `ws-${String(n)}`, workspaceName: `Workspace ${String(n)}` }),
    );
    server([dashboard({ sunoProblems: { data: { count: 7, problems: workspaces } } })]);

    renderApp('/');

    const problems = await screen.findByRole('region', { name: 'Suno problems' });
    expect(await within(problems).findAllByTestId('suno-problem')).toHaveLength(5);
    expect(within(problems).getByTestId('attention-more')).toHaveTextContent('2 more');
    expect(within(problems).getByTestId('attention-more')).toHaveAttribute(
      'href',
      '/settings/suno-workspaces',
    );
  });

  it('shows a section that could not be read as failed, and the others still', async () => {
    server([dashboard({ sunoReviews: { error: { code: 'section_failed' } } })]);

    renderApp('/');

    const reviews = await screen.findByRole('region', { name: 'Suno reviews' });
    expect(await within(reviews).findByText('Suno reviews could not be loaded.')).toBeVisible();
    expect(within(reviews).getByRole('button', { name: 'Retry Suno reviews' })).toBeVisible();
    expect(
      within(section('Unmatched Files')).getByText('No audio files are waiting to be placed.'),
    ).toBeVisible();
  });

  it('dismisses a failure, which then leaves the section', async () => {
    const fake = server([
      dashboard({ sunoProblems: { data: { count: 1, problems: [FAILED_SYNC] } } }),
      dashboard(),
    ]);
    const user = userEvent.setup();

    renderApp('/');

    const problems = await screen.findByRole('region', { name: 'Suno problems' });
    await user.click(await within(problems).findByRole('button', { name: /^Dismiss/ }));

    await waitFor(() => {
      expect(
        within(section('Suno problems')).getByText('No Suno problems to report.'),
      ).toBeVisible();
    });
    expect(fake.dismissed).toEqual([{ kind: 'failedSync', subject: SYNC }]);
    expect(fake.reads()).toBe(2);
  });

  it('keeps a failure it could not dismiss, and says so', async () => {
    server([dashboard({ sunoProblems: { data: { count: 1, problems: [FAILED_SYNC] } } })], 500);
    const user = userEvent.setup();

    renderApp('/');

    const problems = await screen.findByRole('region', { name: 'Suno problems' });
    await user.click(await within(problems).findByRole('button', { name: /^Dismiss/ }));

    expect(await within(problems).findByRole('alert')).toHaveTextContent(
      'It could not be dismissed. Try again.',
    );
    expect(within(problems).getAllByTestId('suno-problem')).toHaveLength(1);
  });

  it('shows what needs attention under the welcome of an instance with no Songs', async () => {
    server([
      dashboard({
        recentlyEdited: { data: { songs: [], total: 0 } },
        workflowStates: { data: { states: [{ ...WRITING, hidden: false, songCount: 0 }] } },
        unmatchedFiles: { data: { count: 1, mediaUnavailable: false } },
      }),
    ]);

    renderApp('/');

    expect(await screen.findByTestId('dashboard-welcome')).toBeVisible();
    expect(
      within(section('Unmatched Files')).getByRole('link', {
        name: '1 audio file unmatched: open Unmatched Files',
      }),
    ).toBeVisible();
    expect(screen.queryByRole('region', { name: 'Recently edited' })).toBeNull();
  });
});
