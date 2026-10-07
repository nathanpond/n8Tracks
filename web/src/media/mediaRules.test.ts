import { describe, expect, it } from 'vitest';
import type { ScanJob } from '../api/media';
import {
  durationText,
  isIndeterminate,
  LISTING_MESSAGE,
  majorityMissingText,
  progressText,
} from './mediaRules';

function job(change: Partial<ScanJob>): ScanJob {
  return { id: 'j', status: 'running', progress: 0, message: null, error: null, ...change };
}

describe('the Media page rules', () => {
  it.each([
    [0.4, 'under a second'],
    [1, '1 second'],
    [42.4, '42 seconds'],
    [60, '1 min'],
    [185, '3 min 5 s'],
  ])('words a duration of %s seconds as “%s”', (seconds, text) => {
    expect(durationText(seconds)).toBe(text);
  });

  it('makes the bar indeterminate while queued or listing, and determinate once it counts', () => {
    expect(isIndeterminate(null)).toBe(true);
    expect(isIndeterminate(job({ status: 'queued' }))).toBe(true);
    expect(isIndeterminate(job({ message: LISTING_MESSAGE }))).toBe(true);
    expect(isIndeterminate(job({ progress: 0, message: '0 of 10 files: …' }))).toBe(false);
    expect(isIndeterminate(job({ progress: 30, message: '3 of 10 files: …' }))).toBe(false);
  });

  it('shows the job’s own message, or what it waits for', () => {
    expect(progressText(job({ status: 'queued' }))).toBe('Waiting to start…');
    expect(progressText(job({ message: '3 of 10 files: …' }))).toBe('3 of 10 files: …');
    expect(progressText(job({ message: null }))).toBe('Starting…');
  });

  it('names the counts in the warning when it has them', () => {
    expect(
      majorityMissingText(
        {
          seen: 1,
          new: 0,
          changed: 0,
          unchanged: 1,
          missing: 1200,
          restored: 0,
          associated: 0,
          unmatched: 0,
          skipped: 0,
          unreadable: 0,
          unreadableDirectories: 0,
          availableBefore: 2000,
        },
        '/media',
      ),
    ).toMatch(/^1,200 of the 2,000 files that were available before the last scan/);
    expect(majorityMissingText(null, '/music')).toMatch(
      /^More than half of the files .* mounted at \/music\./,
    );
  });
});
