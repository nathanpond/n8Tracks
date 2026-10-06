import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { Song } from '../api/songs';
import {
  healthyReport,
  jsonResponse,
  neverAnswers,
  renderApp,
  requestPath,
  stubFetch,
} from '../test/helpers';

const WRITING_ID = '01a10a6e-dc81-7001-8000-000000000002';

const song: Song = {
  id: '0199b1a0-0000-7000-8000-000000000007',
  shortcode: 'n8-7',
  title: 'Running in a Pack',
  concept: 'Fast and loud.\nWith a quiet bridge.',
  state: { id: WRITING_ID, name: 'Writing', colour: 'blue' },
  currentVersion: {
    id: '0199b1a0-0000-7000-9000-000000000007',
    number: '1',
    shortcode: 'n8-7-v1',
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
};

/** The query string a request was made with, whatever form `fetch` was given it in. */
function searchOf(input: RequestInfo | URL): string {
  const url = input instanceof Request ? input.url : typeof input === 'string' ? input : input.href;
  return new URL(url, document.baseURI).search;
}

/** Answers the health report, the states, the list (with `song`), and `song` by its shortcode. */
function backend(answer: (reference: string) => Response) {
  const mock = stubFetch();
  mock.mockImplementation((input) => {
    const path = requestPath(input);
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (path.endsWith('/api/v1/workflow-states')) {
      return Promise.resolve(jsonResponse(200, { items: [] }));
    }
    if (path.endsWith('/api/v1/songs')) {
      return Promise.resolve(jsonResponse(200, { items: [song], page: 1, pageSize: 50, total: 1 }));
    }
    const match = /\/api\/v1\/songs\/([^/]+)$/.exec(path);
    return Promise.resolve(
      match?.[1] === undefined
        ? jsonResponse(404, { code: 'not_found' })
        : answer(decodeURIComponent(match[1])),
    );
  });
  return mock;
}

describe('the Song page', () => {
  it('shows the shortcode, title, concept, and state', async () => {
    backend((reference) =>
      reference === 'n8-7' ? jsonResponse(200, song) : jsonResponse(404, { code: 'not_found' }),
    );

    renderApp('/songs/n8-7');

    expect(
      await screen.findByRole('heading', { level: 2, name: 'Running in a Pack' }),
    ).toBeVisible();
    expect(screen.getByTestId('shortcode')).toHaveTextContent('n8-7');
    expect(screen.getByText(/^Fast and loud\./)).toHaveTextContent(
      'Fast and loud. With a quiet bridge.',
    );
    expect(screen.getByText('Writing').closest('[data-state-colour]')).toHaveAttribute(
      'data-state-colour',
      'blue',
    );
    // The page is Songs' own: the sidebar marks Songs.
    expect(
      screen.getByRole('navigation', { name: 'Main' }).querySelector('[aria-current="page"]'),
    ).toHaveTextContent('Songs');
  });

  it('says when a Song has no concept', async () => {
    backend(() => jsonResponse(200, { ...song, concept: null }));

    renderApp('/songs/n8-7');

    expect(await screen.findByText('No concept yet.')).toBeVisible();
  });

  it('shows a loading indicator until the Song answers', async () => {
    const mock = stubFetch();
    mock.mockImplementation(neverAnswers);

    renderApp('/songs/n8-7');

    expect(await screen.findByLabelText('Loading the Song')).toBeInTheDocument();
  });

  it('says when there is no such Song', async () => {
    backend(() => jsonResponse(404, { code: 'not_found' }));

    renderApp('/songs/n8-99');

    expect(await screen.findByRole('heading', { level: 2, name: 'Song not found' })).toBeVisible();
    expect(
      screen.getByText('There is no Song n8-99. It may have a different shortcode.'),
    ).toBeVisible();
  });

  it('says the Song could not be loaded, and tries again', async () => {
    let fail = true;
    backend(() => (fail ? jsonResponse(500, { code: 'internal_error' }) : jsonResponse(200, song)));
    const user = userEvent.setup();

    renderApp('/songs/n8-7');

    expect(await screen.findByText('The Song could not be loaded')).toBeVisible();
    fail = false;
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(
      await screen.findByRole('heading', { level: 2, name: 'Running in a Pack' }),
    ).toBeVisible();
  });

  it('goes back to the Songs view it was opened from', async () => {
    const mock = backend(() => jsonResponse(200, song));
    const user = userEvent.setup();

    renderApp(`/songs?sort=title&state=${WRITING_ID}`);
    await user.click(await screen.findByRole('link', { name: 'Running in a Pack' }));
    await screen.findByRole('heading', { level: 2, name: 'Running in a Pack' });
    await user.click(screen.getByRole('link', { name: '← Songs' }));

    await screen.findByRole('table', { name: 'Songs' });
    await waitFor(() => {
      const lists = mock.mock.calls
        .filter(([input]) => requestPath(input).endsWith('/api/v1/songs'))
        .map(([input]) => searchOf(input));
      expect(lists).toEqual([`?sort=title&state=${WRITING_ID}`, `?sort=title&state=${WRITING_ID}`]);
    });
  });
});
