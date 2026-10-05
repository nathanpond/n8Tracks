import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import {
  DEFAULT_SCHEDULE,
  type Backup,
  type BackupList,
  type RestoreValidation,
} from '../api/backups';
import type { MaintenanceStatus } from '../api/maintenance';
import { ANTIFORGERY_HEADER } from '../api/session';
import { formatDateTime } from '../api/timeZone';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';

const VALIDATION_ID = '0199b1a0-0000-7000-8000-0000000000bb';

const valid: Backup = {
  location: 'mount',
  name: 'n8tracks-backup-20261004-120000-v0.1.0.zip',
  size: 3 * 1024 * 1024,
  createdAt: '2026-10-04T12:00:00Z',
  applicationVersion: '0.1.0',
  kind: 'scheduled',
  status: 'valid',
};

const newer: Backup = { ...valid, name: 'n8tracks-backup-newer.zip', status: 'newer' };

const list: BackupList = {
  destination: 'mount',
  sharesDiskWithData: false,
  activeJobId: null,
  items: [valid, newer],
  lastSuccessAt: valid.createdAt,
  lastRestore: null,
  schedule: { ...DEFAULT_SCHEDULE, nextAt: null, lastAttempt: null },
};

function validation(change: Partial<RestoreValidation['archive']> = {}): RestoreValidation {
  return {
    validationId: VALIDATION_ID,
    expiresAt: '2026-10-05T10:00:00Z',
    archive: {
      name: valid.name,
      location: 'mount',
      size: valid.size,
      createdAt: valid.createdAt,
      applicationVersion: '0.1.0',
      kind: 'scheduled',
      ...change,
    },
  };
}

function problem(status: number, code: string, title: string, extra: object = {}): Response {
  return jsonResponse(status, { status, code, title, requestId: 'r', ...extra });
}

/** A backend that answers the Backups page, and the restore endpoints as given. */
function backend(answers: {
  validate?: () => Response;
  upload?: () => Response;
  start?: () => Response;
  maintenance?: MaintenanceStatus[];
}) {
  const statuses = [...(answers.maintenance ?? [])];
  const mock = stubFetch();
  mock.mockImplementation((input, init) => {
    const path = requestPath(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (method === 'GET' && path.endsWith('/api/v1/backups')) {
      return Promise.resolve(jsonResponse(200, list));
    }
    if (path.endsWith('/api/v1/settings/backup-schedule')) {
      return Promise.resolve(jsonResponse(200, { ...DEFAULT_SCHEDULE, revision: 1 }));
    }
    if (path.endsWith('/api/v1/restores/validate')) {
      return Promise.resolve(answers.validate?.() ?? jsonResponse(200, validation()));
    }
    if (path.endsWith('/api/v1/restores/uploads')) {
      return Promise.resolve(
        answers.upload?.() ??
          jsonResponse(200, validation({ name: 'from-elsewhere.zip', location: null })),
      );
    }
    if (method === 'POST' && path.endsWith('/api/v1/restores')) {
      return Promise.resolve(
        answers.start?.() ??
          jsonResponse(202, { active: true, stage: 'validating', percent: 0, outcome: null }),
      );
    }
    if (path.endsWith('/api/v1/maintenance')) {
      const next = statuses.length > 1 ? statuses.shift() : statuses[0];
      return Promise.resolve(jsonResponse(200, next));
    }
    return Promise.resolve(jsonResponse(404, { code: 'not_found' }));
  });
  return mock;
}

function calls(mock: ReturnType<typeof stubFetch>, method: string, suffix: string) {
  return mock.mock.calls.filter(
    ([input, init]) =>
      (init?.method ?? 'GET').toUpperCase() === method && requestPath(input).endsWith(suffix),
  );
}

async function openRestore(name: string) {
  const user = userEvent.setup();
  renderApp('/settings/backups');
  await user.click(await screen.findByRole('button', { name: `Restore ${name}` }));
  const dialog = await screen.findByRole('dialog', { name: 'Restore from this backup?' });
  return { user, dialog };
}

describe('Restoring from Settings → Backups', () => {
  it('offers Restore only on valid backups', async () => {
    backend({});
    renderApp('/settings/backups');

    expect(await screen.findByRole('button', { name: `Restore ${valid.name}` })).toBeVisible();
    expect(screen.queryByRole('button', { name: `Restore ${newer.name}` })).toBeNull();
  });

  it('checks the backup, shows its summary, and starts only once RESTORE is typed', async () => {
    const mock = backend({
      maintenance: [{ active: true, stage: 'safety-backup', percent: 40, outcome: null }],
    });
    const { user, dialog } = await openRestore(valid.name);

    const summary = await within(dialog).findByRole('table', { name: 'Backup to restore' });
    expect(summary).toHaveTextContent(formatDateTime(valid.createdAt, 'UTC'));
    expect(summary).toHaveTextContent('n8Tracks 0.1.0');
    expect(summary).toHaveTextContent('Scheduled');
    expect(summary).toHaveTextContent(valid.name);
    expect(within(dialog).getByText(/administrator account and its password/)).toBeVisible();

    const [validate] = calls(mock, 'POST', '/api/v1/restores/validate');
    expect(JSON.parse(validate?.[1]?.body as string)).toEqual({
      location: 'mount',
      name: valid.name,
    });

    const restore = within(dialog).getByRole('button', { name: 'Restore' });
    expect(restore).toBeDisabled();
    const field = within(dialog).getByRole('textbox', { name: 'Type RESTORE to confirm' });
    await user.type(field, 'restore');
    expect(restore).toBeDisabled();
    await user.clear(field);
    await user.type(field, 'RESTORE');
    expect(restore).toBeEnabled();
    await user.click(restore);

    // The app gives way to the maintenance page, with the stage and its progress.
    expect(
      await screen.findByRole('heading', { level: 2, name: 'Restoring a backup' }),
    ).toBeVisible();
    expect(
      await screen.findByText('Taking a safety backup of the current data… 40%'),
    ).toBeVisible();
    expect(screen.getByRole('progressbar', { name: 'Restore progress' })).toBeVisible();

    const [start] = calls(mock, 'POST', '/api/v1/restores');
    expect(JSON.parse(start?.[1]?.body as string)).toEqual({
      validationId: VALIDATION_ID,
      confirmation: 'RESTORE',
    });
    expect(new Headers(start?.[1]?.headers).get(ANTIFORGERY_HEADER)).toBe('1');
  });

  it('says why a backup cannot be restored, and offers nothing to confirm', async () => {
    backend({
      validate: () =>
        problem(422, 'backup_newer_schema', 'Restore it with n8Tracks 0.4.0 or later.', {
          reason: 'newer-schema',
          neededVersion: '0.4.0',
        }),
    });
    const { dialog } = await openRestore(valid.name);

    expect(await within(dialog).findByTestId('restore-refusal')).toHaveTextContent(
      'Restore it with n8Tracks 0.4.0 or later.',
    );
    expect(within(dialog).getByText('Nothing was changed.')).toBeVisible();
    expect(within(dialog).queryByRole('textbox')).toBeNull();
    expect(within(dialog).queryByRole('button', { name: 'Restore' })).toBeNull();
  });

  it('uploads a chosen file and refuses one that is not a backup', async () => {
    const mock = backend({
      upload: () =>
        problem(
          422,
          'backup_invalid',
          'The file is not a ZIP archive, so it is not an n8Tracks backup.',
          {
            reason: 'not-a-zip',
          },
        ),
    });
    const user = userEvent.setup();
    const { container } = renderApp('/settings/backups');
    await screen.findByRole('button', { name: 'Restore from a file…' });
    const input = container.ownerDocument.querySelector<HTMLInputElement>('input[type="file"]');
    if (!input) {
      throw new Error('No file input.');
    }

    await user.upload(input, new File(['hello'], 'notes.zip', { type: 'application/zip' }));

    const dialog = await screen.findByRole('dialog', { name: 'Restore from a file?' });
    expect(await within(dialog).findByTestId('restore-refusal')).toHaveTextContent(
      'not a ZIP archive',
    );
    const [upload] = calls(mock, 'POST', '/api/v1/restores/uploads');
    const sent = upload?.[1];
    expect(sent?.body).toBeInstanceOf(FormData);
    expect((sent?.body as FormData).get('file')).toBeInstanceOf(File);
    expect(new Headers(sent?.headers).get(ANTIFORGERY_HEADER)).toBe('1');
  });

  it('shows an uploaded backup’s summary', async () => {
    backend({});
    const user = userEvent.setup();
    const { container } = renderApp('/settings/backups');
    await screen.findByRole('button', { name: 'Restore from a file…' });
    const input = container.ownerDocument.querySelector<HTMLInputElement>('input[type="file"]');
    if (!input) {
      throw new Error('No file input.');
    }

    await user.upload(input, new File(['zip'], 'from-elsewhere.zip', { type: 'application/zip' }));

    const dialog = await screen.findByRole('dialog', { name: 'Restore from a file?' });
    expect(
      await within(dialog).findByRole('table', { name: 'Backup to restore' }),
    ).toHaveTextContent('from-elsewhere.zip');
  });

  it('says why a confirmed restore did not start, and stays open', async () => {
    backend({
      start: () =>
        problem(
          409,
          'restore_blocked_by_jobs',
          'Background work is queued or running. Try again when it has finished.',
        ),
    });
    const { user, dialog } = await openRestore(valid.name);

    await user.type(
      await within(dialog).findByRole('textbox', { name: 'Type RESTORE to confirm' }),
      'RESTORE',
    );
    await user.click(within(dialog).getByRole('button', { name: 'Restore' }));

    expect(await within(dialog).findByText(/Background work is queued or running/)).toBeVisible();
    expect(screen.queryByRole('heading', { name: 'Restoring a backup' })).toBeNull();
    await waitFor(() => {
      expect(within(dialog).getByRole('button', { name: 'Restore' })).toBeEnabled();
    });
  });
});
