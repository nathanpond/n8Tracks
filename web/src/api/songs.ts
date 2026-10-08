import { useCallback, useEffect, useState } from 'react';
import { apiFetch } from './client';
import { isSongArtwork, type ArtworkCrop, type SongArtwork } from './artwork';
import { patchWithRevision, writeWithRevision, type SaveResult } from './saves';
import { isSongWorkspace, type SongWorkspace } from './sunoWorkspaces';

const SONGS_PATH = 'api/v1/songs';
export const WORKFLOW_STATES_PATH = 'api/v1/workflow-states';

/** The longest title and concept the API takes, in UTF-16 code units after trimming. */
export const TITLE_MAXIMUM_LENGTH = 300;
export const CONCEPT_MAXIMUM_LENGTH = 2000;

/** The longest Song notes the API takes, in UTF-16 code units once normalised. */
export const SONG_NOTES_MAXIMUM_LENGTH = 10_000;

/** The `genre` filter value that matches Songs with no Genre. */
export const NO_GENRE = 'none';

/** The `tag` filter value that matches Songs with no Tag. */
export const NO_TAG = 'none';

/** The `artist` filter value that matches Songs credited to no one. */
export const NO_ARTIST = 'none';

/** The most featured Artists a Song may have. */
export const FEATURED_ARTISTS_MAXIMUM = 50;

/** The page size the list is asked for: the API's default. */
export const SONGS_PAGE_SIZE = 50;

/** What a Version creates, as its `kind` option and the API's `kind` say it. */
export type VersionKind = 'song' | 'speech' | 'sound';

const KINDS: readonly string[] = ['song', 'speech', 'sound'];

export function isVersionKind(value: unknown): value is VersionKind {
  return typeof value === 'string' && KINDS.includes(value);
}

/** A kind as the page writes it: Song, Speech, or Sound. */
export function kindLabel(kind: VersionKind): string {
  return kind.charAt(0).toUpperCase() + kind.slice(1);
}

/** A Genre as a Song shows it. */
export interface SongGenre {
  id: string;
  name: string;
}

/** A Tag as a Song shows it: its name and the name of its palette colour. */
export interface SongTag {
  id: string;
  name: string;
  colour: string;
}

/** An Artist as a Song's credits show it. */
export interface SongArtist {
  id: string;
  name: string;
}

/** Who a Song is credited to: one primary Artist or none, and featured Artists in the user's order. */
export interface SongCredits {
  primary: SongArtist | null;
  featured: SongArtist[];
}

/** A Playlist as a Song names it. */
export interface SongPlaylist {
  id: string;
  title: string;
}

/** An Album as a Song names it: its ID and title, and the Song's disc and track on it. */
export interface SongAlbum {
  id: string;
  title: string;
  disc: number;
  track: number;
}

/** Which way a relationship reads from a Song: by its type's forward name or its reverse name. */
export type RelationshipDirection = 'forward' | 'reverse';

/**
 * A relationship as a Song shows it: its type, the type's name as read from this Song, which way
 * that is, and the other Song.
 */
export interface SongRelationship {
  id: string;
  typeId: string;
  name: string;
  direction: RelationshipDirection;
  song: { id: string; shortcode: string; title: string };
}

/** The longest copyright or publishing text the API takes, once normalised. */
export const SONG_RIGHTS_MAXIMUM_LENGTH = 500;

/** The most links a Song's release details may have. */
export const SONG_LINK_MAXIMUM_COUNT = 20;

/** Whether a Song is marked explicit or clean; null when not set. */
export type ExplicitContent = 'explicit' | 'clean';

/** An external link: its label (null for none) and an http or https URL. */
export interface SongLink {
  label: string | null;
  url: string;
}

/**
 * How a Song is released, as the API answers it: every member null when not set. Dates are
 * partial dates as entered (`YYYY`, `YYYY-MM`, `YYYY-MM-DD`); the ISRC is 12 characters, upper
 * case; the language is a code from the API's list.
 */
export interface SongRelease {
  releaseDate: string | null;
  originalReleaseDate: string | null;
  explicit: ExplicitContent | null;
  copyright: string | null;
  publishing: string | null;
  isrc: string | null;
  language: string | null;
  links: SongLink[];
}

/** A Song with no release details. */
export const NO_RELEASE: SongRelease = {
  releaseDate: null,
  originalReleaseDate: null,
  explicit: null,
  copyright: null,
  publishing: null,
  isrc: null,
  language: null,
  links: [],
};

/** Something allowed but worth telling the user: `duplicate_isrc` names the other Songs with the ISRC. */
export interface SongWarning {
  code: string;
  field: string;
  message: string;
  songs: { id: string; shortcode: string; title: string }[];
}

/** A Song as the API answers it. Times are UTC ISO 8601. */
export interface Song {
  id: string;
  shortcode: string;
  title: string;
  concept: string | null;
  state: { id: string; name: string; colour: string };
  /** The Version the user is working from, and what it creates. */
  currentVersion: { id: string; number: string; shortcode: string; kind: VersionKind };
  versionCount: number;
  createdAt: string;
  updatedAt: string;
  revision: number;
  /** Free-form notes; null when there are none. */
  notes: string | null;
  /** Its Genres, alphabetically. */
  genres: SongGenre[];
  /** Its Tags, alphabetically ignoring case. */
  tags: SongTag[];
  /** Its primary and featured Artists. */
  credits: SongCredits;
  /** The Playlists it is on, by title. */
  playlists: SongPlaylist[];
  /** The Albums it is on, by title, with its disc and track on each. */
  albums: SongAlbum[];
  /** Its relationships to other Songs, each read from this Song: by name as seen from here, then the other Song's title. */
  relationships: SongRelationship[];
  /** Its release details; every member null (and no links) when not set. */
  release: SongRelease;
  /** What is allowed but worth telling the user, such as an ISRC another Song has. */
  warnings: SongWarning[];
  /**
   * What it shows as artwork: its own (`source` `own`), or else its Selected Generation's image
   * (`selectedGeneration`, uncropped), or null when it has neither.
   */
  artwork: SongArtwork | null;
  /** Whether it has a Selected Generation. */
  hasSelectedGeneration: boolean;
  /** Its Selected Generation, the Song's chosen output, with that Generation's states; null when it has none. */
  selectedGeneration: SelectedGeneration | null;
  /** The Suno workspace it lives in (#129), by Suno's ID, with its name and state; null when it is in none. */
  sunoWorkspace: SongWorkspace | null;
  /**
   * How many local audio files are associated with it (#211), at Song level or through its
   * Generations, whatever their status. The API always sends it; test fixtures may leave it out.
   */
  audioFileCount?: number;
  /**
   * The highest rating of its live Generations (#226), or null when none is rated. The API always
   * sends it; test fixtures may leave it out.
   */
  highestRating?: number | null;
  /**
   * What Play on it does (#219), as the server's playback rule decides it. The API always sends it;
   * test fixtures may leave it out, and then Play asks the server when pressed.
   */
  playback?: SongPlayability;
  /**
   * With a full-text search (#223), the Song's best matches, best first (at most three); absent
   * otherwise.
   */
  matches?: SongMatch[];
  /** With a full-text search, how many places the Song matched in all; absent otherwise. */
  matchCount?: number;
}

/**
 * What a matched field belongs to (#223): a Version or Generation (by shortcode, `label` such as
 * `v2.1` or `v2.1-g3`), or an Album or Playlist (by ID, `label` its title). `state` marks text in
 * an archived Version, or an archived or trashed Generation.
 */
export interface SongMatchOwner {
  kind: 'version' | 'generation' | 'album' | 'playlist';
  reference: string;
  label: string;
  state: 'active' | 'archived' | 'trashed';
}

/** A matched word: where it starts in the excerpt's text, and how long it is (UTF-16 code units). */
export interface SongMatchHighlight {
  start: number;
  length: number;
}

/**
 * One place a searched Song matched (#223): the field (`lyrics`, `title`, …), what it belongs to
 * (null for the Song's own text and its Tags), and an excerpt with the matched words as offsets.
 */
export interface SongMatch {
  field: string;
  owner: SongMatchOwner | null;
  excerpt: { text: string; highlights: SongMatchHighlight[] };
}

function isSongMatchOwner(value: unknown): value is SongMatchOwner {
  return (
    isRecord(value) &&
    (value.kind === 'version' ||
      value.kind === 'generation' ||
      value.kind === 'album' ||
      value.kind === 'playlist') &&
    typeof value.reference === 'string' &&
    typeof value.label === 'string' &&
    (value.state === 'active' || value.state === 'archived' || value.state === 'trashed')
  );
}

export function isSongMatch(value: unknown): value is SongMatch {
  return (
    isRecord(value) &&
    typeof value.field === 'string' &&
    (value.owner === null || isSongMatchOwner(value.owner)) &&
    isRecord(value.excerpt) &&
    typeof value.excerpt.text === 'string' &&
    Array.isArray(value.excerpt.highlights) &&
    value.excerpt.highlights.every(
      (highlight) =>
        isRecord(highlight) &&
        typeof highlight.start === 'number' &&
        typeof highlight.length === 'number',
    )
  );
}

/**
 * What Play on a Song does (#219): `ready` plays its choice (its Song-level preferred file or its
 * Selected Generation's file), `needs-choice` asks which Generation to play (nothing is selected),
 * `selected-unplayable` says the Selected Generation has nothing to play, and `none` has nothing to
 * offer: only it disables Play. `reason` is null when ready, and a playback reason code otherwise.
 */
export type SongPlaybackState = 'ready' | 'needs-choice' | 'selected-unplayable' | 'none';

/** What Play on a Song does, as Song answers, Album tracks and Playlist songs carry it (#219). */
export interface SongPlayability {
  state: SongPlaybackState;
  reason: string | null;
}

const SONG_PLAYBACK_STATES: readonly string[] = [
  'ready',
  'needs-choice',
  'selected-unplayable',
  'none',
];

export function isSongPlaybackState(value: unknown): value is SongPlaybackState {
  return typeof value === 'string' && SONG_PLAYBACK_STATES.includes(value);
}

/** Whether `value` is a Song's playability; an absent one (`undefined`) is allowed by the callers. */
export function isSongPlayability(value: unknown): value is SongPlayability {
  return (
    isRecord(value) &&
    isSongPlaybackState(value.state) &&
    (value.reason === null || typeof value.reason === 'string')
  );
}

/**
 * A Song's Selected Generation as the Song shows it: the Generation, its shortcode, its own state
 * (`active` or `archived`), and whether Suno still lists the clip (`present`, `trashed`, `missing`).
 */
export interface SelectedGeneration {
  id: string;
  shortcode: string;
  state: 'active' | 'archived';
  remoteState: 'present' | 'trashed' | 'missing';
}

function isSelectedGeneration(value: unknown): value is SelectedGeneration {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.shortcode === 'string' &&
    (value.state === 'active' || value.state === 'archived') &&
    (value.remoteState === 'present' ||
      value.remoteState === 'trashed' ||
      value.remoteState === 'missing')
  );
}

export interface SongPage {
  items: Song[];
  page: number;
  pageSize: number;
  total: number;
  /** With a full-text search, whether the index is being rebuilt (results may be missing); absent otherwise. */
  indexRebuilding?: boolean;
}

export interface WorkflowState {
  id: string;
  name: string;
  colour: string;
  order: number;
  hidden: boolean;
  /** How many Songs are in it. The API always sends it; test fixtures may leave it out. */
  songCount?: number;
}

/**
 * How the table is sorted (#226: title, creation date, last update, highest Generation rating,
 * workflow state, last Generation date; #211: local audio files). `relevance` is the order of a
 * full-text search, best first: the list's default while searching, never sent (the API orders by
 * relevance when `sort` is left out), and with no direction of its own.
 */
export type SongSort =
  | 'updated'
  | 'title'
  | 'audioFiles'
  | 'relevance'
  | 'created'
  | 'rating'
  | 'state'
  | 'lastGeneration';
export type SortDirection = 'asc' | 'desc';

/** What the Songs table shows: the list's own query parameters. */
export interface SongQuery {
  sort: SongSort;
  direction: SortDirection;
  states: string[];
  /** Genre IDs, and {@link NO_GENRE} for Songs with none: Songs with any of them. */
  genres: string[];
  /** Tag IDs, and {@link NO_TAG} for Songs with none: Songs with any of them. */
  tags: string[];
  /** Artist IDs, and {@link NO_ARTIST} for Songs credited to no one: Songs crediting any of them. */
  artists: string[];
  /**
   * A title as typed: Songs with that title, ignoring case and spacing (the server compares). Left
   * out (undefined) for every title; never blank.
   */
  title?: string;
  /**
   * Full-text search text (#224), as typed and cut to {@link SEARCH_MAXIMUM_LENGTH}: Songs matching
   * every word, each with where it matched. Left out (undefined) for no search; never blank.
   */
  search?: string;
  /** #225: `any` for Songs with any of {@link tags}; left out for every one of them (the API's default). */
  tagMode?: TagMode;
  /** #225: only Songs in the Archived state, or only the others; left out for both. */
  archived?: ArchivedFilter;
  /** #225: an Album's ID: only the Songs on it. */
  album?: string;
  /** #225: a Playlist's ID: only the Songs on it. */
  playlist?: string;
  /** #225: models as Generations report them: Songs with a Generation reporting any of them. */
  models?: string[];
  /** #225: a day, `yyyy-mm-dd`, in the configured zone: Songs created on it or later. */
  createdFrom?: string;
  /** #225: a day, `yyyy-mm-dd`, in the configured zone: Songs created on it or earlier. */
  createdTo?: string;
  /** #225: 1 to 5: Songs whose highest Generation rating is at least it. */
  minRating?: number;
  /** #225: `none`: Songs with no rated Generation (with {@link minRating}, either). */
  rated?: 'none';
  /** #225: Songs with (`yes`) or without (`no`) a Selected Generation. */
  selected?: SelectedFilter;
  /** #225: local-audio availability. */
  audio?: AudioFilter;
  /** #228: Songs with at least one Generation, in any state (`some`), or with none (`none`). */
  generations?: GenerationsFilter;
  page: number;
}

export type TagMode = 'all' | 'any';
export type ArchivedFilter = 'active' | 'archived';
export type SelectedFilter = 'yes' | 'no';
export type AudioFilter = 'available' | 'unavailable' | 'none';
export type GenerationsFilter = 'some' | 'none';

/** The ratings a Generation can have, and so the minimum ratings the filter offers. */
export const RATING_SCALE = [1, 2, 3, 4, 5] as const;

const ARCHIVED_FILTERS: readonly string[] = ['active', 'archived'];
const SELECTED_FILTERS: readonly string[] = ['yes', 'no'];
const AUDIO_FILTERS: readonly string[] = ['available', 'unavailable', 'none'];
const GENERATIONS_FILTERS: readonly string[] = ['some', 'none'];
const DAY = /^\d{4}-\d{2}-\d{2}$/;

/** #225: how many filters are set: each chosen value, each set option (not the search or the page). */
export function activeFilterCount(query: SongQuery): number {
  return (
    query.states.length +
    query.genres.length +
    query.tags.length +
    query.artists.length +
    (query.title === undefined ? 0 : 1) +
    (query.archived === undefined ? 0 : 1) +
    (query.album === undefined ? 0 : 1) +
    (query.playlist === undefined ? 0 : 1) +
    (query.models?.length ?? 0) +
    (query.createdFrom === undefined ? 0 : 1) +
    (query.createdTo === undefined ? 0 : 1) +
    (query.minRating === undefined ? 0 : 1) +
    (query.rated === undefined ? 0 : 1) +
    (query.selected === undefined ? 0 : 1) +
    (query.audio === undefined ? 0 : 1) +
    (query.generations === undefined ? 0 : 1)
  );
}

/** The longest search text the page sends: the API reads no more. */
export const SEARCH_MAXIMUM_LENGTH = 200;

/** `text` cut to {@link SEARCH_MAXIMUM_LENGTH}; undefined when nothing but white space is left. */
export function searchTextOf(text: string): string | undefined {
  const cut = text.slice(0, SEARCH_MAXIMUM_LENGTH);
  return cut.trim() === '' ? undefined : cut;
}

/** The sort a table shows when none was chosen: relevance while searching, otherwise last update. */
export function defaultSort(query: Pick<SongQuery, 'search'>): SongSort {
  return query.search === undefined ? 'updated' : 'relevance';
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

export function isSongGenre(value: unknown): value is SongGenre {
  return isRecord(value) && typeof value.id === 'string' && typeof value.name === 'string';
}

export function isSongTag(value: unknown): value is SongTag {
  return isSongGenre(value) && isRecord(value) && typeof value.colour === 'string';
}

export function isSongArtist(value: unknown): value is SongArtist {
  return isSongGenre(value);
}

export function isSongCredits(value: unknown): value is SongCredits {
  return (
    isRecord(value) &&
    (value.primary === null || isSongArtist(value.primary)) &&
    Array.isArray(value.featured) &&
    value.featured.every(isSongArtist)
  );
}

const isText = (value: unknown) => value === null || typeof value === 'string';

export function isSongLink(value: unknown): value is SongLink {
  return isRecord(value) && isText(value.label) && typeof value.url === 'string';
}

export function isSongRelease(value: unknown): value is SongRelease {
  return (
    isRecord(value) &&
    isText(value.releaseDate) &&
    isText(value.originalReleaseDate) &&
    (value.explicit === null || value.explicit === 'explicit' || value.explicit === 'clean') &&
    isText(value.copyright) &&
    isText(value.publishing) &&
    isText(value.isrc) &&
    isText(value.language) &&
    Array.isArray(value.links) &&
    value.links.every(isSongLink)
  );
}

function isSongWarning(value: unknown): value is SongWarning {
  return (
    isRecord(value) &&
    typeof value.code === 'string' &&
    typeof value.field === 'string' &&
    typeof value.message === 'string' &&
    Array.isArray(value.songs) &&
    value.songs.every(
      (song) =>
        isRecord(song) &&
        typeof song.id === 'string' &&
        typeof song.shortcode === 'string' &&
        typeof song.title === 'string',
    )
  );
}

export function isSong(value: unknown): value is Song {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.shortcode === 'string' &&
    typeof value.title === 'string' &&
    (value.concept === null || typeof value.concept === 'string') &&
    isRecord(value.state) &&
    typeof value.state.id === 'string' &&
    typeof value.state.name === 'string' &&
    typeof value.state.colour === 'string' &&
    isRecord(value.currentVersion) &&
    typeof value.currentVersion.shortcode === 'string' &&
    isVersionKind(value.currentVersion.kind) &&
    typeof value.versionCount === 'number' &&
    typeof value.createdAt === 'string' &&
    typeof value.updatedAt === 'string' &&
    typeof value.revision === 'number' &&
    (value.notes === null || typeof value.notes === 'string') &&
    Array.isArray(value.genres) &&
    value.genres.every(isSongGenre) &&
    Array.isArray(value.tags) &&
    value.tags.every(isSongTag) &&
    isSongCredits(value.credits) &&
    Array.isArray(value.playlists) &&
    value.playlists.every(
      (playlist) =>
        isRecord(playlist) && typeof playlist.id === 'string' && typeof playlist.title === 'string',
    ) &&
    Array.isArray(value.albums) &&
    value.albums.every(
      (album) =>
        isRecord(album) &&
        typeof album.id === 'string' &&
        typeof album.title === 'string' &&
        typeof album.disc === 'number' &&
        typeof album.track === 'number',
    ) &&
    Array.isArray(value.relationships) &&
    value.relationships.every(isSongRelationship) &&
    isSongRelease(value.release) &&
    Array.isArray(value.warnings) &&
    value.warnings.every(isSongWarning) &&
    (value.artwork === null || isSongArtwork(value.artwork)) &&
    typeof value.hasSelectedGeneration === 'boolean' &&
    (value.selectedGeneration === null || isSelectedGeneration(value.selectedGeneration)) &&
    (value.sunoWorkspace === null || isSongWorkspace(value.sunoWorkspace)) &&
    (value.audioFileCount === undefined || typeof value.audioFileCount === 'number') &&
    (value.highestRating === undefined ||
      value.highestRating === null ||
      typeof value.highestRating === 'number') &&
    (value.playback === undefined || isSongPlayability(value.playback)) &&
    (value.matches === undefined ||
      (Array.isArray(value.matches) && value.matches.every(isSongMatch))) &&
    (value.matchCount === undefined || typeof value.matchCount === 'number')
  );
}

export function isSongRelationship(value: unknown): value is SongRelationship {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.typeId === 'string' &&
    typeof value.name === 'string' &&
    (value.direction === 'forward' || value.direction === 'reverse') &&
    isRecord(value.song) &&
    typeof value.song.id === 'string' &&
    typeof value.song.shortcode === 'string' &&
    typeof value.song.title === 'string'
  );
}

export function isSongPage(value: unknown): value is SongPage {
  return (
    isRecord(value) &&
    Array.isArray(value.items) &&
    value.items.every(isSong) &&
    typeof value.page === 'number' &&
    typeof value.pageSize === 'number' &&
    typeof value.total === 'number' &&
    (value.indexRebuilding === undefined || typeof value.indexRebuilding === 'boolean')
  );
}

export function isWorkflowState(value: unknown): value is WorkflowState {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.name === 'string' &&
    typeof value.colour === 'string' &&
    typeof value.order === 'number' &&
    typeof value.hidden === 'boolean' &&
    (value.songCount === undefined || typeof value.songCount === 'number')
  );
}

export function isErrorMap(value: unknown): value is Record<string, string[]> {
  return (
    isRecord(value) &&
    Object.values(value).every(
      (messages) => Array.isArray(messages) && messages.every((m) => typeof m === 'string'),
    )
  );
}

export async function body(response: Response): Promise<unknown> {
  try {
    return await response.json();
  } catch {
    return undefined;
  }
}

/** The list's query string for `query`; values that are the API's defaults are left out. */
export function songListParameters(query: SongQuery): URLSearchParams {
  const parameters = new URLSearchParams();
  // Relevance is the API's order for a search when no sort is sent, and is never sent itself.
  const sort = query.sort === 'relevance' && query.search === undefined ? 'updated' : query.sort;
  if (query.search !== undefined) {
    parameters.set('search', query.search);
  }
  if (sort !== defaultSort(query) && sort !== 'relevance') {
    parameters.set('sort', sort);
  }
  // Relevance has no direction (the API ignores one).
  if (sort !== 'relevance' && query.direction !== defaultDirection(sort)) {
    parameters.set('direction', query.direction);
  }
  for (const state of query.states) {
    parameters.append('state', state);
  }
  for (const genre of query.genres) {
    parameters.append('genre', genre);
  }
  for (const tag of query.tags) {
    parameters.append('tag', tag);
  }
  for (const artist of query.artists) {
    parameters.append('artist', artist);
  }
  if (query.title !== undefined) {
    parameters.set('title', query.title);
  }
  if (query.tagMode === 'any' && query.tags.length > 0) {
    parameters.set('tagMode', 'any');
  }
  if (query.archived !== undefined) {
    parameters.set('archived', query.archived);
  }
  if (query.album !== undefined) {
    parameters.set('album', query.album);
  }
  if (query.playlist !== undefined) {
    parameters.set('playlist', query.playlist);
  }
  for (const model of query.models ?? []) {
    parameters.append('model', model);
  }
  if (query.createdFrom !== undefined) {
    parameters.set('createdFrom', query.createdFrom);
  }
  if (query.createdTo !== undefined) {
    parameters.set('createdTo', query.createdTo);
  }
  if (query.minRating !== undefined) {
    parameters.set('minRating', String(query.minRating));
  }
  if (query.rated !== undefined) {
    parameters.set('rated', query.rated);
  }
  if (query.selected !== undefined) {
    parameters.set('selected', query.selected);
  }
  if (query.audio !== undefined) {
    parameters.set('audio', query.audio);
  }
  if (query.generations !== undefined) {
    parameters.set('generations', query.generations);
  }
  if (query.page !== 1) {
    parameters.set('page', String(query.page));
  }
  return parameters;
}

/**
 * The direction a sort starts in (#226): A to Z by title, the user's order by workflow state; newest
 * first by a date, highest first by rating, most audio files first, best first by relevance.
 */
export function defaultDirection(sort: SongSort): SortDirection {
  return sort === 'title' || sort === 'state' ? 'asc' : 'desc';
}

/** The sorts an address may name: relevance is never named, only implied by a search. */
const SONG_SORTS: readonly SongSort[] = [
  'updated',
  'title',
  'audioFiles',
  'created',
  'rating',
  'state',
  'lastGeneration',
];

/**
 * Reads a table view out of a page URL's query string. Anything it does not understand is left at
 * its default, so a hand-edited URL still shows a table.
 */
export function songQueryFrom(parameters: URLSearchParams): SongQuery {
  const search = searchTextOf(parameters.get('search') ?? '');
  const sortText = parameters.get('sort');
  const sort: SongSort = SONG_SORTS.includes(sortText as SongSort)
    ? (sortText as SongSort)
    : defaultSort({ search });
  const direction = parameters.get('direction');
  const page = Number(parameters.get('page') ?? '1');
  // A blank title would be refused, so it means no title filter.
  const title = parameters.get('title') ?? '';
  const archived = parameters.get('archived') ?? '';
  const album = parameters.get('album') ?? '';
  const playlist = parameters.get('playlist') ?? '';
  const models = [...new Set(parameters.getAll('model'))].filter((model) => model.trim() !== '');
  const createdFrom = parameters.get('createdFrom') ?? '';
  const createdTo = parameters.get('createdTo') ?? '';
  const minRating = Number(parameters.get('minRating') ?? '');
  const selected = parameters.get('selected') ?? '';
  const audio = parameters.get('audio') ?? '';
  const generations = parameters.get('generations') ?? '';
  return {
    sort,
    direction: direction === 'asc' || direction === 'desc' ? direction : defaultDirection(sort),
    states: [...new Set(parameters.getAll('state'))],
    genres: [...new Set(parameters.getAll('genre'))],
    tags: [...new Set(parameters.getAll('tag'))],
    artists: [...new Set(parameters.getAll('artist'))],
    ...(title.trim() === '' ? {} : { title }),
    ...(search === undefined ? {} : { search }),
    ...(parameters.get('tagMode') === 'any' ? { tagMode: 'any' as const } : {}),
    ...(ARCHIVED_FILTERS.includes(archived) ? { archived: archived as ArchivedFilter } : {}),
    ...(album === '' ? {} : { album }),
    ...(playlist === '' ? {} : { playlist }),
    ...(models.length === 0 ? {} : { models }),
    ...(DAY.test(createdFrom) ? { createdFrom } : {}),
    ...(DAY.test(createdTo) ? { createdTo } : {}),
    ...((RATING_SCALE as readonly number[]).includes(minRating) ? { minRating } : {}),
    ...(parameters.get('rated') === 'none' ? { rated: 'none' as const } : {}),
    ...(SELECTED_FILTERS.includes(selected) ? { selected: selected as SelectedFilter } : {}),
    ...(AUDIO_FILTERS.includes(audio) ? { audio: audio as AudioFilter } : {}),
    ...(GENERATIONS_FILTERS.includes(generations)
      ? { generations: generations as GenerationsFilter }
      : {}),
    page: Number.isSafeInteger(page) && page >= 1 ? page : 1,
  };
}

/** A resource as it loads; `not-found` keeps the 404's answer, which may say why (a deleted Song). */
export type LoadState<T> =
  | { phase: 'loading' }
  | { phase: 'error' }
  | { phase: 'not-found'; problem?: unknown }
  | { phase: 'ready'; data: T };

/**
 * GETs `path` and keeps what `accept` takes from the answer. A new path starts loading again; a 404
 * is `not-found`, with its answer; anything else unexpected is `error`. `reload` asks again.
 */
export function useResource<T>(
  path: string,
  accept: (answer: unknown) => T | undefined,
): { state: LoadState<T>; reload: () => void } {
  const [state, setState] = useState<{ path: string; state: LoadState<T> }>({
    path,
    state: { phase: 'loading' },
  });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    const settle = (next: LoadState<T>) => {
      if (!controller.signal.aborted) {
        setState({ path, state: next });
      }
    };
    const load = async () => {
      try {
        const response = await apiFetch(path, { signal: controller.signal });
        const answer = await body(response);
        const data = response.ok ? accept(answer) : undefined;
        if (data !== undefined) {
          settle({ phase: 'ready', data });
        } else {
          settle(
            response.status === 404 ? { phase: 'not-found', problem: answer } : { phase: 'error' },
          );
        }
      } catch {
        settle({ phase: 'error' });
      }
    };
    void load();
    return () => {
      controller.abort();
    };
  }, [path, accept, attempt]);

  const reload = useCallback(() => {
    setState({ path, state: { phase: 'loading' } });
    setAttempt((previous) => previous + 1);
  }, [path]);

  return { state: state.path === path ? state.state : { phase: 'loading' }, reload };
}

const acceptSongPage = (answer: unknown) => (isSongPage(answer) ? answer : undefined);
const acceptSong = (answer: unknown) => (isSong(answer) ? answer : undefined);
const acceptStates = (answer: unknown) =>
  isRecord(answer) && Array.isArray(answer.items) && answer.items.every(isWorkflowState)
    ? answer.items
    : undefined;

/** One page of Songs for the table's view. */
export function useSongs(query: SongQuery) {
  const parameters = songListParameters(query).toString();
  return useResource(parameters ? `${SONGS_PATH}?${parameters}` : SONGS_PATH, acceptSongPage);
}

/** One Song, by its ID or shortcode. */
export function useSong(reference: string) {
  return useResource(`${SONGS_PATH}/${encodeURIComponent(reference)}`, acceptSong);
}

/** Every workflow state, hidden ones included, in order. */
export function useWorkflowStates() {
  return useResource(WORKFLOW_STATES_PATH, acceptStates);
}

/** How many other Songs with the same title the Song page lists. */
export const SAME_TITLE_LIMIT = 20;

/**
 * The other Songs with `title` (ignoring case and spacing), leaving out the Song `excludeId`, most
 * recently updated first: the first {@link SAME_TITLE_LIMIT}, and how many there are (`total`). A
 * new title asks again.
 */
export function useSongsTitled(title: string, excludeId: string) {
  const parameters = new URLSearchParams({
    title,
    excludeId,
    pageSize: String(SAME_TITLE_LIMIT),
    // Sent rather than left to the list's default, which could change.
    sort: 'updated',
    direction: 'desc',
  });
  return useResource(`${SONGS_PATH}?${parameters.toString()}`, acceptSongPage);
}

/** How many Songs a search answers at once: the API's default for `q`. */
export const SONG_SEARCH_RESULTS = 10;

/**
 * The first Songs whose title contains `search` (ignoring case) or whose shortcode starts with it,
 * by title, and how many match in all: {@link SONG_SEARCH_RESULTS} of them, or `limit` when given.
 * Nothing for blank text; undefined when the list cannot be read.
 */
export async function searchSongs(
  search: string,
  signal?: AbortSignal,
  limit?: number,
): Promise<{ songs: Song[]; total: number } | undefined> {
  if (search.trim() === '') {
    return { songs: [], total: 0 };
  }
  const parameters = new URLSearchParams({ q: search.trim(), sort: 'title' });
  if (limit !== undefined) {
    parameters.set('pageSize', String(limit));
  }
  try {
    const response = await apiFetch(`${SONGS_PATH}?${parameters.toString()}`, { signal });
    const answer = await body(response);
    return response.ok && isSongPage(answer)
      ? { songs: answer.items, total: answer.total }
      : undefined;
  } catch {
    return undefined;
  }
}

/** The most matches {@link readSongMatches} answers. */
export const SONG_MATCHES_LIMIT = 50;

/**
 * Every place the Song `reference` matched the search `search` (#224), best first: at most
 * {@link SONG_MATCHES_LIMIT}, and how many there are in all; undefined when they cannot be read.
 */
export async function readSongMatches(
  reference: string,
  search: string,
  signal?: AbortSignal,
): Promise<{ matches: SongMatch[]; matchCount: number } | undefined> {
  try {
    const response = await apiFetch(
      `${SONGS_PATH}/${encodeURIComponent(reference)}/matches?${new URLSearchParams({ search }).toString()}`,
      { signal },
    );
    const answer = await body(response);
    return response.ok &&
      isRecord(answer) &&
      Array.isArray(answer.matches) &&
      answer.matches.every(isSongMatch) &&
      typeof answer.matchCount === 'number'
      ? { matches: answer.matches, matchCount: answer.matchCount }
      : undefined;
  } catch {
    return undefined;
  }
}

/** One Song as it is now, by its ID or shortcode; undefined when it cannot be read. */
export async function readSong(reference: string): Promise<Song | undefined> {
  try {
    const response = await apiFetch(`${SONGS_PATH}/${encodeURIComponent(reference)}`);
    const answer = await body(response);
    return response.ok && isSong(answer) ? answer : undefined;
  } catch {
    return undefined;
  }
}

export interface NewSong {
  title: string;
  concept: string;
  /** The primary Artist's ID, or null for none; left out, the default Artist is used. */
  primaryArtistId?: string | null;
}

/** How a create ended, by the API's answer. Never a rejection. */
export type CreateSongResult =
  | { kind: 'created'; song: Song }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'failed' };

/** Creates a Song; it comes with its Version 1. */
export async function createSong(request: NewSong): Promise<CreateSongResult> {
  try {
    const response = await apiFetch(SONGS_PATH, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    });
    const answer = await body(response);
    if (response.status === 201 && isSong(answer)) {
      return { kind: 'created', song: answer };
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

/**
 * An edit of a Song's details: only the fields given change. A null or blank concept or notes
 * clears them; `genreIds` and `tagIds` replace the Song's Genres and Tags; `release` changes the
 * release members it gives (null clears one; `links` replaces the list).
 */
export interface SongEdit {
  title?: string;
  concept?: string | null;
  stateId?: string;
  notes?: string | null;
  genreIds?: string[];
  tagIds?: string[];
  release?: Partial<SongRelease>;
  /** An uploaded asset's ID to show as the Song's artwork, or null to remove it. */
  artworkAssetId?: string | null;
  /** The artwork's square crop, in pixels of the original, or null for the centred square. */
  artworkCrop?: ArtworkCrop | null;
  /** The Suno ID of the workspace the Song lives in (an Available one), or null for none. */
  sunoWorkspaceId?: string | null;
}

/** Edits a Song, based on `song`'s revision; a stale revision comes back as a conflict. */
export function updateSong(
  song: Pick<Song, 'id' | 'revision'>,
  edit: SongEdit,
): Promise<SaveResult<Song>> {
  return patchWithRevision(
    `${SONGS_PATH}/${encodeURIComponent(song.id)}`,
    song.revision,
    { ...edit },
    acceptSong,
  );
}

/**
 * Replaces a Song's credits as a whole, based on `song`'s revision; a stale revision comes back as
 * a conflict, and a refused set (an Artist gone, one featured twice) as invalid.
 */
export function setSongCredits(
  song: Pick<Song, 'id' | 'revision'>,
  credits: SongCredits,
): Promise<SaveResult<Song>> {
  return writeWithRevision(
    'PUT',
    `${SONGS_PATH}/${encodeURIComponent(song.id)}/credits`,
    song.revision,
    {
      primaryArtistId: credits.primary?.id ?? null,
      featuredArtistIds: credits.featured.map((artist) => artist.id),
    },
    acceptSong,
  );
}

/** What the Songs filter pickers offer values of (#225). */
export type FilterValueKind = 'genre' | 'tag' | 'album' | 'playlist' | 'model';

/** One value a filter picker offers: its ID (a model's is its name as reported), its name, a Tag's colour. */
export interface FilterValue {
  id: string;
  name: string;
  colour?: string;
}

/** Where the pickers' values are read. */
export const FILTER_VALUES_PATH = `${SONGS_PATH}/filter-values`;

function isFilterValue(value: unknown): value is FilterValue {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.name === 'string' &&
    (value.colour === undefined || typeof value.colour === 'string')
  );
}

/**
 * The values of `kind` whose name has a word beginning with each word of `query` (every value when
 * it is blank), at most fifty, by name; or, given `ids`, the values with those IDs that still exist
 * (one left out was deleted). Undefined when the API did not answer as expected.
 */
export async function readFilterValues(
  kind: FilterValueKind,
  { query, ids }: { query?: string; ids?: readonly string[] },
  signal?: AbortSignal,
): Promise<FilterValue[] | undefined> {
  const parameters = new URLSearchParams({ kind });
  if (ids !== undefined) {
    for (const id of ids) {
      parameters.append('ids', id);
    }
  } else if (query !== undefined && query.trim() !== '') {
    parameters.set('query', query.trim());
  }
  try {
    const response = await apiFetch(`${FILTER_VALUES_PATH}?${parameters.toString()}`, { signal });
    const answer = await body(response);
    return response.ok &&
      isRecord(answer) &&
      Array.isArray(answer.items) &&
      answer.items.every(isFilterValue)
      ? answer.items
      : undefined;
  } catch {
    return undefined;
  }
}
