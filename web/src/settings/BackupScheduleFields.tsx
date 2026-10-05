import { Group, NumberInput, Radio, Stack, Switch, TextInput } from '@mantine/core';
import { MAX_KEEP, MIN_KEEP } from '../api/backups';
import type { ScheduleDraft, ScheduleFieldErrors } from './scheduleDraft';

/**
 * The four schedule settings: on or off, daily or weekly (Sundays), the time of day in the
 * configured time zone, and how many scheduled backups are kept. Shared by the setup wizard and
 * Settings → Backups.
 */
export function BackupScheduleFields({
  draft,
  errors,
  disabled = false,
  onChange,
}: {
  draft: ScheduleDraft;
  errors: ScheduleFieldErrors;
  disabled?: boolean;
  onChange: (draft: ScheduleDraft) => void;
}) {
  const off = disabled || !draft.enabled;
  return (
    <Stack gap="sm">
      <Switch
        label="Back up on a schedule"
        checked={draft.enabled}
        disabled={disabled}
        error={errors.enabled}
        onChange={(event) => {
          onChange({ ...draft, enabled: event.currentTarget.checked });
        }}
      />
      <Radio.Group
        label="How often"
        value={draft.frequency}
        error={errors.frequency}
        onChange={(value) => {
          onChange({ ...draft, frequency: value === 'weekly' ? 'weekly' : 'daily' });
        }}
      >
        <Group gap="md" mt={4}>
          <Radio value="daily" label="Daily" disabled={off} />
          <Radio value="weekly" label="Weekly, on Sundays" disabled={off} />
        </Group>
      </Radio.Group>
      <Group gap="md" align="flex-start">
        <TextInput
          type="time"
          label="Time of day"
          description="In the instance's time zone."
          value={draft.time}
          disabled={off}
          error={errors.time}
          onChange={(event) => {
            onChange({ ...draft, time: event.currentTarget.value });
          }}
        />
        <NumberInput
          label="Scheduled backups to keep"
          description={`${String(MIN_KEEP)} to ${String(MAX_KEEP)}. Manual backups are never deleted.`}
          min={MIN_KEEP}
          max={MAX_KEEP}
          allowDecimal={false}
          allowNegative={false}
          clampBehavior="none"
          value={draft.keep}
          disabled={off}
          error={errors.keep}
          onChange={(value) => {
            onChange({ ...draft, keep: value });
          }}
        />
      </Group>
    </Stack>
  );
}
