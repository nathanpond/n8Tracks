import { apiFetch } from './client';

const PASSWORD_PATH = 'api/v1/account/password';

export interface PasswordChange {
  currentPassword: string;
  newPassword: string;
  newPasswordConfirmation: string;
}

/** How a password change ended, by the API's answer. */
export type PasswordChangeResult =
  | { kind: 'changed' }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'throttled'; message: string }
  | { kind: 'failed' };

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isErrorMap(value: unknown): value is Record<string, string[]> {
  return (
    isRecord(value) &&
    Object.values(value).every(
      (messages) => Array.isArray(messages) && messages.every((m) => typeof m === 'string'),
    )
  );
}

async function problemBody(response: Response): Promise<Record<string, unknown>> {
  try {
    const body: unknown = await response.json();
    return isRecord(body) ? body : {};
  } catch {
    return {};
  }
}

/**
 * Changes the signed-in administrator's password; every other session ends. Never rejects: a
 * network failure, an unknown answer, or a session that ended and was not signed in again is
 * `failed`. A wrong current password is `invalid`, on `currentPassword`.
 */
export async function changePassword(change: PasswordChange): Promise<PasswordChangeResult> {
  try {
    const response = await apiFetch(PASSWORD_PATH, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(change),
    });
    if (response.status === 204) {
      return { kind: 'changed' };
    }

    const problem = await problemBody(response);
    if (
      response.status === 422 &&
      problem.code === 'validation_failed' &&
      isErrorMap(problem.errors)
    ) {
      return { kind: 'invalid', errors: problem.errors };
    }
    if (
      response.status === 429 &&
      problem.code === 'sign_in_throttled' &&
      typeof problem.detail === 'string'
    ) {
      return { kind: 'throttled', message: problem.detail };
    }
    return { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}
