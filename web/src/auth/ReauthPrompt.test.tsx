import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { PASSWORD_CHANGED_MESSAGE } from '../settings/AccountPage';
import { jsonResponse, renderApp, requestPath, signedInSession, stubFetch } from '../test/helpers';

const PROMPT = 'Your session has ended';

function problem(status: number, code: string): Response {
  return new Response(JSON.stringify({ status, code, title: 'refused' }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

function isPasswordChange(input: RequestInfo | URL, init?: RequestInit): boolean {
  return requestPath(input).endsWith('/api/v1/account/password') && init?.method === 'POST';
}

function isSignIn(input: RequestInfo | URL, init?: RequestInit): boolean {
  return requestPath(input).endsWith('/api/v1/session') && init?.method === 'POST';
}

/**
 * The session has ended until someone signs in with `password`; the password change answers 401
 * `not_authenticated` until then, and 204 after.
 */
function backend(password = 'correct horse battery') {
  const state = { signedIn: false };
  const mock = stubFetch();
  mock.mockImplementation((input, init) => {
    if (isSignIn(input, init)) {
      const body = JSON.parse(init?.body as string) as { password: string };
      if (body.password !== password) {
        return Promise.resolve(problem(401, 'invalid_credentials'));
      }
      state.signedIn = true;
      return Promise.resolve(jsonResponse(201, signedInSession));
    }
    if (isPasswordChange(input, init)) {
      return Promise.resolve(
        state.signedIn ? new Response(null, { status: 204 }) : problem(401, 'not_authenticated'),
      );
    }
    return Promise.resolve(jsonResponse(404, {}));
  });
  return mock;
}

async function typeChange(user: ReturnType<typeof userEvent.setup>) {
  const form = await screen.findByRole('form', { name: 'Change password' });
  await user.type(within(form).getByLabelText(/^Current password/), 'correct horse battery');
  await user.type(within(form).getByLabelText(/^New password/), 'a brand new passphrase');
  await user.type(within(form).getByLabelText(/^Confirm new password/), 'a brand new passphrase');
  await user.click(within(form).getByRole('button', { name: 'Change password' }));
  return form;
}

async function signInFromThePrompt(user: ReturnType<typeof userEvent.setup>, password: string) {
  const prompt = await screen.findByRole('dialog', { name: PROMPT });
  const form = within(prompt).getByRole('form', { name: 'Sign in' });
  await user.type(within(form).getByLabelText(/^Password/), password);
  await user.click(within(form).getByRole('button', { name: 'Sign in' }));
  return prompt;
}

describe('the in-place sign-in prompt', () => {
  it('appears over the page on a 401, keeps the page as it was, and replays the held request after sign-in', async () => {
    const mock = backend();
    const user = userEvent.setup();

    renderApp('/settings/account');
    const page = await typeChange(user);

    const prompt = await screen.findByRole('dialog', { name: PROMPT });
    // The username is filled in; the page and what was typed into it are still there.
    expect(within(prompt).getByLabelText(/^Username/)).toHaveValue('owner');
    expect(within(page).getByLabelText(/^New password/)).toHaveValue('a brand new passphrase');
    expect(screen.queryByText(PASSWORD_CHANGED_MESSAGE)).not.toBeInTheDocument();

    await signInFromThePrompt(user, 'correct horse battery');

    // The held request went again and completed: the page carries on.
    expect(await screen.findByText(PASSWORD_CHANGED_MESSAGE)).toBeVisible();
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: PROMPT })).not.toBeInTheDocument();
    });
    const changes = mock.mock.calls.filter(([input, init]) => isPasswordChange(input, init));
    expect(changes).toHaveLength(2);
    expect(changes[1]?.[1]?.body).toBe(changes[0]?.[1]?.body);
    expect(new Headers(changes[1]?.[1]?.headers).get('X-N8Tracks-Request')).toBe('1');
    expect(screen.getByRole('heading', { level: 2, name: 'Account' })).toBeVisible();
  });

  it('shows the prompt form’s own invalid_credentials in the prompt, without replaying or reopening', async () => {
    const mock = backend();
    const user = userEvent.setup();

    renderApp('/settings/account');
    await typeChange(user);
    const prompt = await signInFromThePrompt(user, 'not the password');

    expect(await within(prompt).findByText('The username or password is incorrect.')).toBeVisible();
    expect(screen.getAllByRole('dialog')).toHaveLength(1);
    expect(mock.mock.calls.filter(([input, init]) => isPasswordChange(input, init))).toHaveLength(
      1,
    );

    // The right password then lets the held request go.
    await signInFromThePrompt(user, 'correct horse battery');
    expect(await screen.findByText(PASSWORD_CHANGED_MESSAGE)).toBeVisible();
    expect(mock.mock.calls.filter(([input, init]) => isPasswordChange(input, init))).toHaveLength(
      2,
    );
  });

  it('leaves for the sign-in page when the user chooses Sign out instead', async () => {
    const mock = backend();
    const user = userEvent.setup();

    renderApp('/settings/account');
    await typeChange(user);
    const prompt = await screen.findByRole('dialog', { name: PROMPT });
    await user.click(within(prompt).getByRole('button', { name: 'Sign out' }));

    expect(await screen.findByRole('heading', { level: 2, name: 'Sign in' })).toBeVisible();
    expect(screen.queryByRole('dialog', { name: PROMPT })).not.toBeInTheDocument();
    expect(mock.mock.calls.filter(([input, init]) => isPasswordChange(input, init))).toHaveLength(
      1,
    );
  });
});
