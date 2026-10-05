import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, useLocation } from 'react-router';
import { describe, expect, it } from 'vitest';
import { App } from '../App';
import {
  completeSetup,
  healthyReport,
  incompleteSetup,
  isSessionRequest,
  isSetupStatusRequest,
  jsonResponse,
  neverAnswers,
  signedInSession,
  stubAllFetch,
} from '../test/helpers';

function Location() {
  return <output data-testid="location">{useLocation().pathname}</output>;
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <App />
      <Location />
    </MemoryRouter>,
  );
}

/** Answers the setup status with `status`, the session as signed in, and every other request with a healthy report. */
function answer(status: unknown) {
  const mock = stubAllFetch();
  mock.mockImplementation((input, init) =>
    Promise.resolve(
      isSetupStatusRequest(input)
        ? jsonResponse(200, status)
        : isSessionRequest(input, init)
          ? jsonResponse(200, signedInSession)
          : jsonResponse(200, healthyReport),
    ),
  );
  return mock;
}

describe('the setup gate', () => {
  it.each(['/', '/somewhere/deep', '/setup'])(
    'redirects %s to the wizard while setup is incomplete',
    async (path) => {
      answer(incompleteSetup);

      renderAt(path);

      expect(await screen.findByRole('heading', { name: 'Set up n8Tracks' })).toBeVisible();
      expect(screen.getByTestId('location')).toHaveTextContent('/setup');
      expect(screen.queryByRole('heading', { name: 'Health' })).not.toBeInTheDocument();
    },
  );

  it('asks for the setup status before rendering any route', async () => {
    const mock = answer(incompleteSetup);

    renderAt('/');

    expect(screen.getByText('Loading…')).toBeVisible();
    await screen.findByRole('heading', { name: 'Set up n8Tracks' });
    // Only the status was asked for: the shell's health request never went out.
    expect(mock.mock.calls.map(([input]) => isSetupStatusRequest(input))).toEqual([true]);
  });

  it('sends the wizard route to the app once setup is complete', async () => {
    answer(completeSetup);

    renderAt('/setup');

    expect(await screen.findByRole('heading', { name: 'Health' })).toBeVisible();
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/$/);
    expect(screen.queryByRole('heading', { name: 'Set up n8Tracks' })).not.toBeInTheDocument();
  });

  it('renders any other route as it is once setup is complete', async () => {
    answer(completeSetup);

    renderAt('/somewhere/deep');

    expect(await screen.findByRole('heading', { name: 'Health' })).toBeVisible();
    expect(screen.getByTestId('location')).toHaveTextContent('/somewhere/deep');
  });

  it('shows an error with Retry when the status cannot be read, and Retry asks again', async () => {
    const mock = stubAllFetch();
    mock.mockResolvedValueOnce(jsonResponse(500, { title: 'failed' }));
    mock.mockImplementation((input, init) =>
      Promise.resolve(
        isSetupStatusRequest(input)
          ? jsonResponse(200, incompleteSetup)
          : isSessionRequest(input, init)
            ? jsonResponse(200, signedInSession)
            : jsonResponse(200, healthyReport),
      ),
    );
    const user = userEvent.setup();

    renderAt('/');

    expect(await screen.findByText(/is not answering/)).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByRole('heading', { name: 'Set up n8Tracks' })).toBeVisible();
  });

  it('treats a body that is not a setup status as an error', async () => {
    stubAllFetch().mockResolvedValue(jsonResponse(200, { complete: 'yes' }));

    renderAt('/');

    expect(await screen.findByText(/is not answering/)).toBeVisible();
  });

  it('keeps the loading state while the status has not answered', () => {
    stubAllFetch().mockImplementation(neverAnswers);

    renderAt('/');

    expect(screen.getByText('Loading…')).toBeVisible();
    expect(screen.getByRole('radiogroup', { name: 'Colour scheme' })).toBeVisible();
  });
});
