import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';
import { PASSWORD_CHANGED_MESSAGE } from './AccountPage';

function isPasswordChange(input: RequestInfo | URL, init?: RequestInit): boolean {
  return requestPath(input).endsWith('/api/v1/account/password') && init?.method === 'POST';
}

function problem(status: number, code: string, extra: Record<string, unknown> = {}): Response {
  return new Response(JSON.stringify({ status, code, title: 'refused', ...extra }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

async function fill(current: string, next: string, confirmation: string) {
  const user = userEvent.setup();
  const form = await screen.findByRole('form', { name: 'Change password' });
  await user.type(within(form).getByLabelText(/^Current password/), current);
  await user.type(within(form).getByLabelText(/^New password/), next);
  await user.type(within(form).getByLabelText(/^Confirm new password/), confirmation);
  await user.click(within(form).getByRole('button', { name: 'Change password' }));
  return form;
}

describe('Settings → Account', () => {
  it('changes the password with the anti-forgery header and says other sessions were signed out', async () => {
    const mock = stubFetch();
    mock.mockResolvedValue(new Response(null, { status: 204 }));

    renderApp('/settings/account');
    expect(await screen.findByRole('heading', { level: 2, name: 'Account' })).toBeVisible();
    const form = await fill(
      'correct horse battery',
      'a brand new passphrase',
      'a brand new passphrase',
    );

    expect(await screen.findByText(PASSWORD_CHANGED_MESSAGE)).toBeVisible();
    // The form is emptied once the change is made.
    expect(within(form).getByLabelText(/^Current password/)).toHaveValue('');
    expect(within(form).getByLabelText(/^New password/)).toHaveValue('');

    const call = mock.mock.calls.find(([input, init]) => isPasswordChange(input, init));
    expect(new Headers(call?.[1]?.headers).get('X-N8Tracks-Request')).toBe('1');
    expect(JSON.parse(call?.[1]?.body as string)).toEqual({
      currentPassword: 'correct horse battery',
      newPassword: 'a brand new passphrase',
      newPasswordConfirmation: 'a brand new passphrase',
    });
  });

  it('shows the API field errors, a wrong current password included, and keeps what was typed', async () => {
    stubFetch().mockResolvedValue(
      jsonResponse(422, {
        code: 'validation_failed',
        errors: {
          currentPassword: ['The current password is incorrect.'],
          newPassword: ['A password must be at least 12 characters.'],
        },
      }),
    );

    renderApp('/settings/account');
    const form = await fill('wrong', 'short', 'short');

    expect(await screen.findByText('The current password is incorrect.')).toBeVisible();
    expect(screen.getByText('A password must be at least 12 characters.')).toBeVisible();
    expect(within(form).getByLabelText(/^Current password/)).toHaveAttribute(
      'aria-invalid',
      'true',
    );
    expect(within(form).getByLabelText(/^Confirm new password/)).toHaveAttribute(
      'aria-invalid',
      'false',
    );
    expect(within(form).getByLabelText(/^New password/)).toHaveValue('short');
    // A wrong current password is not a session that ended: no sign-in prompt.
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.queryByText(PASSWORD_CHANGED_MESSAGE)).not.toBeInTheDocument();
  });

  it('says when to try again after too many wrong passwords', async () => {
    stubFetch().mockResolvedValue(
      problem(429, 'sign_in_throttled', {
        detail: 'Too many failed sign-in attempts. Try again at 09:19.',
      }),
    );

    renderApp('/settings/account');
    await fill('wrong', 'a brand new passphrase', 'a brand new passphrase');

    expect(
      await screen.findByText('Too many failed sign-in attempts. Try again at 09:19.'),
    ).toBeVisible();
    expect(screen.getByText('Password not changed')).toBeVisible();
  });

  it('says so when the API does not answer as expected', async () => {
    stubFetch().mockResolvedValue(new Response('<html>', { status: 502 }));

    renderApp('/settings/account');
    await fill('correct horse battery', 'a brand new passphrase', 'a brand new passphrase');

    expect(await screen.findByText(/did not answer as expected/)).toBeVisible();
  });
});
