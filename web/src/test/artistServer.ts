import type { Album } from '../api/albums';
import type { Artist, ArtistMatch } from '../api/artists';
import type { Song } from '../api/songs';
import { healthyReport, jsonResponse, requestPath, stubFetch } from './helpers';

/** One write the fake server received. */
export interface ArtistWrite {
  method: string;
  path: string;
  ifMatch: string | null;
  body: Record<string, unknown>;
}

const key = (name: string) => name.trim().replace(/\s+/g, ' ').normalize('NFC').toUpperCase();

let sequence = 0;

/** A test Artist: `name`, no aliases, notes, or links, at revision 1, unless `change` says otherwise. */
export function testArtist(name: string, change: Partial<Artist> = {}): Artist {
  sequence += 1;
  return {
    id: `01a10e00-0000-7000-8000-${String(sequence).padStart(12, '0')}`,
    name,
    aliases: [],
    notes: null,
    links: [],
    songCount: 0,
    albumCount: 0,
    createdAt: '2026-10-05T09:00:00.000Z',
    updatedAt: '2026-10-05T09:00:00.000Z',
    revision: 1,
    ...change,
  };
}

function problem(status: number, code: string, extra: Record<string, unknown> = {}): Response {
  return new Response(JSON.stringify({ status, code, title: 'refused', ...extra }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

/**
 * A fake n8Tracks holding Artists, answering as the API does: the list sorted by name ignoring case
 * and filtered by `search` (any part of a name or alias, ignoring case), paged by `pageSize`
 * (`server.pageSize`, 50 by default); POST and PATCH with the duplicate check (409
 * `duplicate_artist_name` unless `confirmDuplicate`, only for names an Artist did not already have)
 * and, for PATCH, the revision check first. `server.next` answers the next write some other way;
 * `server.changeElsewhere` plays another client. `server.songs` are the Songs the list answers for
 * `GET /api/v1/songs?artist=<id>` (those crediting that Artist, by title), and `server.albums` those
 * it answers for `GET /api/v1/albums?artist=<id>` (those whose Album Artist it is, by title).
 */
export function artistServer(artists: Artist[] = [], songs: Song[] = [], albums: Album[] = []) {
  const server = {
    artists: artists.map((artist) => ({ ...artist })),
    songs: [...songs],
    albums: [...albums],
    writes: [] as ArtistWrite[],
    queries: [] as string[],
    pageSize: 50,
    next: undefined as (() => Response) | undefined,
    changeElsewhere(id: string, change: Partial<Artist>) {
      server.artists = server.artists.map((artist) =>
        artist.id === id ? { ...artist, ...change, revision: artist.revision + 1 } : artist,
      );
    },
  };

  const matches = (keys: Set<string>, excluding?: string): ArtistMatch[] =>
    server.artists
      .filter((artist) => artist.id !== excluding)
      .flatMap((artist) => [
        ...(keys.has(key(artist.name))
          ? [
              {
                id: artist.id,
                name: artist.name,
                matchedText: artist.name,
                matchedOn: 'name' as const,
              },
            ]
          : []),
        ...artist.aliases
          .filter((alias) => keys.has(key(alias)))
          .map((alias) => ({
            id: artist.id,
            name: artist.name,
            matchedText: alias,
            matchedOn: 'alias' as const,
          })),
      ]);

  const keysOf = (name: string, aliases: readonly string[]) =>
    new Set([key(name), ...aliases.map(key)]);

  stubFetch().mockImplementation((input, init) => {
    const path = requestPath(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    const url = new URL(input instanceof Request ? input.url : input.toString(), document.baseURI);
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (path.endsWith('/api/v1/songs') && method === 'GET') {
      const artist = url.searchParams.get('artist');
      const items = server.songs
        .filter(
          (song) =>
            song.credits.primary?.id === artist ||
            song.credits.featured.some((featured) => featured.id === artist),
        )
        .sort((a, b) => a.title.localeCompare(b.title));
      return Promise.resolve(
        jsonResponse(200, {
          items: items.slice(0, 50),
          page: 1,
          pageSize: 50,
          total: items.length,
        }),
      );
    }
    if (path.endsWith('/api/v1/albums') && method === 'GET') {
      const artist = url.searchParams.get('artist');
      const items = server.albums
        .filter((album) => album.albumArtist?.id === artist)
        .sort((a, b) => a.title.localeCompare(b.title));
      return Promise.resolve(
        jsonResponse(200, { items, page: 1, pageSize: 50, total: items.length }),
      );
    }
    if (!path.includes('/api/v1/artists')) {
      return Promise.resolve(problem(404, 'not_found'));
    }

    if (method !== 'GET') {
      const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as Record<
        string,
        unknown
      >;
      const headers = new Headers(init?.headers);
      server.writes.push({ method, path, ifMatch: headers.get('If-Match'), body });
      const next = server.next;
      if (next !== undefined) {
        server.next = undefined;
        return Promise.resolve(next());
      }
    }

    const id = /\/api\/v1\/artists\/([^/]+)$/.exec(path)?.[1];
    if (method === 'GET' && id === undefined) {
      server.queries.push(url.search);
      const search = key(url.searchParams.get('search') ?? '');
      const page = Number(url.searchParams.get('page') ?? '1');
      const found = server.artists
        .filter(
          (artist) =>
            search === '' ||
            key(artist.name).includes(search) ||
            artist.aliases.some((alias) => key(alias).includes(search)),
        )
        .sort((a, b) => key(a.name).localeCompare(key(b.name)));
      return Promise.resolve(
        jsonResponse(200, {
          items: found.slice((page - 1) * server.pageSize, page * server.pageSize),
          page,
          pageSize: server.pageSize,
          total: found.length,
        }),
      );
    }

    if (method === 'POST') {
      const write = server.writes[server.writes.length - 1];
      const name = typeof write?.body.name === 'string' ? write.body.name.trim() : '';
      const aliases = (write?.body.aliases as string[] | undefined) ?? [];
      const found = matches(keysOf(name, aliases));
      if (found.length > 0 && write?.body.confirmDuplicate !== true) {
        return Promise.resolve(problem(409, 'duplicate_artist_name', { matches: found }));
      }
      const artist = testArtist(name, { aliases });
      server.artists.push(artist);
      return Promise.resolve(jsonResponse(201, artist));
    }

    const current = server.artists.find((artist) => artist.id === id);
    if (current === undefined) {
      return Promise.resolve(problem(404, 'not_found'));
    }
    if (method === 'GET') {
      return Promise.resolve(jsonResponse(200, current));
    }

    const write = server.writes[server.writes.length - 1];
    if (write?.ifMatch !== `"${String(current.revision)}"`) {
      return Promise.resolve(problem(409, 'revision_conflict', { current }));
    }
    const edit = write.body as Partial<Artist> & { confirmDuplicate?: boolean };
    const changed: Artist = {
      ...current,
      ...(edit.name === undefined ? {} : { name: edit.name }),
      ...(edit.aliases === undefined ? {} : { aliases: edit.aliases }),
      ...(edit.notes === undefined ? {} : { notes: edit.notes }),
      ...(edit.links === undefined ? {} : { links: edit.links }),
    };
    const before = keysOf(current.name, current.aliases);
    const added = new Set([...keysOf(changed.name, changed.aliases)].filter((k) => !before.has(k)));
    const found = matches(added, current.id);
    if (found.length > 0 && edit.confirmDuplicate !== true) {
      return Promise.resolve(problem(409, 'duplicate_artist_name', { matches: found }));
    }
    const saved = { ...changed, revision: current.revision + 1 };
    server.artists = server.artists.map((artist) => (artist.id === id ? saved : artist));
    return Promise.resolve(jsonResponse(200, saved));
  });

  return server;
}
