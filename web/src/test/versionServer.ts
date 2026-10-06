import type { Generation, GenerationComment } from '../api/generations';
import type { LineageSource, SunoPersona, SunoPlaylist } from '../api/lineage';
import type { RelationshipType } from '../api/relationships';
import type { Snapshot } from '../api/snapshots';
import type { Song } from '../api/songs';
import {
  isVersionKind,
  type NumberOption,
  type Version,
  type VersionDetail,
} from '../api/versions';
import { CREATE_FIELDS, DEFAULT_INPUTS } from './createFieldsFixture';
import { healthyReport, jsonResponse, requestPath, stubFetch } from './helpers';
import { baseSong, STATES, SYSTEM_TYPES } from './songServer';

/** A Version of `baseSong` numbered `number`, with an ID built from the number and no lyrics or styles. */
export function testVersion(number: string, change: Partial<VersionDetail> = {}): VersionDetail {
  const inputs = change.inputs ?? DEFAULT_INPUTS;
  return {
    id: `0199b1a0-0000-7000-9000-${number.replace(/\./g, '0').padStart(12, '0')}`,
    songId: baseSong.id,
    number,
    shortcode: `${baseSong.shortcode}-v${number}`,
    name: null,
    notes: null,
    archived: false,
    current: false,
    createdAt: '2026-10-01T09:00:00Z',
    updatedAt: '2026-10-01T09:00:00Z',
    revision: 1,
    isFrozen: false,
    lyrics: '',
    styles: '',
    inputs,
    // A Version's kind is its options' kind, unless the test says otherwise.
    kind: isVersionKind(inputs.kind) ? inputs.kind : 'song',
    ...change,
  };
}

/**
 * Generation `ordinal` of the Version of `baseSong` numbered `number`: an Active, complete clip of
 * 2:05 with a Suno ID built from the shortcode, unrated and without comments.
 */
export function testGeneration(
  number: string,
  ordinal: number,
  change: Partial<Generation> = {},
): Generation {
  const version = testVersion(number);
  const shortcode = `${version.shortcode}-g${String(ordinal)}`;
  return {
    id: `0199b1a0-6000-7000-9000-${`${number.replace(/\./g, '0')}0${String(ordinal)}`.padStart(12, '0')}`,
    shortcode,
    ordinal,
    song: { id: baseSong.id, shortcode: baseSong.shortcode },
    version: { id: version.id, shortcode: version.shortcode },
    sunoId: `suno-${shortcode}`,
    providerStatus: 'complete',
    state: 'active',
    remoteState: 'present',
    title: `Take ${String(ordinal)}`,
    durationSeconds: 125,
    modelVersion: 'chirp-v5',
    modelName: null,
    modelLabel: 'v5',
    sunoCreatedAt: '2026-10-01T09:30:00Z',
    isSelected: false,
    createdAt: '2026-10-01T09:31:00Z',
    revision: 1,
    rating: null,
    comments: [],
    artwork: null,
    ...change,
  };
}

/** Comment `n` (from 1) on a Generation, written at 10:0n and never edited. */
export function testComment(n: number, change: Partial<GenerationComment> = {}): GenerationComment {
  return {
    id: `0199b1a0-7000-7000-9000-${String(n).padStart(12, '0')}`,
    text: `Comment ${String(n)}`,
    createdAt: `2026-10-01T10:0${String(n % 10)}:00Z`,
    editedAt: null,
    revision: 1,
    ...change,
  };
}

/** A write to a Generation (its rating) or to one of its comments, as the fake API received it. */
export interface GenerationWrite {
  method: string;
  path: string;
  ifMatch: string | null;
  body: Record<string, unknown>;
}

/** A Version as the list answers it: without its lyrics, styles, and options. */
function summary(version: VersionDetail): Version {
  const { id, songId, number, shortcode, name, notes, archived, current } = version;
  const { createdAt, updatedAt, revision, isFrozen, kind } = version;
  return {
    id,
    songId,
    number,
    shortcode,
    name,
    notes,
    archived,
    current,
    createdAt,
    updatedAt,
    revision,
    isFrozen,
    kind,
  };
}

/** The tree-numbering rules, as the API applies them, over the numbers in `used`. */
function optionsFor(source: string, used: Set<string>): NumberOption[] {
  const parts = source.split('.').map(Number);
  const last = parts[parts.length - 1] ?? 1;
  const prefix = parts.slice(0, -1);
  let siblingLast = last + 1;
  while (used.has([...prefix, siblingLast].join('.'))) {
    siblingLast++;
  }
  let childLast = 1;
  while (used.has(`${source}.${String(childLast)}`)) {
    childLast++;
  }
  const sibling = [...prefix, siblingLast].join('.');
  const child = `${source}.${String(childLast)}`;
  return siblingLast === last + 1
    ? [
        { number: sibling, kind: 'sibling', proposed: true },
        { number: child, kind: 'child', proposed: false },
      ]
    : [
        { number: child, kind: 'child', proposed: true },
        { number: sibling, kind: 'sibling', proposed: false },
      ];
}

/** A request the fake server received that changes something: its method, path, and body. */
export interface ReceivedWrite {
  method: string;
  path: string;
  body: Record<string, unknown>;
}

/**
 * A fake n8Tracks holding `baseSong` and its Versions, answering as the API does: the Song, its
 * Versions, one Version with its lyrics and styles, a Version's next numbers, creating a Version
 * (which becomes current), making one current, and editing a Version's name, notes, archived flag,
 * lyrics, styles, or options on its revision (lyrics and styles kept as sent, line endings aside,
 * and refused over their limits; options merged key by key; 409 `version_frozen` when a frozen
 * Version's inputs would change; a create may carry its own lyrics and styles and copies the
 * source's options), deleting a Version on its revision (its descendants stay under a placeholder,
 * the current Version moves as the API moves it, and the last one is replaced by a blank one), and
 * Suno's Create-screen fields ({@link CREATE_FIELDS}). A deleted Version answers 404
 * `version_deleted` and resolves as `deleted`. The Song's deletion impact and deletion (on its
 * revision, with the typed title when the API's rule asks for one) are answered too; once deleted the
 * Song answers 404 `song_deleted`, and the Songs list is empty. A test changes `server.versions` to play another
 * client, or sets `server.next` to answer the next write some other way.
 */
export function versionServer(versions: VersionDetail[], song: Song = baseSong) {
  // The Song names its current Version, and says what it creates.
  const current = versions.find((version) => version.current);
  const server = {
    song: {
      ...song,
      currentVersion: current
        ? {
            id: current.id,
            number: current.number,
            shortcode: current.shortcode,
            kind: current.kind,
          }
        : song.currentVersion,
    },
    versions: versions.map((version) => ({ ...version })),
    writes: [] as ReceivedWrite[],
    /** Every snapshot stored, oldest first (the API's deduplication applied). */
    snapshots: [] as Snapshot[],
    /** Every snapshot request received, stored or not. */
    snapshotRequests: [] as Record<string, unknown>[],
    /** When set, answers the next snapshot request (once) instead of the fake API. */
    nextSnapshot: undefined as (() => Response | Promise<Response>) | undefined,
    /** The IDs of the snapshots deleted, in order (a refused or unknown one included). */
    deletedSnapshots: [] as string[],
    /** When set, answers the next snapshot deletion (once) instead of the fake API. */
    nextDelete: undefined as (() => Response | Promise<Response>) | undefined,
    /** When set, answers the next write (once) instead of the fake API. */
    next: undefined as (() => Response | Promise<Response>) | undefined,
    /** The Versions deleted, in order. */
    deleted: [] as VersionDetail[],
    /** When the Song itself was deleted (it then answers 404 `song_deleted`); undefined while live. */
    songDeletedAt: undefined as string | undefined,
    /** Every Song deletion request, in order: its If-Match and body. */
    songDeletes: [] as { ifMatch: string | null; body: Record<string, unknown> }[],
    /** When set, answers the next Song deletion (once) instead of the fake API. */
    nextSongDelete: undefined as (() => Response | Promise<Response>) | undefined,
    /** Generations counted by the deletion impact (apart from {@link generations}). */
    generationCount: 0,
    /** The Song's Generations, as its Generation list answers them (Version order, then ordinal). */
    generations: [] as Generation[],
    /** How many times the Song's Generation list was read. */
    generationReads: 0,
    /** When set, answers the next read of the Generation list (once) instead of the fake API. */
    nextGenerations: undefined as (() => Response | Promise<Response>) | undefined,
    /** Every rating and comment write received, in order (a refused one included). */
    generationWrites: [] as GenerationWrite[],
    /** When set, answers the next rating or comment write (once) instead of the fake API. */
    nextGenerationWrite: undefined as (() => Response | Promise<Response>) | undefined,
    /** Every Selected Generation write received (PUT to choose, DELETE to clear), in order. */
    selectionWrites: [] as {
      method: string;
      ifMatch: string | null;
      body: Record<string, unknown>;
    }[],
    /** When set, answers the next Selected Generation write (once) instead of the fake API. */
    nextSelectionWrite: undefined as (() => Response | Promise<Response>) | undefined,
    /** Every "Create new Song from Generation" request (#123), in order: its Generation, If-Match, and body. */
    moves: [] as { reference: string; ifMatch: string | null; body: Record<string, unknown> }[],
    /** When set, answers the next move (once) instead of the fake API. */
    nextMove: undefined as (() => Response | Promise<Response>) | undefined,
    /** Every Generation deletion request (#124), in order: its Generation, If-Match, and body (null when none). */
    generationDeletes: [] as {
      reference: string;
      ifMatch: string | null;
      body: Record<string, unknown> | null;
    }[],
    /** When set, answers the next Generation deletion (once) instead of the fake API. */
    nextGenerationDelete: undefined as (() => Response | Promise<Response>) | undefined,
    /** How many Versions use each Generation (by ID) as a source, as its deletion impact counts them. */
    sourceVersionCounts: new Map<string, number>(),
    /** The relationship types served (#125's audio actions are the system ones). */
    relationshipTypes: [...SYSTEM_TYPES] as RelationshipType[],
    /** The Suno playlists seen in imports (#125), served by name. */
    playlists: [] as SunoPlaylist[],
    /** The Suno personas seen in imported clips (#125). */
    personas: [] as SunoPersona[],
    /** Other Songs the search finds (#125's source picker), besides the Song itself. */
    otherSongs: [] as Song[],
    /** The Generations of {@link otherSongs}, each naming its Song. */
    otherGenerations: [] as Generation[],
    /** Old shortcodes of moved Generations (#123), each with the Song and shortcode it has now: they resolve as `moved`. */
    moved: new Map<string, { song: string; shortcode: string }>(),
    /** Plays another client editing the Song: its revision goes up. */
    touchSongElsewhere() {
      server.song = { ...server.song, revision: server.song.revision + 1 };
    },
    /** Plays another client rating the Generation `id`: its rating changes and its revision goes up. */
    rateElsewhere(id: string, rating: number | null) {
      server.generations = server.generations.map((generation) =>
        generation.id === id
          ? { ...generation, rating, revision: generation.revision + 1 }
          : generation,
      );
    },
    /** Every number the Song has used, deleted Versions' included. */
    usedNumbers: new Set(versions.map((version) => version.number)),
    /** Plays another client deleting the Version numbered `number` (no current Version moves). */
    deleteElsewhere(number: string) {
      const version = server.versions.find((candidate) => candidate.number === number);
      if (version) {
        server.versions = server.versions.filter((candidate) => candidate !== version);
        server.deleted.push(version);
      }
    },
    /** Plays another client creating a Version with `number`. */
    addElsewhere(number: string) {
      server.versions.push(testVersion(number));
      server.usedNumbers.add(number);
    },
    /** Plays another client editing the Version numbered `number`: its revision goes up by one. */
    changeElsewhere(number: string, change: Partial<VersionDetail>) {
      server.versions = server.versions.map((version) =>
        version.number === number
          ? { ...version, ...change, revision: version.revision + 1 }
          : version,
      );
    },
  };

  const used = () => server.usedNumbers;

  const isBelow = (number: string, ancestor: string) => number.startsWith(`${ancestor}.`);
  const byNumber = (left: VersionDetail, right: VersionDetail) => {
    const a = left.number.split('.').map(Number);
    const b = right.number.split('.').map(Number);
    for (let index = 0; index < Math.min(a.length, b.length); index++) {
      const difference = (a[index] ?? 0) - (b[index] ?? 0);
      if (difference !== 0) {
        return difference;
      }
    }
    return a.length - b.length;
  };
  /** The tree's placeholders: used numbers without a live Version, with a live descendant. */
  const placeholders = () =>
    [...used()]
      .filter(
        (number) =>
          !server.versions.some((version) => version.number === number) &&
          server.versions.some((version) => isBelow(version.number, number)),
      )
      .sort((left, right) => byNumber(testVersion(left), testVersion(right)));
  const deletedAnswer = (id: string) => {
    const version = server.deleted.find((candidate) => candidate.id === id);
    return version
      ? jsonResponse(404, {
          code: 'version_deleted',
          versionId: version.id,
          versionShortcode: version.shortcode,
          number: version.number,
          deletedAt: '2026-10-01T10:00:00Z',
        })
      : jsonResponse(404, { code: 'not_found' });
  };

  /** Deletes `version` as the API does; the Song's current Version afterwards. */
  const remove = (version: VersionDetail): VersionDetail => {
    server.versions = server.versions.filter((candidate) => candidate.id !== version.id);
    server.deleted.push(version);
    let current = server.versions.find((candidate) => candidate.current);
    if (server.versions.length === 0) {
      const top = Math.max(...[...used()].map((number) => Number(number.split('.')[0])));
      const blank = testVersion(String(top + 1), { current: true, inputs: version.inputs });
      server.versions.push(blank);
      server.usedNumbers.add(blank.number);
      current = blank;
    } else if (version.current) {
      const ancestors = version.number
        .split('.')
        .map((_, index, parts) => parts.slice(0, parts.length - 1 - index).join('.'))
        .filter((number) => number !== '');
      const sorted = [...server.versions].sort(byNumber);
      current =
        ancestors
          .map((number) => server.versions.find((other) => other.number === number))
          .find((other) => other !== undefined && !other.archived) ??
        sorted.find((other) => !other.archived) ??
        sorted[0];
    }
    if (current === undefined) {
      throw new Error('The fake Song lost its current Version.');
    }
    const currentId = current.id;
    server.versions = server.versions.map((other) => ({
      ...other,
      current: other.id === currentId,
    }));
    const now = server.versions.find((other) => other.id === currentId) ?? current;
    server.song = {
      ...server.song,
      currentVersion: { id: now.id, number: now.number, shortcode: now.shortcode, kind: now.kind },
      versionCount: server.versions.length,
      revision: server.song.revision + 1,
    };
    return now;
  };
  let snapshotCount = 0;

  const keep = (versionId: string, lyrics: string, styles: string, createdAt: string) => {
    const own = server.snapshots.filter((snapshot) => snapshot.versionId === versionId);
    const newest = own[own.length - 1];
    if (newest?.lyrics === lyrics && newest.styles === styles) {
      return { snapshot: newest, created: false };
    }
    snapshotCount++;
    const snapshot: Snapshot = {
      id: `0199b1a0-5000-7000-9000-${String(snapshotCount).padStart(12, '0')}`,
      versionId,
      createdAt,
      lyrics,
      styles,
    };
    server.snapshots.push(snapshot);
    return { snapshot, created: true };
  };

  /** The snapshots API: take one, list them newest first, read one, restore one on a revision, delete one. */
  const answerHistory = async (
    versionId: string,
    snapshotId: string | undefined,
    restore: boolean,
    method: string,
    init: RequestInit | undefined,
  ): Promise<Response> => {
    const version = server.versions.find((candidate) => candidate.id === versionId);
    if (!version) {
      return jsonResponse(404, { code: 'not_found' });
    }
    if (snapshotId === undefined && method === 'POST') {
      const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as Record<
        string,
        unknown
      >;
      server.snapshotRequests.push(body);
      const next = server.nextSnapshot;
      if (next) {
        server.nextSnapshot = undefined;
        return next();
      }
      const { snapshot, created } = keep(
        versionId,
        String(body.lyrics),
        String(body.styles),
        typeof body.capturedAt === 'string' ? body.capturedAt : '2026-10-01T09:30:00Z',
      );
      return jsonResponse(created ? 201 : 200, snapshot);
    }
    const own = server.snapshots.filter((snapshot) => snapshot.versionId === versionId);
    if (snapshotId === undefined) {
      return jsonResponse(200, {
        items: [...own].reverse().map(({ id, createdAt }) => ({ id, versionId, createdAt })),
      });
    }
    await Promise.resolve();
    if (method === 'DELETE' && !restore) {
      server.deletedSnapshots.push(snapshotId);
      const next = server.nextDelete;
      if (next) {
        server.nextDelete = undefined;
        return next();
      }
      if (!own.some((candidate) => candidate.id === snapshotId)) {
        return jsonResponse(404, { code: 'not_found' });
      }
      server.snapshots = server.snapshots.filter((candidate) => candidate.id !== snapshotId);
      return new Response(null, { status: 204 });
    }
    const snapshot = own.find((candidate) => candidate.id === snapshotId);
    if (!snapshot) {
      return jsonResponse(404, { code: 'not_found' });
    }
    if (!restore) {
      return jsonResponse(200, snapshot);
    }
    server.writes.push({ method, path: `restore ${snapshotId}`, body: {} });
    const ifMatch = new Headers(init?.headers).get('If-Match');
    if (ifMatch !== `"${String(version.revision)}"`) {
      return jsonResponse(409, { code: 'revision_conflict', current: version });
    }
    if (version.isFrozen) {
      return jsonResponse(409, { code: 'version_frozen', versionId: version.id });
    }
    keep(versionId, version.lyrics, version.styles, '2026-10-01T10:00:00Z');
    const restored: VersionDetail = {
      ...version,
      lyrics: snapshot.lyrics,
      styles: snapshot.styles,
      revision: version.revision + 1,
    };
    server.versions = server.versions.map((other) => (other.id === version.id ? restored : other));
    return jsonResponse(200, restored);
  };

  /**
   * The resolve endpoint: the Song or one of its Versions, by ID or shortcode in any letter case,
   * and a Generation of any Version by its shortcode (every ordinal exists).
   */
  const answerResolve = (reference: string) => {
    const key = reference.toLowerCase();
    const song = server.song;
    const moved = server.moved.get(key);
    if (moved !== undefined) {
      return jsonResponse(200, {
        entityType: 'generation',
        id: '0199b1a0-6000-7000-9000-0000000000ff',
        shortcode: moved.shortcode,
        status: 'moved',
        canonicalShortcode: moved.shortcode,
        song: { id: '0199b1a0-1000-7000-9000-0000000000ff', shortcode: moved.song },
        version: { id: '0199b1a0-2000-7000-9000-0000000000ff', shortcode: `${moved.song}-v1` },
      });
    }
    const generation = /^(.+)-g([1-9][0-9]*)$/.exec(key);
    const generated = server.versions.find((candidate) => candidate.shortcode === generation?.[1]);
    if (generation && generated) {
      return jsonResponse(200, {
        entityType: 'generation',
        id: `${generated.id.slice(0, -2)}9${generation[2] ?? ''}`,
        shortcode: key,
        status: 'active',
        song: { id: song.id, shortcode: song.shortcode },
        version: { id: generated.id, shortcode: generated.shortcode },
      });
    }
    if (key === song.shortcode || key === song.id.toLowerCase()) {
      return jsonResponse(200, {
        entityType: 'song',
        id: song.id,
        shortcode: song.shortcode,
        status: 'active',
      });
    }
    const version = server.versions.find(
      (candidate) => key === candidate.shortcode || key === candidate.id.toLowerCase(),
    );
    const deleted = server.deleted.find(
      (candidate) => key === candidate.shortcode || key === candidate.id.toLowerCase(),
    );
    const found = version ?? deleted;
    return found
      ? jsonResponse(200, {
          entityType: 'version',
          id: found.id,
          shortcode: found.shortcode,
          status: version === undefined ? 'deleted' : version.archived ? 'archived' : 'active',
          song: { id: song.id, shortcode: song.shortcode },
        })
      : jsonResponse(404, { code: 'reference_not_found' });
  };

  /** What deleting the Song would do, as the API counts it; the title rule is the API's. */
  const songImpact = () => {
    const song = server.song;
    const counts = {
      versionCount: server.versions.length,
      generationCount: server.generationCount,
      artworkCount: song.artwork === null ? 0 : 1,
      albumCount: song.albums.length,
      playlistCount: song.playlists.length,
      relationshipCount: song.relationships.length,
      audioFileCount: 0,
    };
    return {
      id: song.id,
      shortcode: song.shortcode,
      title: song.title,
      ...counts,
      titleRequired:
        counts.versionCount > 1 ||
        counts.generationCount > 0 ||
        counts.albumCount > 0 ||
        counts.playlistCount > 0 ||
        counts.relationshipCount > 0,
      revision: song.revision,
    };
  };

  // A source as the API reads it back: its Generation or Song looked up (a pasted Suno ID n8Tracks
  // has is that Generation), with titles, shortcodes, and availability.
  const readSource = (sent: Record<string, unknown>, withType: boolean): LineageSource => {
    const all = [...server.generations, ...server.otherGenerations];
    const songs = [server.song, ...server.otherSongs];
    const typeId = typeof sent.typeId === 'string' ? sent.typeId : undefined;
    const typed = withType
      ? {
          typeId,
          sunoAction:
            server.relationshipTypes.find((type) => type.id === typeId)?.sunoAction ?? null,
          continueAtSeconds:
            typeof sent.continueAtSeconds === 'number' ? sent.continueAtSeconds : null,
          secondaryIds: null,
        }
      : {};
    const idOf = (value: unknown) =>
      typeof value === 'string'
        ? value
        : typeof value === 'object' && value !== null && 'id' in value
          ? String(value.id)
          : undefined;
    const external =
      typeof sent.external === 'object' && sent.external !== null
        ? (sent.external as Record<string, unknown>)
        : undefined;
    const generation = all.find(
      (candidate) =>
        candidate.id === idOf(sent.generation) ||
        (external?.sunoId !== undefined && candidate.sunoId === external.sunoId),
    );
    if (generation !== undefined) {
      return {
        ...typed,
        generation: {
          id: generation.id,
          shortcode: generation.shortcode,
          songId: generation.song.id,
          songShortcode: generation.song.shortcode,
          songTitle: songs.find((song) => song.id === generation.song.id)?.title ?? null,
          title: generation.title,
          durationSeconds: generation.durationSeconds,
          missing: false,
        },
        availability:
          generation.remoteState === 'present'
            ? 'ok'
            : generation.remoteState === 'trashed'
              ? 'trashed'
              : 'missing',
      };
    }
    const song = songs.find((candidate) => candidate.id === idOf(sent.song));
    if (song !== undefined) {
      return {
        ...typed,
        song: { id: song.id, shortcode: song.shortcode, title: song.title, missing: false },
        availability: 'ok',
      };
    }
    return {
      ...typed,
      external: {
        sunoId: typeof external?.sunoId === 'string' ? external.sunoId : '',
        title: typeof external?.title === 'string' ? external.title : null,
        address: typeof external?.address === 'string' ? external.address : null,
        label: null,
      },
      availability: 'not_imported',
    };
  };
  const readLineage = (sent: Record<string, unknown>): Record<string, unknown> => {
    const read: Record<string, unknown> = { ...sent };
    if ('sources' in sent) {
      read.sources = Array.isArray(sent.sources)
        ? sent.sources.map((source) => readSource(source as Record<string, unknown>, true))
        : [];
    }
    if ('inspiration' in sent) {
      const inspiration = sent.inspiration as Record<string, unknown> | null;
      read.inspiration =
        inspiration === null
          ? null
          : Array.isArray(inspiration.sources) && inspiration.sources.length > 0
            ? {
                sources: inspiration.sources.map((source) =>
                  readSource(source as Record<string, unknown>, false),
                ),
              }
            : inspiration.playlist
              ? { playlist: inspiration.playlist }
              : null;
    }
    return read;
  };

  const mock = stubFetch();
  mock.mockImplementation(async (input, init) => {
    const path = requestPath(input);
    const method = init?.method ?? 'GET';
    if (path.endsWith('/health')) {
      return jsonResponse(200, healthyReport);
    }
    if (path.endsWith('/api/v1/workflow-states')) {
      return jsonResponse(200, { items: STATES });
    }
    if (path.endsWith('/api/v1/suno/create-fields')) {
      return jsonResponse(200, CREATE_FIELDS);
    }
    if (path.endsWith('/api/v1/relationship-types')) {
      return jsonResponse(200, { items: server.relationshipTypes });
    }
    if (path.endsWith('/api/v1/suno/playlists')) {
      return jsonResponse(200, { items: server.playlists });
    }
    if (path.endsWith('/api/v1/suno/personas')) {
      return jsonResponse(200, { items: server.personas });
    }
    const resolve = /\/api\/v1\/resolve\/([^/]+)$/.exec(path);
    if (resolve) {
      return answerResolve(decodeURIComponent(resolve[1] ?? ''));
    }
    const numbers = /\/api\/v1\/versions\/([^/]+)\/next-numbers$/.exec(path);
    if (numbers) {
      const source = server.versions.find((version) => version.id === numbers[1]);
      return source
        ? jsonResponse(200, { options: optionsFor(source.number, used()) })
        : jsonResponse(404, { code: 'not_found' });
    }
    const impact = /\/api\/v1\/versions\/([^/]+)\/deletion-impact$/.exec(path);
    if (impact) {
      const version = server.versions.find((candidate) => candidate.id === impact[1]);
      return version
        ? jsonResponse(200, {
            generationCount: version.isFrozen ? 1 : 0,
            remainingDescendantCount: server.versions.filter((other) =>
              isBelow(other.number, version.number),
            ).length,
            isLastVersion: server.versions.length === 1,
            revision: version.revision,
          })
        : deletedAnswer(impact[1] ?? '');
    }
    const history = /\/api\/v1\/versions\/([^/]+)\/snapshots(?:\/([^/]+))?(\/restore)?$/.exec(path);
    if (history) {
      return answerHistory(history[1] ?? '', history[2], history[3] !== undefined, method, init);
    }
    const edited = /\/api\/v1\/versions\/([^/]+)$/.exec(path);
    if (edited && method === 'GET') {
      const version = server.versions.find((candidate) => candidate.id === edited[1]);
      return version ? jsonResponse(200, version) : deletedAnswer(edited[1] ?? '');
    }
    if (edited && method === 'DELETE') {
      server.writes.push({ method, path, body: {} });
      const next = server.next;
      if (next) {
        server.next = undefined;
        return next();
      }
      const version = server.versions.find((candidate) => candidate.id === edited[1]);
      if (!version) {
        return deletedAnswer(edited[1] ?? '');
      }
      const ifMatch = new Headers(init?.headers).get('If-Match');
      if (ifMatch !== `"${String(version.revision)}"`) {
        return jsonResponse(409, { code: 'revision_conflict', current: version });
      }
      return jsonResponse(200, remove(version));
    }
    if (edited && method === 'PATCH') {
      const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as Record<
        string,
        unknown
      >;
      server.writes.push({ method, path, body });
      const next = server.next;
      if (next) {
        server.next = undefined;
        return next();
      }
      const version = server.versions.find((candidate) => candidate.id === edited[1]);
      if (!version) {
        return deletedAnswer(edited[1] ?? '');
      }
      const ifMatch = new Headers(init?.headers).get('If-Match');
      if (ifMatch !== `"${String(version.revision)}"`) {
        return jsonResponse(409, { code: 'revision_conflict', current: version });
      }
      const text = (value: unknown) =>
        typeof value === 'string' && value.trim() !== '' ? value.trim() : null;
      const input = (value: unknown) =>
        typeof value === 'string' ? value.replace(/\r\n|\r/g, '\n') : '';
      const errors: Record<string, string[]> = {};
      if ('lyrics' in body && input(body.lyrics).length > 5_000) {
        errors.lyrics = ['Use at most 5,000 characters.'];
      }
      if ('styles' in body && input(body.styles).length > 1_000) {
        errors.styles = ['Use at most 1,000 characters.'];
      }
      if (Object.keys(errors).length > 0) {
        return jsonResponse(422, { code: 'validation_failed', errors });
      }
      const options =
        typeof body.inputs === 'object' && body.inputs !== null
          ? (readLineage(body.inputs as Record<string, unknown>) as VersionDetail['inputs'])
          : {};
      const inputs = { ...version.inputs, ...options };
      if (
        version.isFrozen &&
        (('lyrics' in body && input(body.lyrics) !== version.lyrics) ||
          ('styles' in body && input(body.styles) !== version.styles) ||
          JSON.stringify(inputs) !== JSON.stringify(version.inputs))
      ) {
        return jsonResponse(409, { code: 'version_frozen', versionId: version.id });
      }
      const changed: VersionDetail = {
        ...version,
        ...('name' in body ? { name: text(body.name) } : {}),
        ...('notes' in body ? { notes: text(body.notes) } : {}),
        ...(typeof body.archived === 'boolean' ? { archived: body.archived } : {}),
        ...('lyrics' in body ? { lyrics: input(body.lyrics) } : {}),
        ...('styles' in body ? { styles: input(body.styles) } : {}),
        inputs,
        kind: isVersionKind(inputs.kind) ? inputs.kind : version.kind,
        revision: version.revision + 1,
      };
      server.versions = server.versions.map((other) => (other.id === version.id ? changed : other));
      return jsonResponse(200, changed);
    }

    const move = /\/api\/v1\/generations\/([^/]+)\/move-to-new-song$/.exec(path);
    if (move && method === 'POST') {
      const reference = decodeURIComponent(move[1] ?? '').toLowerCase();
      const ifMatch = new Headers(init?.headers).get('If-Match');
      const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as Record<
        string,
        unknown
      >;
      server.moves.push({ reference, ifMatch, body });
      const nextMove = server.nextMove;
      if (nextMove) {
        server.nextMove = undefined;
        return nextMove();
      }
      const generation = server.generations.find(
        (candidate) =>
          candidate.id.toLowerCase() === reference ||
          candidate.shortcode.toLowerCase() === reference,
      );
      if (generation === undefined) {
        return jsonResponse(404, { code: 'not_found' });
      }
      if (ifMatch !== `"${String(generation.revision)}"`) {
        return jsonResponse(409, { code: 'revision_conflict', current: generation });
      }
      const title = typeof body.title === 'string' ? body.title.trim() : '';
      if (title === '') {
        return jsonResponse(422, {
          code: 'validation_failed',
          errors: { title: ['Enter a title.'] },
        });
      }
      const choice = body.replacementGeneration ?? body.workflowState;
      if (generation.isSelected && choice === undefined) {
        return jsonResponse(422, { code: 'selection_choice_required' });
      }
      const newSong = {
        ...baseSong,
        id: '0199b1a0-1000-7000-9000-0000000000ff',
        shortcode: 'n8-8',
        title,
      };
      const moved = {
        ...generation,
        shortcode: 'n8-8-v1-g1',
        ordinal: 1,
        song: { id: newSong.id, shortcode: newSong.shortcode },
        version: { id: '0199b1a0-2000-7000-9000-0000000000ff', shortcode: 'n8-8-v1' },
        isSelected: true,
        revision: generation.revision + 1,
      };
      server.generations = server.generations.filter((other) => other.id !== generation.id);
      server.moved.set(generation.shortcode, {
        song: newSong.shortcode,
        shortcode: moved.shortcode,
      });
      return jsonResponse(201, {
        song: newSong,
        version: { id: moved.version.id, shortcode: moved.version.shortcode },
        generation: moved,
        alias: generation.shortcode,
      });
    }

    const generationDeletion = /\/api\/v1\/generations\/([^/]+)(\/deletion-impact)?$/.exec(path);
    if (
      generationDeletion &&
      ((generationDeletion[2] !== undefined && method === 'GET') ||
        (generationDeletion[2] === undefined && method === 'DELETE'))
    ) {
      const reference = decodeURIComponent(generationDeletion[1] ?? '').toLowerCase();
      const generation = server.generations.find(
        (candidate) =>
          candidate.id.toLowerCase() === reference ||
          candidate.shortcode.toLowerCase() === reference,
      );
      if (method === 'GET') {
        return generation === undefined
          ? jsonResponse(404, { code: 'not_found' })
          : jsonResponse(200, {
              id: generation.id,
              shortcode: generation.shortcode,
              isSelected: generation.isSelected,
              replacements: generation.isSelected
                ? server.generations.filter((other) => other.id !== generation.id)
                : [],
              commentCount: generation.comments.length,
              artworkCount: generation.artwork === null ? 0 : 1,
              sourceVersionCount: server.sourceVersionCounts.get(generation.id) ?? 0,
              revision: generation.revision,
            });
      }
      const ifMatch = new Headers(init?.headers).get('If-Match');
      const body =
        typeof init?.body === 'string' ? (JSON.parse(init.body) as Record<string, unknown>) : null;
      server.generationDeletes.push({ reference, ifMatch, body });
      const nextGenerationDelete = server.nextGenerationDelete;
      if (nextGenerationDelete) {
        server.nextGenerationDelete = undefined;
        return nextGenerationDelete();
      }
      if (generation === undefined) {
        return jsonResponse(404, { code: 'not_found' });
      }
      if (ifMatch !== `"${String(generation.revision)}"`) {
        return jsonResponse(409, { code: 'revision_conflict', current: generation });
      }
      const replacement = body?.replacementGeneration;
      const stateId = body?.workflowState;
      if (generation.isSelected && replacement === undefined && stateId === undefined) {
        return jsonResponse(422, { code: 'selection_choice_required' });
      }
      const chosen = server.generations.find(
        (other) => other.id === replacement && other.id !== generation.id,
      );
      const state = STATES.find((candidate) => candidate.id === stateId);
      if (
        (replacement !== undefined || stateId !== undefined) &&
        (!generation.isSelected ||
          (replacement !== undefined) === (stateId !== undefined) ||
          (replacement !== undefined && chosen === undefined) ||
          (stateId !== undefined && state === undefined))
      ) {
        return jsonResponse(422, {
          code: 'invalid_replacement',
          errors: { replacementGeneration: ['Choose another Generation of this Song.'] },
        });
      }
      if (chosen !== undefined || state !== undefined) {
        server.song = {
          ...server.song,
          hasSelectedGeneration: chosen !== undefined,
          selectedGeneration:
            chosen === undefined
              ? null
              : {
                  id: chosen.id,
                  shortcode: chosen.shortcode,
                  state: chosen.state,
                  remoteState: chosen.remoteState,
                },
          state:
            state === undefined
              ? server.song.state
              : { id: state.id, name: state.name, colour: state.colour },
          revision: server.song.revision + 1,
        };
      }
      server.generations = server.generations
        .filter((other) => other.id !== generation.id)
        .map((other) => ({
          ...other,
          isSelected: other.id === chosen?.id || (other.isSelected && chosen === undefined),
        }));
      return jsonResponse(200, { song: server.song });
    }

    const generationWrite = /\/api\/v1\/generations\/([^/]+)(?:\/comments(?:\/([^/]+))?)?$/.exec(
      path,
    );
    if (generationWrite && method !== 'GET') {
      const reference = decodeURIComponent(generationWrite[1] ?? '').toLowerCase();
      const commentId =
        generationWrite[2] === undefined ? undefined : decodeURIComponent(generationWrite[2]);
      const isComments = path.includes('/comments');
      const ifMatch = new Headers(init?.headers).get('If-Match');
      const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as Record<
        string,
        unknown
      >;
      server.generationWrites.push({ method, path, ifMatch, body });
      const nextGenerationWrite = server.nextGenerationWrite;
      if (nextGenerationWrite) {
        server.nextGenerationWrite = undefined;
        return nextGenerationWrite();
      }
      const generation = server.generations.find(
        (candidate) =>
          candidate.id.toLowerCase() === reference ||
          candidate.shortcode.toLowerCase() === reference,
      );
      if (generation === undefined) {
        return jsonResponse(404, { code: 'not_found' });
      }
      const store = (changed: Generation) => {
        server.generations = server.generations.map((other) =>
          other.id === changed.id ? changed : other,
        );
      };
      const textOf = (value: unknown) => (typeof value === 'string' ? value.trim() : '');
      const invalidText = (text: string) =>
        text === '' || text.length > 2000
          ? jsonResponse(422, {
              code: 'validation_failed',
              errors: { text: ['Write a comment of up to 2000 characters.'] },
            })
          : undefined;
      if (!isComments && method === 'PATCH') {
        if (ifMatch !== `"${String(generation.revision)}"`) {
          return jsonResponse(409, { code: 'revision_conflict', current: generation });
        }
        const rating = body.rating === undefined ? generation.rating : body.rating;
        const state =
          body.state === undefined
            ? generation.state
            : body.state === 'active' || body.state === 'archived'
              ? body.state
              : undefined;
        if (state === undefined) {
          return jsonResponse(422, {
            code: 'validation_failed',
            errors: { state: ['Send active or archived.'] },
          });
        }
        if (rating === generation.rating && state === generation.state) {
          return jsonResponse(200, generation);
        }
        if (
          rating !== null &&
          (typeof rating !== 'number' || !Number.isInteger(rating) || rating < 1 || rating > 5)
        ) {
          return jsonResponse(422, {
            code: 'validation_failed',
            errors: { rating: ['1 to 5 stars, or null.'] },
          });
        }
        const changed = { ...generation, rating, state, revision: generation.revision + 1 };
        store(changed);
        const chosen = server.song.selectedGeneration;
        if (chosen?.id === changed.id) {
          server.song = { ...server.song, selectedGeneration: { ...chosen, state } };
        }
        return jsonResponse(200, changed);
      }
      if (isComments && commentId === undefined && method === 'POST') {
        const text = textOf(body.text);
        const refused = invalidText(text);
        if (refused) {
          return refused;
        }
        const comment = testComment(generation.comments.length + 1, {
          id: `0199b1a0-7000-7000-9000-${String(server.generationWrites.length).padStart(12, '0')}`,
          text,
          createdAt: '2026-10-02T09:00:00Z',
        });
        store({ ...generation, comments: [...generation.comments, comment] });
        return jsonResponse(201, comment);
      }
      const comment = generation.comments.find((candidate) => candidate.id === commentId);
      if (comment === undefined) {
        return jsonResponse(404, { code: 'not_found' });
      }
      if (ifMatch !== `"${String(comment.revision)}"`) {
        return jsonResponse(409, { code: 'revision_conflict', current: comment });
      }
      if (method === 'DELETE') {
        store({
          ...generation,
          comments: generation.comments.filter((other) => other !== comment),
        });
        return new Response(null, { status: 204 });
      }
      const text = textOf(body.text);
      const refused = invalidText(text);
      if (refused) {
        return refused;
      }
      if (text === comment.text) {
        return jsonResponse(200, comment);
      }
      const edited = {
        ...comment,
        text,
        editedAt: '2026-10-02T09:30:00Z',
        revision: comment.revision + 1,
      };
      store({
        ...generation,
        comments: generation.comments.map((other) => (other === comment ? edited : other)),
      });
      return jsonResponse(200, edited);
    }

    const songGenerations = /\/api\/v1\/songs\/([^/]+)\/generations$/.exec(path);
    if (songGenerations && method === 'GET') {
      server.generationReads++;
      const nextGenerations = server.nextGenerations;
      if (nextGenerations) {
        server.nextGenerations = undefined;
        return nextGenerations();
      }
      const named = decodeURIComponent(songGenerations[1] ?? '');
      const other = server.otherSongs.find((song) => song.id === named || song.shortcode === named);
      if (other !== undefined) {
        return jsonResponse(200, {
          items: server.otherGenerations.filter((generation) => generation.song.id === other.id),
        });
      }
      if (named !== server.song.id && named !== server.song.shortcode) {
        return jsonResponse(404, { code: 'not_found' });
      }
      const numberOf = (generation: Generation) =>
        generation.version.shortcode.slice(`${server.song.shortcode}-v`.length);
      const items = [...server.generations].sort(
        (left, right) =>
          byNumber(testVersion(numberOf(left)), testVersion(numberOf(right))) ||
          left.ordinal - right.ordinal,
      );
      return jsonResponse(200, { items });
    }

    const selection = /\/api\/v1\/songs\/([^/]+)\/selected-generation$/.exec(path);
    if (selection && (method === 'PUT' || method === 'DELETE')) {
      const ifMatch = new Headers(init?.headers).get('If-Match');
      const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as Record<
        string,
        unknown
      >;
      server.selectionWrites.push({ method, ifMatch, body });
      const nextSelectionWrite = server.nextSelectionWrite;
      if (nextSelectionWrite) {
        server.nextSelectionWrite = undefined;
        return nextSelectionWrite();
      }
      if (ifMatch !== `"${String(server.song.revision)}"`) {
        return jsonResponse(409, { code: 'revision_conflict', current: server.song });
      }
      const chosen =
        method === 'DELETE'
          ? null
          : server.generations.find(
              (candidate) =>
                candidate.id === body.generation || candidate.shortcode === body.generation,
            );
      if (chosen === undefined) {
        return jsonResponse(404, { code: 'not_found' });
      }
      if ((server.song.selectedGeneration?.id ?? null) !== (chosen?.id ?? null)) {
        server.song = {
          ...server.song,
          hasSelectedGeneration: chosen !== null,
          selectedGeneration:
            chosen === null
              ? null
              : {
                  id: chosen.id,
                  shortcode: chosen.shortcode,
                  state: chosen.state,
                  remoteState: chosen.remoteState,
                },
          revision: server.song.revision + 1,
        };
        server.generations = server.generations.map((generation) => ({
          ...generation,
          isSelected: generation.id === chosen?.id,
        }));
      }
      return jsonResponse(200, server.song);
    }

    if (path.endsWith('/api/v1/songs') && method === 'GET') {
      const url = input instanceof Request ? input.url : input.toString();
      const search = new URL(url, document.baseURI).searchParams.get('q')?.toLowerCase();
      const live = server.songDeletedAt === undefined ? [server.song] : [];
      const items =
        search === undefined
          ? live
          : [...live, ...server.otherSongs].filter(
              (song) =>
                song.title.toLowerCase().includes(search) ||
                song.shortcode.toLowerCase().startsWith(search),
            );
      return jsonResponse(200, { items, page: 1, pageSize: 50, total: items.length });
    }
    const deletion = /\/api\/v1\/songs\/([^/]+)(\/deletion-impact)?$/.exec(path);
    const named = deletion?.[1] === undefined ? undefined : decodeURIComponent(deletion[1]);
    const isSong = named === server.song.id || named === server.song.shortcode;
    if (isSong && server.songDeletedAt !== undefined) {
      return jsonResponse(404, {
        code: 'song_deleted',
        songId: server.song.id,
        shortcode: server.song.shortcode,
        title: server.song.title,
        deletedAt: server.songDeletedAt,
      });
    }
    if (isSong && (deletion?.[2] !== undefined || method === 'DELETE')) {
      const impact = songImpact();
      if (method === 'GET') {
        return jsonResponse(200, impact);
      }
      const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as Record<
        string,
        unknown
      >;
      const ifMatch = new Headers(init?.headers).get('If-Match');
      server.songDeletes.push({ ifMatch, body });
      const nextSongDelete = server.nextSongDelete;
      if (nextSongDelete) {
        server.nextSongDelete = undefined;
        return nextSongDelete();
      }
      if (ifMatch !== `"${String(server.song.revision)}"`) {
        return jsonResponse(409, { code: 'revision_conflict', current: server.song });
      }
      const typed = typeof body.confirmTitle === 'string' ? body.confirmTitle.trim() : undefined;
      if (impact.titleRequired && typed !== server.song.title) {
        return jsonResponse(422, { code: 'confirmation_required', impact });
      }
      server.songDeletedAt = '2026-10-01T10:00:00Z';
      return new Response(null, { status: 204 });
    }

    const match = /\/api\/v1\/songs\/([^/]+)(\/versions|\/current-version)?$/.exec(path);
    const reference = match?.[1] === undefined ? undefined : decodeURIComponent(match[1]);
    if (reference !== server.song.id && reference !== server.song.shortcode) {
      return jsonResponse(404, { code: 'not_found' });
    }
    const resource = match?.[2];
    if (method === 'GET') {
      return resource === '/versions'
        ? jsonResponse(200, {
            items: [...server.versions].sort(byNumber).map(summary),
            deletedPlaceholders: placeholders(),
          })
        : jsonResponse(200, server.song);
    }

    const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as Record<
      string,
      unknown
    >;
    server.writes.push({ method, path, body });
    const next = server.next;
    if (next) {
      server.next = undefined;
      return next();
    }

    const makeCurrent = (version: VersionDetail) => {
      server.versions = server.versions.map((other) => ({
        ...other,
        current: other.id === version.id,
      }));
      server.song = {
        ...server.song,
        currentVersion: {
          id: version.id,
          number: version.number,
          shortcode: version.shortcode,
          kind: version.kind,
        },
      };
    };

    if (method === 'PUT' && resource === '/current-version') {
      const version = server.versions.find((candidate) => candidate.id === body.versionId);
      if (!version) {
        return jsonResponse(422, {
          code: 'validation_failed',
          errors: { versionId: ['Choose a Version of this Song.'] },
        });
      }
      makeCurrent(version);
      return jsonResponse(200, server.song);
    }

    if (method === 'POST' && resource === '/versions') {
      const source = server.versions.find((candidate) => candidate.id === body.sourceVersionId);
      if (!source || typeof body.number !== 'string') {
        return jsonResponse(422, { code: 'validation_failed', errors: { number: ['Choose.'] } });
      }
      const options = optionsFor(source.number, used());
      if (!options.some((option) => option.number === body.number)) {
        return used().has(body.number)
          ? jsonResponse(409, { code: 'version_number_taken', options })
          : jsonResponse(422, { code: 'version_number_not_offered', options });
      }
      const name =
        typeof body.name === 'string' && body.name.trim() !== '' ? body.name.trim() : null;
      const created = testVersion(body.number, {
        name,
        current: true,
        lyrics: typeof body.lyrics === 'string' ? body.lyrics : source.lyrics,
        styles: typeof body.styles === 'string' ? body.styles : source.styles,
        inputs: source.inputs,
      });
      server.versions.push(created);
      server.usedNumbers.add(created.number);
      makeCurrent(created);
      server.song = { ...server.song, versionCount: server.versions.length };
      return jsonResponse(201, summary(created));
    }

    return jsonResponse(404, { code: 'not_found' });
  });

  return { server, mock };
}
