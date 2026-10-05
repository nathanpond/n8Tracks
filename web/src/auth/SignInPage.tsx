import { Button, PasswordInput, Stack, Text, TextInput, Title } from '@mantine/core';
import { useEffect, useRef, useState, type SyntheticEvent } from 'react';
import { signIn, type Session } from '../api/session';
import { Notice } from '../components/Notice';

type Field = 'username' | 'password';

/** What went wrong with the last attempt, if anything: one message for the whole form. */
type Failure =
  { kind: 'invalidCredentials' } | { kind: 'throttled'; message: string } | { kind: 'failed' };

export const INVALID_CREDENTIALS_MESSAGE = 'The username or password is incorrect.';

function FailureNotice({ failure }: { failure: Failure }) {
  switch (failure.kind) {
    case 'invalidCredentials':
      return (
        <Notice title="Not signed in">
          <Text>{INVALID_CREDENTIALS_MESSAGE}</Text>
        </Notice>
      );
    case 'throttled':
      return (
        <Notice title="Sign-in is paused">
          <Text>{failure.message}</Text>
        </Notice>
      );
    case 'failed':
      return (
        <Notice title="Not signed in">
          <Text>n8Tracks did not answer as expected. Check that it is running and try again.</Text>
        </Notice>
      );
  }
}

/**
 * The sign-in form. A wrong username or password gets one generic message; after too many, the
 * API's message says when to try again. On success `onSignedIn` gets the new session.
 */
export function SignInPage({ onSignedIn }: { onSignedIn: (session: Session) => void }) {
  const [values, setValues] = useState<Record<Field, string>>({ username: '', password: '' });
  const [errors, setErrors] = useState<Partial<Record<Field, string>>>({});
  const [failure, setFailure] = useState<Failure | undefined>();
  const [submitting, setSubmitting] = useState(false);
  const heading = useRef<HTMLHeadingElement>(null);

  useEffect(() => {
    heading.current?.focus();
  }, []);

  const change = (field: Field) => (event: { currentTarget: HTMLInputElement }) => {
    const { value } = event.currentTarget;
    setValues((previous) => ({ ...previous, [field]: value }));
  };

  const submit = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    setSubmitting(true);
    setFailure(undefined);
    const result = await signIn(values.username, values.password);
    setSubmitting(false);

    switch (result.kind) {
      case 'signedIn':
        onSignedIn(result.session);
        return;
      case 'invalid':
        setErrors({
          username: result.errors.username?.join(' '),
          password: result.errors.password?.join(' '),
        });
        return;
      case 'invalidCredentials':
      case 'throttled':
      case 'failed':
        setErrors({});
        // The password is cleared after a refusal; the username is kept.
        setValues((previous) => ({ ...previous, password: '' }));
        setFailure(result);
        return;
    }
  };

  return (
    <Stack gap="sm">
      <Title order={2} ref={heading} tabIndex={-1}>
        Sign in
      </Title>
      <form
        noValidate
        aria-label="Sign in"
        onSubmit={(event) => {
          void submit(event);
        }}
      >
        <Stack gap="sm">
          <TextInput
            label="Username"
            name="username"
            autoComplete="username"
            required
            value={values.username}
            onChange={change('username')}
            error={errors.username}
          />
          <PasswordInput
            label="Password"
            name="password"
            autoComplete="current-password"
            required
            value={values.password}
            onChange={change('password')}
            error={errors.password}
            // PasswordInput marks its error only visually; screen readers need the attribute.
            aria-invalid={errors.password !== undefined}
          />
          <div role="alert">{failure && <FailureNotice failure={failure} />}</div>
          <div>
            <Button type="submit" loading={submitting}>
              Sign in
            </Button>
          </div>
        </Stack>
      </form>
    </Stack>
  );
}
