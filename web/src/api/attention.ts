import { UNMATCHED_PATH } from './audioFiles';
import { apiFetch } from './client';
import { isAttention, type SunoProblem } from './dashboard';
import { useResource } from './songs';
import { IMPORTS_PATH } from './sunoImports';

export const ATTENTION_PATH = 'api/v1/attention';
export const DISMISSALS_PATH = 'api/v1/attention/dismissals';

/** How many entries a section lists at most; the rest are "n more". */
export const ATTENTION_LIST_LIMIT = 5;

/** The Suno workspaces page, where the workspaces past the five listed are. */
export const WORKSPACES_PATH = '/settings/suno-workspaces';

/** Where Unmatched Files' count leads: the page whose total it is. */
export function unmatchedAddress(): string {
  return UNMATCHED_PATH;
}

/** Where the Suno reviews section's "n more" leads. */
export function reviewsAddress(): string {
  return IMPORTS_PATH;
}

/**
 * Where a problem is resolved: a failed sync on the Suno import page (its notice), a failed Generate
 * on Suno request at its Version, an Unavailable workspace on its settings page.
 */
export function problemAddress(problem: SunoProblem): string {
  switch (problem.kind) {
    case 'failedSync':
      return IMPORTS_PATH;
    case 'failedGenerate':
      return problem.songShortcode !== null && problem.versionNumber !== null
        ? `/songs/${problem.songShortcode}/v/${problem.versionNumber}`
        : '/songs';
    case 'unavailableWorkspace':
      return `${WORKSPACES_PATH}/${encodeURIComponent(problem.subject)}`;
  }
}

/** Where Suno problems' "n more" leads: past the failures (two at most), the rest are workspaces. */
export function problemsAddress(): string {
  return WORKSPACES_PATH;
}

const acceptAttention = (answer: unknown) => (isAttention(answer) ? answer : undefined);

/** What needs attention (#229), for the Suno import page's notice. */
export function useAttention() {
  return useResource(ATTENTION_PATH, acceptAttention);
}

/** Dismisses a failed sync or a failed Generate on Suno request; true once it is dismissed. */
export async function dismissProblem(problem: SunoProblem): Promise<boolean> {
  try {
    const response = await apiFetch(DISMISSALS_PATH, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ kind: problem.kind, subject: problem.subject }),
    });
    return response.status === 204;
  } catch {
    return false;
  }
}
