import {
  Button,
  Group,
  Loader,
  NumberInput,
  Paper,
  Stack,
  Switch,
  Text,
  Title,
} from '@mantine/core';
import { useState } from 'react';
import {
  describeInterval,
  MAX_SCAN_INTERVAL,
  MIN_SCAN_INTERVAL,
  saveMediaScanSchedule,
  useMediaScanSchedule,
  type MediaScanSchedule,
} from '../api/mediaScan';
import { Notice } from '../components/Notice';

const FAILED_MESSAGE = 'Check your connection and try again.';

const INTERVAL_MESSAGE = `Enter a whole number of minutes from ${String(MIN_SCAN_INTERVAL)} to ${MAX_SCAN_INTERVAL.toLocaleString('en-US')}.`;

/** The schedule being edited: the interval is what the number field holds, which may be empty text. */
interface Draft {
  enabled: boolean;
  intervalMinutes: number | string;
}

type FieldErrors = Partial<Record<keyof Draft, string>>;

type SaveNotice = 'saved' | 'conflict' | 'failed' | null;

/** The interval of `draft` when it is a whole number in range. */
function intervalOf(draft: Draft): number | undefined {
  const value = draft.intervalMinutes;
  return typeof value === 'number' &&
    Number.isInteger(value) &&
    value >= MIN_SCAN_INTERVAL &&
    value <= MAX_SCAN_INTERVAL
    ? value
    : undefined;
}

/** What the schedule is now, in words. */
function ScheduleSummary({ schedule }: { schedule: MediaScanSchedule }) {
  return (
    <Text data-testid="scan-schedule-summary">
      {schedule.enabled ? (
        <>
          Scheduled scans run <strong>{describeInterval(schedule.intervalMinutes)}</strong>, counted
          from the end of the previous scan.
        </>
      ) : (
        <>
          Scheduled scans are <strong>off</strong>. The media folder is still scanned when n8Tracks
          starts and whenever you ask.
        </>
      )}
    </Text>
  );
}

/** The schedule form once it has loaded: on or off, and the interval, saved with the schedule's revision. */
function ScheduleForm({ initial }: { initial: MediaScanSchedule }) {
  const [schedule, setSchedule] = useState(initial);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<SaveNotice>(null);
  const shown: Draft = draft ?? schedule;

  const change = (next: Draft) => {
    setDraft(next);
    setErrors({});
    setNotice(null);
  };

  const save = async () => {
    const intervalMinutes = intervalOf(shown);
    if (intervalMinutes === undefined) {
      setErrors({ intervalMinutes: INTERVAL_MESSAGE });
      return;
    }
    setSaving(true);
    setNotice(null);
    const result = await saveMediaScanSchedule(schedule.revision, {
      enabled: shown.enabled,
      intervalMinutes,
    });
    setSaving(false);
    switch (result.kind) {
      case 'saved':
        setSchedule(result.record);
        setDraft(null);
        setNotice('saved');
        return;
      case 'conflict':
        setSchedule(result.current);
        setDraft(null);
        setNotice('conflict');
        return;
      case 'invalid':
        setErrors({
          enabled: result.errors.enabled?.[0],
          intervalMinutes: result.errors.intervalMinutes?.[0],
        });
        return;
      case 'failed':
        setNotice('failed');
        return;
    }
  };

  return (
    <Stack gap="sm">
      <ScheduleSummary schedule={schedule} />
      <form
        noValidate
        aria-label="Scan schedule"
        onSubmit={(event) => {
          event.preventDefault();
          void save();
        }}
      >
        <Stack gap="sm">
          <Switch
            label="Scan on a schedule"
            checked={shown.enabled}
            disabled={saving}
            error={errors.enabled}
            onChange={(event) => {
              change({ ...shown, enabled: event.currentTarget.checked });
            }}
          />
          <NumberInput
            label="Minutes between scans"
            description={`${String(MIN_SCAN_INTERVAL)} to ${MAX_SCAN_INTERVAL.toLocaleString('en-US')}, counted from the end of the previous scan. Kept while scheduled scans are off.`}
            min={MIN_SCAN_INTERVAL}
            max={MAX_SCAN_INTERVAL}
            allowDecimal={false}
            allowNegative={false}
            clampBehavior="none"
            maw={320}
            value={shown.intervalMinutes}
            disabled={saving || !shown.enabled}
            error={errors.intervalMinutes}
            onChange={(value) => {
              change({ ...shown, intervalMinutes: value });
            }}
          />
          <Group>
            <Button type="submit" loading={saving}>
              Save schedule
            </Button>
          </Group>
          <div role="status" data-testid="scan-schedule-status">
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
    </Stack>
  );
}

/**
 * Settings → Library: how the media folder is kept in the catalog. It is scanned when n8Tracks
 * starts and then on a schedule; nothing watches the folder, so a file copied in shows up after the
 * next scan.
 */
export function LibrarySettingsPage() {
  const { state, reload } = useMediaScanSchedule();
  return (
    <Stack gap="lg">
      <Title order={2}>Library</Title>
      <Paper p="md" withBorder>
        <Stack gap="sm" component="section" aria-labelledby="scan-schedule-heading">
          <Title order={3} size="h4" id="scan-schedule-heading">
            Scanning the media folder
          </Title>
          <Text size="sm">
            n8Tracks scans the media folder when it starts and then on this schedule. Files copied
            into the folder show up after the next scan; nothing watches the folder for changes.
            Changes take effect at once.
          </Text>
          {state.phase === 'loading' && <Loader aria-label="Loading the scan schedule" size="sm" />}
          {(state.phase === 'error' || state.phase === 'not-found') && (
            <Notice title="The scan schedule could not be loaded">
              <Text>{FAILED_MESSAGE}</Text>
              <div>
                <Button variant="default" size="xs" onClick={reload}>
                  Try again
                </Button>
              </div>
            </Notice>
          )}
          {state.phase === 'ready' && <ScheduleForm initial={state.data} />}
        </Stack>
      </Paper>
    </Stack>
  );
}
