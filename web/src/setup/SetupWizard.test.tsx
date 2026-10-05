import { MantineProvider } from '@mantine/core';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { SetupBackups, SetupStatus } from '../api/setup';
import { jsonResponse, requestPath, stubAllFetch } from '../test/helpers';
import { BACKUP_SHARED_DISK_TITLE } from './BackupStep';
import { SetupWizard } from './SetupWizard';

const dataFolderBackups: SetupBackups = {
  destination: 'data',
  sharesDiskWithData: true,
  defaults: { enabled: true, frequency: 'daily', time: '03:00', keep: 7 },
};

const ready: SetupStatus = {
  complete: false,
  storage: { writable: true },
  media: { available: true },
  backups: dataFolderBackups,
};

function renderWizard(status: SetupStatus = ready, checking = false) {
  const onRecheck = vi.fn();
  const onComplete = vi.fn();
  const view = render(
    <MantineProvider>
      <SetupWizard
        status={status}
        checking={checking}
        onRecheck={onRecheck}
        onComplete={onComplete}
      />
    </MantineProvider>,
  );
  return { ...view, onRecheck, onComplete };
}

function stepHeading(name: string): HTMLElement {
  return screen.getByRole('heading', { level: 3, name });
}

async function goToBackups(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('button', { name: 'Next' }));
  await user.click(screen.getByRole('button', { name: 'Next' }));
  expect(stepHeading('Backups')).toBeVisible();
}

async function goToAdministrator(user: ReturnType<typeof userEvent.setup>) {
  await goToBackups(user);
  await user.click(screen.getByRole('button', { name: 'Next' }));
  expect(stepHeading('Administrator')).toBeVisible();
}

/** The body of the one setup submission. */
function submitted(fetchMock: ReturnType<typeof stubAllFetch>): unknown {
  const calls = fetchMock.mock.calls.filter(
    ([input, init]) => requestPath(input) === '/api/v1/setup' && init?.method === 'POST',
  );
  expect(calls).toHaveLength(1);
  return JSON.parse(calls[0]?.[1]?.body as string);
}

async function fillAndSubmit(
  user: ReturnType<typeof userEvent.setup>,
  username: string,
  password: string,
  confirmation: string,
) {
  await user.type(screen.getByLabelText('Username', { exact: false }), username);
  await user.type(screen.getByLabelText(/^Password/), password);
  await user.type(screen.getByLabelText(/^Repeat the password/), confirmation);
  await user.click(screen.getByRole('button', { name: 'Finish setup' }));
}

function problem(status: number, code: string, extra: Record<string, unknown> = {}): Response {
  return new Response(JSON.stringify({ status, code, ...extra }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

describe('the storage step', () => {
  it('shows a writable data folder and lets the owner go on', async () => {
    const user = userEvent.setup();
    renderWizard();

    expect(stepHeading('Storage')).toHaveFocus();
    expect(screen.getByText('writable')).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Retry' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(stepHeading('Media library')).toBeVisible();
  });

  it('shows a failure with Retry and blocks the next step', async () => {
    const user = userEvent.setup();
    const { onRecheck } = renderWizard({ ...ready, storage: { writable: false } });

    expect(screen.getByText('not writable')).toBeVisible();
    expect(screen.getByText('The data folder cannot be written')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'Retry' }));
    expect(onRecheck).toHaveBeenCalledTimes(1);
    expect(stepHeading('Storage')).toBeVisible();
  });

  it('goes on once a retry finds the folder writable', async () => {
    const user = userEvent.setup();
    const view = renderWizard({ ...ready, storage: { writable: false } });

    view.rerender(
      <MantineProvider>
        <SetupWizard status={ready} checking={false} onRecheck={vi.fn()} onComplete={vi.fn()} />
      </MantineProvider>,
    );

    expect(screen.getByRole('button', { name: 'Next' })).toBeEnabled();
    await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(stepHeading('Media library')).toBeVisible();
  });
});

describe('the media step', () => {
  it('shows an available media folder', async () => {
    const user = userEvent.setup();
    renderWizard();
    await user.click(screen.getByRole('button', { name: 'Next' }));

    expect(stepHeading('Media library')).toHaveFocus();
    expect(screen.getByText('available')).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Check again' })).not.toBeInTheDocument();
  });

  it('warns about an unavailable media folder without blocking, and can check again', async () => {
    const user = userEvent.setup();
    const { onRecheck } = renderWizard({ ...ready, media: { available: false } });
    await user.click(screen.getByRole('button', { name: 'Next' }));

    expect(screen.getByText('unavailable')).toBeVisible();
    expect(screen.getByText('The media folder is not available')).toBeVisible();
    expect(screen.getByText(/authoring songs works without it/)).toBeVisible();

    await user.click(screen.getByRole('button', { name: 'Check again' }));
    expect(onRecheck).toHaveBeenCalledTimes(1);

    expect(screen.getByRole('button', { name: 'Next' })).toBeEnabled();
    await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(stepHeading('Backups')).toBeVisible();
  });

  it('goes back to the storage step', async () => {
    const user = userEvent.setup();
    renderWizard();
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await user.click(screen.getByRole('button', { name: 'Back' }));

    expect(stepHeading('Storage')).toBeVisible();
  });
});

describe('the backup step', () => {
  it('is the third step, and shows the defaults and the data folder with the shared-disk warning', async () => {
    const user = userEvent.setup();
    const { container } = renderWizard();

    const labels = [...container.querySelectorAll('.mantine-Stepper-stepLabel')].map(
      (label) => label.textContent,
    );
    expect(labels).toEqual(['Storage', 'Media', 'Backups', 'Administrator']);

    await goToBackups(user);
    expect(stepHeading('Backups')).toHaveFocus();
    expect(screen.getByTestId('backup-step-schedule')).toHaveTextContent('Daily at 03:00, keep 7');
    expect(screen.getByTestId('backup-step-location')).toHaveTextContent(
      'the data folder (/data/backups)',
    );
    expect(screen.getByText(BACKUP_SHARED_DISK_TITLE)).toBeVisible();
    // The fields stay closed until the owner asks to change the schedule.
    expect(screen.queryByLabelText(/^Scheduled backups to keep/)).not.toBeInTheDocument();
  });

  it('shows the backup folder, without a warning, when one is mounted', async () => {
    const user = userEvent.setup();
    renderWizard({
      ...ready,
      backups: { ...dataFolderBackups, destination: 'mount', sharesDiskWithData: false },
    });

    await goToBackups(user);
    expect(screen.getByTestId('backup-step-location')).toHaveTextContent(
      'the backup folder (/backup)',
    );
    expect(screen.queryByText(BACKUP_SHARED_DISK_TITLE)).not.toBeInTheDocument();
  });

  it('sends the accepted defaults with the administrator', async () => {
    const fetchMock = stubAllFetch().mockResolvedValue(
      jsonResponse(201, { id: '0199a1b2-0000-7000-8000-000000000000', username: 'owner' }),
    );
    const user = userEvent.setup();
    renderWizard();
    await goToAdministrator(user);

    await fillAndSubmit(user, 'owner', 'correct horse battery', 'correct horse battery');

    await waitFor(() => {
      expect(submitted(fetchMock)).toMatchObject({
        backupSchedule: { enabled: true, frequency: 'daily', time: '03:00', keep: 7 },
      });
    });
  });

  it('sends a changed schedule, and keeps it when the owner comes back to the step', async () => {
    const fetchMock = stubAllFetch().mockResolvedValue(
      jsonResponse(201, { id: '0199a1b2-0000-7000-8000-000000000000', username: 'owner' }),
    );
    const user = userEvent.setup();
    renderWizard();
    await goToBackups(user);

    await user.click(screen.getByRole('button', { name: 'Change the schedule' }));
    await user.click(screen.getByRole('radio', { name: 'Weekly, on Sundays' }));
    const keep = screen.getByLabelText(/^Scheduled backups to keep/);
    await user.clear(keep);
    await user.type(keep, '14');
    expect(screen.getByTestId('backup-step-schedule')).toHaveTextContent(
      'Weekly on Sunday at 03:00, keep 14',
    );
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await user.click(screen.getByRole('button', { name: 'Back' }));
    expect(screen.getByTestId('backup-step-schedule')).toHaveTextContent(
      'Weekly on Sunday at 03:00, keep 14',
    );
    await user.click(screen.getByRole('button', { name: 'Next' }));

    await fillAndSubmit(user, 'owner', 'correct horse battery', 'correct horse battery');

    await waitFor(() => {
      expect(submitted(fetchMock)).toMatchObject({
        backupSchedule: { enabled: true, frequency: 'weekly', time: '03:00', keep: 14 },
      });
    });
  });

  it('refuses a number kept outside 1 to 365 and stays on the step', async () => {
    const user = userEvent.setup();
    renderWizard();
    await goToBackups(user);
    await user.click(screen.getByRole('button', { name: 'Change the schedule' }));

    const keep = screen.getByLabelText(/^Scheduled backups to keep/);
    await user.clear(keep);
    await user.type(keep, '400');
    await user.click(screen.getByRole('button', { name: 'Next' }));

    expect(screen.getByText('Keep from 1 to 365 backups.')).toBeVisible();
    expect(keep).toHaveAttribute('aria-invalid', 'true');
    expect(stepHeading('Backups')).toBeVisible();
  });

  it('goes back to the backup step with its errors when the API refuses the schedule', async () => {
    stubAllFetch().mockResolvedValue(
      problem(422, 'validation_failed', {
        errors: { 'backupSchedule.keep': ['Keep from 1 to 365 backups.'] },
      }),
    );
    const user = userEvent.setup();
    renderWizard();
    await goToAdministrator(user);

    await fillAndSubmit(user, 'owner', 'correct horse battery', 'correct horse battery');

    expect(await screen.findByRole('heading', { level: 3, name: 'Backups' })).toBeVisible();
    expect(screen.getByText('Keep from 1 to 365 backups.')).toBeVisible();
  });
});

describe('the administrator step', () => {
  it('creates the administrator and completes setup', async () => {
    const fetchMock = stubAllFetch().mockResolvedValue(
      jsonResponse(201, { id: '0199a1b2-0000-7000-8000-000000000000', username: 'owner' }),
    );
    const user = userEvent.setup();
    const { onComplete } = renderWizard();
    await goToAdministrator(user);

    await fillAndSubmit(user, 'owner', 'correct horse battery', 'correct horse battery');

    await waitFor(() => {
      expect(onComplete).toHaveBeenCalledTimes(1);
    });
    expect(submitted(fetchMock)).toEqual({
      username: 'owner',
      password: 'correct horse battery',
      passwordConfirmation: 'correct horse battery',
      backupSchedule: { enabled: true, frequency: 'daily', time: '03:00', keep: 7 },
    });
  });

  it('shows the field errors the API returns', async () => {
    stubAllFetch().mockResolvedValue(
      problem(422, 'validation_failed', {
        errors: {
          password: ['A password must be at least 12 characters.'],
          passwordConfirmation: ['The passwords do not match.'],
        },
      }),
    );
    const user = userEvent.setup();
    const { onComplete } = renderWizard();
    await goToAdministrator(user);

    await fillAndSubmit(user, 'owner', 'short', 'shorter');

    expect(await screen.findByText('A password must be at least 12 characters.')).toBeVisible();
    expect(screen.getByText('The passwords do not match.')).toBeVisible();
    expect(screen.getByLabelText(/^Password/)).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByLabelText(/^Repeat the password/)).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByLabelText('Username', { exact: false })).not.toHaveAttribute(
      'aria-invalid',
      'true',
    );
    expect(onComplete).not.toHaveBeenCalled();
  });

  it('says setup has already been done when another browser finished first', async () => {
    stubAllFetch().mockResolvedValue(problem(409, 'setup_already_complete'));
    const user = userEvent.setup();
    const { onComplete } = renderWizard();
    await goToAdministrator(user);

    await fillAndSubmit(user, 'second', 'correct horse battery', 'correct horse battery');

    expect(await screen.findByText('Setup has already been done')).toBeVisible();
    expect(onComplete).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    expect(onComplete).toHaveBeenCalledTimes(1);
  });

  it('returns to the storage step when storage stopped being writable', async () => {
    stubAllFetch().mockResolvedValue(problem(409, 'storage_not_writable'));
    const user = userEvent.setup();
    const { onRecheck, onComplete } = renderWizard();
    await goToAdministrator(user);

    await fillAndSubmit(user, 'owner', 'correct horse battery', 'correct horse battery');

    expect(await screen.findByText('Setup was not completed')).toBeVisible();
    expect(stepHeading('Storage')).toBeVisible();
    expect(onRecheck).toHaveBeenCalledTimes(1);
    expect(onComplete).not.toHaveBeenCalled();
  });

  it('says so when the API does not answer as expected, and keeps what was typed', async () => {
    stubAllFetch().mockRejectedValue(new TypeError('Failed to fetch'));
    const user = userEvent.setup();
    renderWizard();
    await goToAdministrator(user);

    await fillAndSubmit(user, 'owner', 'correct horse battery', 'correct horse battery');

    const form = screen.getByRole('form', { name: 'Create the administrator' });
    expect(await within(form).findByText('Setup could not be completed')).toBeVisible();
    expect(screen.getByLabelText('Username', { exact: false })).toHaveValue('owner');
  });

  it('goes back to the backup step', async () => {
    const user = userEvent.setup();
    renderWizard();
    await goToAdministrator(user);

    await user.click(screen.getByRole('button', { name: 'Back' }));
    expect(stepHeading('Backups')).toBeVisible();
  });
});
