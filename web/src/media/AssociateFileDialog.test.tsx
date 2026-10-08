import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { MatchSuggestion, UnmatchedFile } from '../api/audioFiles';
import type { Generation } from '../api/generations';
import type { Song } from '../api/songs';
import { ARCHIVED_STATE_ID } from '../api/workflow';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';
import { testSong } from '../test/playlistServer';
import { testGeneration } from '../test/versionServer';

const FILE_ID = '0192f1a4-0000-7000-8000-0000000000a1';

const NIGHT = testSong(7, 'Night Drive');
const ARCHIVED = testSong(8, 'Night Drive (old)', {
  state: { id: ARCHIVED_STATE_ID, name: 'Archived', colour: 'gray' },
});
const TAKE_1 = testGeneration('1', 1, { title: 'Night Drive' });
const TAKE_2 = testGeneration('1', 2, { title: 'Night Drive', state: 'archived' });

function suggestion(change: Partial<MatchSuggestion> = {}): MatchSuggestion {
  return {
    song: { id: NIGHT.id, shortcode: NIGHT.shortcode, title: NIGHT.title },
    generation: null,
    score: 100,
    reasons: [{ code: 'title_equals_file_name' }],
    ...change,
  };
}

function file(change: Partial<UnmatchedFile> = {}): UnmatchedFile {
  return {
    id: FILE_ID,
    path: 'Night Drive.mp3',
    fileName: 'Night Drive.mp3',
    format: 'mp3',
    sizeBytes: 2048,
    firstSeenAt: '2026-10-08T10:00:00Z',
    status: 'available',
    durationSeconds: 125,
    song: null,
    generation: null,
    associationOrigin: null,
    unmatchedReason: null,
    revision: 3,
    autoMatchBlocked: false,
    isPreferred: false,
    suggestions: [suggestion()],
    ...change,
  };
}

const associated = (change: Partial<UnmatchedFile> = {}) =>
  file({
    song: { id: NIGHT.id, shortcode: NIGHT.shortcode, title: NIGHT.title },
    generation: { id: TAKE_1.id, shortcode: TAKE_1.shortcode },
    associationOrigin: 'suno-id',
    suggestions: [],
    ...change,
  });

interface Write {
  method: string;
  path: string;
  ifMatch: string | null;
  body: Record<string, unknown> | undefined;
}

/**
 * A fake n8Tracks: the audio file list answers `server.files` filtered by association; writes are
 * kept in `server.writes` and answered by `server.answer` (by default: the file associated as asked,
 * no content for a removal, the file unchanged for a match again). Songs are searched in
 * `server.songs`, and every Song has `server.generations`.
 */
function associationServer(files: UnmatchedFile[]) {
  const server = {
    files,
    songs: [NIGHT, ARCHIVED] as Song[],
    generations: [TAKE_1, TAKE_2] as Generation[],
    queries: [] as URLSearchParams[],
    songQueries: [] as URLSearchParams[],
    writes: [] as Write[],
    answer: undefined as ((write: Write) => Response) | undefined,
  };
  const answer = (input: RequestInfo | URL, init?: RequestInit): Response => {
    const path = requestPath(input);
    const url = new URL(input instanceof Request ? input.url : input.toString(), document.baseURI);
    const method = init?.method ?? 'GET';
    if (path.endsWith('/health')) {
      return jsonResponse(200, healthyReport);
    }
    if (path.endsWith('/api/v1/audio-files')) {
      server.queries.push(url.searchParams);
      const association = url.searchParams.get('association');
      const items = server.files.filter((item) =>
        association === 'none'
          ? item.song === null
          : association === 'associated'
            ? item.song !== null
            : true,
      );
      return jsonResponse(200, { items, total: items.length, offset: 0, limit: 100 });
    }
    if (/\/api\/v1\/audio-files\/[^/]+\/(association|rematch)$/.test(path)) {
      const headers = new Headers(init?.headers);
      const write: Write = {
        method,
        path: path.slice(path.indexOf('/api/v1/')),
        ifMatch: headers.get('If-Match'),
        body:
          typeof init?.body === 'string'
            ? (JSON.parse(init.body) as Record<string, unknown>)
            : undefined,
      };
      server.writes.push(write);
      if (server.answer !== undefined) {
        return server.answer(write);
      }
      const current = server.files.find((item) => path.includes(item.id)) ?? file();
      if (method === 'DELETE') {
        server.files = server.files.map((item) =>
          item.id === current.id
            ? {
                ...item,
                song: null,
                generation: null,
                associationOrigin: null,
                unmatchedReason: 'unassociated_by_user',
                revision: item.revision + 1,
              }
            : item,
        );
        return new Response(null, { status: 204 });
      }
      if (method === 'PUT') {
        const song = server.songs.find((candidate) => candidate.id === write.body?.song) ?? NIGHT;
        const generation = server.generations.find(
          (candidate) => candidate.id === write.body?.generation,
        );
        const changed: UnmatchedFile = {
          ...current,
          song: { id: song.id, shortcode: song.shortcode, title: song.title },
          generation:
            generation === undefined
              ? null
              : { id: generation.id, shortcode: generation.shortcode },
          associationOrigin: 'user',
          unmatchedReason: null,
          revision: current.revision + 1,
          suggestions: [],
        };
        server.files = server.files.map((item) => (item.id === current.id ? changed : item));
        return jsonResponse(200, changed);
      }
      return jsonResponse(200, { ...current, autoMatchBlocked: false, unmatchedReason: null });
    }
    if (path.endsWith('/api/v1/songs')) {
      server.songQueries.push(url.searchParams);
      const text = (url.searchParams.get('q') ?? '').toLowerCase();
      const items = server.songs.filter((song) => song.title.toLowerCase().includes(text));
      return jsonResponse(200, { items, page: 1, pageSize: 20, total: items.length });
    }
    if (/\/api\/v1\/songs\/[^/]+\/generations$/.test(path)) {
      return jsonResponse(200, { items: server.generations });
    }
    return jsonResponse(404, { code: 'not_found' });
  };
  stubFetch().mockImplementation((input, init) => Promise.resolve(answer(input, init)));
  return server;
}

async function openUnmatched(path = '/library/unmatched') {
  const rendered = renderApp(path);
  await screen.findByRole('heading', { level: 2, name: 'Unmatched Files' });
  return rendered;
}

async function onlyRow() {
  const [row] = await screen.findAllByTestId('unmatched-file');
  if (row === undefined) {
    throw new Error('A row was expected.');
  }
  return row;
}

/** Finds a Song in the dialog's search and chooses it. */
async function chooseSong(user: ReturnType<typeof userEvent.setup>, title: string) {
  const dialog = screen.getByRole('dialog');
  await user.type(within(dialog).getByRole('textbox', { name: 'Song' }), 'night');
  await user.click(await within(dialog).findByRole('option', { name: new RegExp(title) }));
  return dialog;
}

describe('Associating unmatched files', () => {
  it('accepts a suggestion in one action and reads the list again, so the file leaves it', async () => {
    const server = associationServer([file()]);
    await openUnmatched();
    const row = await onlyRow();
    const user = userEvent.setup();

    await user.click(
      within(row).getByRole('button', {
        name: 'Associate Night Drive.mp3 with Night Drive (n8-7)',
      }),
    );

    await waitFor(() => {
      expect(screen.getByTestId('no-unmatched-files')).toHaveTextContent('No unmatched files');
    });
    expect(server.writes).toEqual([
      {
        method: 'PUT',
        path: `/api/v1/audio-files/${FILE_ID}/association`,
        ifMatch: '"3"',
        body: { song: NIGHT.id, generation: null },
      },
    ]);
    expect(screen.getByTestId('association-announcement')).toHaveTextContent(
      'Associated Night Drive.mp3 with Night Drive (n8-7).',
    );
  });

  it('associates with the Generation a suggestion names', async () => {
    const server = associationServer([
      file({
        suggestions: [
          suggestion({
            generation: {
              id: TAKE_1.id,
              shortcode: TAKE_1.shortcode,
              sunoTitle: 'Night Drive',
              durationSeconds: 125,
            },
          }),
        ],
      }),
    ]);
    await openUnmatched();
    const row = await onlyRow();
    const user = userEvent.setup();

    await user.click(
      within(row).getByRole('button', {
        name: `Associate Night Drive.mp3 with Night Drive (n8-7), Generation ${TAKE_1.shortcode}`,
      }),
    );
    await waitFor(() => {
      expect(server.writes).toHaveLength(1);
    });
    expect(server.writes[0]?.body).toEqual({ song: NIGHT.id, generation: TAKE_1.id });
  });

  it('chooses any Song in the dialog, Archived ones marked, then one of its Generations, a Generation being preferred', async () => {
    const server = associationServer([file({ suggestions: [] })]);
    await openUnmatched();
    const row = await onlyRow();
    const user = userEvent.setup();

    await user.click(
      within(row).getByRole('button', { name: 'Choose a Song for Night Drive.mp3' }),
    );
    const dialog = await screen.findByRole('dialog', { name: 'Associate file' });
    expect(within(dialog).queryByTestId('current-association')).not.toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Associate' })).toBeDisabled();

    await user.type(within(dialog).getByRole('textbox', { name: 'Song' }), 'night');
    const archived = await within(dialog).findByRole('option', { name: /Night Drive \(old\)/ });
    expect(archived).toHaveTextContent('Archived');
    expect(server.songQueries.at(-1)?.get('pageSize')).toBe('20');
    await user.click(within(dialog).getByRole('option', { name: /^n8-7/ }));

    const choice = await within(dialog).findByRole('radiogroup', { name: 'Generation' });
    expect(within(dialog).getByText(/a Generation is preferred/)).toBeVisible();
    expect(within(choice).getByRole('radio', { name: 'None: the Song only' })).toBeChecked();
    expect(within(choice).getByText(/Archived/)).toBeVisible();
    await user.click(within(choice).getByRole('radio', { name: TAKE_1.shortcode }));
    await user.click(within(dialog).getByRole('button', { name: 'Associate' }));

    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    expect(server.writes.map((write) => [write.method, write.ifMatch, write.body])).toEqual([
      ['PUT', '"3"', { song: NIGHT.id, generation: TAKE_1.id }],
    ]);
    expect(screen.getByTestId('association-announcement')).toHaveTextContent(
      `Associated Night Drive.mp3 with Night Drive (n8-7), Generation ${TAKE_1.shortcode}.`,
    );
    expect(await screen.findByTestId('no-unmatched-files')).toBeVisible();
  });

  it('associates with the Song alone when no Generation is chosen', async () => {
    const server = associationServer([file({ suggestions: [] })]);
    await openUnmatched();
    const user = userEvent.setup();
    await user.click(
      within(await onlyRow()).getByRole('button', { name: 'Choose a Song for Night Drive.mp3' }),
    );
    await screen.findByRole('dialog');
    const dialog = await chooseSong(user, '^n8-7');
    await within(dialog).findByRole('radiogroup', { name: 'Generation' });
    await user.click(within(dialog).getByRole('button', { name: 'Associate' }));

    await waitFor(() => {
      expect(server.writes).toHaveLength(1);
    });
    expect(server.writes[0]?.body).toEqual({ song: NIGHT.id, generation: null });
  });

  it('lists the associated files under Show, names the current association, and removes it', async () => {
    const server = associationServer([associated()]);
    const { router } = await openUnmatched();
    const user = userEvent.setup();
    expect(await screen.findByTestId('no-unmatched-files')).toHaveTextContent('No unmatched files');

    await user.selectOptions(screen.getByRole('combobox', { name: 'Show' }), 'Associated');
    await waitFor(() => {
      expect(server.queries.at(-1)?.get('association')).toBe('associated');
    });
    expect(router.state.location.search).toBe('?show=associated');
    const row = await onlyRow();
    expect(within(row).getByTestId('file-association')).toHaveTextContent(
      `Night Drive (n8-7), Generation ${TAKE_1.shortcode}, matched by its Suno ID`,
    );

    await user.click(
      within(row).getByRole('button', {
        name: 'Change or remove the association of Night Drive.mp3',
      }),
    );
    const dialog = await screen.findByRole('dialog', { name: 'Change association' });
    expect(within(dialog).getByTestId('current-association')).toHaveTextContent(
      `It is associated with Night Drive (n8-7), Generation ${TAKE_1.shortcode}, matched by its Suno ID. Choosing another Song or Generation replaces that association.`,
    );
    await user.click(within(dialog).getByRole('button', { name: 'Remove association' }));

    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    expect(server.writes).toEqual([
      {
        method: 'DELETE',
        path: `/api/v1/audio-files/${FILE_ID}/association`,
        ifMatch: '"3"',
        body: undefined,
      },
    ]);
    expect(screen.getByTestId('association-announcement')).toHaveTextContent(
      'Removed the association of Night Drive.mp3. It is back in Unmatched Files.',
    );
    expect(await screen.findByTestId('no-unmatched-files')).toHaveTextContent(
      'No associated files',
    );

    // Under All it is listed again, unmatched, unassociated by the user.
    await user.selectOptions(screen.getByRole('combobox', { name: 'Show' }), 'All');
    const back = await onlyRow();
    expect(within(back).getByTestId('unmatched-reason')).toHaveTextContent(
      'You removed its association.',
    );
  });

  it('changes an association to another Song after naming the current one', async () => {
    const server = associationServer([associated({ associationOrigin: 'user' })]);
    await openUnmatched('/library/unmatched?show=all');
    const user = userEvent.setup();
    await user.click(
      within(await onlyRow()).getByRole('button', {
        name: 'Change or remove the association of Night Drive.mp3',
      }),
    );
    const dialog = await screen.findByRole('dialog', { name: 'Change association' });
    expect(within(dialog).getByTestId('current-association')).toHaveTextContent('by you');
    await chooseSong(user, 'Night Drive \\(old\\)');
    expect(within(dialog).getByTestId('chosen-song')).toHaveTextContent('Archived');
    await within(dialog).findByRole('radiogroup', { name: 'Generation' });
    await user.click(within(dialog).getByRole('button', { name: 'Associate' }));

    await waitFor(() => {
      expect(server.writes).toHaveLength(1);
    });
    expect(server.writes[0]?.body).toEqual({ song: ARCHIVED.id, generation: null });
  });

  it('shows the file as it is now when it changed since the list was loaded', async () => {
    const server = associationServer([associated()]);
    server.answer = () =>
      jsonResponse(409, { code: 'revision_conflict', current: associated({ revision: 4 }) });
    await openUnmatched('/library/unmatched?show=associated');
    const user = userEvent.setup();
    await user.click(
      within(await onlyRow()).getByRole('button', {
        name: 'Change or remove the association of Night Drive.mp3',
      }),
    );
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Remove association' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'This file changed since the list was loaded.',
    );

    // Trying again sends the revision it was found at.
    server.answer = undefined;
    await user.click(within(dialog).getByRole('button', { name: 'Remove association' }));
    await waitFor(() => {
      expect(server.writes.map((write) => write.ifMatch)).toEqual(['"3"', '"4"']);
    });
  });

  it('says why an association was not saved', async () => {
    const server = associationServer([file()]);
    server.answer = () => jsonResponse(404, { code: 'not_found' });
    await openUnmatched();
    const user = userEvent.setup();
    await user.click(
      within(await onlyRow()).getByRole('button', {
        name: 'Associate Night Drive.mp3 with Night Drive (n8-7)',
      }),
    );
    expect(await screen.findByText(/no longer there/)).toBeVisible();
  });

  it('offers Match by Suno ID again only on a file whose automatic match the user blocked', async () => {
    const server = associationServer([
      file({ unmatchedReason: 'unassociated_by_user', autoMatchBlocked: true }),
      file({
        id: '0192f1a4-0000-7000-8000-0000000000a2',
        path: 'plain.mp3',
        fileName: 'plain.mp3',
        unmatchedReason: 'unassociated_by_user',
      }),
    ]);
    await openUnmatched();
    const user = userEvent.setup();
    const [blocked, plain] = await screen.findAllByTestId('unmatched-file');
    if (blocked === undefined || plain === undefined) {
      throw new Error('Two rows were expected.');
    }
    expect(
      within(plain).queryByRole('button', { name: /Match by Suno ID again/ }),
    ).not.toBeInTheDocument();

    await user.click(
      within(blocked).getByRole('button', { name: 'Match by Suno ID again: Night Drive.mp3' }),
    );
    await waitFor(() => {
      expect(server.writes).toHaveLength(1);
    });
    expect(server.writes[0]).toMatchObject({
      method: 'POST',
      path: `/api/v1/audio-files/${FILE_ID}/rematch`,
      ifMatch: '"3"',
    });
    expect(await screen.findByTestId('association-announcement')).toHaveTextContent(
      'Night Drive.mp3 matches no Generation by its Suno ID; it stays unmatched.',
    );
  });
});
