import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, useLocation } from 'react-router';
import { describe, expect, it } from 'vitest';
import { App } from '../App';
import {
  completeSetup,
  healthyReport,
  isSessionRequest,
  isSetupStatusRequest,
  jsonResponse,
  neverAnswers,
  requestPath,
  signedInSession,
  stubAllFetch,
} from '../test/helpers';
import { safeReturnTo, signInPath } from './returnTo';

function Location() {
  const { pathname, search } = useLocation();
  return <output data-testid="location">{`${pathname}${search}`}</output>;
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <App />
      <Location />
    </MemoryRouter>,
  );
}

function problem(status: number, code: string, extra: Record<string, unknown> = {}): Response {
  return new Response(JSON.stringify({ status, code, title: 'refused', ...extra }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

type Answer = (input: RequestInfo | URL, init?: RequestInit) => Response | undefined;

/**
 * Setup is complete; the session is whatever `signedIn` says at the time of the request; health is
 * healthy. `other` answers anything else first (sign-in, sign-out).
 */
function backend(state: { signedIn: boolean }, other: Answer = () => undefined) {
  const mock = stubAllFetch();
  mock.mockImplementation((input, init) => {
    const answered = other(input, init);
    if (answered) {
      return Promise.resolve(answered);
    }
    if (isSetupStatusRequest(input)) {
      return Promise.resolve(jsonResponse(200, completeSetup));
    }
    if (isSessionRequest(input, init)) {
      return Promise.resolve(
        state.signedIn ? jsonResponse(200, signedInSession) : problem(401, 'not_authenticated'),
      );
    }
    return Promise.resolve(jsonResponse(200, healthyReport));
  });
  return mock;
}

function isSignIn(input: RequestInfo | URL, init?: RequestInit): boolean {
  return requestPath(input).endsWith('/api/v1/session') && init?.method === 'POST';
}

async function fillAndSubmit(username: string, password: string) {
  const user = userEvent.setup();
  const form = await screen.findByRole('form', { name: 'Sign in' });
  await user.clear(within(form).getByLabelText(/^Username/));
  await user.type(within(form).getByLabelText(/^Username/), username);
  await user.type(within(form).getByLabelText(/^Password/), password);
  await user.click(within(form).getByRole('button', { name: 'Sign in' }));
  return user;
}

describe('without a session', () => {
  it.each([
    ['/', '/sign-in'],
    ['/library/deep/link', '/sign-in?returnTo=%2Flibrary%2Fdeep%2Flink'],
    ['/songs?page=2', '/sign-in?returnTo=%2Fsongs%3Fpage%3D2'],
  ])('redirects %s to the sign-in page', async (path, expected) => {
    backend({ signedIn: false });

    renderAt(path);

    expect(await screen.findByRole('heading', { level: 2, name: 'Sign in' })).toBeVisible();
    expect(screen.getByTestId('location')).toHaveTextContent(expected);
    expect(screen.queryByRole('navigation', { name: 'Main' })).not.toBeInTheDocument();
    expect(screen.queryByTestId('user-menu')).not.toBeInTheDocument();
  });

  it('asks only for the setup status and the session before showing the sign-in page', async () => {
    const mock = backend({ signedIn: false });

    renderAt('/');

    await screen.findByRole('heading', { name: 'Sign in' });
    expect(mock.mock.calls.map(([input]) => requestPath(input))).toEqual([
      '/api/v1/setup/status',
      '/api/v1/session',
    ]);
  });

  it('signs in with the anti-forgery header and goes on to where the user was going', async () => {
    const state = { signedIn: false };
    const mock = backend(state, (input, init) => {
      if (!isSignIn(input, init)) {
        return undefined;
      }
      state.signedIn = true;
      return jsonResponse(201, signedInSession);
    });

    renderAt('/settings/system?tab=health');
    await fillAndSubmit('  Owner ', 'correct horse battery');

    expect(await screen.findByRole('heading', { name: 'System' })).toBeVisible();
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/settings\/system\?tab=health$/);
    expect(screen.getByRole('navigation', { name: 'Main' })).toBeVisible();
    expect(screen.getByTestId('user-menu')).toHaveTextContent('owner');

    const call = mock.mock.calls.find(([input, init]) => isSignIn(input, init));
    const init = call?.[1];
    expect(new Headers(init?.headers).get('X-N8Tracks-Request')).toBe('1');
    expect(typeof init?.body).toBe('string');
    expect(JSON.parse(init?.body as string)).toEqual({
      username: '  Owner ',
      password: 'correct horse battery',
    });
  });

  it.each([
    'https://evil.example/',
    '//evil.example/path',
    '/\\evil.example',
    'javascript:alert(1)',
  ])('ignores a returnTo of %s and goes to the app root', async (returnTo) => {
    const state = { signedIn: false };
    backend(state, (input, init) => {
      if (!isSignIn(input, init)) {
        return undefined;
      }
      state.signedIn = true;
      return jsonResponse(201, signedInSession);
    });

    renderAt(`/sign-in?returnTo=${encodeURIComponent(returnTo)}`);
    await fillAndSubmit('owner', 'correct horse battery');

    // The app root, which is the Songs page.
    expect(await screen.findByRole('heading', { name: 'Songs' })).toBeVisible();
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/songs$/);
  });

  it('shows one generic message for a wrong username or password and clears the password', async () => {
    backend({ signedIn: false }, (input, init) =>
      isSignIn(input, init) ? problem(401, 'invalid_credentials') : undefined,
    );

    renderAt('/');
    await fillAndSubmit('owner', 'wrong password');

    const alert = await screen.findByRole('alert');
    expect(await within(alert).findByText('The username or password is incorrect.')).toBeVisible();
    expect(screen.getByLabelText(/^Password/)).toHaveValue('');
    expect(screen.getByLabelText(/^Username/)).toHaveValue('owner');
    expect(screen.getByTestId('location')).toHaveTextContent('/sign-in');
  });

  it('shows when to try again after too many failed attempts', async () => {
    backend({ signedIn: false }, (input, init) =>
      isSignIn(input, init)
        ? problem(429, 'sign_in_throttled', {
            detail: 'Too many failed sign-in attempts. Try again at 09:19.',
            retryAt: '2026-10-01T09:19:00Z',
          })
        : undefined,
    );

    renderAt('/');
    await fillAndSubmit('owner', 'correct horse battery');

    expect(
      await screen.findByText('Too many failed sign-in attempts. Try again at 09:19.'),
    ).toBeVisible();
    expect(screen.getByText('Sign-in is paused')).toBeVisible();
  });

  it('shows field errors for missing fields', async () => {
    backend({ signedIn: false }, (input, init) =>
      isSignIn(input, init)
        ? jsonResponse(422, {
            code: 'validation_failed',
            errors: { username: ['Enter your username.'], password: ['Enter your password.'] },
          })
        : undefined,
    );
    const user = userEvent.setup();

    renderAt('/');
    await user.click(await screen.findByRole('button', { name: 'Sign in' }));

    expect(await screen.findByText('Enter your username.')).toBeVisible();
    expect(screen.getByText('Enter your password.')).toBeVisible();
    expect(screen.getByLabelText(/^Password/)).toHaveAttribute('aria-invalid', 'true');
  });

  it('says so when the API does not answer', async () => {
    backend({ signedIn: false }, (input, init) =>
      isSignIn(input, init) ? new Response('<html>', { status: 502 }) : undefined,
    );

    renderAt('/');
    await fillAndSubmit('owner', 'correct horse battery');

    expect(await screen.findByText(/did not answer as expected/)).toBeVisible();
  });
});

describe('with a session', () => {
  it('sends the sign-in page on to the app', async () => {
    backend({ signedIn: true });

    renderAt('/sign-in?returnTo=%2Fsongs');

    expect(await screen.findByRole('heading', { name: 'Songs' })).toBeVisible();
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/songs$/);
  });

  it.each([
    ['Sign out', '/api/v1/session'],
    ['Sign out everywhere', '/api/v1/sessions'],
  ])(
    '"%s" ends the session with the anti-forgery header and shows the sign-in page',
    async (action, path) => {
      const state = { signedIn: true };
      const mock = backend(state, (_input, init) => {
        if (init?.method !== 'DELETE') {
          return undefined;
        }
        state.signedIn = false;
        return new Response(null, { status: 204 });
      });
      const user = userEvent.setup();

      renderAt('/');
      await user.click(await screen.findByTestId('user-menu'));
      await user.click(await screen.findByRole('menuitem', { name: action }));

      expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeVisible();
      const call = mock.mock.calls.find(([, init]) => init?.method === 'DELETE');
      expect(call && requestPath(call[0])).toBe(path);
      expect(new Headers(call?.[1]?.headers).get('X-N8Tracks-Request')).toBe('1');
    },
  );

  it('stays signed in when signing out fails and the session is still there', async () => {
    backend({ signedIn: true }, (_input, init) =>
      init?.method === 'DELETE' ? new Response(null, { status: 500 }) : undefined,
    );
    const user = userEvent.setup();

    renderAt('/');
    await user.click(await screen.findByTestId('user-menu'));
    await user.click(await screen.findByRole('menuitem', { name: 'Sign out' }));

    await waitFor(() => {
      expect(screen.getByTestId('user-menu')).toBeVisible();
    });
    expect(screen.getByRole('heading', { name: 'Songs' })).toBeVisible();
  });
});

describe('the session check', () => {
  it('shows a loading state, then an error with Retry when the API does not answer', async () => {
    const mock = stubAllFetch();
    let failing = true;
    mock.mockImplementation((input, init) => {
      if (isSetupStatusRequest(input)) {
        return Promise.resolve(jsonResponse(200, completeSetup));
      }
      if (isSessionRequest(input, init)) {
        return Promise.resolve(failing ? jsonResponse(500, {}) : problem(401, 'not_authenticated'));
      }
      return neverAnswers(input, init);
    });
    const user = userEvent.setup();

    renderAt('/');

    expect(await screen.findByText(/is not answering/)).toBeVisible();
    failing = false;
    await user.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeVisible();
  });
});

describe('returnTo', () => {
  it.each([
    [null, '/'],
    ['/', '/'],
    ['/songs/1?tab=lyrics#top', '/songs/1?tab=lyrics#top'],
    ['songs', '/'],
    ['//evil.example', '/'],
    ['/\\evil.example', '/'],
    ['https://evil.example', '/'],
    ['/a\nb', '/'],
  ])('accepts only a local path: %s gives %s', (value, expected) => {
    expect(safeReturnTo(value)).toBe(expected);
  });

  it('is left out for the app root', () => {
    expect(signInPath('/')).toBe('/sign-in');
    expect(signInPath('/songs')).toBe('/sign-in?returnTo=%2Fsongs');
  });
});
