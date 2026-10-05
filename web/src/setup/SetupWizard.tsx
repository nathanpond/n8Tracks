import {
  Button,
  Group,
  PasswordInput,
  Stack,
  Stepper,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useEffect, useRef, useState, type ReactNode, type SyntheticEvent } from 'react';
import { submitSetup, type SetupStatus } from '../api/setup';
import { CheckBadge } from '../components/CheckBadge';
import { Notice } from '../components/Notice';

const STORAGE_STEP = 0;
const MEDIA_STEP = 1;
// Step 2 is reserved for backup defaults: shown, and skipped until the scheduled-backup story fills it in.
const ADMINISTRATOR_STEP = 3;

type Field = 'username' | 'password' | 'passwordConfirmation';

export interface SetupWizardProps {
  status: SetupStatus;
  /** Whether the status is being fetched again. */
  checking: boolean;
  /** Fetches the status again: the backend runs every check afresh. */
  onRecheck: () => void;
  /** Setup is complete (by this browser or another one). */
  onComplete: () => void;
}

/** A step's heading. It takes the focus when its step opens, so keyboard and screen-reader users land on it. */
function StepHeading({ children }: { children: string }) {
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    heading.current?.focus();
  }, []);

  return (
    <Title order={3} ref={heading} tabIndex={-1}>
      {children}
    </Title>
  );
}

function StepActions({ children }: { children: ReactNode }) {
  return (
    <Group gap="sm" mt="md">
      {children}
    </Group>
  );
}

function StorageStep({
  writable,
  checking,
  onRetry,
  onNext,
}: {
  writable: boolean;
  checking: boolean;
  onRetry: () => void;
  onNext: () => void;
}) {
  return (
    <Stack gap="sm" data-testid="storage-step">
      <StepHeading>Storage</StepHeading>
      <Group gap="sm">
        <Text>Data folder</Text>
        <CheckBadge tone={writable ? 'healthy' : 'unhealthy'}>
          {writable ? 'writable' : 'not writable'}
        </CheckBadge>
      </Group>
      {writable ? (
        <Text>n8Tracks can write its database and files in the data folder.</Text>
      ) : (
        <Notice title="The data folder cannot be written">
          <Text>
            n8Tracks needs to write its database and files in the data folder (/data in the
            container). Check that the folder is mounted and that the user n8Tracks runs as can
            write to it, then retry. Setup cannot continue until it can.
          </Text>
        </Notice>
      )}
      <StepActions>
        {!writable && (
          <Button variant="default" onClick={onRetry} loading={checking}>
            Retry
          </Button>
        )}
        <Button onClick={onNext} disabled={!writable || checking}>
          Next
        </Button>
      </StepActions>
    </Stack>
  );
}

function MediaStep({
  available,
  checking,
  onRecheck,
  onBack,
  onNext,
}: {
  available: boolean;
  checking: boolean;
  onRecheck: () => void;
  onBack: () => void;
  onNext: () => void;
}) {
  return (
    <Stack gap="sm" data-testid="media-step">
      <StepHeading>Media library</StepHeading>
      <Group gap="sm">
        <Text>Media folder</Text>
        <CheckBadge tone={available ? 'healthy' : 'degraded'}>
          {available ? 'available' : 'unavailable'}
        </CheckBadge>
      </Group>
      {available ? (
        <Text>n8Tracks can read the media folder.</Text>
      ) : (
        <Notice title="The media folder is not available">
          <Text>
            n8Tracks cannot read the media folder (/media in the container). You can still finish
            setup: authoring songs works without it, and the media library becomes available once
            the folder is mounted.
          </Text>
        </Notice>
      )}
      <StepActions>
        <Button variant="default" onClick={onBack}>
          Back
        </Button>
        {!available && (
          <Button variant="default" onClick={onRecheck} loading={checking}>
            Check again
          </Button>
        )}
        <Button onClick={onNext} disabled={checking}>
          Next
        </Button>
      </StepActions>
    </Stack>
  );
}

function AdministratorStep({
  onBack,
  onComplete,
  onStorageFailed,
}: {
  onBack: () => void;
  onComplete: () => void;
  onStorageFailed: () => void;
}) {
  const [values, setValues] = useState<Record<Field, string>>({
    username: '',
    password: '',
    passwordConfirmation: '',
  });
  const [errors, setErrors] = useState<Partial<Record<Field, string>>>({});
  const [submitting, setSubmitting] = useState(false);
  const [outcome, setOutcome] = useState<'alreadyComplete' | 'failed' | undefined>();

  const change = (field: Field) => (event: { currentTarget: HTMLInputElement }) => {
    const { value } = event.currentTarget;
    setValues((previous) => ({ ...previous, [field]: value }));
  };

  const submit = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    setSubmitting(true);
    setOutcome(undefined);
    const result = await submitSetup(values);
    setSubmitting(false);

    switch (result.kind) {
      case 'created':
        onComplete();
        return;
      case 'alreadyComplete':
        setErrors({});
        setOutcome('alreadyComplete');
        return;
      case 'storageNotWritable':
        onStorageFailed();
        return;
      case 'invalid':
        setErrors({
          username: result.errors.username?.join(' '),
          password: result.errors.password?.join(' '),
          passwordConfirmation: result.errors.passwordConfirmation?.join(' '),
        });
        return;
      case 'failed':
        setOutcome('failed');
        return;
    }
  };

  if (outcome === 'alreadyComplete') {
    return (
      <Stack gap="sm" data-testid="administrator-step">
        <StepHeading>Administrator</StepHeading>
        <Notice title="Setup has already been done">
          <Text>
            The administrator was created from another browser while this one was open. Nothing you
            entered here was saved.
          </Text>
        </Notice>
        <StepActions>
          <Button onClick={onComplete}>Continue</Button>
        </StepActions>
      </Stack>
    );
  }

  return (
    <Stack gap="sm" data-testid="administrator-step">
      <StepHeading>Administrator</StepHeading>
      <Text>
        Create the administrator: the one account that signs in to this instance. There is no
        password reset by email, so keep the password somewhere safe.
      </Text>
      <form
        noValidate
        aria-label="Create the administrator"
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
            autoComplete="new-password"
            description="12 to 256 characters."
            required
            value={values.password}
            onChange={change('password')}
            error={errors.password}
            // PasswordInput marks its error only visually; screen readers need the attribute.
            aria-invalid={errors.password !== undefined}
          />
          <PasswordInput
            label="Repeat the password"
            name="passwordConfirmation"
            autoComplete="new-password"
            required
            value={values.passwordConfirmation}
            onChange={change('passwordConfirmation')}
            error={errors.passwordConfirmation}
            aria-invalid={errors.passwordConfirmation !== undefined}
          />
          {outcome === 'failed' && (
            <Notice title="Setup could not be completed">
              <Text>
                n8Tracks did not answer as expected. Check that it is running and try again.
              </Text>
            </Notice>
          )}
          <StepActions>
            <Button variant="default" onClick={onBack} disabled={submitting}>
              Back
            </Button>
            <Button type="submit" loading={submitting}>
              Finish setup
            </Button>
          </StepActions>
        </Stack>
      </form>
    </Stack>
  );
}

/**
 * First-run setup: storage, media, backups (reserved and skipped for now), then the administrator,
 * whose creation completes setup. Storage must be writable to go on; unavailable media is a warning.
 */
export function SetupWizard({ status, checking, onRecheck, onComplete }: SetupWizardProps) {
  const [active, setActive] = useState(STORAGE_STEP);
  const [storageLost, setStorageLost] = useState(false);
  const writable = status.storage?.writable ?? false;
  const available = status.media?.available ?? false;

  return (
    <Stack gap="lg" component="section" aria-labelledby="setup-heading">
      <Title order={2} id="setup-heading">
        Set up n8Tracks
      </Title>
      <Text>This instance has not been set up yet. A few short steps, and it is ready to use.</Text>
      <Stepper active={active} allowNextStepsSelect={false} size="sm" aria-label="Setup steps">
        <Stepper.Step label="Storage" description="Data folder">
          {storageLost && (
            <Notice title="Setup was not completed">
              <Text>
                The data folder stopped being writable before the administrator was saved.
              </Text>
            </Notice>
          )}
          <StorageStep
            writable={writable}
            checking={checking}
            onRetry={onRecheck}
            onNext={() => {
              setStorageLost(false);
              setActive(MEDIA_STEP);
            }}
          />
        </Stepper.Step>
        <Stepper.Step label="Media" description="Media library">
          <MediaStep
            available={available}
            checking={checking}
            onRecheck={onRecheck}
            onBack={() => {
              setActive(STORAGE_STEP);
            }}
            onNext={() => {
              setActive(ADMINISTRATOR_STEP);
            }}
          />
        </Stepper.Step>
        {/* Never opened: the media step goes straight to the administrator. */}
        <Stepper.Step label="Backups" description="Skipped for now" />
        <Stepper.Step label="Administrator" description="Your account">
          <AdministratorStep
            onBack={() => {
              setActive(MEDIA_STEP);
            }}
            onComplete={onComplete}
            onStorageFailed={() => {
              setStorageLost(true);
              setActive(STORAGE_STEP);
              onRecheck();
            }}
          />
        </Stepper.Step>
      </Stepper>
    </Stack>
  );
}
