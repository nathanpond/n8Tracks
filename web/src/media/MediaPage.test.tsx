import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { MediaStatus, ScanCounts, ScanJob, ScanReport } from '../api/media';
import { fakeTimeouts } from '../test/fakeClock';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';

const JOB = '0192f1a4-0000-7000-8000-0000000000aa';
const PREVIOUS = '0192f1a4-0000-7000-8000-000000000001';

function testCounts(change: Partial<ScanCounts> = {}): ScanCounts {
  return {
    seen: 3,
    new: 3,
    changed: 0,
    unchanged: 0,
    missing: 0,
    restored: 0,
    associated: 1,
    unmatched: 2,
    skipped: 1,
    unreadable: 0,
    unreadableDirectories: 0,
    availableBefore: 0,
    ...change,
  };
}

function testScan(change: Partial<ScanReport> = {}): ScanReport {
  return {
    jobId: PREVIOUS,
    trigger: 'manual',
    outcome: 'succeeded',
    startedAt: '2026-10-08T10:00:00Z',
    finishedAt: '2026-10-08T10:00:04Z',
    durationSeconds: 4.2,
    counts: testCounts(),
    failure: null,
    ...change,
  };
}

/** A media status with nothing scanned yet. */
function neverScanned(change: Partial<MediaStatus> = {}): MediaStatus {
  return {
    mount: { state: 'available', since: null, path: '/media' },
    counts: { total: 0, available: 0, missing: 0, associated: 0, unmatched: 0 },
    lastScan: null,
    lastSuccessfulScan: null,
    activeScanJobId: null,
    schedule: { enabled: true, intervalMinutes: 15 },
    nextScheduledScan: '2099-01-01T00:00:00Z',
    majorityMissingWarning: false,
    ...change,
  };
}

function scanned(change: Partial<MediaStatus> = {}): MediaStatus {
  const scan = testScan();
  return neverScanned({
    mount: { state: 'available', since: '2026-10-08T09:00:00Z', path: '/media' },
    counts: { total: 3, available: 3, missing: 0, associated: 1, unmatched: 2 },
    lastScan: scan,
    lastSuccessfulScan: scan,
    ...change,
  });
}

function testJob(change: Partial<ScanJob> = {}): ScanJob {
  return { id: JOB, status: 'queued', progress: 0, message: null, error: null, ...change };
}

/**
 * A fake n8Tracks with the media status and scan job: `server.status` and `server.job` are what the
 * next reads answer; `server.starts` counts Scan Library presses.
 */
function mediaServer(status: MediaStatus) {
  const server = {
    status,
    job: testJob(),
    starts: 0,
    statusReads: 0,
    jobReads: 0,
    failStart: false,
  };
  stubFetch().mockImplementation((input, init) => {
    const path = requestPath(input);
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (path.endsWith('/api/v1/media/status')) {
      server.statusReads++;
      return Promise.resolve(jsonResponse(200, server.status));
    }
    if (path.endsWith('/api/v1/media/scans') && init?.method === 'POST') {
      server.starts++;
      if (server.failStart) {
        return Promise.resolve(jsonResponse(500, { code: 'internal' }));
      }
      server.status = { ...server.status, activeScanJobId: JOB };
      return Promise.resolve(jsonResponse(202, { jobId: JOB, alreadyInProgress: false }));
    }
    if (path.endsWith(`/api/v1/jobs/${JOB}`)) {
      server.jobReads++;
      return Promise.resolve(jsonResponse(200, server.job));
    }
    return Promise.resolve(jsonResponse(404, { code: 'not_found' }));
  });
  return server;
}

async function openMedia() {
  renderApp('/library/media');
  await screen.findByRole('heading', { level: 2, name: 'Media' });
  return screen.findByRole('button', { name: 'Scan Library' });
}

const fileCount = (name: string) => screen.getByTestId(`files-${name}`);

describe('Library → Media', () => {
  it('says the library has not been scanned yet and shows the folder and the next scheduled scan', async () => {
    mediaServer(neverScanned());
    const button = await openMedia();

    expect(button).toBeEnabled();
    expect(screen.getByTestId('media-folder-state')).toHaveTextContent('Available');
    expect(screen.queryByTestId('media-folder-since')).not.toBeInTheDocument();
    expect(screen.getByTestId('media-folder-path')).toHaveTextContent('/media');
    expect(screen.getByTestId('not-scanned')).toHaveTextContent('Not scanned yet.');
    expect(fileCount('available')).toHaveTextContent('0');
    expect(fileCount('unmatched')).toHaveTextContent('0');
    const next = screen.getByTestId('next-scheduled-scan');
    expect(next).toHaveAttribute('data-scheduled', 'on');
    expect(next).toHaveTextContent(/The next scheduled scan is at /);
    expect(within(next).getByRole('link', { name: 'Settings → Library' })).toHaveAttribute(
      'href',
      '/settings/library',
    );
    expect(screen.queryByTestId('scan-progress')).not.toBeInTheDocument();
    expect(screen.queryByTestId('majority-missing-warning')).not.toBeInTheDocument();
  });

  it('shows the counts by status and association and what the last scan counted', async () => {
    mediaServer(scanned());
    await openMedia();

    expect(screen.getByTestId('media-folder-since')).toHaveTextContent(/^since /);
    expect(fileCount('available')).toHaveTextContent('3');
    expect(fileCount('missing')).toHaveTextContent('0');
    expect(fileCount('associated')).toHaveTextContent('1');
    expect(fileCount('unmatched')).toHaveTextContent('2');
    expect(screen.getByTestId('scan-finished')).toHaveTextContent(
      /^Finished .+, after 4 seconds\. Started with Scan Library\.$/,
    );
    const counts = screen.getByRole('table', { name: 'What the last scan counted' });
    expect(
      within(counts)
        .getAllByRole('rowheader')
        .map((cell) => cell.textContent),
    ).toEqual([
      'Seen',
      'New',
      'Changed',
      'Unchanged',
      'Newly Missing',
      'Restored',
      'Associated',
      'Unmatched',
      'Skipped',
      'Unreadable',
    ]);
    expect(screen.getByTestId('scan-count-seen')).toHaveTextContent('3');
    expect(screen.getByTestId('scan-count-skipped')).toHaveTextContent('1');
    expect(screen.queryByTestId('scan-failed')).not.toBeInTheDocument();
  });

  it('starts a scan, keeps the button disabled while it is queued or running, and shows the bar and message', async () => {
    const clock = fakeTimeouts();
    const server = mediaServer(scanned());
    const user = userEvent.setup({ advanceTimers: clock.advanceTimers });
    const button = await openMedia();

    await user.click(button);
    expect(server.starts).toBe(1);
    const progress = await screen.findByTestId('scan-progress');
    expect(screen.getByRole('button', { name: 'Scan Library' })).toBeDisabled();
    expect(within(progress).getByRole('progressbar', { name: 'Scan progress' })).toBeVisible();
    expect(screen.getByTestId('scan-progress-text')).toHaveTextContent('Waiting to start…');

    // Still queued after the next read: never enabled.
    await clock.advanceTimersAsync(1000);
    expect(screen.getByRole('button', { name: 'Scan Library' })).toBeDisabled();

    server.job = testJob({ status: 'running', progress: 0, message: 'Listing the media folder' });
    await clock.advanceTimersAsync(1000);
    await waitFor(() => {
      expect(screen.getByTestId('scan-progress-text')).toHaveTextContent(
        'Listing the media folder',
      );
    });
    expect(screen.getByRole('progressbar', { name: 'Scan progress' })).toHaveAttribute(
      'aria-valuetext',
      'Listing the media folder',
    );

    server.job = testJob({
      status: 'running',
      progress: 40,
      message: '1,240 of 3,000 files: 2 new, 0 changed, 1,238 unchanged, 0 unreadable, 0 skipped',
    });
    await clock.advanceTimersAsync(1000);
    await waitFor(() => {
      expect(screen.getByTestId('scan-progress-text')).toHaveTextContent('1,240 of 3,000 files');
    });
    expect(screen.getByRole('progressbar', { name: 'Scan progress' })).toHaveAttribute(
      'aria-valuenow',
      '40',
    );
    expect(screen.getByRole('button', { name: 'Scan Library' })).toBeDisabled();

    // It finishes: the counts update by themselves, and the end is announced politely.
    server.job = testJob({ status: 'succeeded', progress: 100, message: 'done' });
    server.status = scanned({
      counts: { total: 5, available: 5, missing: 0, associated: 1, unmatched: 4 },
      lastScan: testScan({ jobId: JOB, counts: testCounts({ seen: 5, new: 2, unchanged: 3 }) }),
      activeScanJobId: null,
    });
    await clock.advanceTimersAsync(1000);
    await waitFor(() => {
      expect(fileCount('available')).toHaveTextContent('5');
    });
    expect(screen.queryByTestId('scan-progress')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Scan Library' })).toBeEnabled();
    const announcement = screen.getByTestId('scan-announcement');
    expect(announcement).toHaveAttribute('role', 'status');
    expect(announcement).toHaveAttribute('aria-live', 'polite');
    expect(announcement).toHaveTextContent('The scan has finished.');
    expect(screen.getByTestId('scan-count-seen')).toHaveTextContent('5');
  });

  it('picks up a scan already in progress from the status and follows it', async () => {
    const clock = fakeTimeouts();
    const server = mediaServer(scanned({ activeScanJobId: JOB }));
    server.job = testJob({ status: 'running', progress: 10, message: '1 of 10 files: …' });
    await openMedia();

    expect(await screen.findByTestId('scan-progress-text')).toHaveTextContent('1 of 10 files');
    expect(screen.getByRole('button', { name: 'Scan Library' })).toBeDisabled();
    expect(server.starts).toBe(0);

    server.job = testJob({ status: 'succeeded', progress: 100 });
    server.status = scanned();
    await clock.advanceTimersAsync(1000);
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Scan Library' })).toBeEnabled();
    });
  });

  it('notices a scheduled scan by reading the status again every 10 seconds', async () => {
    const clock = fakeTimeouts();
    const server = mediaServer(scanned());
    server.job = testJob({ status: 'running', progress: 50, message: '5 of 10 files: …' });
    await openMedia();
    expect(server.statusReads).toBe(1);

    await clock.advanceTimersAsync(9000);
    expect(server.statusReads).toBe(1);
    server.status = scanned({ activeScanJobId: JOB });
    await clock.advanceTimersAsync(1000);
    expect(await screen.findByTestId('scan-progress-text')).toHaveTextContent('5 of 10 files');
    expect(server.statusReads).toBe(2);

    // While it runs, every second.
    await clock.advanceTimersAsync(1000);
    expect(server.statusReads).toBe(3);
  });

  it('shows a failed scan’s reason over the counts before it', async () => {
    mediaServer(
      scanned({
        mount: { state: 'unavailable', since: '2026-10-08T11:00:00Z', path: '/media' },
        lastScan: testScan({
          jobId: JOB,
          outcome: 'failed',
          counts: testCounts({ seen: 0, new: 0 }),
          failure: 'media_folder_unavailable',
        }),
      }),
    );
    await openMedia();

    expect(screen.getByTestId('scan-failed')).toHaveTextContent(
      'The scan failed because the media folder could not be read.',
    );
    const previous = screen.getByTestId('previous-scan');
    expect(
      within(previous).getByRole('table', { name: 'What the last successful scan counted' }),
    ).toBeVisible();
    expect(within(previous).getByTestId('scan-count-seen')).toHaveTextContent('3');
    expect(fileCount('available')).toHaveTextContent('3');
  });

  it.each([
    ['interrupted', 'The scan was interrupted by a restart.'],
    ['failed', `The scan failed (job ${JOB}).`],
  ] as const)('words a %s scan', async (failure, text) => {
    mediaServer(
      scanned({
        lastScan: testScan({ jobId: JOB, outcome: 'failed', counts: null, failure, trigger: null }),
        lastSuccessfulScan: null,
      }),
    );
    await openMedia();

    expect(screen.getByTestId('scan-failed')).toHaveTextContent(text);
    expect(screen.getByTestId('no-previous-scan')).toHaveTextContent('No scan has finished yet.');
    expect(screen.getByTestId('scan-finished')).toHaveTextContent(/after 4 seconds\.$/);
  });

  it('says when the media folder is unavailable, that associations are kept, and keeps Scan Library available', async () => {
    mediaServer(
      scanned({ mount: { state: 'unavailable', since: '2026-10-08T11:00:00Z', path: '/media' } }),
    );
    const button = await openMedia();

    expect(screen.getByTestId('media-folder-state')).toHaveTextContent('Unavailable');
    expect(screen.getByTestId('media-folder-since')).toHaveTextContent(/^since /);
    expect(screen.getByTestId('media-unavailable')).toHaveTextContent(
      /associations are kept: they come back as they were once the folder can be read again/,
    );
    expect(button).toBeEnabled();
    expect(screen.getByTestId('next-scheduled-scan')).toHaveTextContent(
      'Scheduled scans wait until the media folder can be read.',
    );
    expect(fileCount('available')).toHaveTextContent('3');
  });

  it('warns that the wrong folder may be mounted when the server says most files went missing', async () => {
    mediaServer(
      scanned({
        counts: { total: 4, available: 1, missing: 3, associated: 1, unmatched: 3 },
        lastSuccessfulScan: testScan({ counts: testCounts({ missing: 3, availableBefore: 4 }) }),
        majorityMissingWarning: true,
      }),
    );
    await openMedia();

    expect(screen.getByTestId('majority-missing-warning')).toHaveTextContent(
      '3 of the 4 files that were available before the last scan could not be found. The wrong folder may be mounted at /media.',
    );
  });

  it('shows no warning when the server says none, exactly half included', async () => {
    mediaServer(
      scanned({
        counts: { total: 4, available: 2, missing: 2, associated: 0, unmatched: 4 },
        lastSuccessfulScan: testScan({ counts: testCounts({ missing: 2, availableBefore: 4 }) }),
        majorityMissingWarning: false,
      }),
    );
    await openMedia();

    expect(screen.queryByTestId('majority-missing-warning')).not.toBeInTheDocument();
    expect(fileCount('missing')).toHaveTextContent('2');
  });

  it('says scheduled scans are off, with the link to turn them on', async () => {
    mediaServer(
      scanned({ schedule: { enabled: false, intervalMinutes: 15 }, nextScheduledScan: null }),
    );
    await openMedia();

    const next = screen.getByTestId('next-scheduled-scan');
    expect(next).toHaveAttribute('data-scheduled', 'off');
    expect(next).toHaveTextContent('Scheduled scans are off. Turn them on in Settings → Library.');
  });

  it('says so when a scan could not be started, and lets the user try again', async () => {
    const server = mediaServer(scanned());
    server.failStart = true;
    const user = userEvent.setup();
    const button = await openMedia();

    await user.click(button);
    expect(await screen.findByText('The scan was not started')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Scan Library' })).toBeEnabled();
  });
});
