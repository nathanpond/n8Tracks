import { Button, Group, Loader, Paper, Stack, Text, Title } from '@mantine/core';
import { useState } from 'react';
import {
  describeSchedule,
  saveBackupSchedule,
  useBackupSchedule,
  type BackupScheduleRecord,
  type BackupScheduleStatus,
} from '../api/backups';
import { formatDateTime } from '../api/timeZone';
import { Notice } from '../components/Notice';
import { BackupScheduleFields } from './BackupScheduleFields';
import {
  apiFieldErrors,
  settingsOf,
  type ScheduleDraft,
  type ScheduleFieldErrors,
} from './scheduleDraft';

const FAILED_MESSAGE = 'Check your connection and try again.';

function draftOf(record: BackupScheduleRecord): ScheduleDraft {
  return {
    enabled: record.enabled,
    frequency: record.frequency,
    time: record.time,
    keep: record.keep,
  };
}

/** The last successful backup, the next planned one, and how the latest scheduled attempt went. */
export function ScheduleStatus({
  status,
  lastSuccessAt,
  timeZone,
}: {
  status: BackupScheduleStatus;
  lastSuccessAt: string | null;
  timeZone: string;
}) {
  const attempt = status.lastAttempt;
  const at = (utc: string) => formatDateTime(utc, timeZone);
  return (
    <Stack gap={4} data-testid="backup-schedule-status">
      <Text>
        Schedule: <strong>{describeSchedule(status)}</strong>
      </Text>
      <Text>
        Last successful backup:{' '}
        <strong data-testid="last-success">{lastSuccessAt ? at(lastSuccessAt) : 'none yet'}</strong>
      </Text>
      {status.nextAt !== null && (
        <Text>
          Next planned backup: <strong data-testid="next-planned">{at(status.nextAt)}</strong>
        </Text>
      )}
      {attempt?.outcome === 'running' && (
        <Text>A scheduled backup is running (started {at(attempt.startedAt)}).</Text>
      )}
      {attempt?.outcome === 'succeeded' && (
        <Text>
          The latest scheduled backup succeeded ({at(attempt.finishedAt ?? attempt.startedAt)}).
        </Text>
      )}
      {attempt?.outcome === 'failed' && (
        <Notice title="The latest scheduled backup failed">
          <Text>
            It started {at(attempt.startedAt)}: {attempt.error ?? 'no reason was recorded'}.
          </Text>
          <Text>
            {attempt.retryAt !== null
              ? `It is tried once more at ${at(attempt.retryAt)}.`
              : 'The next attempt is at the next planned time.'}{' '}
            Earlier backups were left as they were.
          </Text>
        </Notice>
      )}
      <Text size="sm" c="var(--n8-color-secondary-text)">
        Times are in {timeZone}.
      </Text>
    </Stack>
  );
}

type SaveNotice = 'saved' | 'conflict' | 'failed' | null;

/** The schedule form: loads the schedule with its revision and saves it with that revision. */
function ScheduleForm({ onSaved }: { onSaved: () => void }) {
  const { state, reload, replace } = useBackupSchedule();
  const [draft, setDraft] = useState<ScheduleDraft | null>(null);
  const [errors, setErrors] = useState<ScheduleFieldErrors>({});
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<SaveNotice>(null);

  if (state.phase === 'loading') {
    return <Loader aria-label="Loading the schedule" size="sm" />;
  }
  if (state.phase === 'error') {
    return (
      <Notice title="The schedule could not be loaded">
        <Text>{FAILED_MESSAGE}</Text>
        <div>
          <Button variant="default" size="xs" onClick={reload}>
            Try again
          </Button>
        </div>
      </Notice>
    );
  }

  const record = state.record;
  const shown = draft ?? draftOf(record);

  const save = async () => {
    const { settings, errors: found } = settingsOf(shown);
    if (settings === undefined) {
      setErrors(found);
      return;
    }
    setSaving(true);
    setNotice(null);
    const result = await saveBackupSchedule(record.revision, settings);
    setSaving(false);
    switch (result.kind) {
      case 'saved':
        replace(result.record);
        setDraft(null);
        setNotice('saved');
        onSaved();
        return;
      case 'conflict':
        replace(result.current);
        setDraft(null);
        setNotice('conflict');
        onSaved();
        return;
      case 'invalid':
        setErrors(apiFieldErrors(result.errors));
        return;
      case 'failed':
        setNotice('failed');
        return;
    }
  };

  return (
    <form
      noValidate
      aria-label="Backup schedule"
      onSubmit={(event) => {
        event.preventDefault();
        void save();
      }}
    >
      <Stack gap="sm">
        <BackupScheduleFields
          draft={shown}
          errors={errors}
          disabled={saving}
          onChange={(changed) => {
            setDraft(changed);
            setErrors({});
            setNotice(null);
          }}
        />
        <Group>
          <Button type="submit" loading={saving}>
            Save schedule
          </Button>
        </Group>
        <div role="status" data-testid="schedule-save-status">
          {notice === 'saved' && <Text>Schedule saved.</Text>}
          {notice === 'conflict' && (
            <Notice title="The schedule was changed elsewhere">
              <Text>Your change was not saved. This is the schedule now; change it again.</Text>
            </Notice>
          )}
          {notice === 'failed' && (
            <Notice title="The schedule was not saved">
              <Text>{FAILED_MESSAGE}</Text>
            </Notice>
          )}
        </div>
      </Stack>
    </form>
  );
}

/**
 * The Backups page's Schedule section: what the schedule is and how it is going, then the form
 * to change it. A change takes effect from the next planned time; a lower number kept applies at
 * the next successful scheduled backup.
 */
export function BackupSchedulePanel({
  status,
  lastSuccessAt,
  timeZone,
  onSaved,
}: {
  status: BackupScheduleStatus | null;
  lastSuccessAt: string | null;
  timeZone: string;
  onSaved: () => void;
}) {
  return (
    <Stack gap="sm" component="section" aria-labelledby="backup-schedule-heading">
      <Title order={3} id="backup-schedule-heading">
        Schedule
      </Title>
      {status !== null && (
        <Paper p="sm" withBorder>
          <ScheduleStatus status={status} lastSuccessAt={lastSuccessAt} timeZone={timeZone} />
        </Paper>
      )}
      <ScheduleForm onSaved={onSaved} />
    </Stack>
  );
}
