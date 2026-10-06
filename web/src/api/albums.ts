import { isArtwork, type Artwork, type ArtworkCrop } from './artwork';
import { apiFetch } from './client';
import { deleteCollection, type DeleteCollectionResult } from './collectionDeletion';
import { ifMatch, patchWithRevision, type SaveResult } from './saves';
import { body, isErrorMap, isRecord, useResource } from './songs';

const ALBUMS_PATH = 'api/v1/albums';

/** The limits the API checks, in UTF-16 code units once normalised. */
export const ALBUM_TITLE_MAXIMUM_LENGTH = 300;
export const ALBUM_DESCRIPTION_MAXIMUM_LENGTH = 10_000;
export const ALBUM_RIGHTS_MAXIMUM_LENGTH = 500;
export const ALBUM_LINK_MAXIMUM_COUNT = 20;

/** The highest disc and track number; numbers start at 1. */
export const ALBUM_TRACK_MAXIMUM_NUMBER = 999;

/** The page size the list is asked for: the API's default. */
export const ALBUMS_PAGE_SIZE = 50;

/** An external link of an Album: an http or https URL and an optional label. */
export interface AlbumLink {
  label: string | null;
  url: string;
}

/** Something allowed but worth telling the user, such as a UPC/EAN another Album has. */
export interface AlbumWarning {
  code: string;
  /** The field it is about, as the API names it (`upc`). */
  field: string;
  message: string;
  albums: { id: string; title: string }[];
}

/** A Song on an Album, as a track with its disc and track number. */
export interface AlbumTrack {
  songId: string;
  shortcode: string;
  title: string;
  primaryArtist: { id: string; name: string } | null;
  state: { id: string; name: string; colour: string };
  disc: number;
  track: number;
  /** False marks the track incomplete. */
  hasSelectedGeneration: boolean;
}

/** An Album as the API answers it. Dates are partial dates as entered; times are UTC ISO 8601. */
export interface Album {
  id: string;
  title: string;
  description: string | null;
  albumArtist: { id: string; name: string } | null;
  /** `YYYY`, `YYYY-MM`, or `YYYY-MM-DD`, or null. */
  releaseDate: string | null;
  originalReleaseDate: string | null;
  /** 12 or 13 digits, or null. */
  upc: string | null;
  copyright: string | null;
  publishing: string | null;
  links: AlbumLink[];
  songCount: number;
  createdAt: string;
  updatedAt: string;
  revision: number;
  warnings: AlbumWarning[];
  /** Its tracks, by disc and then track number. */
  tracks: AlbumTrack[];
  /** Its own artwork, or null when it has none (never its Songs'). */
  artwork: Artwork | null;
}

export interface AlbumPage {
  items: Album[];
  page: number;
  pageSize: number;
  total: number;
}

export type AlbumSort = 'title' | 'releaseDate' | 'artist';

/** What the Albums list shows: the list's own query parameters. */
export interface AlbumQuery {
  sort: AlbumSort;
  direction: 'asc' | 'desc';
  page: number;
}

export const DEFAULT_ALBUM_QUERY: AlbumQuery = { sort: 'title', direction: 'asc', page: 1 };

const isNullableString = (value: unknown) => value === null || typeof value === 'string';

function isNamed(value: unknown, name: 'name' | 'title'): boolean {
  return isRecord(value) && typeof value.id === 'string' && typeof value[name] === 'string';
}

function isAlbumLink(value: unknown): value is AlbumLink {
  return isRecord(value) && isNullableString(value.label) && typeof value.url === 'string';
}

function isAlbumWarning(value: unknown): value is AlbumWarning {
  return (
    isRecord(value) &&
    typeof value.code === 'string' &&
    typeof value.field === 'string' &&
    typeof value.message === 'string' &&
    Array.isArray(value.albums) &&
    value.albums.every((album) => isNamed(album, 'title'))
  );
}

function isAlbumTrack(value: unknown): value is AlbumTrack {
  return (
    isRecord(value) &&
    typeof value.songId === 'string' &&
    typeof value.shortcode === 'string' &&
    typeof value.title === 'string' &&
    (value.primaryArtist === null || isNamed(value.primaryArtist, 'name')) &&
    isRecord(value.state) &&
    typeof value.state.id === 'string' &&
    typeof value.state.name === 'string' &&
    typeof value.state.colour === 'string' &&
    typeof value.disc === 'number' &&
    typeof value.track === 'number' &&
    typeof value.hasSelectedGeneration === 'boolean'
  );
}

export function isAlbum(value: unknown): value is Album {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.title === 'string' &&
    isNullableString(value.description) &&
    (value.albumArtist === null || isNamed(value.albumArtist, 'name')) &&
    isNullableString(value.releaseDate) &&
    isNullableString(value.originalReleaseDate) &&
    isNullableString(value.upc) &&
    isNullableString(value.copyright) &&
    isNullableString(value.publishing) &&
    Array.isArray(value.links) &&
    value.links.every(isAlbumLink) &&
    typeof value.songCount === 'number' &&
    typeof value.createdAt === 'string' &&
    typeof value.updatedAt === 'string' &&
    typeof value.revision === 'number' &&
    Array.isArray(value.warnings) &&
    value.warnings.every(isAlbumWarning) &&
    Array.isArray(value.tracks) &&
    value.tracks.every(isAlbumTrack) &&
    (value.artwork === null || isArtwork(value.artwork))
  );
}

function isAlbumPage(value: unknown): value is AlbumPage {
  return (
    isRecord(value) &&
    Array.isArray(value.items) &&
    value.items.every(isAlbum) &&
    typeof value.page === 'number' &&
    typeof value.pageSize === 'number' &&
    typeof value.total === 'number'
  );
}

/** The list's query string for `query`; values that are the API's defaults are left out. */
export function albumListParameters(query: AlbumQuery): URLSearchParams {
  const parameters = new URLSearchParams();
  if (query.sort !== 'title') {
    parameters.set('sort', query.sort);
  }
  if (query.direction !== 'asc') {
    parameters.set('direction', query.direction);
  }
  if (query.page !== 1) {
    parameters.set('page', String(query.page));
  }
  return parameters;
}

/** Reads the list's view out of a page URL's query string; anything not understood is the default. */
export function albumQueryFrom(parameters: URLSearchParams): AlbumQuery {
  const sort = parameters.get('sort');
  const page = Number(parameters.get('page') ?? '1');
  return {
    sort: sort === 'releaseDate' || sort === 'artist' ? sort : 'title',
    direction: parameters.get('direction') === 'desc' ? 'desc' : 'asc',
    page: Number.isSafeInteger(page) && page >= 1 ? page : 1,
  };
}

const acceptPage = (answer: unknown) => (isAlbumPage(answer) ? answer : undefined);
const acceptAlbum = (answer: unknown) => (isAlbum(answer) ? answer : undefined);

/** One page of Albums for the list's view; with `artist`, only the Albums it is Album Artist of. */
export function useAlbums(query: AlbumQuery, artist?: string) {
  const parameters = albumListParameters(query);
  if (artist !== undefined) {
    parameters.set('artist', artist);
  }
  const text = parameters.toString();
  return useResource(text ? `${ALBUMS_PATH}?${text}` : ALBUMS_PATH, acceptPage);
}

/** One Album, by its ID. */
export function useAlbum(id: string) {
  return useResource(`${ALBUMS_PATH}/${encodeURIComponent(id)}`, acceptAlbum);
}

/** How a create ended, by the API's answer. Never a rejection. */
export type CreateAlbumResult =
  | { kind: 'created'; album: Album }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'failed' };

/** Creates an Album with a title; everything else is set on its page. */
export async function createAlbum(title: string): Promise<CreateAlbumResult> {
  try {
    const response = await apiFetch(ALBUMS_PATH, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ title }),
    });
    const answer = await body(response);
    if (response.status === 201 && isAlbum(answer)) {
      return { kind: 'created', album: answer };
    }
    if (
      response.status === 422 &&
      isRecord(answer) &&
      answer.code === 'validation_failed' &&
      isErrorMap(answer.errors)
    ) {
      return { kind: 'invalid', errors: answer.errors };
    }
    return { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/** An edit of an Album: only the fields given change; null clears an optional one; links replace the list. */
export interface AlbumEdit {
  title?: string;
  description?: string | null;
  albumArtistId?: string | null;
  releaseDate?: string | null;
  originalReleaseDate?: string | null;
  upc?: string | null;
  copyright?: string | null;
  publishing?: string | null;
  links?: AlbumLink[];
  /** An uploaded asset's ID to show as the Album's artwork, or null to remove it. */
  artworkAssetId?: string | null;
  /** The artwork's square crop, in pixels of the original, or null for the centred square. */
  artworkCrop?: ArtworkCrop | null;
}

/** Edits an Album, based on `album`'s revision; a stale revision comes back as a conflict. */
export function updateAlbum(
  album: Pick<Album, 'id' | 'revision'>,
  edit: AlbumEdit,
): Promise<SaveResult<Album>> {
  return patchWithRevision(
    `${ALBUMS_PATH}/${encodeURIComponent(album.id)}`,
    album.revision,
    { ...edit },
    acceptAlbum,
  );
}

/**
 * Deletes an Album with its tracks, links, and artwork, based on `album`'s revision. Its Songs are
 * not deleted.
 */
export function deleteAlbum(
  album: Pick<Album, 'id' | 'revision'>,
): Promise<DeleteCollectionResult<Album>> {
  return deleteCollection(
    `${ALBUMS_PATH}/${encodeURIComponent(album.id)}`,
    album.revision,
    acceptAlbum,
  );
}

/** A track's place, as the full-list PUT sends it. */
export interface AlbumTrackPlace {
  songId: string;
  disc: number;
  track: number;
}

/**
 * How a change to an Album's tracks ended. Never a rejection. Every refusal but `not-found` and
 * `failed` carries the Album as it is now: `conflict` (it changed elsewhere, or the list no longer
 * matches its Songs), `duplicate` (the Song is on it already), `disc-full` (the last disc has track
 * 999), `taken` (two tracks on a disc would share a number; `message` names the one holding it).
 */
export type AlbumTracksResult =
  | { kind: 'saved'; album: Album }
  | { kind: 'conflict'; current: Album }
  | { kind: 'duplicate'; current: Album }
  | { kind: 'disc-full'; current: Album; message: string }
  | { kind: 'taken'; current: Album; message: string }
  | { kind: 'invalid'; message: string }
  | { kind: 'not-found' }
  | { kind: 'failed' };

const TRACK_REFUSALS: Record<string, 'conflict' | 'duplicate' | 'disc-full' | 'taken'> = {
  revision_conflict: 'conflict',
  order_mismatch: 'conflict',
  song_already_on_album: 'duplicate',
  disc_full: 'disc-full',
  track_number_taken: 'taken',
};

async function changeTracks(
  method: 'POST' | 'PUT' | 'DELETE',
  path: string,
  revision: number,
  payload?: Record<string, unknown>,
): Promise<AlbumTracksResult> {
  try {
    const headers: Record<string, string> = { 'If-Match': ifMatch(revision) };
    if (payload !== undefined) {
      headers['Content-Type'] = 'application/json';
    }
    const response = await apiFetch(path, {
      method,
      headers,
      ...(payload === undefined ? {} : { body: JSON.stringify(payload) }),
    });
    const answer = await body(response);
    if (response.ok) {
      return isAlbum(answer) ? { kind: 'saved', album: answer } : { kind: 'failed' };
    }
    if (response.status === 409 && isRecord(answer) && typeof answer.code === 'string') {
      const kind = TRACK_REFUSALS[answer.code];
      if (kind !== undefined && isAlbum(answer.current)) {
        const message = typeof answer.title === 'string' ? answer.title : '';
        return kind === 'disc-full' || kind === 'taken'
          ? { kind, current: answer.current, message }
          : { kind, current: answer.current };
      }
    }
    if (
      response.status === 422 &&
      isRecord(answer) &&
      answer.code === 'validation_failed' &&
      isErrorMap(answer.errors)
    ) {
      return { kind: 'invalid', message: Object.values(answer.errors).flat()[0] ?? '' };
    }
    return response.status === 404 ? { kind: 'not-found' } : { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

const tracksPath = (album: Pick<Album, 'id'>) =>
  `${ALBUMS_PATH}/${encodeURIComponent(album.id)}/tracks`;

/** Adds a Song at the end of the Album's last disc, based on `album`'s revision. */
export function addAlbumTrack(
  album: Pick<Album, 'id' | 'revision'>,
  songId: string,
): Promise<AlbumTracksResult> {
  return changeTracks('POST', tracksPath(album), album.revision, { songId });
}

/** Takes a Song off the Album, based on `album`'s revision; the API renumbers the rest of its disc. */
export function removeAlbumTrack(
  album: Pick<Album, 'id' | 'revision'>,
  songId: string,
): Promise<AlbumTracksResult> {
  return changeTracks(
    'DELETE',
    `${tracksPath(album)}/${encodeURIComponent(songId)}`,
    album.revision,
  );
}

/** Makes `tracks` (every Song on the Album, once) the Album's tracks, based on its revision. */
export function setAlbumTracks(
  album: Pick<Album, 'id' | 'revision'>,
  tracks: readonly AlbumTrackPlace[],
): Promise<AlbumTracksResult> {
  return changeTracks('PUT', tracksPath(album), album.revision, {
    tracks: tracks.map((track) => ({
      songId: track.songId,
      disc: track.disc,
      track: track.track,
    })),
  });
}
