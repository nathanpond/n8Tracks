import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { MatchSuggestion, UnmatchedFile } from '../api/audioFiles';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';

function suggestion(change: Partial<MatchSuggestion> = {}): MatchSuggestion {
  return {
    song: { id: '0192f1a4-0000-7000-8000-0000000000b1', shortcode: 'n8-1', title: 'My Song Title' },
    generation: null,
    score: 100,
    reasons: [{ code: 'title_equals_file_name' }],
    ...change,
  };
}

function file(change: Partial<UnmatchedFile> = {}): UnmatchedFile {
  return {
    id: '0192f1a4-0000-7000-8000-0000000000a1',
    path: 'My Song Title.mp3',
    fileName: 'My Song Title.mp3',
    format: 'mp3',
    sizeBytes: 2048,
    firstSeenAt: '2026-10-08T10:00:00Z',
    status: 'available',
    durationSeconds: 187.4,
    song: null,
    generation: null,
    associationOrigin: null,
    unmatchedReason: null,
    revision: 1,
    autoMatchBlocked: false,
    isPreferred: false,
    suggestions: [suggestion()],
    ...change,
  };
}

/** A fake n8Tracks answering the audio file list with `server.page`; `server.queries` keeps every list query asked. */
function unmatchedServer(items: UnmatchedFile[], total = items.length) {
  const server = {
    page: { items, total, offset: 0, limit: 100 },
    queries: [] as URLSearchParams[],
  };
  stubFetch().mockImplementation((input) => {
    const path = requestPath(input);
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (path.endsWith('/api/v1/audio-files')) {
      const url = input instanceof Request ? input.url : input.toString();
      server.queries.push(new URL(url, document.baseURI).searchParams);
      return Promise.resolve(jsonResponse(200, server.page));
    }
    return Promise.resolve(jsonResponse(404, { code: 'not_found' }));
  });
  return server;
}

async function openUnmatched(path = '/library/unmatched') {
  const rendered = renderApp(path);
  await screen.findByRole('heading', { level: 2, name: 'Unmatched Files' });
  return rendered;
}

const rows = () => screen.findAllByTestId('unmatched-file');

describe('Library → Unmatched Files', () => {
  it('lists each file with its folder, format, duration, size, status, first seen, and suggestions, asking for unassociated files with suggestions', async () => {
    const server = unmatchedServer([
      file(),
      file({
        id: '0192f1a4-0000-7000-8000-0000000000a2',
        path: 'Album/Disc 1/take 3.wav',
        fileName: 'take 3.wav',
        format: 'wav',
        durationSeconds: null,
        suggestions: [],
      }),
    ]);
    await openUnmatched();

    const [first, second] = await rows();
    if (first === undefined || second === undefined) {
      throw new Error('Two rows were expected.');
    }
    const cells = within(first).getAllByRole('cell');
    expect(within(first).getByRole('rowheader')).toHaveTextContent('My Song Title.mp3');
    expect(cells.map((cell) => cell.textContent).slice(0, 4)).toEqual([
      'Top level',
      'mp3',
      '3:07',
      '2 KB',
    ]);
    expect(within(first).getByTestId('file-status')).toHaveTextContent('Available');
    const list = within(first).getByRole('list', { name: 'Suggested Songs for My Song Title.mp3' });
    expect(within(list).getByRole('link', { name: 'My Song Title' })).toHaveAttribute(
      'href',
      '/go/n8-1',
    );
    expect(within(list).getByText('Title matches the file name')).toBeVisible();
    expect(within(second).getAllByRole('cell')[0]).toHaveTextContent('Album/Disc 1');
    expect(within(second).getByText('Unknown')).toBeVisible();
    expect(within(second).getByTestId('no-suggestions')).toHaveTextContent('No suggestions');
    expect(screen.getByTestId('unmatched-total')).toHaveTextContent('2 files');

    const query = server.queries[0];
    expect(query?.get('association')).toBe('none');
    expect(query?.get('include')).toBe('suggestions');
    expect(query?.get('sort')).toBe('firstSeen');
    expect(query?.get('direction')).toBe('desc');
    expect(query?.get('limit')).toBe('100');
    expect(query?.get('offset')).toBe('0');
  });

  it('writes every reason and names the suggested Generation', async () => {
    const generation = {
      id: '0192f1a4-0000-7000-8000-0000000000c2',
      shortcode: 'n8-1-v1-g2',
      sunoTitle: 'Night Drive',
      durationSeconds: 180.4,
    };
    unmatchedServer([
      file({
        path: 'Neon Fox/Night Drive/take.wav',
        fileName: 'take.wav',
        suggestions: [
          suggestion({
            song: { id: 'song-1', shortcode: 'n8-1', title: 'Night Drive' },
            generation,
            score: 70,
            reasons: [
              { code: 'folder_equals_title', folder: 'Night Drive' },
              { code: 'artist_present', artist: 'Neon Fox', artistSource: 'folder' },
              { code: 'duration_close', generation, differenceSeconds: 0.4 },
            ],
          }),
          suggestion({
            song: { id: 'song-2', shortcode: 'n8-2', title: 'Night' },
            score: 80,
            reasons: [
              { code: 'generation_title_equals_file_name', generation },
              { code: 'embedded_title_equals_title' },
              { code: 'title_in_file_name' },
              { code: 'something_new' },
            ],
          }),
        ],
      }),
    ]);
    await openUnmatched();
    const [row] = await rows();
    if (row === undefined) {
      throw new Error('A row was expected.');
    }

    const suggestions = within(row).getAllByTestId('suggestion');
    expect(suggestions.map((item) => item.getAttribute('data-song'))).toEqual(['n8-1', 'n8-2']);
    const [first, second] = suggestions;
    if (first === undefined || second === undefined) {
      throw new Error('Two suggestions were expected.');
    }
    expect(within(first).getByTestId('suggested-generation')).toHaveAttribute(
      'href',
      '/go/n8-1-v1-g2',
    );
    expect(
      within(within(first).getByTestId('suggestion-reasons'))
        .getAllByRole('listitem')
        .map((item) => item.textContent),
    ).toEqual([
      'Folder “Night Drive” matches the title',
      'Artist Neon Fox is in a folder name',
      'Duration within 2 seconds of Generation n8-1-v1-g2 (0.4 s apart)',
    ]);
    expect(within(second).queryByTestId('suggested-generation')).not.toBeInTheDocument();
    expect(
      within(within(second).getByTestId('suggestion-reasons'))
        .getAllByRole('listitem')
        .map((item) => item.textContent),
    ).toEqual([
      'Suno title of Generation n8-1-v1-g2 matches the file name',
      'Title matches the embedded title',
      'Title is in the file name',
      'something_new',
    ]);
  });

  it('marks a Missing file and says why a file whose Generation was deleted is unmatched', async () => {
    unmatchedServer([
      file({ status: 'missing' }),
      file({
        id: '0192f1a4-0000-7000-8000-0000000000a3',
        path: 'Night Drive (suno-0c90d621-e30c-4c76-814a-e1fdeb500582).mp3',
        fileName: 'Night Drive (suno-0c90d621-e30c-4c76-814a-e1fdeb500582).mp3',
        unmatchedReason: 'generation_deleted',
      }),
    ]);
    await openUnmatched();
    const [missing, deleted] = await rows();
    if (missing === undefined || deleted === undefined) {
      throw new Error('Two rows were expected.');
    }

    const status = within(missing).getByTestId('file-status');
    expect(status).toHaveTextContent('Missing');
    expect(status).toHaveAttribute('data-status', 'missing');
    expect(within(missing).queryByTestId('unmatched-reason')).not.toBeInTheDocument();
    expect(within(deleted).getByTestId('unmatched-reason')).toHaveTextContent(
      'Its Generation was deleted.',
    );
  });

  it('says there are no unmatched files, and that nothing matches a search', async () => {
    const server = unmatchedServer([]);
    await openUnmatched();
    expect(await screen.findByTestId('no-unmatched-files')).toHaveTextContent('No unmatched files');
    expect(screen.queryByRole('table')).not.toBeInTheDocument();

    const user = userEvent.setup();
    await user.type(screen.getByRole('textbox', { name: 'Search' }), 'zeta');
    await user.click(screen.getByRole('button', { name: 'Search' }));
    await waitFor(() => {
      expect(server.queries.at(-1)?.get('q')).toBe('zeta');
    });
    expect(await screen.findByText('No unmatched file matches.')).toBeVisible();
  });

  it('sorts by name and folder and filters by text, keeping both in the address', async () => {
    const server = unmatchedServer([file()]);
    const { router } = await openUnmatched();
    const user = userEvent.setup();

    await user.selectOptions(screen.getByRole('combobox', { name: 'Sort by' }), 'Name, A to Z');
    await waitFor(() => {
      expect(server.queries.at(-1)?.get('sort')).toBe('name');
    });
    expect(server.queries.at(-1)?.get('direction')).toBe('asc');
    expect(router.state.location.search).toBe('?sort=name');

    await user.selectOptions(screen.getByRole('combobox', { name: 'Sort by' }), 'Folder, Z to A');
    await waitFor(() => {
      expect(server.queries.at(-1)?.get('sort')).toBe('folder');
    });
    expect(server.queries.at(-1)?.get('direction')).toBe('desc');

    await user.type(screen.getByRole('textbox', { name: 'Search' }), 'Album/');
    await user.click(screen.getByRole('button', { name: 'Search' }));
    await waitFor(() => {
      expect(server.queries.at(-1)?.get('q')).toBe('Album/');
    });
    expect(server.queries.at(-1)?.get('sort')).toBe('folder');
    expect(router.state.location.search).toBe('?q=Album%2F&sort=folder&direction=desc');
  });

  it('reads the order, the search, and the page from the address and pages at 100', async () => {
    const server = unmatchedServer([file()], 250);
    await openUnmatched('/library/unmatched?sort=firstSeen&direction=asc&q=take&page=3');
    await rows();

    const query = server.queries[0];
    expect(query?.get('direction')).toBe('asc');
    expect(query?.get('q')).toBe('take');
    expect(query?.get('offset')).toBe('200');
    expect(screen.getByRole('navigation', { name: 'Pages' })).toBeVisible();
    expect(screen.getByRole('combobox', { name: 'Sort by' })).toHaveValue('firstSeen-asc');
  });

  it('offers no way to hide or dismiss a file: only to associate it', async () => {
    unmatchedServer([file()]);
    await openUnmatched();
    const [row] = await rows();
    if (row === undefined) {
      throw new Error('A row was expected.');
    }

    expect(
      within(row)
        .getAllByRole('button')
        .map((button) => button.textContent),
    ).toEqual(['Associate', 'Choose a Song…']);
    expect(screen.queryByRole('button', { name: /hide|dismiss|ignore/i })).not.toBeInTheDocument();
  });
});
