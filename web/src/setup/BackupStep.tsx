import { Button, Group, Paper, Stack, Text } from '@mantine/core';
import { useState } from 'react';
import { describeSchedule, type ScheduleSettings } from '../api/backups';
import type { SetupBackups } from '../api/setup';
import { Notice } from '../components/Notice';
import { BackupScheduleFields } from '../settings/BackupScheduleFields';
import {
  settingsOf,
  type ScheduleDraft,
  type ScheduleFieldErrors,
} from '../settings/scheduleDraft';
import { StepActions, StepHeading } from './StepParts';

export const BACKUP_SHARED_DISK_TITLE = 'Backups will share a disk with your data';

/**
 * The wizard's backup step: the schedule it will store (the defaults until the owner changes them)
 * and where backups will be written, with the shared-disk warning when no backup folder is mounted.
 * Nothing is saved here: the choice goes with the final setup submission.
 */
export function BackupStep({
  backups,
  schedule,
  errors: refused,
  onBack,
  onNext,
}: {
  backups: SetupBackups | undefined;
  schedule: ScheduleSettings;
  /** Field errors the API returned for the choice, which reopen the fields. */
  errors: ScheduleFieldErrors;
  onBack: () => void;
  onNext: (schedule: ScheduleSettings) => void;
}) {
  const [changing, setChanging] = useState(Object.keys(refused).length > 0);
  const [draft, setDraft] = useState<ScheduleDraft>(schedule);
  const [errors, setErrors] = useState<ScheduleFieldErrors>(refused);

  const next = () => {
    const { settings, errors: found } = settingsOf(draft);
    if (settings === undefined) {
      setErrors(found);
      setChanging(true);
      return;
    }
    onNext(settings);
  };

  const mount = backups?.destination === 'mount';
  return (
    <Stack gap="sm" data-testid="backup-step">
      <StepHeading>Backups</StepHeading>
      <Paper p="sm" withBorder>
        <Stack gap={4}>
          <Text>
            Scheduled backups:{' '}
            <strong data-testid="backup-step-schedule">
              {describeSchedule(settingsOf(draft).settings ?? schedule)}
            </strong>
          </Text>
          <Text size="sm" c="var(--n8-color-secondary-text)">
            Times are in the instance&apos;s time zone (the TZ setting). Older scheduled backups
            beyond the number kept are deleted after each successful one; manual backups are kept.
          </Text>
          <Text>
            Stored in:{' '}
            <strong data-testid="backup-step-location">
              {mount ? 'the backup folder (/backup)' : 'the data folder (/data/backups)'}
            </strong>
          </Text>
        </Stack>
      </Paper>
      {backups?.sharesDiskWithData === true && (
        <Notice title={BACKUP_SHARED_DISK_TITLE}>
          <Text>
            No writable backup folder is mounted at /backup, so backups will be saved in the data
            folder, on the same disk as the data they protect. Mount a folder on another disk at
            /backup to keep them apart; you can finish setup either way.
          </Text>
        </Notice>
      )}
      {changing ? (
        <BackupScheduleFields
          draft={draft}
          errors={errors}
          onChange={(changed) => {
            setDraft(changed);
            setErrors({});
          }}
        />
      ) : (
        <Group>
          <Button
            variant="default"
            onClick={() => {
              setChanging(true);
            }}
          >
            Change the schedule
          </Button>
        </Group>
      )}
      <StepActions>
        <Button variant="default" onClick={onBack}>
          Back
        </Button>
        <Button onClick={next}>Next</Button>
      </StepActions>
    </Stack>
  );
}
