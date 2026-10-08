import { useCallback, useEffect, useState } from 'react';
import { apiFetch } from './client';
import type { DashboardSectionKey } from './dashboard';
import { writeWithRevision, type SaveResult } from './saves';
import { body, isRecord } from './songs';

export const DASHBOARD_LAYOUT_PATH = 'api/v1/settings/dashboard';
export const LAST_SONG_PATH = 'api/v1/settings/last-song';

/** Every dashboard section, in the default order (#230): what the API answers as `defaultOrder`. */
export const DEFAULT_SECTION_ORDER: readonly DashboardSectionKey[] = [
  'recentlyEdited',
  'unmatchedFiles',
  'sunoReviews',
  'sunoProblems',
  'workflowStates',
  'withoutSelection',
];

/** One section's place in the arrangement. */
export interface SectionPlacement {
  key: DashboardSectionKey;
  hidden: boolean;
}

/**
 * The dashboard's arrangement (#230), as `GET /api/v1/settings/dashboard` answers it: every section in
 * order, whether it is the user's or the default, and the default order Reset goes back to.
 */
export interface DashboardLayout {
  revision: number;
  customized: boolean;
  sections: SectionPlacement[];
  defaultOrder: DashboardSectionKey[];
}

function isSectionKey(value: unknown): value is DashboardSectionKey {
  return typeof value === 'string' && (DEFAULT_SECTION_ORDER as readonly string[]).includes(value);
}

/**
 * The arrangement in `value`, or undefined when it is not one. A section key this page does not know
 * is left out (the API answers only its own, which are this page's in the same build).
 */
export function layoutOf(value: unknown): DashboardLayout | undefined {
  if (
    !isRecord(value) ||
    typeof value.revision !== 'number' ||
    typeof value.customized !== 'boolean' ||
    !Array.isArray(value.sections) ||
    !Array.isArray(value.defaultOrder)
  ) {
    return undefined;
  }
  const sections: SectionPlacement[] = [];
  for (const section of value.sections) {
    if (!isRecord(section) || typeof section.hidden !== 'boolean') {
      return undefined;
    }
    if (isSectionKey(section.key)) {
      sections.push({ key: section.key, hidden: section.hidden });
    }
  }
  return {
    revision: value.revision,
    customized: value.customized,
    sections,
    defaultOrder: value.defaultOrder.filter(isSectionKey),
  };
}

/** What a dashboard shows before its arrangement is read, or when it cannot be: every section, in the default order. */
export const DEFAULT_LAYOUT: DashboardLayout = {
  revision: 0,
  customized: false,
  sections: DEFAULT_SECTION_ORDER.map((key) => ({ key, hidden: false })),
  defaultOrder: [...DEFAULT_SECTION_ORDER],
};

/** The default arrangement of `layout`: its default order, every section shown. */
export function defaultSections(layout: DashboardLayout): SectionPlacement[] {
  return layout.defaultOrder.map((key) => ({ key, hidden: false }));
}

/**
 * The arrangement, read once when mounted. `layout` is the default until it is read, and stays the
 * default when the read fails (`failed`); `replace` takes the one a save answered.
 */
export function useDashboardLayout(): {
  layout: DashboardLayout;
  loading: boolean;
  failed: boolean;
  replace: (layout: DashboardLayout) => void;
  reload: () => void;
} {
  const [state, setState] = useState<{
    layout: DashboardLayout;
    loading: boolean;
    failed: boolean;
  }>({ layout: DEFAULT_LAYOUT, loading: true, failed: false });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    const read = async () => {
      let layout: DashboardLayout | undefined;
      try {
        const response = await apiFetch(DASHBOARD_LAYOUT_PATH, { signal: controller.signal });
        const answer = await body(response);
        layout = response.ok ? layoutOf(answer) : undefined;
      } catch {
        layout = undefined;
      }
      if (controller.signal.aborted) {
        return;
      }
      setState((previous) =>
        layout === undefined
          ? { ...previous, loading: false, failed: true }
          : { layout, loading: false, failed: false },
      );
    };
    void read();
    return () => {
      controller.abort();
    };
  }, [attempt]);

  const replace = useCallback((layout: DashboardLayout) => {
    setState({ layout, loading: false, failed: false });
  }, []);
  const reload = useCallback(() => {
    setAttempt((previous) => previous + 1);
  }, []);

  return { ...state, replace, reload };
}

const acceptLayout = (answer: unknown) => layoutOf(answer);

/** Saves `sections` as the arrangement, based on `revision` (0 before the first save). */
export function saveDashboardLayout(
  revision: number,
  sections: SectionPlacement[],
): Promise<SaveResult<DashboardLayout>> {
  return writeWithRevision(
    'PUT',
    DASHBOARD_LAYOUT_PATH,
    revision,
    { sections: sections.map(({ key, hidden }) => ({ key, hidden })) },
    acceptLayout,
  );
}

/** Clears the saved arrangement, based on `revision`, so the default order applies. */
export function resetDashboardLayout(revision: number): Promise<SaveResult<DashboardLayout>> {
  return writeWithRevision('DELETE', DASHBOARD_LAYOUT_PATH, revision, undefined, acceptLayout);
}

/** The Song opened last, as Open last Song shows it: none yet, deleted since, or the Song. */
export type LastSong =
  | { kind: 'none' }
  | { kind: 'deleted' }
  | { kind: 'song'; id: string; shortcode: string; title: string };

export function lastSongOf(value: unknown): LastSong | undefined {
  if (!isRecord(value) || typeof value.deleted !== 'boolean') {
    return undefined;
  }
  const song = value.song;
  if (song === null) {
    return value.deleted ? { kind: 'deleted' } : { kind: 'none' };
  }
  return isRecord(song) &&
    typeof song.id === 'string' &&
    typeof song.shortcode === 'string' &&
    typeof song.title === 'string'
    ? { kind: 'song', id: song.id, shortcode: song.shortcode, title: song.title }
    : undefined;
}

/** The Song opened last, read once when mounted; undefined while it is read, or when it could not be. */
export function useLastSong(): LastSong | undefined | 'failed' {
  const [state, setState] = useState<LastSong | undefined | 'failed'>(undefined);
  useEffect(() => {
    const controller = new AbortController();
    const read = async () => {
      let answer: LastSong | undefined;
      try {
        const response = await apiFetch(LAST_SONG_PATH, { signal: controller.signal });
        answer = response.ok ? lastSongOf(await body(response)) : undefined;
      } catch {
        answer = undefined;
      }
      if (!controller.signal.aborted) {
        setState(answer ?? 'failed');
      }
    };
    void read();
    return () => {
      controller.abort();
    };
  }, []);
  return state;
}

/**
 * Records the Song `songId` as the one opened last (the Song page, each time it opens a Song). Last
 * write wins; a failure is ignored: Open last Song is a convenience.
 */
export async function rememberLastSong(songId: string): Promise<void> {
  try {
    await apiFetch(LAST_SONG_PATH, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ song: songId }),
    });
  } catch {
    // Not recorded: the dashboard offers the Song opened before, which still opens.
  }
}
