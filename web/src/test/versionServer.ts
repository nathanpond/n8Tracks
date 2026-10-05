import type { Song } from '../api/songs';
import type { NumberOption, Version } from '../api/versions';
import { healthyReport, jsonResponse, requestPath, stubFetch } from './helpers';
import { baseSong, STATES } from './songServer';

/** A Version of `baseSong` numbered `number`, with an ID built from the number. */
export function testVersion(number: string, change: Partial<Version> = {}): Version {
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
    ...change,
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
 * Versions, a Version's next numbers, creating a Version (which becomes current), making one
 * current, and editing a Version's name, notes, or archived flag on its revision. A test changes `server.versions` to play another client, or sets `server.next` to
 * answer the next write some other way.
 */
export function versionServer(versions: Version[], song: Song = baseSong) {
  const server = {
    song: { ...song },
    versions: versions.map((version) => ({ ...version })),
    writes: [] as ReceivedWrite[],
    /** When set, answers the next write (once) instead of the fake API. */
    next: undefined as (() => Response | Promise<Response>) | undefined,
    /** Plays another client creating a Version with `number`. */
    addElsewhere(number: string) {
      server.versions.push(testVersion(number));
    },
    /** Plays another client editing the Version numbered `number`: its revision goes up by one. */
    changeElsewhere(number: string, change: Partial<Version>) {
      server.versions = server.versions.map((version) =>
        version.number === number
          ? { ...version, ...change, revision: version.revision + 1 }
          : version,
      );
    },
  };

  const used = () => new Set(server.versions.map((version) => version.number));

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
    const numbers = /\/api\/v1\/versions\/([^/]+)\/next-numbers$/.exec(path);
    if (numbers) {
      const source = server.versions.find((version) => version.id === numbers[1]);
      return source
        ? jsonResponse(200, { options: optionsFor(source.number, used()) })
        : jsonResponse(404, { code: 'not_found' });
    }
    const edited = /\/api\/v1\/versions\/([^/]+)$/.exec(path);
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
      const changed: Version = {
        ...version,
        ...('name' in body ? { name: text(body.name) } : {}),
        ...('notes' in body ? { notes: text(body.notes) } : {}),
        ...(typeof body.archived === 'boolean' ? { archived: body.archived } : {}),
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
        ? jsonResponse(200, { items: server.versions })
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

    const makeCurrent = (version: Version) => {
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
      const created = testVersion(body.number, { name, current: true });
      server.versions.push(created);
      makeCurrent(created);
      server.song = { ...server.song, versionCount: server.versions.length };
      return jsonResponse(201, created);
    }

    return jsonResponse(404, { code: 'not_found' });
  });

  return { server, mock };
}
