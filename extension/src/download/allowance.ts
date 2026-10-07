/**
 * The plan's download allowance (TS-004): Suno's `GET /api/billing/info/`, which the page requests
 * when a clip's Download dialog opens, holds `download_usage` with the downloads used and allowed
 * this period and any bought on top. Each WAV, MP3, or M4A download of a clip not yet unlocked
 * spends one (one unlock covers all three formats of that clip); the playback stream spends none.
 */

/** The counts as the page read them. */
export interface DownloadUsage {
  used: number;
  limit: number;
  additional: number;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isCount(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0;
}

/** The allowance in a billing answer (as the observer forwards it), or null when it has none. */
export function downloadUsageOf(body: unknown): DownloadUsage | null {
  const usage = isRecord(body) ? body.download_usage : undefined;
  if (
    !isRecord(usage) ||
    !isCount(usage.current_period_downloads_used) ||
    !isCount(usage.current_period_downloads_limit)
  ) {
    return null;
  }
  return {
    used: usage.current_period_downloads_used,
    limit: usage.current_period_downloads_limit,
    additional: isCount(usage.additional_download_remaining)
      ? usage.additional_download_remaining
      : 0,
  };
}

/** How many unlocks remain this period: the plan's own left, plus any bought on top. */
export function remainingUnlocks(usage: DownloadUsage): number {
  return Math.max(0, usage.limit - usage.used) + usage.additional;
}
