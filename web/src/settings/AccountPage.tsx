import { Button, Paper, PasswordInput, Stack, Text, Title } from '@mantine/core';
import { useState, type SyntheticEvent } from 'react';
import { changePassword, type PasswordChange } from '../api/account';
import { useSignedInSession } from '../auth/sessionContext';
import { Notice } from '../components/Notice';

type Field = keyof PasswordChange;

const emptyForm: PasswordChange = {
  currentPassword: '',
  newPassword: '',
  newPasswordConfirmation: '',
};

/** What happened to the last submission, if it was not a field error. */
type Outcome = { kind: 'changed' } | { kind: 'throttled'; message: string } | { kind: 'failed' };

export const PASSWORD_CHANGED_MESSAGE =
  'Your password has been changed. Every other session has been signed out.';

function OutcomeNotice({ outcome }: { outcome: Outcome }) {
  switch (outcome.kind) {
    case 'changed':
      return (
        <Paper p="sm" withBorder>
          <Text>{PASSWORD_CHANGED_MESSAGE}</Text>
        </Paper>
      );
    case 'throttled':
      return (
        <Notice title="Password not changed">
          <Text>{outcome.message}</Text>
        </Notice>
      );
    case 'failed':
      return (
        <Notice title="Password not changed">
          <Text>n8Tracks did not answer as expected. Check that it is running and try again.</Text>
        </Notice>
      );
  }
}

/**
 * The change-password form: the current password, and a new one (with its confirmation) meeting
 * the setup rule, which the API holds; its messages are shown by the fields. Changing it signs out
 * every other session and keeps this one.
 */
function ChangePasswordForm() {
  const session = useSignedInSession();
  const [values, setValues] = useState<PasswordChange>(emptyForm);
  const [errors, setErrors] = useState<Partial<Record<Field, string>>>({});
  const [outcome, setOutcome] = useState<Outcome | undefined>();
  const [submitting, setSubmitting] = useState(false);

  const change = (field: Field) => (event: { currentTarget: HTMLInputElement }) => {
    const { value } = event.currentTarget;
    setValues((previous) => ({ ...previous, [field]: value }));
  };

  const submit = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    setSubmitting(true);
    setOutcome(undefined);
    const result = await changePassword(values);
    setSubmitting(false);

    switch (result.kind) {
      case 'changed':
        setErrors({});
        setValues(emptyForm);
        setOutcome({ kind: 'changed' });
        return;
      case 'invalid':
        setErrors({
          currentPassword: result.errors.currentPassword?.join(' '),
          newPassword: result.errors.newPassword?.join(' '),
          newPasswordConfirmation: result.errors.newPasswordConfirmation?.join(' '),
        });
        return;
      case 'throttled':
      case 'failed':
        setErrors({});
        setOutcome(result);
        return;
    }
  };

  return (
    <Stack component="section" gap="sm" aria-labelledby="change-password-heading">
      <Title order={3} id="change-password-heading">
        Change password
      </Title>
      <form
        noValidate
        aria-labelledby="change-password-heading"
        onSubmit={(event) => {
          void submit(event);
        }}
      >
        <Stack gap="sm">
          {/* Lets the browser's password manager tie the new password to the right account. */}
          <input
            type="text"
            name="username"
            autoComplete="username"
            value={session?.username ?? ''}
            readOnly
            hidden
          />
          <PasswordInput
            label="Current password"
            name="currentPassword"
            autoComplete="current-password"
            required
            value={values.currentPassword}
            onChange={change('currentPassword')}
            error={errors.currentPassword}
            aria-invalid={errors.currentPassword !== undefined}
          />
          <PasswordInput
            label="New password"
            name="newPassword"
            description="At least 12 characters."
            autoComplete="new-password"
            required
            value={values.newPassword}
            onChange={change('newPassword')}
            error={errors.newPassword}
            aria-invalid={errors.newPassword !== undefined}
          />
          <PasswordInput
            label="Confirm new password"
            name="newPasswordConfirmation"
            autoComplete="new-password"
            required
            value={values.newPasswordConfirmation}
            onChange={change('newPasswordConfirmation')}
            error={errors.newPasswordConfirmation}
            aria-invalid={errors.newPasswordConfirmation !== undefined}
          />
          <div role="status">{outcome && <OutcomeNotice outcome={outcome} />}</div>
          <div>
            <Button type="submit" loading={submitting}>
              Change password
            </Button>
          </div>
        </Stack>
      </form>
    </Stack>
  );
}

/** Settings → Account: the signed-in administrator's own account. */
export function AccountPage() {
  return (
    <Stack gap="lg">
      <Title order={2}>Account</Title>
      <ChangePasswordForm />
    </Stack>
  );
}
