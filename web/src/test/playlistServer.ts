import type { Playlist, PlaylistSong, PlaylistSummary } from '../api/playlists';
import type { Song } from '../api/songs';
import { artworkFake } from './artworkFake';
import { healthyReport, jsonResponse, requestPath, stubFetch } from './helpers';
import { baseSong, IDEA, WRITING } from './songServer';

/** One write the fake server received. */
export interface PlaylistWrite {
  method: string;
  path: string;
  ifMatch: string | null;
  body: Record<string, unknown>;
}

let sequence = 0;

/** A test Song numbered `number`: `n8-<number>`, titled `title`. */
export function testSong(number: number, title: string, change: Partial<Song> = {}): Song {
  return {
    ...baseSong,
    id: `0199b1a0-0000-7000-8000-${String(number).padStart(12, '0')}`,
    shortcode: `n8-${String(number)}`,
    title,
    ...change,
  };
}

export const HIGHWAY = testSong(1, 'Highway Lights', {
  credits: { primary: { id: '01a1a000-0000-7000-8000-000000000001', name: 'n8' }, featured: [] },
});
export const SUNRISE = testSong(2, 'Sunrise Exit', {
  state: { id: WRITING.id, name: WRITING.name, colour: WRITING.colour },
});
export const TOLL = testSong(3, 'Toll Booth Blues');
export const SONGS = [HIGHWAY, SUNRISE, TOLL];

/** A Song as a Playlist lists it. */
export function entryOf(song: Song): PlaylistSong {
  return {
    id: song.id,
    shortcode: song.shortcode,
    title: song.title,
    primaryArtist: song.credits.primary,
    state: song.state,
    hasSelectedGeneration: false,
  };
}

/** A test Playlist: `title`, holding `songs` in order, at revision 1, unless `change` says otherwise. */
export function testPlaylist(
  title: string,
  songs: Song[] = [],
  change: Partial<Playlist> = {},
): Playlist {
  sequence += 1;
  return {
    id: `01a1c000-0000-7000-8000-${String(sequence).padStart(12, '0')}`,
    title,
    description: null,
    songCount: songs.length,
    createdAt: '2026-10-05T09:00:00.000Z',
    updatedAt: '2026-10-05T09:00:00.000Z',
    revision: 1,
    artwork: null,
    songs: songs.map(entryOf),
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
 * A fake n8Tracks holding Playlists and Songs, answering as the API does: the list by title, fifty
 * to a page; one Playlist with its Songs; POST from a title; PATCH of the title and description,
 * and POST, DELETE, and PUT of its Songs, each under the revision (409 `revision_conflict`, a
 * duplicate 409 `song_already_on_playlist`, a wrong order 409 `order_mismatch`, each with
 * `current`). `GET /api/v1/songs?q=` searches the Songs by title or shortcode. `server.next`
 * answers the next write some other way; `server.changeElsewhere` plays another client. Artwork
 * uploads and the PATCH's artwork fields follow {@link artworkFake} (`server.artwork`).
 */
export function playlistServer(playlists: Playlist[] = [], songs: Song[] = SONGS) {
  const server = {
    playlists: playlists.map((playlist) => ({ ...playlist })),
    songs,
    artwork: artworkFake(),
    writes: [] as PlaylistWrite[],
    searches: [] as string[],
    next: undefined as (() => Response) | undefined,
    changeElsewhere(id: string, change: Partial<Playlist>) {
      server.playlists = server.playlists.map((playlist) =>
        playlist.id === id
          ? {
              ...playlist,
              ...change,
              songCount: (change.songs ?? playlist.songs).length,
              revision: playlist.revision + 1,
            }
          : playlist,
      );
    },
  };

  const summary = (playlist: Playlist): PlaylistSummary => ({
    id: playlist.id,
    title: playlist.title,
    description: playlist.description,
    songCount: playlist.songs.length,
    createdAt: playlist.createdAt,
    updatedAt: playlist.updatedAt,
    artwork: playlist.artwork,
    revision: playlist.revision,
  });

  stubFetch().mockImplementation((input, init) => {
    const path = requestPath(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    const url = new URL(input instanceof Request ? input.url : input.toString(), document.baseURI);
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    const body = (): Record<string, unknown> =>
      JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as Record<string, unknown>;

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
    if (path.endsWith('/api/v1/workflow-states')) {
      return Promise.resolve(jsonResponse(200, { revision: 1, items: [IDEA, WRITING] }));
    }
    if (path.endsWith('/api/v1/artwork')) {
      return Promise.resolve(server.artwork.upload(init));
    }
    if (!path.includes('/api/v1/playlists')) {
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

    const match = /\/api\/v1\/playlists(?:\/([^/]+))?(\/songs(?:\/([^/]+))?)?$/.exec(path);
    const id = match?.[1];
    if (id === undefined) {
      if (method === 'POST') {
        const sent = body().title;
        const title = typeof sent === 'string' ? sent.replace(/\r\n|\r|\n/g, ' ').trim() : '';
        if (title === '') {
          return Promise.resolve(
            problem(422, 'validation_failed', { errors: { title: ['Enter a title.'] } }),
          );
        }
        const playlist = testPlaylist(title);
        server.playlists.push(playlist);
        return Promise.resolve(jsonResponse(201, playlist));
      }
      const page = Number(url.searchParams.get('page') ?? '1');
      const sorted = [...server.playlists].sort((a, b) =>
        a.title.toUpperCase().localeCompare(b.title.toUpperCase()),
      );
      return Promise.resolve(
        jsonResponse(200, {
          items: sorted.slice((page - 1) * 50, page * 50).map(summary),
          page,
          pageSize: 50,
          total: sorted.length,
        }),
      );
    }

    const current = server.playlists.find((playlist) => playlist.id === id);
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

    const store = (change: Partial<Playlist>) => {
      const saved: Playlist = {
        ...current,
        ...change,
        songCount: (change.songs ?? current.songs).length,
        revision: current.revision + 1,
      };
      server.playlists = server.playlists.map((playlist) =>
        playlist.id === id ? saved : playlist,
      );
      return Promise.resolve(jsonResponse(200, saved));
    };
    const sent = write.body;

    if (match?.[2] === undefined) {
      const change: Partial<Playlist> = {};
      if (typeof sent.title === 'string') {
        change.title = sent.title.replace(/\r\n|\r|\n/g, ' ').trim();
      }
      if (Object.hasOwn(sent, 'description')) {
        change.description = typeof sent.description === 'string' ? sent.description : null;
      }
      const artwork = server.artwork.apply(current.artwork, sent);
      if ('errors' in artwork) {
        return Promise.resolve(problem(422, 'validation_failed', { errors: artwork.errors }));
      }
      change.artwork = artwork.artwork;
      return store(change);
    }

    if (method === 'POST') {
      const song = server.songs.find((candidate) => candidate.id === sent.songId);
      if (song === undefined) {
        return Promise.resolve(
          problem(422, 'validation_failed', { errors: { songId: ['There is no such Song.'] } }),
        );
      }
      if (current.songs.some((entry) => entry.id === song.id)) {
        return Promise.resolve(problem(409, 'song_already_on_playlist', { current }));
      }
      return store({ songs: [...current.songs, entryOf(song)] });
    }
    if (method === 'DELETE') {
      const songId = decodeURIComponent(match[3] ?? '');
      return store({ songs: current.songs.filter((entry) => entry.id !== songId) });
    }
    const order = Array.isArray(sent.songIds) ? (sent.songIds as unknown[]) : [];
    const entries = order.map((songId) => current.songs.find((entry) => entry.id === songId));
    if (
      entries.length !== current.songs.length ||
      new Set(order).size !== order.length ||
      entries.some((entry) => entry === undefined)
    ) {
      return Promise.resolve(problem(409, 'order_mismatch', { current }));
    }
    return store({ songs: entries.filter((entry) => entry !== undefined) });
  });

  return server;
}
