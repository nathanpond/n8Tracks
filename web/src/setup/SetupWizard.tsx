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
import { useState, type SyntheticEvent } from 'react';
import { DEFAULT_SCHEDULE, type ScheduleSettings } from '../api/backups';
import { submitSetup, type SetupStatus } from '../api/setup';
import { CheckBadge } from '../components/CheckBadge';
import { Notice } from '../components/Notice';
import { apiFieldErrors, type ScheduleFieldErrors } from '../settings/scheduleDraft';
import { BackupStep } from './BackupStep';
import { StepActions, StepHeading } from './StepParts';

const STORAGE_STEP = 0;
const MEDIA_STEP = 1;
const BACKUP_STEP = 2;
const ADMINISTRATOR_STEP = 3;

/** The prefix the API puts before the backup schedule's field names in a refused submission. */
const BACKUP_FIELD_PREFIX = 'backupSchedule.';

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
  schedule,
  onBack,
  onComplete,
  onStorageFailed,
  onScheduleRefused,
}: {
  /** The backup step's choice, sent with the administrator. */
  schedule: ScheduleSettings;
  onBack: () => void;
  onComplete: () => void;
  onStorageFailed: () => void;
  /** The API refused the backup step's choice: back to that step with its errors. */
  onScheduleRefused: (errors: ScheduleFieldErrors) => void;
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
    const result = await submitSetup({ ...values, backupSchedule: schedule });
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
      case 'invalid': {
        const scheduleErrors = apiFieldErrors(result.errors, BACKUP_FIELD_PREFIX);
        if (Object.keys(scheduleErrors).length > 0) {
          onScheduleRefused(scheduleErrors);
        }
        setErrors({
          username: result.errors.username?.join(' '),
          password: result.errors.password?.join(' '),
          passwordConfirmation: result.errors.passwordConfirmation?.join(' '),
        });
        return;
      }
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
 * First-run setup: storage, media, the backup schedule, then the administrator, whose creation
 * completes setup. Storage must be writable to go on; unavailable media is a warning. The backup
 * step's choice is held here and sent with the administrator, so nothing is saved before then.
 */
export function SetupWizard({ status, checking, onRecheck, onComplete }: SetupWizardProps) {
  const [active, setActive] = useState(STORAGE_STEP);
  const [storageLost, setStorageLost] = useState(false);
  const [schedule, setSchedule] = useState<ScheduleSettings>(
    status.backups?.defaults ?? DEFAULT_SCHEDULE,
  );
  const [scheduleRefused, setScheduleRefused] = useState<ScheduleFieldErrors>({});
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
              setActive(BACKUP_STEP);
            }}
          />
        </Stepper.Step>
        <Stepper.Step label="Backups" description="Schedule">
          <BackupStep
            backups={status.backups}
            schedule={schedule}
            errors={scheduleRefused}
            onBack={() => {
              setActive(MEDIA_STEP);
            }}
            onNext={(chosen) => {
              setSchedule(chosen);
              setScheduleRefused({});
              setActive(ADMINISTRATOR_STEP);
            }}
          />
        </Stepper.Step>
        <Stepper.Step label="Administrator" description="Your account">
          <AdministratorStep
            schedule={schedule}
            onBack={() => {
              setActive(BACKUP_STEP);
            }}
            onScheduleRefused={(errors) => {
              setScheduleRefused(errors);
              setActive(BACKUP_STEP);
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
