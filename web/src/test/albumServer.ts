import type { Album, AlbumTrack, AlbumTrackPlace } from '../api/albums';
import type { Artist } from '../api/artists';
import type { Song } from '../api/songs';
import { normaliseUpc, upcError } from '../albums/albumRules';
import { placesOf } from '../albums/trackOrder';
import { artworkFake, withoutArtwork } from './artworkFake';
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
    tracks: [],
    artwork: null,
    ...change,
  };
}

/** A Song as an Album lists it, at `disc` and `track`. */
export function trackOf(song: Song, disc: number, track: number): AlbumTrack {
  return {
    songId: song.id,
    shortcode: song.shortcode,
    title: song.title,
    primaryArtist: song.credits.primary,
    state: song.state,
    disc,
    track,
    hasSelectedGeneration: false,
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
 * search and create. Tracks: POST adds a Song at the end of the last disc (409
 * `song_already_on_album`), DELETE renumbers the rest of its disc, and PUT replaces the list (409
 * `order_mismatch` or `track_number_taken`), each under the revision with `current`; a PUT with a
 * number outside 1 to 999 is 422, as the API answers it; `GET /api/v1/songs?q=` searches `songs`. `server.next` answers the next write some other way;
 * `server.changeElsewhere` plays another client. Artwork uploads and the PATCH's artwork fields
 * follow {@link artworkFake} (`server.artwork`). DELETE of an Album under its revision answers 204
 * and records it in `server.deleted`.
 */
export function albumServer(albums: Album[] = [], artists: Artist[] = [], songs: Song[] = []) {
  const server = {
    albums: albums.map((album) => ({ ...album })),
    artists: artists.map((artist) => ({ ...artist })),
    songs,
    searches: [] as string[],
    artwork: artworkFake(),
    writes: [] as AlbumWrite[],
    /** The IDs of the Albums deleted, in order. */
    deleted: [] as string[],
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

  /** The Album with `places` as its tracks (Songs looked up in `current`'s tracks or `server.songs`), stored at the next revision. */
  const storeTracks = (current: Album, places: readonly AlbumTrackPlace[]): Response => {
    const tracks = placesOf(places).map((place) => {
      const known = current.tracks.find((track) => track.songId === place.songId);
      const song = server.songs.find((candidate) => candidate.id === place.songId);
      const base = known ?? (song === undefined ? undefined : trackOf(song, 1, 1));
      if (base === undefined) {
        throw new Error(`No Song ${place.songId}`);
      }
      return { ...base, disc: place.disc, track: place.track };
    });
    const saved: Album = {
      ...current,
      tracks,
      songCount: tracks.length,
      revision: current.revision + 1,
    };
    server.albums = server.albums.map((album) => (album.id === current.id ? saved : album));
    return jsonResponse(200, saved);
  };

  const changeTracks = (method: string, id: string, reference: string | undefined): Response => {
    const current = server.albums.find((album) => album.id === id);
    if (current === undefined) {
      return problem(404, 'not_found');
    }
    const write = server.writes[server.writes.length - 1];
    if (write?.ifMatch !== `"${String(current.revision)}"`) {
      return problem(409, 'revision_conflict', { current });
    }
    const places = placesOf(current.tracks);
    if (method === 'POST') {
      const song = server.songs.find((candidate) => candidate.id === write.body.songId);
      if (song === undefined) {
        return problem(422, 'validation_failed', {
          errors: { songId: ['There is no such Song.'] },
        });
      }
      if (places.some((place) => place.songId === song.id)) {
        return problem(409, 'song_already_on_album', { current });
      }
      const disc = Math.max(1, ...places.map((place) => place.disc));
      const last = Math.max(0, ...places.filter((p) => p.disc === disc).map((p) => p.track));
      return storeTracks(current, [...places, { songId: song.id, disc, track: last + 1 }]);
    }
    if (method === 'DELETE') {
      const songId = decodeURIComponent(reference ?? '');
      const leaving = places.find((place) => place.songId === songId);
      if (leaving === undefined) {
        return jsonResponse(200, current);
      }
      const rest = places.filter((place) => place.songId !== songId);
      let number = 0;
      const renumbered = rest.map((place) =>
        place.disc === leaving.disc ? { ...place, track: (number += 1) } : place,
      );
      const discs = [...new Set(renumbered.map((place) => place.disc))].sort((a, b) => a - b);
      return storeTracks(
        current,
        renumbered.map((place) => ({ ...place, disc: discs.indexOf(place.disc) + 1 })),
      );
    }
    const sent = Array.isArray(write.body.tracks) ? (write.body.tracks as AlbumTrackPlace[]) : [];
    // As the API: numbers outside 1 to 999 are refused before anything else is checked.
    if (
      sent.some((place) => [place.disc, place.track].some((number) => number < 1 || number > 999))
    ) {
      return problem(422, 'validation_failed', {
        errors: { tracks: ['Disc and track numbers are whole numbers from 1 to 999.'] },
      });
    }
    const ids = sent.map((place) => place.songId);
    if (
      ids.length !== places.length ||
      new Set(ids).size !== ids.length ||
      ids.some((songId) => !places.some((place) => place.songId === songId))
    ) {
      return problem(409, 'order_mismatch', { current });
    }
    const discs = [...new Set(sent.map((place) => place.disc))].sort((a, b) => a - b);
    const next = sent.map((place) => ({ ...place, disc: discs.indexOf(place.disc) + 1 }));
    for (const place of next) {
      const sharing = next.filter(
        (other) => other.disc === place.disc && other.track === place.track,
      );
      if (sharing.length > 1) {
        const holder =
          sharing.find((other) =>
            places.some(
              (before) =>
                before.songId === other.songId &&
                before.disc === other.disc &&
                before.track === other.track,
            ),
          ) ?? sharing[0];
        const track = current.tracks.find((item) => item.songId === holder?.songId);
        return problem(409, 'track_number_taken', {
          title: `Track ${String(place.track)} on disc ${String(place.disc)} is held by ${track?.title ?? ''} (${track?.shortcode ?? ''}).`,
          current,
        });
      }
    }
    return storeTracks(current, next);
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
    if (path.endsWith('/api/v1/songs')) {
      const q = (url.searchParams.get('q') ?? '').trim().toLowerCase();
      server.searches.push(q);
      const found = q
        ? server.songs.filter(
            (song) => song.title.toLowerCase().includes(q) || song.shortcode.startsWith(q),
          )
        : [];
      return Promise.resolve(
        jsonResponse(200, {
          items: found.slice(0, 10),
          page: 1,
          pageSize: 10,
          total: found.length,
        }),
      );
    }
    if (path.endsWith('/api/v1/artwork')) {
      return Promise.resolve(server.artwork.upload(init));
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

    const tracksMatch = /\/api\/v1\/albums\/([^/]+)\/tracks(?:\/([^/]+))?$/.exec(path);
    if (tracksMatch !== null) {
      return Promise.resolve(changeTracks(method, tracksMatch[1] ?? '', tracksMatch[2]));
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
    if (method === 'DELETE') {
      server.albums = server.albums.filter((album) => album.id !== id);
      server.deleted.push(current.id);
      return Promise.resolve(new Response(null, { status: 204 }));
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
    const artwork = server.artwork.apply(current.artwork, edit);
    if ('errors' in artwork) {
      Object.assign(errors, artwork.errors);
    }
    if (Object.keys(errors).length > 0 || 'errors' in artwork) {
      return Promise.resolve(problem(422, 'validation_failed', { errors }));
    }
    const fields: Record<string, unknown> = withoutArtwork(edit);
    delete fields.albumArtistId;
    delete fields.upc;
    const upc = edit.upc;
    const saved: Album = {
      ...current,
      ...(fields as Partial<Album>),
      albumArtist,
      artwork: artwork.artwork,
      ...(upc === undefined ? {} : { upc: typeof upc === 'string' ? normaliseUpc(upc) : null }),
      revision: current.revision + 1,
    };
    server.albums = server.albums.map((album) => (album.id === id ? saved : album));
    return Promise.resolve(jsonResponse(200, withWarnings(saved)));
  });

  return server;
}
