/**
 * Suno's clips as the completion watch reads them (#154): which are finished, and which of a feed
 * answer's clips are watched. Suno's statuses are `submitted`, `streaming`, `complete`, and `error`
 * (TS-001); the last two are final.
 */

/** Suno's statuses of a clip that has finished: complete, or ended in error. */
export const FINAL_STATUSES: ReadonlySet<string> = new Set(['complete', 'error']);

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Whether `clip` is one Suno has finished: complete, or ended in error. */
export function isFinished(clip: unknown): clip is Record<string, unknown> & { id: string } {
  return (
    isRecord(clip) &&
    typeof clip.id === 'string' &&
    typeof clip.status === 'string' &&
    FINAL_STATUSES.has(clip.status)
  );
}

/** The clips of a feed answer (`{ clips: [...] }`) whose Suno IDs are in `watched`, as Suno returned them. */
export function watchedClipsOf(
  body: unknown,
  watched: ReadonlySet<string>,
): Record<string, unknown>[] {
  const clips = isRecord(body) && Array.isArray(body.clips) ? body.clips : [];
  return clips.filter(
    (clip): clip is Record<string, unknown> =>
      isRecord(clip) && typeof clip.id === 'string' && watched.has(clip.id),
  );
}
