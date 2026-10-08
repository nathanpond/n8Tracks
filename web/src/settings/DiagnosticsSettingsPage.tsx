import {
  Button,
  Group,
  Loader,
  Modal,
  NativeSelect,
  NumberInput,
  Paper,
  Stack,
  Text,
  Title,
} from '@mantine/core';
import { useState } from 'react';
import {
  describeBytes,
  levelLabel,
  MAX_MAX_MEGABYTES,
  MAX_RETENTION_DAYS,
  MIN_MAX_MEGABYTES,
  MIN_RETENTION_DAYS,
  saveLoggingSettings,
  SETTABLE_LEVELS,
  useLoggingSettings,
  type LoggingChoice,
  type LoggingSettings,
  type SettableLevel,
} from '../api/logging';
import { formatDateTime, useConfiguredTimeZone } from '../api/timeZone';
import { Notice } from '../components/Notice';

const FAILED_MESSAGE = 'Check your connection and try again.';

const RETENTION_MESSAGE = `Enter a whole number of days from ${String(MIN_RETENTION_DAYS)} to ${String(MAX_RETENTION_DAYS)}.`;

const SIZE_MESSAGE = `Enter a whole number of megabytes from ${String(MIN_MAX_MEGABYTES)} to ${MAX_MAX_MEGABYTES.toLocaleString('en-US')} (5 GB).`;

const LEVEL_OPTIONS = SETTABLE_LEVELS.map((level) => ({ value: level, label: levelLabel(level) }));

/** What the level field offers, as words. */
const LEVEL_DESCRIPTION =
  'Error writes the least; Debug the most, and switches itself back to Information after 24 hours.';

/** The settings being edited: the numbers are what the fields hold, which may be empty text. */
interface Draft {
  level: SettableLevel;
  retentionDays: number | string;
  maxMegabytes: number | string;
}

type FieldErrors = Partial<Record<keyof Draft, string>>;

type SaveNotice = 'saved' | 'conflict' | 'failed' | null;

function isSettable(level: string): level is SettableLevel {
  return (SETTABLE_LEVELS as readonly string[]).includes(level);
}

function draftOf(settings: LoggingSettings): Draft {
  return {
    level: isSettable(settings.level) ? settings.level : 'information',
    retentionDays: settings.retentionDays,
    maxMegabytes: settings.maxMegabytes,
  };
}

/** `value` when it is a whole number from `min` to `max`. */
function wholeNumber(value: number | string, min: number, max: number): number | undefined {
  return typeof value === 'number' && Number.isInteger(value) && value >= min && value <= max
    ? value
    : undefined;
}

/** What is in effect now, in words. */
function LoggingSummary({ settings, timeZone }: { settings: LoggingSettings; timeZone: string }) {
  return (
    <Stack gap={4} data-testid="logging-summary">
      <Text>
        Logging at <strong>{levelLabel(settings.level)}</strong>
        {settings.levelSource === 'environment'
          ? ', set by N8TRACKS_LOG_LEVEL until you save a level here.'
          : '.'}
      </Text>
      {settings.debugUntil !== null && (
        <Text data-testid="debug-until">
          Debug switches back to Information on{' '}
          <strong>{formatDateTime(settings.debugUntil, timeZone)}</strong>.
        </Text>
      )}
      <Text>
        Log files are kept for{' '}
        <strong>{settings.retentionDays.toLocaleString('en-US')} days</strong> and take at most{' '}
        <strong>{settings.maxMegabytes.toLocaleString('en-US')} MB</strong> together.
      </Text>
    </Stack>
  );
}

/** Asks before saving limits that delete log files. */
function ConfirmDeletion({
  deletion,
  saving,
  onCancel,
  onConfirm,
}: {
  deletion: { files: number; bytes: number } | null;
  saving: boolean;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  return (
    <Modal
      opened={deletion !== null}
      onClose={onCancel}
      title="Delete log files?"
      centered
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <Stack gap="md">
        <Text data-testid="deletion-summary">
          These limits delete{' '}
          <strong>
            {deletion?.files === 1 ? '1 log file' : `${String(deletion?.files ?? 0)} log files`} (
            {describeBytes(deletion?.bytes ?? 0)})
          </strong>{' '}
          now. This cannot be undone.
        </Text>
        <Group justify="end">
          <Button variant="default" onClick={onCancel} data-autofocus>
            Cancel
          </Button>
          <Button color="red" loading={saving} onClick={onConfirm}>
            Delete and save
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** The form once the settings have loaded, saved with their revision. */
function LoggingForm({ initial }: { initial: LoggingSettings }) {
  const timeZone = useConfiguredTimeZone();
  const [settings, setSettings] = useState(initial);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<SaveNotice>(null);
  const [deletion, setDeletion] = useState<{ files: number; bytes: number } | null>(null);
  const shown: Draft = draft ?? draftOf(settings);

  const change = (next: Draft) => {
    setDraft(next);
    setErrors({});
    setNotice(null);
  };

  const choice = (): LoggingChoice | undefined => {
    const retentionDays = wholeNumber(shown.retentionDays, MIN_RETENTION_DAYS, MAX_RETENTION_DAYS);
    const maxMegabytes = wholeNumber(shown.maxMegabytes, MIN_MAX_MEGABYTES, MAX_MAX_MEGABYTES);
    if (retentionDays === undefined || maxMegabytes === undefined) {
      setErrors({
        retentionDays: retentionDays === undefined ? RETENTION_MESSAGE : undefined,
        maxMegabytes: maxMegabytes === undefined ? SIZE_MESSAGE : undefined,
      });
      return undefined;
    }
    return { level: shown.level, retentionDays, maxMegabytes };
  };

  const save = async (confirmDelete: boolean) => {
    const chosen = choice();
    if (chosen === undefined) {
      return;
    }
    setSaving(true);
    setNotice(null);
    const result = await saveLoggingSettings(settings.revision, chosen, confirmDelete);
    setSaving(false);
    switch (result.kind) {
      case 'saved':
        setSettings(result.record);
        setDraft(null);
        setDeletion(null);
        setNotice('saved');
        return;
      case 'confirm':
        setDeletion({ files: result.files, bytes: result.bytes });
        return;
      case 'conflict':
        setSettings(result.current);
        setDraft(null);
        setDeletion(null);
        setNotice('conflict');
        return;
      case 'invalid':
        setDeletion(null);
        setErrors({
          level: result.errors.level?.[0],
          retentionDays: result.errors.retentionDays?.[0],
          maxMegabytes: result.errors.maxMegabytes?.[0],
        });
        return;
      case 'failed':
        setDeletion(null);
        setNotice('failed');
        return;
    }
  };

  return (
    <Stack gap="sm">
      {settings.folderProblem !== null && (
        <Notice title="Log files are not being written">
          <Text data-testid="log-folder-problem">{settings.folderProblem}</Text>
          <Text size="sm">n8Tracks tries the folder again every hour.</Text>
        </Notice>
      )}
      <LoggingSummary settings={settings} timeZone={timeZone} />
      <form
        noValidate
        aria-label="Log settings"
        onSubmit={(event) => {
          event.preventDefault();
          void save(false);
        }}
      >
        <Stack gap="sm">
          <NativeSelect
            label="Log level"
            description={LEVEL_DESCRIPTION}
            data={LEVEL_OPTIONS}
            value={shown.level}
            disabled={saving}
            error={errors.level}
            maw={320}
            onChange={(event) => {
              const value = event.currentTarget.value;
              if (isSettable(value)) {
                change({ ...shown, level: value });
              }
            }}
          />
          <NumberInput
            label="Days to keep log files"
            description={`${String(MIN_RETENTION_DAYS)} to ${String(MAX_RETENTION_DAYS)}, counted in UTC by the date in each file's name.`}
            min={MIN_RETENTION_DAYS}
            max={MAX_RETENTION_DAYS}
            allowDecimal={false}
            allowNegative={false}
            clampBehavior="none"
            maw={320}
            value={shown.retentionDays}
            disabled={saving}
            error={errors.retentionDays}
            onChange={(value) => {
              change({ ...shown, retentionDays: value });
            }}
          />
          <NumberInput
            label="Most space for log files (MB)"
            description={`${String(MIN_MAX_MEGABYTES)} MB to ${MAX_MAX_MEGABYTES.toLocaleString('en-US')} MB (5 GB). The oldest files are deleted first.`}
            min={MIN_MAX_MEGABYTES}
            max={MAX_MAX_MEGABYTES}
            allowDecimal={false}
            allowNegative={false}
            clampBehavior="none"
            thousandSeparator=","
            maw={320}
            value={shown.maxMegabytes}
            disabled={saving}
            error={errors.maxMegabytes}
            onChange={(value) => {
              change({ ...shown, maxMegabytes: value });
            }}
          />
          <Group>
            <Button type="submit" loading={saving && deletion === null}>
              Save log settings
            </Button>
          </Group>
          <div role="status" data-testid="logging-status">
            {notice === 'saved' && <Text>Log settings saved.</Text>}
            {notice === 'conflict' && (
              <Notice title="The log settings were changed elsewhere">
                <Text>
                  Your change was not saved. These are the settings now; change them again.
                </Text>
              </Notice>
            )}
            {notice === 'failed' && (
              <Notice title="The log settings were not saved">
                <Text>{FAILED_MESSAGE}</Text>
              </Notice>
            )}
          </div>
        </Stack>
      </form>
      <ConfirmDeletion
        deletion={deletion}
        saving={saving}
        onCancel={() => {
          setDeletion(null);
        }}
        onConfirm={() => {
          void save(true);
        }}
      />
    </Stack>
  );
}

/**
 * Settings → Diagnostics: how much n8Tracks logs and how long its log files are kept (#234). The
 * level applies to standard output and the files alike, and takes effect at once.
 */
export function DiagnosticsSettingsPage() {
  const { state, reload } = useLoggingSettings();
  return (
    <Stack gap="lg">
      <Title order={2}>Diagnostics</Title>
      <Paper p="md" withBorder>
        <Stack gap="sm" component="section" aria-labelledby="logging-heading">
          <Title order={3} size="h4" id="logging-heading">
            Logs
          </Title>
          <Text size="sm">
            n8Tracks writes its log to the container&apos;s output and to files in the logs folder
            of the data folder. Passwords, tokens, cookies, lyrics, prompts, and Suno&apos;s raw
            answers are never written, at any level. Log files are not part of backups.
          </Text>
          {state.phase === 'loading' && <Loader aria-label="Loading the log settings" size="sm" />}
          {(state.phase === 'error' || state.phase === 'not-found') && (
            <Notice title="The log settings could not be loaded">
              <Text>{FAILED_MESSAGE}</Text>
              <div>
                <Button variant="default" size="xs" onClick={reload}>
                  Try again
                </Button>
              </div>
            </Notice>
          )}
          {state.phase === 'ready' && <LoggingForm initial={state.data} />}
        </Stack>
      </Paper>
    </Stack>
  );
}
