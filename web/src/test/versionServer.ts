import type { Snapshot } from '../api/snapshots';
import type { Song } from '../api/songs';
import type { NumberOption, Version, VersionDetail } from '../api/versions';
import { healthyReport, jsonResponse, requestPath, stubFetch } from './helpers';
import { baseSong, STATES } from './songServer';

/** A Version of `baseSong` numbered `number`, with an ID built from the number and no lyrics or styles. */
export function testVersion(number: string, change: Partial<VersionDetail> = {}): VersionDetail {
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
    ...change,
  };
}

/** A Version as the list answers it: without its lyrics and styles. */
function summary(version: VersionDetail): Version {
  const { id, songId, number, shortcode, name, notes, archived, current } = version;
  const { createdAt, updatedAt, revision, isFrozen } = version;
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
 * lyrics, or styles on its revision (lyrics and styles kept as sent, line endings aside, and refused
 * over their limits, or with 409 `version_frozen` when the Version is frozen; a create may carry
 * its own lyrics and styles). A test changes `server.versions` to play another client, or sets `server.next` to
 * answer the next write some other way.
 */
export function versionServer(versions: VersionDetail[], song: Song = baseSong) {
  const server = {
    song: { ...song },
    versions: versions.map((version) => ({ ...version })),
    writes: [] as ReceivedWrite[],
    /** Every snapshot stored, oldest first (the API's deduplication applied). */
    snapshots: [] as Snapshot[],
    /** Every snapshot request received, stored or not. */
    snapshotRequests: [] as Record<string, unknown>[],
    /** When set, answers the next snapshot request (once) instead of the fake API. */
    nextSnapshot: undefined as (() => Response | Promise<Response>) | undefined,
    /** When set, answers the next write (once) instead of the fake API. */
    next: undefined as (() => Response | Promise<Response>) | undefined,
    /** Plays another client creating a Version with `number`. */
    addElsewhere(number: string) {
      server.versions.push(testVersion(number));
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

  const used = () => new Set(server.versions.map((version) => version.number));
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

  /** The snapshots API: take one, list them newest first, read one, restore one on a revision. */
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
    return version
      ? jsonResponse(200, {
          entityType: 'version',
          id: version.id,
          shortcode: version.shortcode,
          status: version.archived ? 'archived' : 'active',
          song: { id: song.id, shortcode: song.shortcode },
        })
      : jsonResponse(404, { code: 'reference_not_found' });
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
    const history = /\/api\/v1\/versions\/([^/]+)\/snapshots(?:\/([^/]+))?(\/restore)?$/.exec(path);
    if (history) {
      return answerHistory(history[1] ?? '', history[2], history[3] !== undefined, method, init);
    }
    const edited = /\/api\/v1\/versions\/([^/]+)$/.exec(path);
    if (edited && method === 'GET') {
      const version = server.versions.find((candidate) => candidate.id === edited[1]);
      return version ? jsonResponse(200, version) : jsonResponse(404, { code: 'not_found' });
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
        return jsonResponse(404, { code: 'not_found' });
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
      if (
        version.isFrozen &&
        (('lyrics' in body && input(body.lyrics) !== version.lyrics) ||
          ('styles' in body && input(body.styles) !== version.styles))
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
        revision: version.revision + 1,
      };
      server.versions = server.versions.map((other) => (other.id === version.id ? changed : other));
      return jsonResponse(200, changed);
    }

    const match = /\/api\/v1\/songs\/([^/]+)(\/versions|\/current-version)?$/.exec(path);
    const reference = match?.[1] === undefined ? undefined : decodeURIComponent(match[1]);
    if (reference !== server.song.id && reference !== server.song.shortcode) {
      return jsonResponse(404, { code: 'not_found' });
    }
    const resource = match?.[2];
    if (method === 'GET') {
      return resource === '/versions'
        ? jsonResponse(200, { items: server.versions.map(summary) })
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
        currentVersion: { id: version.id, number: version.number, shortcode: version.shortcode },
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
      });
      server.versions.push(created);
      makeCurrent(created);
      server.song = { ...server.song, versionCount: server.versions.length };
      return jsonResponse(201, summary(created));
    }

    return jsonResponse(404, { code: 'not_found' });
  });

  return { server, mock };
}
