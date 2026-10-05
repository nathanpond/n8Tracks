import type { Genre } from '../api/genres';
import type { Song, SongGenre, WorkflowState } from '../api/songs';
import { healthyReport, jsonResponse, requestPath, stubFetch } from './helpers';

export const IDEA: WorkflowState = {
  id: '01a10a6e-dc80-7000-8000-000000000001',
  name: 'Idea',
  colour: 'yellow',
  order: 1,
  hidden: false,
};
export const WRITING: WorkflowState = {
  id: '01a10a6e-dc81-7001-8000-000000000002',
  name: 'Writing',
  colour: 'blue',
  order: 2,
  hidden: false,
};
export const FINAL: WorkflowState = {
  id: '01a10a6e-dc84-7004-8000-000000000005',
  name: 'Final',
  colour: 'green',
  order: 5,
  hidden: false,
};
export const SHELVED: WorkflowState = {
  id: '01a10a6e-dc87-7007-8000-000000000008',
  name: 'Shelved',
  colour: 'gray',
  order: 8,
  hidden: true,
};

export const STATES = [IDEA, WRITING, FINAL, SHELVED];

export const baseSong: Song = {
  id: '0199b1a0-0000-7000-8000-000000000007',
  shortcode: 'n8-7',
  title: 'Running in a Pack',
  concept: 'Fast and loud.',
  state: { id: IDEA.id, name: IDEA.name, colour: IDEA.colour },
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
};

export const FOLK: Genre = {
  id: '0199b1a0-0000-7000-a000-000000000001',
  name: 'Folk',
  songCount: 3,
};
export const INDIE_POP: Genre = {
  id: '0199b1a0-0000-7000-a000-000000000002',
  name: 'Indie Pop',
  songCount: 1,
};
export const ROCK: Genre = {
  id: '0199b1a0-0000-7000-a000-000000000003',
  name: 'Rock',
  songCount: 0,
};
export const GENRES = [FOLK, INDIE_POP, ROCK];

/** One PATCH the fake server received: the revision it named and the edit it sent. */
export interface ReceivedEdit {
  ifMatch: string | null;
  body: Record<string, unknown>;
}

/**
 * A fake n8Tracks holding one Song, answering as the API does: GET it, and PATCH it against its
 * revision (409 `revision_conflict` with `current` when stale; its notes and Genres too, 422 on a
 * Genre not in `server.genres`), and the Genre list: GET it, and POST a name (the existing Genre
 * when the name matches ignoring case, otherwise a new one). A test changes `server.song` to play
 * another tab, or sets `server.next` to answer the next PATCH some other way.
 */
export function songServer(song: Song = baseSong, genres: Genre[] = GENRES) {
  const server = {
    song: { ...song },
    genres: genres.map((genre) => ({ ...genre })),
    /** Every name POSTed to the Genre list, in order. */
    created: [] as string[],
    edits: [] as ReceivedEdit[],
    /** When set, answers the next PATCH (once) instead of the fake API. */
    next: undefined as (() => Response | Promise<Response>) | undefined,
    /** Plays another tab: changes the Song and raises its revision. */
    changeElsewhere(change: Partial<Song>) {
      server.song = { ...server.song, ...change, revision: server.song.revision + 1 };
    },
  };

  const mock = stubFetch();
  mock.mockImplementation(async (input, init) => {
    const path = requestPath(input);
    if (path.endsWith('/health')) {
      return jsonResponse(200, healthyReport);
    }
    if (path.endsWith('/api/v1/workflow-states')) {
      return jsonResponse(200, { items: STATES });
    }
    if (path.endsWith('/api/v1/genres')) {
      if ((init?.method ?? 'GET') === 'GET') {
        return jsonResponse(200, {
          items: [...server.genres].sort((a, b) => a.name.localeCompare(b.name)),
        });
      }
      const { name } = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as {
        name: string;
      };
      server.created.push(name);
      const normalised = name.trim().replace(/\s+/g, ' ');
      const existing = server.genres.find(
        (genre) => genre.name.toUpperCase() === normalised.toUpperCase(),
      );
      if (existing) {
        return jsonResponse(200, existing);
      }
      const genre: Genre = {
        id: `0199b1a0-0000-7000-a000-${String(900 + server.genres.length).padStart(12, '0')}`,
        name: normalised,
        songCount: 0,
      };
      server.genres.push(genre);
      return jsonResponse(201, genre);
    }
    const match = /\/api\/v1\/songs\/([^/]+)$/.exec(path);
    if (match?.[1] === undefined) {
      return jsonResponse(404, { code: 'not_found' });
    }
    const reference = decodeURIComponent(match[1]);
    if (reference !== server.song.id && reference !== server.song.shortcode) {
      return jsonResponse(404, { code: 'not_found' });
    }
    if ((init?.method ?? 'GET') !== 'PATCH') {
      return jsonResponse(200, server.song);
    }

    const ifMatch = new Headers(init?.headers).get('If-Match');
    const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as Record<
      string,
      unknown
    >;
    server.edits.push({ ifMatch, body });
    const next = server.next;
    if (next) {
      server.next = undefined;
      return next();
    }
    if (ifMatch !== `"${String(server.song.revision)}"`) {
      return jsonResponse(409, { code: 'revision_conflict', current: server.song });
    }
    const updated: Song = { ...server.song };
    if (typeof body.title === 'string') {
      updated.title = body.title;
    }
    if ('concept' in body) {
      updated.concept = typeof body.concept === 'string' ? body.concept : null;
    }
    if (typeof body.stateId === 'string') {
      const state = STATES.find((candidate) => candidate.id === body.stateId);
      if (!state) {
        return jsonResponse(422, {
          code: 'validation_failed',
          errors: { stateId: ['Choose one of the workflow states.'] },
        });
      }
      updated.state = { id: state.id, name: state.name, colour: state.colour };
    }
    if ('notes' in body) {
      updated.notes = typeof body.notes === 'string' ? body.notes : null;
    }
    if (Array.isArray(body.genreIds)) {
      const chosen: SongGenre[] = [];
      for (const id of body.genreIds) {
        const genre = server.genres.find((candidate) => candidate.id === id);
        if (!genre) {
          return jsonResponse(422, {
            code: 'validation_failed',
            errors: { genreIds: ['A Genre chosen no longer exists. Choose again.'] },
          });
        }
        if (!chosen.some((other) => other.id === genre.id)) {
          chosen.push({ id: genre.id, name: genre.name });
        }
      }
      updated.genres = chosen.sort((a, b) => a.name.localeCompare(b.name));
    }
    const changed =
      updated.title !== server.song.title ||
      updated.concept !== server.song.concept ||
      updated.state.id !== server.song.state.id ||
      updated.notes !== server.song.notes ||
      JSON.stringify(updated.genres) !== JSON.stringify(server.song.genres);
    server.song = changed ? { ...updated, revision: server.song.revision + 1 } : server.song;
    return jsonResponse(200, server.song);
  });

  return { server, mock };
}
