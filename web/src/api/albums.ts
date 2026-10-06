import { apiFetch } from './client';
import { patchWithRevision, type SaveResult } from './saves';
import { body, isErrorMap, isRecord, useResource } from './songs';

const ALBUMS_PATH = 'api/v1/albums';

/** The limits the API checks, in UTF-16 code units once normalised. */
export const ALBUM_TITLE_MAXIMUM_LENGTH = 300;
export const ALBUM_DESCRIPTION_MAXIMUM_LENGTH = 10_000;
export const ALBUM_RIGHTS_MAXIMUM_LENGTH = 500;
export const ALBUM_LINK_MAXIMUM_COUNT = 20;

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
    value.warnings.every(isAlbumWarning)
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
