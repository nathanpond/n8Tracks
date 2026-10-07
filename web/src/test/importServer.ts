import type {
  CommitJob,
  CommitResult,
  ImportChoice,
  ImportFilter,
  ImportRecord,
  ImportSummary,
  ImportTargets,
  NamedTarget,
  SunoImport,
} from '../api/sunoImports';
import type { RemoteStateRow } from '../api/sunoRemoteStates';
import { healthyReport, jsonResponse, requestPath, stubFetch } from './helpers';
import { baseSong } from './songServer';

export const EXPORT_ID = '0199c000-0000-7000-8000-000000000001';

/** The commit job the fake starts. */
export const COMMIT_JOB_ID = '0199c300-0000-7000-8000-000000000001';

/** A commit's result: one new Song with two Generations, one record skipped, one failed. */
export function testCommitResult(change: Partial<CommitResult> = {}): CommitResult {
  return {
    records: [
      {
        sunoId: 'a',
        outcome: 'created',
        generation: {
          id: '0199c400-0000-7000-8000-000000000001',
          shortcode: 'n8-9-v1-g1',
          songId: '0199c400-0000-7000-8000-0000000000aa',
        },
      },
      {
        sunoId: 'b',
        outcome: 'created',
        generation: {
          id: '0199c400-0000-7000-8000-000000000002',
          shortcode: 'n8-9-v1-g2',
          songId: '0199c400-0000-7000-8000-0000000000aa',
        },
      },
      { sunoId: 'linked', outcome: 'skipped' },
      { sunoId: 'changed', outcome: 'failed', reason: 'inputs_differ' },
    ],
    created: { songs: 1, versions: 1, generations: 2 },
    songs: [
      {
        id: '0199c400-0000-7000-8000-0000000000aa',
        shortcode: 'n8-9',
        title: 'Morning Light',
        created: true,
      },
    ],
    ...change,
  };
}

/** A Suno state change (#142): by default Alpha (`a`) in Suno's Trash, its active Generation to be archived. */
export function testRemoteState(
  sunoId: string,
  change: Partial<RemoteStateRow> = {},
): RemoteStateRow {
  return {
    sunoId,
    title: `Clip ${sunoId}`,
    change: 'trashed',
    remoteState: 'present',
    newRemoteState: 'trashed',
    state: 'active',
    newState: 'archived',
    archives: true,
    reactivates: false,
    apply: true,
    generation: {
      id: `0199c500-0000-7000-8000-0000000000${sunoId.padStart(2, '0').slice(-2)}`,
      shortcode: `n8-7-v1-g${String(sunoId.length)}`,
      songShortcode: 'n8-7',
    },
    ...change,
  };
}

/** A ready export with the counts given. */
export function testImport(change: Partial<SunoImport> = {}): SunoImport {
  return {
    id: EXPORT_ID,
    state: 'ready',
    createdAt: '2026-10-06T12:00:00Z',
    readyAt: '2026-10-06T12:00:05Z',
    endedAt: null,
    expiresAt: '2026-10-13T12:00:05Z',
    capturedAt: '2026-10-06T12:00:00Z',
    libraryComplete: true,
    counts: {},
    revision: 1,
    libraryExcluded: [],
    ...change,
  };
}

/** A staged record: a new Song by default, in the workspace Studio. */
export function testRecord(sunoId: string, change: Partial<ImportRecord> = {}): ImportRecord {
  return {
    sunoId,
    title: `Song ${sunoId}`,
    workspaceId: 'studio',
    createdAt: '2026-10-01T17:52:08Z',
    durationSeconds: 125,
    class: 'new',
    trashed: false,
    playlistIds: [],
    proposal: null,
    choice: { action: 'skip' },
    flags: [],
    generationId: null,
    target: null,
    generation: null,
    ...change,
  };
}

/** A record imported to a new Song keyed `key` and titled `title`. */
export function toNewSong(record: ImportRecord, key: string, title: string): ImportRecord {
  return {
    ...record,
    choice: {
      action: 'import',
      target: { kind: 'newSong', key, title, workspaceId: record.workspaceId },
    },
    target: {
      kind: 'newSong',
      key,
      song: { id: null, key, shortcode: null, title },
      version: null,
      parent: null,
      number: '1',
    },
  };
}

/** The Song "Target" the targets fake answers for, with Versions 1 and 2 (2 holds every record's inputs). */
export const TARGET_SONG = {
  ...baseSong,
  id: '0199c100-0000-7000-8000-000000000001',
  shortcode: 'n8-7',
  title: 'Target',
};

const V1 = {
  id: '0199c100-0000-7000-8000-0000000000a1',
  number: '1',
  shortcode: 'n8-7-v1',
  isFrozen: true,
};
const V2 = {
  id: '0199c100-0000-7000-8000-0000000000a2',
  number: '2',
  shortcode: 'n8-7-v2',
  isFrozen: false,
};

interface ReceivedPatch {
  ifMatch: string | null;
  body: Record<string, unknown>;
}

function matches(record: ImportRecord, filter: ImportFilter): boolean {
  return (
    (filter.class === undefined || record.class === filter.class) &&
    (filter.workspace === undefined || record.workspaceId === filter.workspace) &&
    (filter.playlist === undefined || record.playlistIds.includes(filter.playlist)) &&
    (filter.q === undefined || (record.title ?? '').toLowerCase().includes(filter.q.toLowerCase()))
  );
}

function filterOf(parameters: URLSearchParams | Record<string, unknown>): ImportFilter {
  const read = (name: string) => {
    const value = parameters instanceof URLSearchParams ? parameters.get(name) : parameters[name];
    return typeof value === 'string' && value !== '' ? value : undefined;
  };
  const filter: ImportFilter = {};
  const recordClass = read('class');
  if (recordClass !== undefined) {
    filter.class = recordClass as ImportFilter['class'];
  }
  const workspace = read('workspace');
  if (workspace !== undefined) {
    filter.workspace = workspace;
  }
  const playlist = read('playlist');
  if (playlist !== undefined) {
    filter.playlist = playlist;
  }
  const q = read('q');
  if (q !== undefined) {
    filter.q = q;
  }
  return filter;
}

/** The target a stored choice names, as the server names it. */
function named(choice: ImportChoice): NamedTarget | null {
  if (choice.action !== 'import') {
    return null;
  }
  const target = choice.target;
  const song = {
    id: TARGET_SONG.id,
    key: null,
    shortcode: TARGET_SONG.shortcode,
    title: TARGET_SONG.title,
  };
  switch (target.kind) {
    case 'newSong':
      return {
        kind: 'newSong',
        key: target.key,
        song: { id: null, key: target.key, shortcode: null, title: target.title },
        version: null,
        parent: null,
        number: '1',
      };
    case 'newVersion':
      return {
        kind: 'newVersion',
        key: target.key,
        song,
        version: null,
        parent: null,
        number: target.number,
      };
    default:
      return {
        kind: 'version',
        key: null,
        song,
        version: [V1, V2].find((version) => version.id === target.version) ?? null,
        parent: null,
        number: '2',
      };
  }
}

function reviewable(record: ImportRecord): boolean {
  return record.class === 'new' || record.class === 'ignored' || record.class === 'deleted';
}

/**
 * A fake n8Tracks holding one export and its records: it answers the export, its summary (computed from
 * the records' choices), a page of records (filtered as the API does), the targets in the Song "Target",
 * the current export, a change of choices (If-Match on the revision; by ID or by filter), discard, and a
 * Song search that finds "Target". `server.patches` holds each change; `server.nextPatch` answers the
 * next one instead; `server.invalid` is the summary's invalid choices; `server.changeElsewhere()` raises
 * the revision as another window would.
 */
export function importServer(records: ImportRecord[], exported: Partial<SunoImport> = {}) {
  const server = {
    export: testImport(exported),
    records: records.map((record) => ({ ...record })),
    patches: [] as ReceivedPatch[],
    recordQueries: [] as string[],
    targetQueries: [] as string[],
    discards: 0,
    nextPatch: undefined as (() => Response) | undefined,
    invalid: {} as Record<string, string[]>,
    workspaces: [
      { id: 'studio', name: 'Studio', count: 0 },
      { id: 'demos', name: 'Demos', count: 0 },
    ],
    playlists: [] as { id: string; name: string | null; count: number }[],
    /** The Suno state changes (#142) and each change of them received. */
    remoteStates: [] as RemoteStateRow[],
    remotePatches: [] as ReceivedPatch[],
    current: undefined as { waiting: SunoImport | null; last: SunoImport | null } | undefined,
    /** The If-Match of each commit request. */
    commits: [] as (string | null)[],
    nextCommit: undefined as (() => Response) | undefined,
    /** The commit job as the jobs endpoint answers it; null answers 404 (pruned). */
    job: null as CommitJob | null,
    jobReads: 0,
    /** The job ends: succeeded with `result` (the export committed), or failed (the export back to ready). */
    finishCommit(result: CommitResult | null) {
      server.job = {
        id: COMMIT_JOB_ID,
        status: result === null ? 'failed' : 'succeeded',
        progress: result === null ? 40 : 100,
        message: '2 of 2 targets',
        error: result === null ? 'interrupted by restart' : null,
        result,
      };
      server.export = { ...server.export, state: result === null ? 'ready' : 'committed' };
    },
    changeElsewhere() {
      server.export = { ...server.export, revision: server.export.revision + 1 };
    },
  };

  const counted = (): SunoImport => {
    const counts: Record<string, number> = { total: server.records.length };
    for (const record of server.records) {
      if (record.class !== null) {
        counts[record.class] = (counts[record.class] ?? 0) + 1;
      }
    }
    return { ...server.export, counts };
  };

  const summary = (): ImportSummary => {
    const imports = server.records.filter((record) => record.choice?.action === 'import');
    const keys = (kind: string) =>
      new Set(
        imports.flatMap((record) =>
          record.choice?.action === 'import' &&
          record.choice.target.kind === kind &&
          'key' in record.choice.target
            ? [record.choice.target.key]
            : [],
        ),
      ).size;
    const ignored = server.records.filter(
      (record) =>
        record.choice?.action !== 'import' &&
        (record.choice?.action === 'ignore' || record.class === 'ignored'),
    ).length;
    const newlyIgnored = server.records.filter(
      (record) => record.choice?.action === 'ignore' && record.class !== 'ignored',
    ).length;
    const invalidCount = Object.keys(server.invalid).length;
    return {
      export: counted(),
      songs: keys('newSong'),
      versions: keys('newSong') + keys('newVersion'),
      generations: imports.length,
      reimports: imports.filter((record) => record.class === 'deleted').length,
      ignored,
      skipped: server.records.length - imports.length - ignored,
      valid: invalidCount === 0,
      invalidCount,
      invalid: server.invalid,
      nothingToDo:
        imports.length === 0 &&
        newlyIgnored === 0 &&
        server.remoteStates.every((row) => !row.apply),
      nextKey: 'new:90',
      workspaces: server.workspaces.map((workspace) => ({
        ...workspace,
        count: server.records.filter((record) => record.workspaceId === workspace.id).length,
      })),
      playlists: server.playlists,
      libraryExcluded: server.export.libraryExcluded,
      revision: server.export.revision,
      remoteChanges: server.remoteStates.filter((row) => row.apply).length,
      remoteChangesTotal: server.remoteStates.length,
    };
  };

  const targets = (parent: string | null): ImportTargets => ({
    song: { id: TARGET_SONG.id, shortcode: TARGET_SONG.shortcode, title: TARGET_SONG.title },
    matching: [V2],
    versions: [V1, V2],
    parent: [V1, V2].find((version) => version.id === parent) ?? null,
    numbers:
      parent === null
        ? [{ number: '3', kind: 'topLevel', proposed: true }]
        : [
            { number: '2.1', kind: 'child', proposed: true },
            { number: '3', kind: 'sibling', proposed: false },
          ],
  });

  stubFetch().mockImplementation((input, init) => {
    const path = requestPath(input);
    const url = new URL(input instanceof Request ? input.url : input.toString(), document.baseURI);
    const method = (init?.method ?? 'GET').toUpperCase();
    const base = `/api/v1/suno/exports/${EXPORT_ID}`;
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (path.endsWith('/api/v1/suno/exports/current')) {
      const ready = server.export.state === 'ready' ? counted() : null;
      return Promise.resolve(
        jsonResponse(200, server.current ?? { waiting: ready, last: counted() }),
      );
    }
    if (path.endsWith('/api/v1/songs') && url.searchParams.has('q')) {
      return Promise.resolve(
        jsonResponse(200, { items: [TARGET_SONG], page: 1, pageSize: 10, total: 1 }),
      );
    }
    if (path.includes(`/api/v1/jobs/${COMMIT_JOB_ID}`)) {
      server.jobReads += 1;
      return Promise.resolve(
        server.job === null
          ? jsonResponse(404, { code: 'not_found' })
          : jsonResponse(200, server.job),
      );
    }
    if (!path.includes(base)) {
      return Promise.resolve(jsonResponse(404, { code: 'not_found' }));
    }
    const rest = path.slice(path.indexOf(base) + base.length);
    if (rest === '' && method === 'GET') {
      return Promise.resolve(jsonResponse(200, counted()));
    }
    if (rest === '/summary') {
      return Promise.resolve(jsonResponse(200, summary()));
    }
    if (rest === '/commit' && method === 'POST') {
      const headers = new Headers(init?.headers);
      server.commits.push(headers.get('If-Match'));
      const next = server.nextCommit;
      if (next !== undefined) {
        server.nextCommit = undefined;
        return Promise.resolve(next());
      }
      if (headers.get('If-Match') !== `"${String(server.export.revision)}"`) {
        return Promise.resolve(
          jsonResponse(409, { code: 'revision_conflict', current: counted() }),
        );
      }
      server.export = { ...server.export, state: 'committing', jobId: COMMIT_JOB_ID };
      server.job = {
        id: COMMIT_JOB_ID,
        status: 'running',
        progress: 50,
        message: '1 of 2 targets',
        error: null,
        result: null,
      };
      return Promise.resolve(jsonResponse(202, counted()));
    }
    if (rest === '/discard' && method === 'POST') {
      server.discards += 1;
      server.export = { ...server.export, state: 'discarded', endedAt: '2026-10-06T13:00:00Z' };
      return Promise.resolve(jsonResponse(200, counted()));
    }
    const target = /^\/records\/([^/]+)\/targets$/.exec(rest);
    if (target !== null) {
      server.targetQueries.push(url.search);
      return Promise.resolve(jsonResponse(200, targets(url.searchParams.get('parent'))));
    }
    if (rest === '/records' && method === 'GET') {
      server.recordQueries.push(url.search);
      const filter = filterOf(url.searchParams);
      const items = server.records.filter((record) => matches(record, filter));
      return Promise.resolve(
        jsonResponse(200, { items, page: 1, pageSize: 100, total: items.length }),
      );
    }
    if (rest === '/remote-states' && method === 'GET') {
      const q = url.searchParams.get('q')?.toLowerCase();
      const items = server.remoteStates.filter(
        (row) => q === undefined || (row.title ?? '').toLowerCase().includes(q),
      );
      const count = (change: string) =>
        server.remoteStates.filter((row) => row.change === change).length;
      const applied = server.remoteStates.filter((row) => row.apply).length;
      return Promise.resolve(
        jsonResponse(200, {
          items,
          page: 1,
          pageSize: 100,
          total: items.length,
          counts: {
            trashed: count('trashed'),
            restored: count('restored'),
            missing: count('missing'),
            applied,
            skipped: server.remoteStates.length - applied,
          },
          missingChecked: server.export.libraryComplete,
          revision: server.export.revision,
        }),
      );
    }
    if (rest === '/remote-states' && method === 'PATCH') {
      const headers = new Headers(init?.headers);
      const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as {
        sunoIds: string[];
        apply: boolean;
      };
      server.remotePatches.push({ ifMatch: headers.get('If-Match'), body });
      if (headers.get('If-Match') !== `"${String(server.export.revision)}"`) {
        return Promise.resolve(
          jsonResponse(409, { code: 'revision_conflict', current: counted() }),
        );
      }
      server.remoteStates = server.remoteStates.map((row) =>
        body.sunoIds.includes(row.sunoId) ? { ...row, apply: body.apply } : row,
      );
      server.export = { ...server.export, revision: server.export.revision + 1 };
      return Promise.resolve(jsonResponse(200, counted()));
    }
    if (rest === '/records' && method === 'PATCH') {
      const headers = new Headers(init?.headers);
      const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as Record<
        string,
        unknown
      >;
      server.patches.push({ ifMatch: headers.get('If-Match'), body });
      const next = server.nextPatch;
      if (next !== undefined) {
        server.nextPatch = undefined;
        return Promise.resolve(next());
      }
      if (headers.get('If-Match') !== `"${String(server.export.revision)}"`) {
        return Promise.resolve(
          jsonResponse(409, { code: 'revision_conflict', current: counted() }),
        );
      }
      const choice = body.choice as ImportChoice;
      const except = Array.isArray(body.except) ? (body.except as string[]) : [];
      const chosen = Array.isArray(body.sunoIds)
        ? (body.sunoIds as string[])
        : server.records
            .filter(
              (record) =>
                reviewable(record) &&
                matches(record, filterOf(body.filter as Record<string, unknown>)) &&
                !except.includes(record.sunoId),
            )
            .map((record) => record.sunoId);
      server.records = server.records.map((record) =>
        chosen.includes(record.sunoId) ? { ...record, choice, target: named(choice) } : record,
      );
      server.export = { ...server.export, revision: server.export.revision + 1 };
      return Promise.resolve(jsonResponse(200, counted()));
    }
    return Promise.resolve(jsonResponse(404, { code: 'not_found' }));
  });
  return server;
}
