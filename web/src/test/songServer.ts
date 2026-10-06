import type { Artist } from '../api/artists';
import type { CatalogSettings } from '../api/catalogSettings';
import type { Genre } from '../api/genres';
import type { Song, SongArtist, SongGenre, SongTag, WorkflowState } from '../api/songs';
import type { Tag } from '../api/tags';
import { testArtist } from './artistServer';
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
  tags: [],
  credits: { primary: null, featured: [] },
  playlists: [],
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

export const RUNNING: Tag = {
  id: '0199b1a0-0000-7000-b000-000000000001',
  name: 'running',
  colour: 'gray',
  songCount: 2,
};
export const SUMMER: Tag = {
  id: '0199b1a0-0000-7000-b000-000000000002',
  name: 'Summer',
  colour: 'red',
  songCount: 1,
};
export const NIGHT: Tag = {
  id: '0199b1a0-0000-7000-b000-000000000003',
  name: 'night drive',
  colour: 'teal',
  songCount: 0,
};
export const TAGS = [RUNNING, SUMMER, NIGHT];

export const N8: Artist = testArtist('n8', {
  id: '01a10e00-0000-7000-9000-000000000001',
  aliases: ['Nate'],
});
export const GUEST: Artist = testArtist('Guest Singer', {
  id: '01a10e00-0000-7000-9000-000000000002',
});
export const CHOIR: Artist = testArtist('Choir', { id: '01a10e00-0000-7000-9000-000000000003' });
export const ARTISTS = [N8, GUEST, CHOIR];

/** An Artist as a Song's credits name it. */
export function credited(artist: Artist): SongArtist {
  return { id: artist.id, name: artist.name };
}

/** One write of a Song's credits the fake server received. */
export interface ReceivedCredits {
  ifMatch: string | null;
  body: { primaryArtistId: string | null; featuredArtistIds: string[] };
}

const artistKey = (name: string) => name.trim().replace(/\s+/g, ' ').normalize('NFC').toUpperCase();

/** The palette order a new Tag's colour is taken from, as the API takes it. */
const PALETTE = [
  'gray',
  'red',
  'pink',
  'grape',
  'violet',
  'indigo',
  'blue',
  'cyan',
  'teal',
  'green',
  'yellow',
  'orange',
];

/** One PATCH the fake server received: the revision it named and the edit it sent. */
export interface ReceivedEdit {
  ifMatch: string | null;
  body: Record<string, unknown>;
}

/**
 * A fake n8Tracks holding one Song, answering as the API does: GET it, and PATCH it against its
 * revision (409 `revision_conflict` with `current` when stale; its notes and Genres too, 422 on a
 * Genre not in `server.genres`), and the Genre list: GET it, and POST a name (the existing Genre
 * when the name matches ignoring case, otherwise a new one). Tags likewise (`server.tags`,
 * `server.createdTags`), a new Tag taking the first palette colour no Tag has. A test changes `server.song` to play
 * another tab, or sets `server.next` to answer the next PATCH some other way.
 */
export function songServer(
  song: Song = baseSong,
  genres: Genre[] = GENRES,
  tags: Tag[] = TAGS,
  artists: Artist[] = ARTISTS,
) {
  const server = {
    song: { ...song },
    artists: artists.map((artist) => ({ ...artist })),
    /** Every credits write (`PUT …/credits`), in order. */
    credits: [] as ReceivedCredits[],
    /** Every Artist POSTed, by name, in order, and whether it confirmed a shared name. */
    createdArtists: [] as { name: string; confirmDuplicate: boolean }[],
    catalog: { revision: 1, defaultArtist: null } as CatalogSettings,
    genres: genres.map((genre) => ({ ...genre })),
    tags: tags.map((tag) => ({ ...tag })),
    /** Every name POSTed to the Tag list, in order. */
    createdTags: [] as string[],
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
    if (path.endsWith('/api/v1/settings/catalog')) {
      return jsonResponse(200, server.catalog);
    }
    if (path.endsWith('/api/v1/artists')) {
      const method = (init?.method ?? 'GET').toUpperCase();
      if (method === 'GET') {
        const url = new URL(
          input instanceof Request ? input.url : input.toString(),
          document.baseURI,
        );
        const search = artistKey(url.searchParams.get('search') ?? '');
        const found = server.artists
          .filter(
            (artist) =>
              artistKey(artist.name).includes(search) ||
              artist.aliases.some((alias) => artistKey(alias).includes(search)),
          )
          .sort((a, b) => artistKey(a.name).localeCompare(artistKey(b.name)));
        return jsonResponse(200, { items: found, page: 1, pageSize: 10, total: found.length });
      }
      const sent = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as {
        name: string;
        confirmDuplicate?: boolean;
      };
      const confirmDuplicate = sent.confirmDuplicate === true;
      server.createdArtists.push({ name: sent.name, confirmDuplicate });
      const wanted = artistKey(sent.name);
      const matches = server.artists.flatMap((artist) => [
        ...(artistKey(artist.name) === wanted
          ? [{ id: artist.id, name: artist.name, matchedText: artist.name, matchedOn: 'name' }]
          : []),
        ...artist.aliases
          .filter((alias) => artistKey(alias) === wanted)
          .map((alias) => ({
            id: artist.id,
            name: artist.name,
            matchedText: alias,
            matchedOn: 'alias',
          })),
      ]);
      if (matches.length > 0 && !confirmDuplicate) {
        return jsonResponse(409, { code: 'duplicate_artist_name', matches });
      }
      const artist = testArtist(sent.name.trim(), {
        id: `01a10e00-0000-7000-9000-${String(900 + server.artists.length).padStart(12, '0')}`,
      });
      server.artists.push(artist);
      return jsonResponse(201, artist);
    }
    const credits = /\/api\/v1\/songs\/([^/]+)\/credits$/.exec(path);
    if (credits?.[1] !== undefined) {
      const ifMatch = new Headers(init?.headers).get('If-Match');
      const body = JSON.parse(
        typeof init?.body === 'string' ? init.body : '{}',
      ) as ReceivedCredits['body'];
      server.credits.push({ ifMatch, body });
      const next = server.next;
      if (next) {
        server.next = undefined;
        return next();
      }
      if (ifMatch !== `"${String(server.song.revision)}"`) {
        return jsonResponse(409, { code: 'revision_conflict', current: server.song });
      }
      const find = (id: string) => server.artists.find((artist) => artist.id === id);
      const primary = body.primaryArtistId === null ? null : find(body.primaryArtistId);
      const featured = body.featuredArtistIds.map(find);
      if (primary === undefined) {
        return jsonResponse(422, {
          code: 'validation_failed',
          errors: {
            primaryArtistId: ['The primary Artist chosen no longer exists. Choose again.'],
          },
        });
      }
      if (featured.some((artist) => artist === undefined)) {
        return jsonResponse(422, {
          code: 'validation_failed',
          errors: {
            featuredArtistIds: ['A featured Artist chosen no longer exists. Choose again.'],
          },
        });
      }
      const nextCredits = {
        primary: primary === null ? null : credited(primary),
        featured: featured.flatMap((artist) => (artist === undefined ? [] : [credited(artist)])),
      };
      if (JSON.stringify(nextCredits) !== JSON.stringify(server.song.credits)) {
        server.song = {
          ...server.song,
          credits: nextCredits,
          revision: server.song.revision + 1,
        };
      }
      return jsonResponse(200, server.song);
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
    if (path.endsWith('/api/v1/tags')) {
      if ((init?.method ?? 'GET') === 'GET') {
        return jsonResponse(200, {
          items: [...server.tags].sort((a, b) =>
            a.name.localeCompare(b.name, 'en', { sensitivity: 'base' }),
          ),
        });
      }
      const { name } = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as {
        name: string;
      };
      server.createdTags.push(name);
      const normalised = name.trim().replace(/\s+/g, ' ');
      const existing = server.tags.find(
        (tag) => tag.name.toUpperCase() === normalised.toUpperCase(),
      );
      if (existing) {
        return jsonResponse(200, existing);
      }
      const tag: Tag = {
        id: `0199b1a0-0000-7000-b000-${String(900 + server.tags.length).padStart(12, '0')}`,
        name: normalised,
        colour: PALETTE.find((colour) => !server.tags.some((t) => t.colour === colour)) ?? 'gray',
        songCount: 0,
      };
      server.tags.push(tag);
      return jsonResponse(201, tag);
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
    if (Array.isArray(body.tagIds)) {
      const chosen: SongTag[] = [];
      for (const id of body.tagIds) {
        const tag = server.tags.find((candidate) => candidate.id === id);
        if (!tag) {
          return jsonResponse(422, {
            code: 'validation_failed',
            errors: { tagIds: ['A Tag chosen no longer exists. Choose again.'] },
          });
        }
        if (!chosen.some((other) => other.id === tag.id)) {
          chosen.push({ id: tag.id, name: tag.name, colour: tag.colour });
        }
      }
      updated.tags = chosen.sort((a, b) =>
        a.name.localeCompare(b.name, 'en', { sensitivity: 'base' }),
      );
    }
    const changed =
      updated.title !== server.song.title ||
      updated.concept !== server.song.concept ||
      updated.state.id !== server.song.state.id ||
      updated.notes !== server.song.notes ||
      JSON.stringify(updated.genres) !== JSON.stringify(server.song.genres) ||
      JSON.stringify(updated.tags) !== JSON.stringify(server.song.tags);
    server.song = changed ? { ...updated, revision: server.song.revision + 1 } : server.song;
    return jsonResponse(200, server.song);
  });

  return { server, mock };
}
