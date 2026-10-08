import type { SunoProblem } from '../api/dashboard';

/** "1 Song", "2 Songs". */
function songCountText(count: number): string {
  return `${String(count)} ${count === 1 ? 'Song' : 'Songs'}`;
}

/** What a problem is, in a sentence. */
export function problemText(problem: SunoProblem): string {
  switch (problem.kind) {
    case 'failedSync':
      return problem.step === 'classifying'
        ? 'Your last Suno sync could not be prepared for review.'
        : `Your last Suno sync failed at the step “${problem.step ?? 'unknown'}”.`;
    case 'failedGenerate': {
      const what = `Generate on Suno for ${problem.versionShortcode ?? 'a Version'}`;
      const how =
        problem.reason === 'expired'
          ? 'expired'
          : `stopped at “${problem.step ?? 'its first step'}”`;
      return problem.message === null ? `${what} ${how}.` : `${what} ${how}: ${problem.message}`;
    }
    case 'unavailableWorkspace': {
      const songs = problem.songCount ?? 0;
      return `The Suno workspace ${problem.workspaceName ?? '(unnamed)'} is unavailable, and ${songCountText(songs)} ${songs === 1 ? 'is' : 'are'} in it.`;
    }
  }
}
