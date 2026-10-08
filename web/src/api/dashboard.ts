import { useCallback, useEffect, useRef, useState } from 'react';
import { apiFetch } from './client';
import { body, isRecord, songListParameters, type SongQuery } from './songs';

export const DASHBOARD_PATH = 'api/v1/dashboard';

/** A focus refresh comes at least this long after the last read began. */
export const DASHBOARD_REFRESH_MS = 30_000;

/** How many Songs Recently edited and Without a Selected Generation list at most. */
export const DASHBOARD_LIST_LIMIT = 10;

/** A Song as a dashboard section lists it. */
export interface DashboardSong {
  id: string;
  shortcode: string;
  title: string;
  state: { id: string; name: string; colour: string };
  updatedAt: string;
}

/** A workflow state shown in By workflow state, with how many Songs are in it. */
export interface DashboardStateCount {
  id: string;
  name: string;
  colour: string;
  hidden: boolean;
  songCount: number;
}

export interface RecentlyEdited {
  songs: DashboardSong[];
  total: number;
}

export interface WorkflowStateCounts {
  states: DashboardStateCount[];
}

export interface WithoutSelection {
  count: number;
  songs: DashboardSong[];
}

/** Unmatched Files (#229): how many audio files are associated with nothing, and whether the media folder is unavailable. */
export interface UnmatchedFiles {
  count: number;
  mediaUnavailable: boolean;
}

/** A Suno export waiting for review (#229). */
export interface SunoReview {
  exportId: string;
  arrivedAt: string;
  recordCount: number;
  changedCount: number;
  conflictCount: number;
}

/** Suno reviews (#229): how many exports wait, and up to five of them (left out for a bearer token). */
export interface SunoReviews {
  count: number;
  exports?: SunoReview[];
}

export type SunoProblemKind = 'failedSync' | 'failedGenerate' | 'unavailableWorkspace';

/** A Suno problem (#229): what it is, and what its link needs. */
export interface SunoProblem {
  kind: SunoProblemKind;
  subject: string;
  occurredAt: string | null;
  reason: string | null;
  step: string | null;
  message: string | null;
  workspaceName: string | null;
  songCount: number | null;
  versionShortcode: string | null;
  songShortcode: string | null;
  versionNumber: string | null;
  dismissible: boolean;
}

/** Suno problems (#229): how many, and up to five of them (left out for a bearer token). */
export interface SunoProblems {
  count: number;
  problems?: SunoProblem[];
}

/** One section as the API answers it: its data, or the error that took its place. */
export type DashboardSection<T> = { data: T } | { error: { code: string } };

/** What needs attention (#229), as `GET /api/v1/attention` and the dashboard answer it. */
export interface Attention {
  unmatchedFiles: DashboardSection<UnmatchedFiles>;
  sunoReviews: DashboardSection<SunoReviews>;
  sunoProblems: DashboardSection<SunoProblems>;
}

/** `GET /api/v1/dashboard`: each catalog section (#228) and what needs attention (#229), read on its own. */
export interface Dashboard extends Attention {
  recentlyEdited: DashboardSection<RecentlyEdited>;
  workflowStates: DashboardSection<WorkflowStateCounts>;
  withoutSelection: DashboardSection<WithoutSelection>;
}

export type DashboardSectionKey = keyof Dashboard;

function isDashboardSong(value: unknown): value is DashboardSong {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.shortcode === 'string' &&
    typeof value.title === 'string' &&
    typeof value.updatedAt === 'string' &&
    isRecord(value.state) &&
    typeof value.state.id === 'string' &&
    typeof value.state.name === 'string' &&
    typeof value.state.colour === 'string'
  );
}

function isStateCount(value: unknown): value is DashboardStateCount {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.name === 'string' &&
    typeof value.colour === 'string' &&
    typeof value.hidden === 'boolean' &&
    typeof value.songCount === 'number'
  );
}

function isSection(value: unknown, isData: (data: unknown) => boolean): boolean {
  if (!isRecord(value)) {
    return false;
  }
  if ('data' in value) {
    return isData(value.data);
  }
  return isRecord(value.error) && typeof value.error.code === 'string';
}

function isRecentlyEdited(value: unknown): value is RecentlyEdited {
  return (
    isRecord(value) &&
    typeof value.total === 'number' &&
    Array.isArray(value.songs) &&
    value.songs.every(isDashboardSong)
  );
}

function isWorkflowStateCounts(value: unknown): value is WorkflowStateCounts {
  return isRecord(value) && Array.isArray(value.states) && value.states.every(isStateCount);
}

function isWithoutSelection(value: unknown): value is WithoutSelection {
  return (
    isRecord(value) &&
    typeof value.count === 'number' &&
    Array.isArray(value.songs) &&
    value.songs.every(isDashboardSong)
  );
}

const textOrNull = (value: unknown) => value === null || typeof value === 'string';

function isUnmatchedFiles(value: unknown): value is UnmatchedFiles {
  return (
    isRecord(value) &&
    typeof value.count === 'number' &&
    typeof value.mediaUnavailable === 'boolean'
  );
}

function isSunoReview(value: unknown): value is SunoReview {
  return (
    isRecord(value) &&
    typeof value.exportId === 'string' &&
    typeof value.arrivedAt === 'string' &&
    typeof value.recordCount === 'number' &&
    typeof value.changedCount === 'number' &&
    typeof value.conflictCount === 'number'
  );
}

function isSunoReviews(value: unknown): value is SunoReviews {
  return (
    isRecord(value) &&
    typeof value.count === 'number' &&
    (value.exports === undefined ||
      (Array.isArray(value.exports) && value.exports.every(isSunoReview)))
  );
}

const PROBLEM_KINDS: readonly string[] = ['failedSync', 'failedGenerate', 'unavailableWorkspace'];

function isSunoProblem(value: unknown): value is SunoProblem {
  return (
    isRecord(value) &&
    typeof value.kind === 'string' &&
    PROBLEM_KINDS.includes(value.kind) &&
    typeof value.subject === 'string' &&
    textOrNull(value.occurredAt) &&
    textOrNull(value.reason) &&
    textOrNull(value.step) &&
    textOrNull(value.message) &&
    textOrNull(value.workspaceName) &&
    (value.songCount === null || typeof value.songCount === 'number') &&
    textOrNull(value.versionShortcode) &&
    textOrNull(value.songShortcode) &&
    textOrNull(value.versionNumber) &&
    typeof value.dismissible === 'boolean'
  );
}

function isSunoProblems(value: unknown): value is SunoProblems {
  return (
    isRecord(value) &&
    typeof value.count === 'number' &&
    (value.problems === undefined ||
      (Array.isArray(value.problems) && value.problems.every(isSunoProblem)))
  );
}

export function isAttention(value: unknown): value is Attention {
  return (
    isRecord(value) &&
    isSection(value.unmatchedFiles, isUnmatchedFiles) &&
    isSection(value.sunoReviews, isSunoReviews) &&
    isSection(value.sunoProblems, isSunoProblems)
  );
}

export function isDashboard(value: unknown): value is Dashboard {
  return (
    isRecord(value) &&
    isSection(value.recentlyEdited, isRecentlyEdited) &&
    isSection(value.workflowStates, isWorkflowStateCounts) &&
    isSection(value.withoutSelection, isWithoutSelection) &&
    isAttention(value)
  );
}

/** A section's data, or undefined when it failed. */
export function sectionData<T>(section: DashboardSection<T>): T | undefined {
  return 'data' in section ? section.data : undefined;
}

/**
 * Whether the catalog has no Songs at all, as far as the answer tells: every workflow state counts
 * none (archived ones included). Unknown (false) when the counts could not be read.
 */
export function isEmptyCatalog(dashboard: Dashboard): boolean {
  const counts = sectionData(dashboard.workflowStates);
  return counts?.states.every((state) => state.songCount === 0) ?? false;
}

const EVERY_SONG: SongQuery = {
  sort: 'updated',
  direction: 'desc',
  states: [],
  genres: [],
  tags: [],
  artists: [],
  page: 1,
};

function songsAddress(query: Partial<SongQuery>): string {
  const parameters = songListParameters({ ...EVERY_SONG, ...query }).toString();
  return parameters === '' ? '/songs' : `/songs?${parameters}`;
}

/** Recently edited's See all: the Songs table's active Songs, last updated first, as the section lists them. */
export function recentlyEditedAddress(): string {
  return songsAddress({ archived: 'active' });
}

/** A state's count opens the Songs table filtered to that state: the count is that list's total. */
export function stateAddress(stateId: string): string {
  return songsAddress({ states: [stateId] });
}

/** Without a Selected Generation opens the Songs table filtered the same way the section counts. */
export function withoutSelectionAddress(): string {
  return songsAddress({ archived: 'active', selected: 'no', generations: 'some' });
}

/**
 * The dashboard as last read. `loading` while a read is in progress (the first, a retry, or a
 * refresh); `data` stays while a later read runs. `failed` when the first read (or a retry) got no
 * dashboard at all; `refreshFailed` when a later read failed and `data` is from before.
 */
export interface DashboardState {
  data: Dashboard | undefined;
  loading: boolean;
  failed: boolean;
  refreshFailed: boolean;
}

/**
 * Reads the dashboard when mounted and again when the window regains focus, at most once every
 * {@link DASHBOARD_REFRESH_MS}; a refresh keeps showing what it had, and keeps it if the refresh
 * fails. `reload` (a section's Retry) reads it again at once.
 */
export function useDashboard(now: () => number = Date.now): {
  state: DashboardState;
  reload: () => void;
} {
  const [state, setState] = useState<DashboardState>({
    data: undefined,
    loading: true,
    failed: false,
    refreshFailed: false,
  });
  const [attempt, setAttempt] = useState(0);
  const startedAt = useRef<number | undefined>(undefined);

  // Every read keeps what an earlier one showed if it fails: there is no data to lose on the first.
  // The first read starts out loading; a later one is marked loading by whatever asks for it.
  useEffect(() => {
    const controller = new AbortController();
    startedAt.current = now();
    const read = async () => {
      let answer: Dashboard | undefined;
      try {
        const response = await apiFetch(DASHBOARD_PATH, { signal: controller.signal });
        const parsed = await body(response);
        answer = response.ok && isDashboard(parsed) ? parsed : undefined;
      } catch {
        answer = undefined;
      }
      if (controller.signal.aborted) {
        return;
      }
      setState((previous) =>
        answer !== undefined
          ? { data: answer, loading: false, failed: false, refreshFailed: false }
          : previous.data !== undefined
            ? { ...previous, loading: false, refreshFailed: true }
            : { data: undefined, loading: false, failed: true, refreshFailed: false },
      );
    };
    void read();
    return () => {
      controller.abort();
    };
  }, [attempt, now]);

  const reload = useCallback(() => {
    setState((previous) => ({ ...previous, loading: true }));
    setAttempt((previous) => previous + 1);
  }, []);

  useEffect(() => {
    const onFocus = () => {
      if (startedAt.current === undefined || now() - startedAt.current >= DASHBOARD_REFRESH_MS) {
        reload();
      }
    };
    window.addEventListener('focus', onFocus);
    return () => {
      window.removeEventListener('focus', onFocus);
    };
  }, [reload, now]);

  return { state, reload };
}
