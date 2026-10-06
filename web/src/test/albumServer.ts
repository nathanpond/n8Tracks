import type { Album } from '../api/albums';
import type { Artist } from '../api/artists';
import { normaliseUpc, upcError } from '../albums/albumRules';
import { healthyReport, jsonResponse, requestPath, stubFetch } from './helpers';
import { testArtist } from './artistServer';

/** One write the fake server received. */
export interface AlbumWrite {
  method: string;
  path: string;
  ifMatch: string | null;
  body: Record<string, unknown>;
}

let sequence = 0;

/** A test Album: `title` and nothing else, at revision 1, unless `change` says otherwise. */
export function testAlbum(title: string, change: Partial<Album> = {}): Album {
  sequence += 1;
  return {
    id: `01a1b000-0000-7000-8000-${String(sequence).padStart(12, '0')}`,
    title,
    description: null,
    albumArtist: null,
    releaseDate: null,
    originalReleaseDate: null,
    upc: null,
    copyright: null,
    publishing: null,
    links: [],
    songCount: 0,
    createdAt: '2026-10-05T09:00:00.000Z',
    updatedAt: '2026-10-05T09:00:00.000Z',
    revision: 1,
    warnings: [],
    ...change,
  };
}

function problem(status: number, code: string, extra: Record<string, unknown> = {}): Response {
  return new Response(JSON.stringify({ status, code, title: 'refused', ...extra }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

const upcKey = (upc: string) => (upc.length === 12 ? `0${upc}` : upc);

/**
 * A fake n8Tracks holding Albums and Artists, answering as the API does: the Albums list by title
 * (or `releaseDate` or `artist`, missing values last), filtered by `artist`, 50 to a page; one
 * Album with its `warnings` (another Album with the same UPC/EAN); POST from a title; PATCH under
 * the revision, checking the UPC/EAN and the Album Artist (422). The Artists answer the picker's
 * search and create. `server.next` answers the next write some other way;
 * `server.changeElsewhere` plays another client.
 */
export function albumServer(albums: Album[] = [], artists: Artist[] = []) {
  const server = {
    albums: albums.map((album) => ({ ...album })),
    artists: artists.map((artist) => ({ ...artist })),
    writes: [] as AlbumWrite[],
    queries: [] as string[],
    next: undefined as (() => Response) | undefined,
    changeElsewhere(id: string, change: Partial<Album>) {
      server.albums = server.albums.map((album) =>
        album.id === id ? { ...album, ...change, revision: album.revision + 1 } : album,
      );
    },
  };

  const withWarnings = (album: Album): Album => {
    const others =
      album.upc === null
        ? []
        : server.albums.filter(
            (other) =>
              other.id !== album.id &&
              other.upc !== null &&
              upcKey(other.upc) === upcKey(album.upc ?? ''),
          );
    return {
      ...album,
      warnings:
        others.length === 0
          ? []
          : [
              {
                code: 'duplicate_upc',
                field: 'upc',
                message:
                  others.length === 1
                    ? 'Another Album has this UPC/EAN.'
                    : 'Other Albums have this UPC/EAN.',
                albums: others.map((other) => ({ id: other.id, title: other.title })),
              },
            ],
    };
  };

  stubFetch().mockImplementation((input, init) => {
    const path = requestPath(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    const url = new URL(input instanceof Request ? input.url : input.toString(), document.baseURI);
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }

    const body = (): Record<string, unknown> =>
      JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as Record<string, unknown>;

    if (path.endsWith('/api/v1/artists')) {
      if (method === 'POST') {
        const artist = testArtist(String(body().name).trim());
        server.artists.push(artist);
        return Promise.resolve(jsonResponse(201, artist));
      }
      const search = (url.searchParams.get('search') ?? '').trim().toUpperCase();
      const items = server.artists.filter((artist) => artist.name.toUpperCase().includes(search));
      return Promise.resolve(
        jsonResponse(200, { items, page: 1, pageSize: 10, total: items.length }),
      );
    }
    if (!path.includes('/api/v1/albums')) {
      return Promise.resolve(problem(404, 'not_found'));
    }

    if (method !== 'GET') {
      const headers = new Headers(init?.headers);
      server.writes.push({ method, path, ifMatch: headers.get('If-Match'), body: body() });
      const next = server.next;
      if (next !== undefined) {
        server.next = undefined;
        return Promise.resolve(next());
      }
    }

    const id = /\/api\/v1\/albums\/([^/]+)$/.exec(path)?.[1];
    if (method === 'GET' && id === undefined) {
      server.queries.push(url.search);
      const artist = url.searchParams.get('artist');
      const sort = url.searchParams.get('sort') ?? 'title';
      const descending = url.searchParams.get('direction') === 'desc';
      const page = Number(url.searchParams.get('page') ?? '1');
      const keyOf = (album: Album) =>
        sort === 'releaseDate'
          ? (album.releaseDate ?? album.originalReleaseDate)
          : sort === 'artist'
            ? (album.albumArtist?.name.toUpperCase() ?? null)
            : album.title.toUpperCase();
      const found = server.albums
        .filter((album) => artist === null || album.albumArtist?.id === artist)
        .sort((a, b) => {
          const [left, right] = [keyOf(a), keyOf(b)];
          if (left === null || right === null) {
            return left === right ? 0 : left === null ? 1 : -1;
          }
          return (descending ? -1 : 1) * left.localeCompare(right);
        })
        .map(withWarnings);
      return Promise.resolve(
        jsonResponse(200, {
          items: found.slice((page - 1) * 50, page * 50),
          page,
          pageSize: 50,
          total: found.length,
        }),
      );
    }

    if (method === 'POST') {
      const sent = server.writes[server.writes.length - 1]?.body.title;
      const title = typeof sent === 'string' ? sent.trim() : '';
      if (title === '') {
        return Promise.resolve(
          problem(422, 'validation_failed', { errors: { title: ['Enter a title.'] } }),
        );
      }
      const album = testAlbum(title);
      server.albums.push(album);
      return Promise.resolve(jsonResponse(201, album));
    }

    const current = server.albums.find((album) => album.id === id);
    if (current === undefined) {
      return Promise.resolve(problem(404, 'not_found'));
    }
    if (method === 'GET') {
      return Promise.resolve(jsonResponse(200, withWarnings(current)));
    }

    const write = server.writes[server.writes.length - 1];
    if (write?.ifMatch !== `"${String(current.revision)}"`) {
      return Promise.resolve(problem(409, 'revision_conflict', { current: withWarnings(current) }));
    }
    const edit = write.body;
    const errors: Record<string, string[]> = {};
    if (typeof edit.upc === 'string') {
      const error = upcError(edit.upc);
      if (error !== undefined) {
        errors.upc = [error];
      }
    }
    let albumArtist = current.albumArtist;
    if (Object.hasOwn(edit, 'albumArtistId')) {
      const artist = server.artists.find((item) => item.id === edit.albumArtistId);
      if (edit.albumArtistId !== null && artist === undefined) {
        errors.albumArtistId = ['There is no such Artist.'];
      }
      albumArtist = artist === undefined ? null : { id: artist.id, name: artist.name };
    }
    if (Object.keys(errors).length > 0) {
      return Promise.resolve(problem(422, 'validation_failed', { errors }));
    }
    const fields: Record<string, unknown> = { ...edit };
    delete fields.albumArtistId;
    delete fields.upc;
    const upc = edit.upc;
    const saved: Album = {
      ...current,
      ...(fields as Partial<Album>),
      albumArtist,
      ...(upc === undefined ? {} : { upc: typeof upc === 'string' ? normaliseUpc(upc) : null }),
      revision: current.revision + 1,
    };
    server.albums = server.albums.map((album) => (album.id === id ? saved : album));
    return Promise.resolve(jsonResponse(200, withWarnings(saved)));
  });

  return server;
}
