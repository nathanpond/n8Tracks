import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import {
  DEFAULT_SCHEDULE,
  formatSize,
  isBackupList,
  type Backup,
  type BackupList,
  type BackupScheduleRecord,
  type BackupScheduleStatus,
} from '../api/backups';
import { ANTIFORGERY_HEADER } from '../api/session';
import { formatDateTime } from '../api/timeZone';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';
import { SHARED_DISK_TITLE } from './BackupsPage';

const JOB_ID = '0199b1a0-0000-7000-8000-0000000000aa';

const valid: Backup = {
  location: 'mount',
  name: 'n8tracks-backup-20261004-120000-v0.1.0.zip',
  size: 3 * 1024 * 1024,
  createdAt: '2026-10-04T12:00:00Z',
  applicationVersion: '0.1.0',
  kind: 'manual',
  status: 'valid',
};

const newer: Backup = {
  location: 'data',
  name: 'n8tracks-backup-20991231-000000-v9.0.0.zip',
  size: 2048,
  createdAt: '2099-12-31T00:00:00Z',
  applicationVersion: '9.0.0',
  kind: 'manual',
  status: 'newer',
};

const invalid: Backup = {
  location: 'data',
  name: 'n8tracks-backup-junk.zip',
  size: 14,
  createdAt: '2026-10-01T08:00:00Z',
  applicationVersion: null,
  kind: null,
  status: 'invalid',
};

const created: Backup = {
  location: 'data',
  name: 'n8tracks-backup-20261005-090000-v0.1.0.zip',
  size: 512 * 1024,
  createdAt: '2026-10-05T09:00:00Z',
  applicationVersion: '0.1.0',
  kind: 'manual',
  status: 'valid',
};

const schedule: BackupScheduleStatus = {
  ...DEFAULT_SCHEDULE,
  nextAt: '2026-10-06T03:00:00Z',
  lastAttempt: null,
};

function list(items: Backup[], change: Partial<BackupList> = {}): BackupList {
  return {
    destination: 'mount',
    sharesDiskWithData: false,
    activeJobId: null,
    items,
    lastSuccessAt: items.find((item) => item.status === 'valid')?.createdAt ?? null,
    schedule,
    lastRestore: null,
    ...change,
  };
}

function job(status: string, progress: number, extra: Record<string, unknown> = {}) {
  return {
    id: JOB_ID,
    type: 'backup',
    status,
    progress,
    message: null,
    createdUtc: '2026-10-05T09:00:00Z',
    startedUtc: null,
    finishedUtc: null,
    error: null,
    result: null,
    ...extra,
  };
}

/**
 * A backend whose list is `state.list`, whose job answers come from `state.jobs` in turn (the last
 * one repeating), and whose POST and DELETE answer as given.
 */
function backend(
  initial: BackupList,
  options: {
    jobs?: unknown[];
    start?: () => Response;
    onStart?: () => void;
    put?: (body: Record<string, unknown>, ifMatch: string | null) => Response;
  } = {},
) {
  const firstSchedule: BackupScheduleRecord = { ...DEFAULT_SCHEDULE, revision: 1 };
  const state = { list: initial, jobs: [...(options.jobs ?? [])], schedule: firstSchedule };
  const mock = stubFetch();
  mock.mockImplementation((input, init) => {
    const path = requestPath(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (method === 'GET' && path.endsWith('/api/v1/backups')) {
      return Promise.resolve(jsonResponse(200, state.list));
    }
    if (path.endsWith('/api/v1/settings/backup-schedule')) {
      if (method === 'GET') {
        return Promise.resolve(jsonResponse(200, state.schedule));
      }
      const body = JSON.parse(init?.body as string) as Record<string, unknown>;
      const ifMatch = new Headers(init?.headers).get('If-Match');
      if (options.put) {
        return Promise.resolve(options.put(body, ifMatch));
      }
      state.schedule = {
        ...(body as unknown as BackupScheduleRecord),
        revision: state.schedule.revision + 1,
      };
      return Promise.resolve(jsonResponse(200, state.schedule));
    }
    if (method === 'POST' && path.endsWith('/api/v1/backups')) {
      options.onStart?.();
      return Promise.resolve(
        options.start ? options.start() : jsonResponse(202, { jobId: JOB_ID }),
      );
    }
    if (method === 'GET' && path.endsWith(`/api/v1/jobs/${JOB_ID}`)) {
      const next = state.jobs.length > 1 ? state.jobs.shift() : state.jobs[0];
      return Promise.resolve(jsonResponse(200, next));
    }
    if (method === 'DELETE' && path.includes('/api/v1/backups/')) {
      const name = decodeURIComponent(path.split('/').pop() ?? '');
      state.list = { ...state.list, items: state.list.items.filter((item) => item.name !== name) };
      return Promise.resolve(new Response(null, { status: 204 }));
    }
    return Promise.resolve(jsonResponse(404, { code: 'not_found' }));
  });
  return { mock, state };
}

function calls(mock: ReturnType<typeof stubFetch>, method: string, suffix: string) {
  return mock.mock.calls.filter(
    ([input, init]) =>
      (init?.method ?? 'GET').toUpperCase() === method && requestPath(input).endsWith(suffix),
  );
}

function row(name: string): HTMLElement {
  const found = document.querySelector<HTMLElement>(`tr[data-backup="${name}"]`);
  if (!found) {
    throw new Error(`No row for ${name}.`);
  }
  return found;
}

function status(): HTMLElement {
  return screen.getByTestId('backup-status');
}

describe('formatSize', () => {
  it('reads as bytes, then binary multiples with one decimal', () => {
    expect(formatSize(14)).toBe('14 bytes');
    expect(formatSize(2048)).toBe('2 KB');
    expect(formatSize(1536 * 1024)).toBe('1.5 MB');
  });
});

describe('Settings → Backups', () => {
  it('lists each backup with its time, size, version, folder, and status', async () => {
    backend(list([newer, valid, invalid]));

    renderApp('/settings/backups');

    const table = await screen.findByRole('table', { name: 'Backups' });
    expect(screen.getByRole('heading', { level: 2, name: 'Backups' })).toBeVisible();
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows.map((r) => r.getAttribute('data-backup'))).toEqual([
      newer.name,
      valid.name,
      invalid.name,
    ]);

    const cells = within(row(valid.name)).getAllByRole('cell');
    expect(within(row(valid.name)).getByRole('rowheader')).toHaveTextContent(
      formatDateTime(valid.createdAt, 'UTC'),
    );
    expect(cells.map((cell) => cell.textContent).slice(0, 4)).toEqual([
      '3 MB',
      '0.1.0',
      'Backup folder',
      'Valid',
    ]);
    expect(within(row(newer.name)).getByText('Made by a newer version')).toBeVisible();
    expect(within(row(newer.name)).getByText('Data folder')).toBeVisible();

    // Valid and newer archives download from the API; an invalid one can only be deleted.
    expect(screen.getByRole('link', { name: `Download ${valid.name}` })).toHaveAttribute(
      'href',
      expect.stringMatching(
        new RegExp(`/api/v1/backups/mount/${valid.name.replace(/\./g, '\\.')}$`),
      ),
    );
    expect(screen.getByRole('link', { name: `Download ${newer.name}` })).toBeVisible();
    expect(within(row(invalid.name)).getByText('Not a valid backup')).toBeVisible();
    expect(
      screen.queryByRole('link', { name: `Download ${invalid.name}` }),
    ).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: `Delete ${invalid.name}` })).toBeEnabled();

    // A backup mount is in use: no warning.
    expect(screen.queryByText(SHARED_DISK_TITLE)).not.toBeInTheDocument();
  });

  it('warns when backups go to the data folder, and says when there are none', async () => {
    backend(list([], { destination: 'data', sharesDiskWithData: true }));

    renderApp('/settings/backups');

    expect(await screen.findByText(SHARED_DISK_TITLE)).toBeVisible();
    expect(screen.getByText(/same disk as the data they protect/)).toBeVisible();
    expect(screen.getByText('There are no backups yet.')).toBeVisible();
  });

  it('backs up now: shows the progress, then the outcome and the new row', async () => {
    const user = userEvent.setup();
    const { mock, state } = backend(list([valid]), {
      jobs: [
        job('running', 21, { message: 'Copying database' }),
        job('succeeded', 100, { message: 'Verifying', result: { name: created.name } }),
      ],
      onStart: () => {
        state.list = list([created, valid]);
      },
    });

    renderApp('/settings/backups');
    await screen.findByRole('table', { name: 'Backups' });
    await user.click(screen.getByRole('button', { name: 'Back up now' }));

    expect(await within(status()).findByText('Copying database… 21%')).toBeVisible();
    expect(within(status()).getByRole('progressbar', { name: 'Backup progress' })).toHaveAttribute(
      'aria-valuenow',
      '21',
    );
    expect(
      await within(status()).findByText(new RegExp(`Backup finished: ${created.name}`), undefined, {
        timeout: 3000,
      }),
    ).toBeVisible();
    expect(within(status()).queryByRole('progressbar')).not.toBeInTheDocument();
    await waitFor(() => {
      expect(row(created.name)).toBeInTheDocument();
    });

    const [post] = calls(mock, 'POST', '/api/v1/backups');
    expect(new Headers(post?.[1]?.headers).get(ANTIFORGERY_HEADER)).toBe('1');
  });

  it('shows why a backup failed', async () => {
    const user = userEvent.setup();
    backend(list([]), {
      jobs: [
        job('failed', 90, {
          error: 'The backup failed verification: the database copy failed its integrity check.',
        }),
      ],
    });

    renderApp('/settings/backups');
    await screen.findByText('There are no backups yet.');
    await user.click(screen.getByRole('button', { name: 'Back up now' }));

    expect(await within(status()).findByText('Backup failed')).toBeVisible();
    expect(within(status()).getByText(/failed its integrity check/)).toBeVisible();
    expect(screen.getByText('There are no backups yet.')).toBeVisible();
  });

  it('says so when a backup is already in progress, and follows that one', async () => {
    const user = userEvent.setup();
    backend(list([]), {
      start: () =>
        jsonResponse(409, { status: 409, code: 'backup_in_progress', jobId: JOB_ID, title: 'x' }),
      jobs: [job('running', 40, { message: 'Archiving' })],
    });

    renderApp('/settings/backups');
    await screen.findByText('There are no backups yet.');
    await user.click(screen.getByRole('button', { name: 'Back up now' }));

    expect(await within(status()).findByText('A backup is already in progress.')).toBeVisible();
    expect(await within(status()).findByText('Archiving… 40%')).toBeVisible();
  });

  it('follows a backup that was already running when the page opened', async () => {
    const { mock } = backend(list([], { activeJobId: JOB_ID }), {
      jobs: [job('queued', 0)],
    });

    renderApp('/settings/backups');

    expect(
      await within(await screen.findByTestId('backup-status')).findByText('Waiting to start… 0%'),
    ).toBeVisible();
    expect(calls(mock, 'POST', '/api/v1/backups')).toHaveLength(0);
  });

  it('says when a backup cannot be started', async () => {
    const user = userEvent.setup();
    backend(list([]), { start: () => jsonResponse(500, { code: 'internal_error' }) });

    renderApp('/settings/backups');
    await screen.findByText('There are no backups yet.');
    await user.click(screen.getByRole('button', { name: 'Back up now' }));

    expect(await within(status()).findByText('The backup could not be started')).toBeVisible();
  });

  it('asks before deleting a backup, and deletes it on confirmation', async () => {
    const user = userEvent.setup();
    const { mock } = backend(list([valid, invalid]));

    renderApp('/settings/backups');
    await screen.findByRole('table', { name: 'Backups' });

    await user.click(screen.getByRole('button', { name: `Delete ${invalid.name}` }));
    let dialog = await screen.findByRole('dialog', { name: 'Delete backup?' });
    expect(within(dialog).getByText(invalid.name)).toBeVisible();
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    expect(calls(mock, 'DELETE', invalid.name)).toHaveLength(0);

    await user.click(screen.getByRole('button', { name: `Delete ${invalid.name}` }));
    dialog = await screen.findByRole('dialog', { name: 'Delete backup?' });
    await user.click(within(dialog).getByRole('button', { name: 'Delete backup' }));

    await waitFor(() => {
      expect(document.querySelector(`tr[data-backup="${invalid.name}"]`)).toBeNull();
    });
    const deletes = calls(mock, 'DELETE', `/api/v1/backups/data/${invalid.name}`);
    expect(deletes).toHaveLength(1);
    expect(new Headers(deletes[0]?.[1]?.headers).get(ANTIFORGERY_HEADER)).toBe('1');
    expect(row(valid.name)).toBeInTheDocument();
  });
});

describe('Settings → Backups: the last restore', () => {
  const safetyBackup = {
    location: 'data' as const,
    name: 'n8tracks-backup-20261005-091500-v0.1.0.zip',
    path: '/data/backups/n8tracks-backup-20261005-091500-v0.1.0.zip',
  };

  it('shows a successful restore with the safety backup taken before it', async () => {
    backend(
      list([valid], {
        lastRestore: {
          outcome: 'succeeded',
          finishedAt: '2026-10-05T09:16:00Z',
          archive: valid.name,
          failedStage: null,
          detail: "The instance now holds the backup's data, and every session was ended.",
          safetyBackup,
        },
      }),
    );

    renderApp('/settings/backups');

    const note = await screen.findByTestId('last-restore');
    expect(note).toHaveTextContent('Last restore');
    expect(note).toHaveTextContent(`Restored from ${valid.name}`);
    expect(note).toHaveTextContent('every session was ended');
    expect(note).toHaveTextContent(safetyBackup.name);
    expect(note).toHaveTextContent(safetyBackup.path);
    expect(note).not.toHaveTextContent('failed');
  });

  it('shows a rolled-back restore with the stage it failed at and where the safety backup is', async () => {
    backend(
      list([valid], {
        lastRestore: {
          outcome: 'rolled-back',
          finishedAt: '2026-10-05T09:16:00Z',
          archive: 'from-elsewhere.zip',
          failedStage: 'migrating',
          detail:
            'The restore failed while updating the restored database, and the data from before it was put back. The server log has the details.',
          safetyBackup,
        },
      }),
    );

    renderApp('/settings/backups');

    const note = await screen.findByTestId('last-restore');
    expect(note).toHaveTextContent('The last restore failed and was undone');
    expect(note).toHaveTextContent('Restoring from from-elsewhere.zip failed');
    expect(note).toHaveTextContent('while updating the database');
    expect(note).toHaveTextContent('the data from before it was put back');
    expect(note).toHaveTextContent(safetyBackup.path);
  });

  it('shows nothing when no restore has replaced data', async () => {
    backend(list([valid]));

    renderApp('/settings/backups');

    await waitFor(() => {
      expect(row(valid.name)).toBeVisible();
    });
    expect(screen.queryByTestId('last-restore')).toBeNull();
  });

  it('refuses a list whose last restore is malformed', () => {
    expect(isBackupList({ ...list([valid]), lastRestore: { outcome: 'maybe' } })).toBe(false);
    expect(isBackupList({ ...list([valid]), lastRestore: undefined })).toBe(false);
    expect(isBackupList(list([valid]))).toBe(true);
  });
});

describe('Settings → Backups: the schedule', () => {
  function scheduleStatus(): HTMLElement {
    return screen.getByTestId('backup-schedule-status');
  }

  it('shows the schedule, the last successful backup, and the next planned time in the configured zone', async () => {
    backend(list([valid]));

    renderApp('/settings/backups');

    expect(await screen.findByRole('heading', { level: 3, name: 'Schedule' })).toBeVisible();
    await within(scheduleStatus()).findByText('Daily at 03:00, keep 7');
    expect(screen.getByTestId('last-success')).toHaveTextContent(
      formatDateTime(valid.createdAt, 'UTC'),
    );
    expect(screen.getByTestId('next-planned')).toHaveTextContent(
      formatDateTime('2026-10-06T03:00:00Z', 'UTC'),
    );
    expect(within(scheduleStatus()).getByText('Times are in UTC.')).toBeVisible();

    // The form starts from the stored schedule.
    const form = await screen.findByRole('form', { name: 'Backup schedule' });
    expect(within(form).getByRole('switch', { name: 'Back up on a schedule' })).toBeChecked();
    expect(within(form).getByRole('radio', { name: 'Daily' })).toBeChecked();
    expect(within(form).getByLabelText(/^Time of day/)).toHaveValue('03:00');
    expect(within(form).getByLabelText(/^Scheduled backups to keep/)).toHaveValue('7');
  });

  it('shows a failed scheduled backup with its reason and the pending retry', async () => {
    backend(
      list([valid], {
        schedule: {
          ...schedule,
          lastAttempt: {
            outcome: 'failed',
            startedAt: '2026-10-05T03:00:00Z',
            finishedAt: '2026-10-05T03:00:09Z',
            error: 'System.IO.IOException: The backup disk is full.',
            retryAt: '2026-10-05T04:00:09Z',
          },
        },
      }),
    );

    renderApp('/settings/backups');

    expect(await screen.findByText('The latest scheduled backup failed')).toBeVisible();
    expect(within(scheduleStatus()).getByText(/The backup disk is full/)).toBeVisible();
    expect(
      within(scheduleStatus()).getByText(
        new RegExp(`tried once more at ${formatDateTime('2026-10-05T04:00:09Z', 'UTC')}`),
      ),
    ).toBeVisible();
    expect(within(scheduleStatus()).getByText(/Earlier backups were left/)).toBeVisible();
    expect(row(valid.name)).toBeInTheDocument();
  });

  it('says a failed retry waits for the next planned time, and shows a running or successful attempt', async () => {
    const failedRetry = list([], {
      schedule: {
        ...schedule,
        lastAttempt: {
          outcome: 'failed',
          startedAt: '2026-10-05T04:00:09Z',
          finishedAt: '2026-10-05T04:00:12Z',
          error: 'disk full',
          retryAt: null,
        },
      },
    });
    const { state } = backend(failedRetry);
    const view = renderApp('/settings/backups');
    expect(await screen.findByText(/The next attempt is at the next planned time/)).toBeVisible();
    view.unmount();

    state.list = list([], {
      schedule: {
        ...schedule,
        lastAttempt: {
          outcome: 'running',
          startedAt: '2026-10-06T03:00:00Z',
          finishedAt: null,
          error: null,
          retryAt: null,
        },
      },
    });
    const running = renderApp('/settings/backups');
    expect(await screen.findByText(/A scheduled backup is running/)).toBeVisible();
    running.unmount();

    state.list = list([], {
      schedule: {
        ...schedule,
        enabled: false,
        nextAt: null,
        lastAttempt: {
          outcome: 'succeeded',
          startedAt: '2026-10-06T03:00:00Z',
          finishedAt: '2026-10-06T03:00:05Z',
          error: null,
          retryAt: null,
        },
      },
    });
    renderApp('/settings/backups');
    expect(await screen.findByText(/The latest scheduled backup succeeded/)).toBeVisible();
    expect(within(scheduleStatus()).getByText('Off')).toBeVisible();
    expect(screen.queryByTestId('next-planned')).not.toBeInTheDocument();
    expect(screen.getByTestId('last-success')).toHaveTextContent('none yet');
  });

  it('saves a changed schedule with its revision, then reloads the page data', async () => {
    const user = userEvent.setup();
    const { mock } = backend(list([valid]));

    renderApp('/settings/backups');
    const form = await screen.findByRole('form', { name: 'Backup schedule' });
    await user.click(within(form).getByRole('radio', { name: 'Weekly, on Sundays' }));
    const time = within(form).getByLabelText(/^Time of day/);
    await user.clear(time);
    await user.type(time, '22:15');
    const keep = within(form).getByLabelText(/^Scheduled backups to keep/);
    await user.clear(keep);
    await user.type(keep, '30');
    const listsBefore = calls(mock, 'GET', '/api/v1/backups').length;
    await user.click(within(form).getByRole('button', { name: 'Save schedule' }));

    expect(
      await within(screen.getByTestId('schedule-save-status')).findByText('Schedule saved.'),
    ).toBeVisible();
    const puts = calls(mock, 'PUT', '/api/v1/settings/backup-schedule');
    expect(puts).toHaveLength(1);
    const headers = new Headers(puts[0]?.[1]?.headers);
    expect(headers.get('If-Match')).toBe('"1"');
    expect(headers.get(ANTIFORGERY_HEADER)).toBe('1');
    expect(JSON.parse(puts[0]?.[1]?.body as string)).toEqual({
      enabled: true,
      frequency: 'weekly',
      time: '22:15',
      keep: 30,
    });
    await waitFor(() => {
      expect(calls(mock, 'GET', '/api/v1/backups').length).toBeGreaterThan(listsBefore);
    });
  });

  it('refuses a number kept outside 1 to 365 without sending it', async () => {
    const user = userEvent.setup();
    const { mock } = backend(list([]));

    renderApp('/settings/backups');
    const form = await screen.findByRole('form', { name: 'Backup schedule' });
    const keep = within(form).getByLabelText(/^Scheduled backups to keep/);
    for (const value of ['0', '366']) {
      await user.clear(keep);
      await user.type(keep, value);
      await user.click(within(form).getByRole('button', { name: 'Save schedule' }));
      expect(await within(form).findByText('Keep from 1 to 365 backups.')).toBeVisible();
      expect(keep).toHaveAttribute('aria-invalid', 'true');
    }
    expect(calls(mock, 'PUT', '/api/v1/settings/backup-schedule')).toHaveLength(0);
  });

  it('shows the current schedule when it was changed elsewhere', async () => {
    const user = userEvent.setup();
    const current = { ...DEFAULT_SCHEDULE, keep: 12, revision: 4 };
    backend(list([]), {
      put: () => jsonResponse(409, { status: 409, code: 'revision_conflict', title: 'x', current }),
    });

    renderApp('/settings/backups');
    const form = await screen.findByRole('form', { name: 'Backup schedule' });
    const keep = within(form).getByLabelText(/^Scheduled backups to keep/);
    await user.clear(keep);
    await user.type(keep, '3');
    await user.click(within(form).getByRole('button', { name: 'Save schedule' }));

    expect(await screen.findByText('The schedule was changed elsewhere')).toBeVisible();
    expect(keep).toHaveValue('12');
  });
});
