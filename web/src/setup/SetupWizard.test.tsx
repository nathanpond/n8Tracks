import { MantineProvider } from '@mantine/core';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { SetupStatus } from '../api/setup';
import { jsonResponse, requestPath, stubAllFetch } from '../test/helpers';
import { SetupWizard } from './SetupWizard';

const ready: SetupStatus = {
  complete: false,
  storage: { writable: true },
  media: { available: true },
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

async function goToAdministrator(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('button', { name: 'Next' }));
  await user.click(screen.getByRole('button', { name: 'Next' }));
  expect(stepHeading('Administrator')).toBeVisible();
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
    expect(stepHeading('Administrator')).toBeVisible();
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
  it('is shown in the steps, before the administrator, and skipped', async () => {
    const user = userEvent.setup();
    const { container } = renderWizard();

    const labels = [...container.querySelectorAll('.mantine-Stepper-stepLabel')].map(
      (label) => label.textContent,
    );
    expect(labels).toEqual(['Storage', 'Media', 'Backups', 'Administrator']);
    expect(screen.getByText('Skipped for now')).toBeVisible();

    await goToAdministrator(user);
    expect(screen.queryByRole('heading', { level: 3, name: 'Backups' })).not.toBeInTheDocument();
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
    const [input, init] = fetchMock.mock.calls[0] ?? [];
    expect(input && requestPath(input)).toBe('/api/v1/setup');
    expect(init?.method).toBe('POST');
    expect(typeof init?.body).toBe('string');
    expect(JSON.parse(init?.body as string)).toEqual({
      username: 'owner',
      password: 'correct horse battery',
      passwordConfirmation: 'correct horse battery',
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

  it('goes back to the media step', async () => {
    const user = userEvent.setup();
    renderWizard();
    await goToAdministrator(user);

    await user.click(screen.getByRole('button', { name: 'Back' }));
    expect(stepHeading('Media library')).toBeVisible();
  });
});
