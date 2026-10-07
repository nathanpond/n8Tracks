import { useEffect, useRef, useState } from 'react';
import { apiFetch } from './client';
import { ifMatch } from './saves';
import { body, isErrorMap, isRecord, useResource } from './songs';

/** Where the review page of the export with ID `id` is (#139); the extension opens it after a sync. */
export function importPath(id: string): string {
  return `/suno/imports/${encodeURIComponent(id)}`;
}

/** The Suno entry of the sidebar: it opens the export waiting for review, or says how to start one. */
export const IMPORTS_PATH = '/suno/imports';

/** How many records the review lists on one page. */
export const IMPORT_PAGE_SIZE = 100;

/** How many records a change of choices may name by ID; more are chosen by filter. */
export const MAXIMUM_NAMED_RECORDS = 1000;

/** Where an export is in its life (#131). */
export type ImportState =
  | 'receiving'
  | 'classifying'
  | 'ready'
  | 'committing'
  | 'committed'
  | 'discarded'
  | 'failed'
  | 'expired';

/** What a record is to n8Tracks, by its Suno ID. */
export type RecordClass = 'new' | 'linked' | 'changed' | 'conflict' | 'ignored' | 'deleted';

export const RECORD_CLASSES: readonly RecordClass[] = [
  'new',
  'linked',
  'changed',
  'conflict',
  'ignored',
  'deleted',
];

/** A staged Suno export, as `GET /api/v1/suno/exports/{id}` answers it (the fields the review reads). */
export interface SunoImport {
  id: string;
  state: ImportState;
  createdAt: string;
  readyAt: string | null;
  endedAt: string | null;
  expiresAt: string | null;
  capturedAt: string;
  libraryComplete: boolean;
  /** How many records it has in each class, and `total`. */
  counts: Record<string, number>;
  revision: number;
  /** The kinds of clip Suno's library filters left out, by filter name (`disliked`, `stem`…). */
  libraryExcluded: string[];
  /** The background job last run for it: classifying a large export, or committing it (#140). */
  jobId?: string | null;
}

/** A new Song, a new Version of a Song, or an existing Version, as stored in a choice. */
export type ImportTargetChoice =
  | { kind: 'newSong'; key: string; title: string; workspaceId: string | null }
  | {
      kind: 'newVersion';
      key: string;
      /** A Song's ID or shortcode, or a new Song's key. */
      song: string;
      parentVersion: string | null;
      number: string;
    }
  | { kind: 'version'; version: string };

/**
 * What the user wants done with a record: import it to a target, Skip this time, or Don't copy
 * (`ignore`). A Changed record may `apply` the fields accepted from Suno; a Conflict record may
 * `moveToNewVersion` or `keep`, each with the fields accepted from the metadata diff beneath (#141).
 */
export type ImportChoice =
  | { action: 'import'; target: ImportTargetChoice }
  | { action: 'skip' }
  | { action: 'ignore' }
  | { action: 'apply' | 'moveToNewVersion' | 'keep'; acceptFields: string[] };

/** The provider fields a diff compares, in the order it lists them (#141). */
export const DIFF_FIELDS = [
  'title',
  'tags',
  'duration',
  'modelVersion',
  'modelName',
  'minimumBpm',
  'maximumBpm',
  'averageBpm',
  'key',
  'imageUrl',
] as const;

/** One field in which Suno's copy differs: n8Tracks' value and Suno's. */
export interface FieldDiff {
  field: string;
  current: string | number | null;
  incoming: string | number | null;
}

/** How a Changed or Conflict record differs (#141): its fields, and for a Conflict the creation inputs. */
export interface RecordDiff {
  sunoId: string;
  class: 'changed' | 'conflict';
  generationId: string;
  fields: FieldDiff[];
  inputs: { field: string; current: string; incoming: string }[];
}

/** A Song a choice names: an existing one (ID, shortcode, title; the title null once gone) or a new one (its key and title). */
export interface TargetSong {
  id: string | null;
  key: string | null;
  shortcode: string | null;
  title: string | null;
}

/** A Version a review names. */
export interface TargetVersion {
  id: string;
  number: string;
  shortcode: string;
  isFrozen: boolean;
}

/** A record's import target as the server names it for the review. */
export interface NamedTarget {
  kind: 'newSong' | 'newVersion' | 'version';
  key: string | null;
  song: TargetSong;
  version: TargetVersion | null;
  parent: TargetVersion | null;
  number: string | null;
}

/** One staged record of the review: never its raw clip. */
export interface ImportRecord {
  sunoId: string;
  title: string | null;
  workspaceId: string | null;
  createdAt: string | null;
  durationSeconds: number | null;
  class: RecordClass | null;
  trashed: boolean;
  playlistIds: string[];
  proposal: { choice: ImportChoice; basis: string; group: number | null } | null;
  choice: ImportChoice | null;
  flags: string[];
  /** The provider fields a Changed or Conflict record differs in (#141). */
  changedFields?: string[];
  generationId: string | null;
  target: NamedTarget | null;
  generation: { id: string; shortcode: string; songShortcode: string } | null;
}

export interface ImportRecordPage {
  items: ImportRecord[];
  page: number;
  pageSize: number;
  total: number;
}

/** A workspace or playlist to filter by, with how many records are in it. */
export interface ImportFacet {
  id: string;
  name: string | null;
  count: number;
}

/** What confirming the export would do as its choices stand, and whether every choice is valid. */
export interface ImportSummary {
  export: SunoImport;
  songs: number;
  versions: number;
  generations: number;
  reimports: number;
  ignored: number;
  skipped: number;
  /** Changed and Conflict records whose differences the user decided (#141). */
  resolved?: number;
  valid: boolean;
  invalidCount: number;
  /** The reasons each invalid choice cannot be made now, by Suno ID (codes such as `inputs_differ`). */
  invalid: Record<string, string[]>;
  nothingToDo: boolean;
  /** A temporary key (`new:<n>`) no choice uses, for a new Song or Version. */
  nextKey: string;
  workspaces: ImportFacet[];
  playlists: ImportFacet[];
  libraryExcluded: string[];
  revision: number;
}

/** The filters of the review: class, workspace, playlist (Suno IDs), and text in the title. */
export interface ImportFilter {
  class?: RecordClass;
  workspace?: string;
  playlist?: string;
  q?: string;
}

/** Where a record may go in one Song: the Versions holding its inputs, every Version, and the numbers a new one may take. */
export interface ImportTargets {
  song: { id: string; shortcode: string; title: string };
  matching: TargetVersion[];
  versions: TargetVersion[];
  parent: TargetVersion | null;
  numbers: { number: string; kind: 'sibling' | 'child' | 'topLevel'; proposed: boolean }[];
}

const STATES: readonly string[] = [
  'receiving',
  'classifying',
  'ready',
  'committing',
  'committed',
  'discarded',
  'failed',
  'expired',
];

function isNullableString(value: unknown): value is string | null {
  return value === null || typeof value === 'string';
}

function isStringList(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === 'string');
}

export function isSunoImport(value: unknown): value is SunoImport {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.state === 'string' &&
    STATES.includes(value.state) &&
    typeof value.createdAt === 'string' &&
    isNullableString(value.readyAt) &&
    isNullableString(value.endedAt) &&
    isNullableString(value.expiresAt) &&
    typeof value.capturedAt === 'string' &&
    typeof value.libraryComplete === 'boolean' &&
    isRecord(value.counts) &&
    typeof value.revision === 'number' &&
    isStringList(value.libraryExcluded)
  );
}

function isTargetVersion(value: unknown): value is TargetVersion {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.number === 'string' &&
    typeof value.shortcode === 'string' &&
    typeof value.isFrozen === 'boolean'
  );
}

function isNamedTarget(value: unknown): value is NamedTarget {
  return (
    isRecord(value) &&
    (value.kind === 'newSong' || value.kind === 'newVersion' || value.kind === 'version') &&
    isNullableString(value.key) &&
    isRecord(value.song) &&
    isNullableString(value.song.title) &&
    (value.version === null || isTargetVersion(value.version)) &&
    (value.parent === null || isTargetVersion(value.parent)) &&
    isNullableString(value.number)
  );
}

function isChoice(value: unknown): value is ImportChoice {
  return (
    isRecord(value) &&
    (value.action === 'skip' ||
      value.action === 'ignore' ||
      (value.action === 'import' && isRecord(value.target)) ||
      ((value.action === 'apply' ||
        value.action === 'moveToNewVersion' ||
        value.action === 'keep') &&
        isStringList(value.acceptFields)))
  );
}

export function isImportRecord(value: unknown): value is ImportRecord {
  return (
    isRecord(value) &&
    typeof value.sunoId === 'string' &&
    isNullableString(value.title) &&
    isNullableString(value.workspaceId) &&
    isNullableString(value.createdAt) &&
    (value.durationSeconds === null || typeof value.durationSeconds === 'number') &&
    (value.class === null ||
      (typeof value.class === 'string' && RECORD_CLASSES.includes(value.class as RecordClass))) &&
    isStringList(value.playlistIds) &&
    (value.choice === null || isChoice(value.choice)) &&
    isStringList(value.flags) &&
    (value.target === null || isNamedTarget(value.target)) &&
    (value.generation === null ||
      (isRecord(value.generation) &&
        typeof value.generation.shortcode === 'string' &&
        typeof value.generation.songShortcode === 'string'))
  );
}

function isFacet(value: unknown): value is ImportFacet {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    isNullableString(value.name) &&
    typeof value.count === 'number'
  );
}

export function isImportSummary(value: unknown): value is ImportSummary {
  return (
    isRecord(value) &&
    isSunoImport(value.export) &&
    ['songs', 'versions', 'generations', 'reimports', 'ignored', 'skipped', 'invalidCount'].every(
      (name) => typeof value[name] === 'number',
    ) &&
    typeof value.valid === 'boolean' &&
    isErrorMap(value.invalid) &&
    typeof value.nothingToDo === 'boolean' &&
    typeof value.nextKey === 'string' &&
    Array.isArray(value.workspaces) &&
    value.workspaces.every(isFacet) &&
    Array.isArray(value.playlists) &&
    value.playlists.every(isFacet) &&
    isStringList(value.libraryExcluded) &&
    typeof value.revision === 'number'
  );
}

function isTargets(value: unknown): value is ImportTargets {
  return (
    isRecord(value) &&
    isRecord(value.song) &&
    typeof value.song.shortcode === 'string' &&
    Array.isArray(value.matching) &&
    value.matching.every(isTargetVersion) &&
    Array.isArray(value.versions) &&
    value.versions.every(isTargetVersion) &&
    (value.parent === null || isTargetVersion(value.parent)) &&
    Array.isArray(value.numbers) &&
    value.numbers.every((number) => isRecord(number) && typeof number.number === 'string')
  );
}

const exportsPath = 'api/v1/suno/exports';

function exportPath(id: string, suffix = ''): string {
  return `${exportsPath}/${encodeURIComponent(id)}${suffix}`;
}

/** The query of the records list for `filter` and `page`. */
export function filterParameters(filter: ImportFilter): URLSearchParams {
  const parameters = new URLSearchParams();
  if (filter.class !== undefined) {
    parameters.set('class', filter.class);
  }
  if (filter.workspace !== undefined) {
    parameters.set('workspace', filter.workspace);
  }
  if (filter.playlist !== undefined) {
    parameters.set('playlist', filter.playlist);
  }
  if (filter.q !== undefined && filter.q !== '') {
    parameters.set('q', filter.q);
  }
  return parameters;
}

const acceptCurrent = (answer: unknown) =>
  isRecord(answer) &&
  (answer.waiting === null || isSunoImport(answer.waiting)) &&
  (answer.last === null || isSunoImport(answer.last))
    ? { waiting: answer.waiting, last: answer.last }
    : undefined;

/** The export waiting for review, and the export created last (for the Suno entry and Settings). */
export function useCurrentImport() {
  return useResource(`${exportsPath}/current`, acceptCurrent);
}

const acceptImport = (answer: unknown) => (isSunoImport(answer) ? answer : undefined);

/** One export, by ID. */
export function useSunoImport(id: string) {
  return useResource(exportPath(id), acceptImport);
}

const acceptSummary = (answer: unknown) => (isImportSummary(answer) ? answer : undefined);

/** What confirming the export would do, and whether every choice is valid. */
export function useImportSummary(id: string) {
  return useResource(exportPath(id, '/summary'), acceptSummary);
}

const acceptRecords = (answer: unknown) =>
  isRecord(answer) &&
  Array.isArray(answer.items) &&
  answer.items.every(isImportRecord) &&
  typeof answer.total === 'number' &&
  typeof answer.page === 'number' &&
  typeof answer.pageSize === 'number'
    ? (answer as unknown as ImportRecordPage)
    : undefined;

/** A page of the export's records matching `filter`. */
export function useImportRecords(id: string, filter: ImportFilter, page: number) {
  const parameters = filterParameters(filter);
  parameters.set('pageSize', String(IMPORT_PAGE_SIZE));
  if (page !== 1) {
    parameters.set('page', String(page));
  }
  return useResource(`${exportPath(id, '/records')}?${parameters.toString()}`, acceptRecords);
}

/** Where the record `sunoId` may go in the Song `song` (an ID or shortcode), a new Version under `parent` (none: top-level). */
export async function readImportTargets(
  id: string,
  sunoId: string,
  song: string,
  parent: string | null,
  signal?: AbortSignal,
): Promise<ImportTargets | undefined> {
  const parameters = new URLSearchParams({ song });
  if (parent !== null) {
    parameters.set('parent', parent);
  }
  try {
    const response = await apiFetch(
      `${exportPath(id, `/records/${encodeURIComponent(sunoId)}/targets`)}?${parameters.toString()}`,
      { signal },
    );
    const answer = await body(response);
    return response.ok && isTargets(answer) ? answer : undefined;
  } catch {
    return undefined;
  }
}

function isDiffValue(value: unknown): value is string | number | null {
  return value === null || typeof value === 'string' || typeof value === 'number';
}

function isRecordDiff(value: unknown): value is RecordDiff {
  return (
    isRecord(value) &&
    typeof value.sunoId === 'string' &&
    (value.class === 'changed' || value.class === 'conflict') &&
    Array.isArray(value.fields) &&
    value.fields.every(
      (field) =>
        isRecord(field) &&
        typeof field.field === 'string' &&
        isDiffValue(field.current) &&
        isDiffValue(field.incoming),
    ) &&
    Array.isArray(value.inputs) &&
    value.inputs.every(
      (input) =>
        isRecord(input) &&
        typeof input.field === 'string' &&
        typeof input.current === 'string' &&
        typeof input.incoming === 'string',
    )
  );
}

/** Reads how a Changed or Conflict record differs (#141); undefined when it cannot be read. */
export async function readRecordDiff(
  id: string,
  sunoId: string,
  signal?: AbortSignal,
): Promise<RecordDiff | undefined> {
  try {
    const response = await apiFetch(exportPath(id, `/records/${encodeURIComponent(sunoId)}/diff`), {
      signal,
    });
    const answer = await body(response);
    return response.ok && isRecordDiff(answer) ? answer : undefined;
  } catch {
    return undefined;
  }
}

/** Which records a change of choices names: these Suno IDs, or every record the review can change matching a filter but those left out. */
export type RecordSelection =
  { kind: 'ids'; sunoIds: string[] } | { kind: 'filter'; filter: ImportFilter; except: string[] };

/**
 * How a change of choices ended: `changed` (the export's new revision); `refused` with the reasons by
 * Suno ID (nothing was changed); `conflict` when the export changed elsewhere; `not-ready` when it is no
 * longer open for review; `none` when the filter matched no record the review can change; `failed`.
 */
export type ChangeChoicesResult =
  | { kind: 'changed'; revision: number }
  | { kind: 'refused'; records: Record<string, string[]> }
  | { kind: 'conflict' }
  | { kind: 'not-ready' }
  | { kind: 'none' }
  | { kind: 'failed' };

/** Changes the choice of the selected records at `revision`; the server checks every one, and refuses the whole change if any is invalid. */
export async function changeImportChoices(
  id: string,
  revision: number,
  selection: RecordSelection,
  choice: ImportChoice,
): Promise<ChangeChoicesResult> {
  const request =
    selection.kind === 'ids'
      ? { sunoIds: selection.sunoIds, choice }
      : {
          filter: {
            class: selection.filter.class ?? null,
            workspace: selection.filter.workspace ?? null,
            playlist: selection.filter.playlist ?? null,
            q: selection.filter.q === '' ? null : (selection.filter.q ?? null),
          },
          except: selection.except,
          choice,
        };
  try {
    const response = await apiFetch(exportPath(id, '/records'), {
      method: 'PATCH',
      headers: {
        'Content-Type': 'application/json',
        'If-Match': ifMatch(revision),
      },
      body: JSON.stringify(request),
    });
    const answer = await body(response);
    if (response.ok && isSunoImport(answer)) {
      return { kind: 'changed', revision: answer.revision };
    }
    const code = isRecord(answer) ? answer.code : undefined;
    if (response.status === 422 && code === 'invalid_choices' && isRecord(answer)) {
      return { kind: 'refused', records: isErrorMap(answer.records) ? answer.records : {} };
    }
    if (response.status === 422 && code === 'validation_failed' && isRecord(answer)) {
      const errors = isErrorMap(answer.errors) ? answer.errors : {};
      return 'filter' in errors ? { kind: 'none' } : { kind: 'failed' };
    }
    if (response.status === 409) {
      return code === 'revision_conflict' ? { kind: 'conflict' } : { kind: 'not-ready' };
    }
    return { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/** Throws the export away: `discarded`, `too-late` once it is being committed, or `failed`. */
export async function discardImport(id: string): Promise<'discarded' | 'too-late' | 'failed'> {
  try {
    const response = await apiFetch(exportPath(id, '/discard'), { method: 'POST' });
    if (response.ok) {
      return 'discarded';
    }
    return response.status === 409 ? 'too-late' : 'failed';
  } catch {
    return 'failed';
  }
}

/** What happened to one record when the import was confirmed (#140). */
export interface CommittedRecord {
  sunoId: string;
  /** `updated`, `declined`, `kept`, and `moved` settle a Changed or Conflict record (#141). */
  outcome:
    | 'created'
    | 'linked'
    | 'skipped'
    | 'ignored'
    | 'failed'
    | 'updated'
    | 'declined'
    | 'kept'
    | 'moved';
  /** Why it was not imported as chosen (`inputs_differ`…), or `number_taken` for a Version renumbered. */
  reason?: string;
  generation?: { id: string; shortcode: string; songId: string };
  /** The deleted Generation came back from Recently deleted. */
  restored?: boolean;
  /** `artwork_missing` when its cover image had gone. */
  note?: string;
}

/** A Song the import created or added to, to link to. */
export interface CommittedSong {
  id: string;
  shortcode: string;
  title: string;
  created: boolean;
}

/** The commit job's result: every record's outcome, what was created, and the Songs touched. */
export interface CommitResult {
  records: CommittedRecord[];
  created: { songs: number; versions: number; generations: number };
  songs: CommittedSong[];
}

/** The commit job as `GET /api/v1/jobs/{id}` answers it, the fields the page reads. */
export interface CommitJob {
  id: string;
  status: 'queued' | 'running' | 'succeeded' | 'failed';
  progress: number;
  message: string | null;
  error: string | null;
  result: CommitResult | null;
}

/** How often the page reads the commit job while it runs. */
export const COMMIT_POLL_MS = 1000;

function isCommitResult(value: unknown): value is CommitResult {
  return (
    isRecord(value) &&
    Array.isArray(value.records) &&
    value.records.every(
      (record) =>
        isRecord(record) && typeof record.sunoId === 'string' && typeof record.outcome === 'string',
    ) &&
    isRecord(value.created) &&
    Array.isArray(value.songs)
  );
}

export function isCommitJob(value: unknown): value is CommitJob {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    (value.status === 'queued' ||
      value.status === 'running' ||
      value.status === 'succeeded' ||
      value.status === 'failed') &&
    typeof value.progress === 'number' &&
    isNullableString(value.message) &&
    (value.result === null || value.result === undefined || isCommitResult(value.result))
  );
}

/**
 * How asking to confirm ended: `started` with the commit job; `conflict` when the choices changed since
 * they were read; `in-progress` while this or another import is being confirmed; `committed` when it
 * was confirmed already; `not-ready` when it is no longer open for review; `failed`.
 */
export type CommitImportResult =
  | { kind: 'started'; jobId: string }
  | { kind: 'conflict' }
  | { kind: 'in-progress' }
  | { kind: 'committed' }
  | { kind: 'not-ready' }
  | { kind: 'failed' };

/** Confirms the import at `revision`: the server applies the choices saved on it in a background job. */
export async function commitImport(id: string, revision: number): Promise<CommitImportResult> {
  try {
    const response = await apiFetch(exportPath(id, '/commit'), {
      method: 'POST',
      headers: { 'If-Match': ifMatch(revision) },
    });
    const answer = await body(response);
    if (response.status === 202 && isSunoImport(answer) && typeof answer.jobId === 'string') {
      return { kind: 'started', jobId: answer.jobId };
    }
    const code = isRecord(answer) ? answer.code : undefined;
    if (response.status === 409) {
      switch (code) {
        case 'revision_conflict':
          return { kind: 'conflict' };
        case 'import_in_progress':
          return { kind: 'in-progress' };
        case 'export_committed':
          return { kind: 'committed' };
        default:
          return { kind: 'not-ready' };
      }
    }
    return { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/**
 * Follows the commit job `jobId` (null for none): reads it every {@link COMMIT_POLL_MS} until it has
 * succeeded or failed, then calls `onFinished` once. A read that fails is tried again at the next tick.
 * `gone` is true when the job no longer exists (finished jobs are pruned after 30 days).
 */
export function useCommitJob(
  jobId: string | null,
  onFinished: (job: CommitJob) => void,
): { job: CommitJob | null; gone: boolean } {
  // Kept with the job it belongs to, so a new jobId never shows the previous job.
  const [seen, setSeen] = useState<{ jobId: string; job: CommitJob | null; gone: boolean } | null>(
    null,
  );
  const finished = useRef(onFinished);
  useEffect(() => {
    finished.current = onFinished;
  }, [onFinished]);

  useEffect(() => {
    if (jobId === null) {
      return;
    }

    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      try {
        const response = await apiFetch(`api/v1/jobs/${encodeURIComponent(jobId)}`, {
          signal: controller.signal,
        });
        const answer = await body(response);
        if (controller.signal.aborted) {
          return;
        }
        if (response.status === 404) {
          setSeen({ jobId, job: null, gone: true });
          return;
        }
        if (response.ok && isCommitJob(answer)) {
          setSeen({ jobId, job: answer, gone: false });
          if (answer.status === 'succeeded' || answer.status === 'failed') {
            finished.current(answer);
            return;
          }
        }
      } catch {
        if (controller.signal.aborted) {
          return;
        }
      }
      timer = setTimeout(() => {
        void poll();
      }, COMMIT_POLL_MS);
    };
    void poll();

    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [jobId]);

  return seen !== null && seen.jobId === jobId
    ? { job: seen.job, gone: seen.gone }
    : { job: null, gone: false };
}
