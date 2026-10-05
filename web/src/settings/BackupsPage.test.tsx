import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { formatSize, type Backup, type BackupList } from '../api/backups';
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

function list(items: Backup[], change: Partial<BackupList> = {}): BackupList {
  return { destination: 'mount', sharesDiskWithData: false, activeJobId: null, items, ...change };
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
  options: { jobs?: unknown[]; start?: () => Response; onStart?: () => void } = {},
) {
  const state = { list: initial, jobs: [...(options.jobs ?? [])] };
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
