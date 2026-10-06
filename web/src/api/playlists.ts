import { isArtwork, type Artwork, type ArtworkCrop } from './artwork';
import { apiFetch } from './client';
import { deleteCollection, type DeleteCollectionResult } from './collectionDeletion';
import { ifMatch, patchWithRevision, type SaveResult } from './saves';
import { body, isErrorMap, isRecord, useResource } from './songs';

const PLAYLISTS_PATH = 'api/v1/playlists';

/** The limits the API checks, in UTF-16 code units once normalised. */
export const PLAYLIST_TITLE_MAXIMUM_LENGTH = 300;
export const PLAYLIST_DESCRIPTION_MAXIMUM_LENGTH = 10_000;

/** The most Songs a Playlist holds. */
export const PLAYLIST_MAXIMUM_SONGS = 1000;

/** The page size the list is asked for: the API's default. */
export const PLAYLISTS_PAGE_SIZE = 50;

/** A Song on a Playlist, as the Playlist page shows it. */
export interface PlaylistSong {
  id: string;
  shortcode: string;
  title: string;
  primaryArtist: { id: string; name: string } | null;
  state: { id: string; name: string; colour: string };
  /** False shows the "No Selected Generation" indicator. */
  hasSelectedGeneration: boolean;
}

/** A Playlist as the list shows it. Times are UTC ISO 8601. */
export interface PlaylistSummary {
  id: string;
  title: string;
  description: string | null;
  songCount: number;
  createdAt: string;
  updatedAt: string;
  revision: number;
  /** Its own artwork, or null when it has none (never its Songs'). */
  artwork: Artwork | null;
}

/** A Playlist as its page shows it: with every Song on it, in order. */
export interface Playlist extends PlaylistSummary {
  songs: PlaylistSong[];
}

export interface PlaylistPage {
  items: PlaylistSummary[];
  page: number;
  pageSize: number;
  total: number;
}

const isNamed = (value: unknown, name: 'name' | 'title') =>
  isRecord(value) && typeof value.id === 'string' && typeof value[name] === 'string';

function isPlaylistSong(value: unknown): value is PlaylistSong {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.shortcode === 'string' &&
    typeof value.title === 'string' &&
    (value.primaryArtist === null || isNamed(value.primaryArtist, 'name')) &&
    isRecord(value.state) &&
    typeof value.state.id === 'string' &&
    typeof value.state.name === 'string' &&
    typeof value.state.colour === 'string' &&
    typeof value.hasSelectedGeneration === 'boolean'
  );
}

function isPlaylistSummary(value: unknown): value is PlaylistSummary {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.title === 'string' &&
    (value.description === null || typeof value.description === 'string') &&
    typeof value.songCount === 'number' &&
    typeof value.createdAt === 'string' &&
    typeof value.updatedAt === 'string' &&
    typeof value.revision === 'number' &&
    (value.artwork === null || isArtwork(value.artwork))
  );
}

export function isPlaylist(value: unknown): value is Playlist {
  return (
    isPlaylistSummary(value) &&
    isRecord(value) &&
    Array.isArray(value.songs) &&
    value.songs.every(isPlaylistSong)
  );
}

function isPlaylistPage(value: unknown): value is PlaylistPage {
  return (
    isRecord(value) &&
    Array.isArray(value.items) &&
    value.items.every(isPlaylistSummary) &&
    typeof value.page === 'number' &&
    typeof value.pageSize === 'number' &&
    typeof value.total === 'number'
  );
}

const acceptPage = (answer: unknown) => (isPlaylistPage(answer) ? answer : undefined);
const acceptPlaylist = (answer: unknown) => (isPlaylist(answer) ? answer : undefined);

/** One page of Playlists, by title. */
export function usePlaylists(page: number) {
  return useResource(
    page === 1 ? PLAYLISTS_PATH : `${PLAYLISTS_PATH}?page=${String(page)}`,
    acceptPage,
  );
}

/** One Playlist with its Songs, by its ID. */
export function usePlaylist(id: string) {
  return useResource(`${PLAYLISTS_PATH}/${encodeURIComponent(id)}`, acceptPlaylist);
}

/** How a create ended, by the API's answer. Never a rejection. */
export type CreatePlaylistResult =
  | { kind: 'created'; playlist: Playlist }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'failed' };

/** Creates an empty Playlist with a title. */
export async function createPlaylist(title: string): Promise<CreatePlaylistResult> {
  try {
    const response = await apiFetch(PLAYLISTS_PATH, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ title }),
    });
    const answer = await body(response);
    if (response.status === 201 && isPlaylist(answer)) {
      return { kind: 'created', playlist: answer };
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

/** An edit of a Playlist: only the fields given change; null clears the description. */
export interface PlaylistEdit {
  title?: string;
  description?: string | null;
  /** An uploaded asset's ID to show as the Playlist's artwork, or null to remove it. */
  artworkAssetId?: string | null;
  /** The artwork's square crop, in pixels of the original, or null for the centred square. */
  artworkCrop?: ArtworkCrop | null;
}

/** Edits a Playlist's title, description, or artwork, based on `playlist`'s revision. */
export function updatePlaylist(
  playlist: Pick<Playlist, 'id' | 'revision'>,
  edit: PlaylistEdit,
): Promise<SaveResult<Playlist>> {
  return patchWithRevision(
    `${PLAYLISTS_PATH}/${encodeURIComponent(playlist.id)}`,
    playlist.revision,
    { ...edit },
    acceptPlaylist,
  );
}

/**
 * Deletes a Playlist with its entries and artwork, based on `playlist`'s revision. Its Songs are
 * not deleted.
 */
export function deletePlaylist(
  playlist: Pick<Playlist, 'id' | 'revision'>,
): Promise<DeleteCollectionResult<Playlist>> {
  return deleteCollection(
    `${PLAYLISTS_PATH}/${encodeURIComponent(playlist.id)}`,
    playlist.revision,
    acceptPlaylist,
  );
}

/**
 * How a change to a Playlist's Songs ended. Never a rejection. Every refusal but `failed` carries
 * the Playlist as it is now: `conflict` (it changed elsewhere, or the order no longer matches),
 * `duplicate` (the Song is on it already), `full` (it holds the most Songs it can).
 */
export type PlaylistSongsResult =
  | { kind: 'saved'; playlist: Playlist }
  | { kind: 'conflict'; current: Playlist }
  | { kind: 'duplicate'; current: Playlist }
  | { kind: 'full'; current: Playlist }
  | { kind: 'not-found' }
  | { kind: 'failed' };

const REFUSALS: Record<string, 'conflict' | 'duplicate' | 'full'> = {
  revision_conflict: 'conflict',
  order_mismatch: 'conflict',
  song_already_on_playlist: 'duplicate',
  playlist_full: 'full',
};

async function changeSongs(
  method: 'POST' | 'PUT' | 'DELETE',
  path: string,
  revision: number,
  payload?: Record<string, unknown>,
): Promise<PlaylistSongsResult> {
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
      return isPlaylist(answer) ? { kind: 'saved', playlist: answer } : { kind: 'failed' };
    }
    if (response.status === 409 && isRecord(answer) && typeof answer.code === 'string') {
      const kind = REFUSALS[answer.code];
      if (kind !== undefined && isPlaylist(answer.current)) {
        return { kind, current: answer.current };
      }
    }
    return response.status === 404 ? { kind: 'not-found' } : { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

const songsPath = (playlist: Pick<Playlist, 'id'>) =>
  `${PLAYLISTS_PATH}/${encodeURIComponent(playlist.id)}/songs`;

/** Adds a Song at the end of the Playlist, based on `playlist`'s revision. */
export function addPlaylistSong(
  playlist: Pick<Playlist, 'id' | 'revision'>,
  songId: string,
): Promise<PlaylistSongsResult> {
  return changeSongs('POST', songsPath(playlist), playlist.revision, { songId });
}

/** Takes a Song off the Playlist, based on `playlist`'s revision. */
export function removePlaylistSong(
  playlist: Pick<Playlist, 'id' | 'revision'>,
  songId: string,
): Promise<PlaylistSongsResult> {
  return changeSongs(
    'DELETE',
    `${songsPath(playlist)}/${encodeURIComponent(songId)}`,
    playlist.revision,
  );
}

/** Puts the Playlist's Songs in the order of `songIds` (every Song on it, once), based on its revision. */
export function reorderPlaylistSongs(
  playlist: Pick<Playlist, 'id' | 'revision'>,
  songIds: string[],
): Promise<PlaylistSongsResult> {
  return changeSongs('PUT', songsPath(playlist), playlist.revision, { songIds });
}

/** What a title is shown and stored as: line breaks become spaces, then trimmed. */
export function normalisePlaylistTitle(title: string): string {
  return title.replace(/\r\n|\r|\n/g, ' ').trim();
}

/** Why a title cannot be saved, or undefined when it can. */
export function playlistTitleError(title: string): string | undefined {
  const normalised = normalisePlaylistTitle(title);
  if (normalised === '') {
    return 'Enter a title.';
  }
  if (normalised.length > PLAYLIST_TITLE_MAXIMUM_LENGTH) {
    return `Use at most ${String(PLAYLIST_TITLE_MAXIMUM_LENGTH)} characters.`;
  }
  return undefined;
}
