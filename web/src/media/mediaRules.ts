import type { ScanCounts, ScanJob, ScanReport, ScanTrigger } from '../api/media';

/** What the scan's job says while the first, names-only pass runs (no total yet). */
export const LISTING_MESSAGE = 'Listing the media folder';

/** The counts a scan shows, in order, with their labels. */
export const SCAN_COUNT_LABELS: readonly (readonly [keyof ScanCounts, string])[] = [
  ['seen', 'Seen'],
  ['new', 'New'],
  ['changed', 'Changed'],
  ['unchanged', 'Unchanged'],
  ['missing', 'Newly Missing'],
  ['restored', 'Restored'],
  ['associated', 'Associated'],
  ['unmatched', 'Unmatched'],
  ['skipped', 'Skipped'],
  ['unreadable', 'Unreadable'],
];

/** Why the scan `report` describes failed, as a sentence; null when it succeeded. */
export function failureText(report: ScanReport): string | null {
  switch (report.failure) {
    case null:
      return null;
    case 'media_folder_unavailable':
      return 'The scan failed because the media folder could not be read.';
    case 'interrupted':
      return 'The scan was interrupted by a restart.';
    default:
      return `The scan failed (job ${report.jobId}).`;
  }
}

const TRIGGERS: Record<ScanTrigger, string> = {
  manual: 'Started with Scan Library',
  startup: 'Started when n8Tracks started',
  scheduled: 'Scheduled',
  recovery: 'Started when the media folder came back',
};

export function triggerText(trigger: ScanTrigger | null): string | null {
  return trigger === null ? null : TRIGGERS[trigger];
}

/** A scan's duration in words: "under a second", "1 second", "42 seconds", "3 min 5 s". */
export function durationText(seconds: number): string {
  if (seconds < 1) {
    return 'under a second';
  }
  const whole = Math.round(seconds);
  if (whole < 60) {
    return whole === 1 ? '1 second' : `${String(whole)} seconds`;
  }
  const minutes = Math.floor(whole / 60);
  const rest = whole % 60;
  return rest === 0 ? `${String(minutes)} min` : `${String(minutes)} min ${String(rest)} s`;
}

/** A count as the page writes it: 1,240. */
export function countText(count: number): string {
  return count.toLocaleString('en-US');
}

/**
 * Whether the scan's progress has no total yet: queued, or in the first pass, which only lists
 * names. The bar is then indeterminate.
 */
export function isIndeterminate(job: ScanJob | null): boolean {
  return (
    job === null ||
    job.status === 'queued' ||
    (job.progress === 0 && (job.message === null || job.message.startsWith(LISTING_MESSAGE)))
  );
}

/** The text under the bar: the job's own message ("1,240 of 3,000 files: …"), or what it waits for. */
export function progressText(job: ScanJob | null): string {
  if (job === null || job.status === 'queued') {
    return 'Waiting to start…';
  }
  return job.message ?? 'Starting…';
}

/** What the live region says when a scan this page followed has ended. */
export function finishedText(job: ScanJob | null): string {
  if (job === null) {
    return 'The scan has finished.';
  }
  return job.status === 'succeeded' ? 'The scan has finished.' : 'The scan has stopped.';
}

/** The majority-missing warning's text: how many of what was there. */
export function majorityMissingText(counts: ScanCounts | null, path: string): string {
  const what =
    counts === null
      ? 'More than half of the files that were available'
      : `${countText(counts.missing)} of the ${countText(counts.availableBefore)} files that were available`;
  return `${what} before the last scan could not be found. The wrong folder may be mounted at ${path}. Check the mount, then scan the library again.`;
}
