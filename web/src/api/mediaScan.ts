import { writeWithRevision, type SaveResult } from './saves';
import { isRecord, useResource } from './songs';

const MEDIA_SCAN_PATH = 'api/v1/settings/media-scan';

/** The fewest and most minutes between scheduled scans, as the API takes them. */
export const MIN_SCAN_INTERVAL = 1;
export const MAX_SCAN_INTERVAL = 1440;

/**
 * The media scan schedule: whether scheduled scans run, and the minutes from the end of one scan to
 * the next (kept while they are off). Revision 0 until it is first saved.
 */
export interface MediaScanSchedule {
  enabled: boolean;
  intervalMinutes: number;
  revision: number;
}

export function isMediaScanSchedule(value: unknown): value is MediaScanSchedule {
  return (
    isRecord(value) &&
    typeof value.enabled === 'boolean' &&
    typeof value.intervalMinutes === 'number' &&
    typeof value.revision === 'number'
  );
}

const acceptSchedule = (answer: unknown) => (isMediaScanSchedule(answer) ? answer : undefined);

/** The media scan schedule, for Settings → Library. */
export function useMediaScanSchedule() {
  return useResource(MEDIA_SCAN_PATH, acceptSchedule);
}

/**
 * Saves the schedule based on `revision` (0 before the first save); a stale revision comes back as a
 * conflict with the schedule as it is now.
 */
export function saveMediaScanSchedule(
  revision: number,
  schedule: Pick<MediaScanSchedule, 'enabled' | 'intervalMinutes'>,
): Promise<SaveResult<MediaScanSchedule>> {
  return writeWithRevision('PUT', MEDIA_SCAN_PATH, revision, { ...schedule }, acceptSchedule);
}

/** The interval as words: "every minute", "every 15 minutes". */
export function describeInterval(minutes: number): string {
  return minutes === 1 ? 'every minute' : `every ${minutes.toLocaleString('en-US')} minutes`;
}
