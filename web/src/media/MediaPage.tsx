import {
  Anchor,
  Badge,
  Button,
  Group,
  Loader,
  Paper,
  Progress,
  Stack,
  Table,
  Text,
  Title,
} from '@mantine/core';
import { useCallback, useState, type ReactNode } from 'react';
import { Link } from 'react-router';
import { UNMATCHED_PATH } from '../api/audioFiles';
import {
  isActive,
  startScan,
  useMediaStatus,
  useScanJob,
  type MediaStatus,
  type ScanCounts,
  type ScanJob,
  type ScanReport,
} from '../api/media';
import { formatDateTime, useConfiguredTimeZone } from '../api/timeZone';
import { Notice } from '../components/Notice';
import {
  countText,
  durationText,
  failureText,
  finishedText,
  isIndeterminate,
  majorityMissingText,
  progressText,
  SCAN_COUNT_LABELS,
  triggerText,
} from './mediaRules';

const FAILED_MESSAGE = 'Check your connection and try again.';

/** A section of the page: a bordered box with its heading. */
function Section({ id, title, children }: { id: string; title: string; children: ReactNode }) {
  return (
    <Paper p="md" withBorder>
      <Stack gap="sm" component="section" aria-labelledby={id}>
        <Title order={3} size="h4" id={id}>
          {title}
        </Title>
        {children}
      </Stack>
    </Paper>
  );
}

/** Whether the media folder can be read, since when, and where it is (in the container). */
function FolderSection({ status, timeZone }: { status: MediaStatus; timeZone: string }) {
  const { mount } = status;
  const available = mount.state === 'available';
  return (
    <Section id="media-folder-heading" title="Media folder">
      <Group gap="xs">
        {/* The theme's own badge variants, which meet contrast in both schemes (#208's e2e). */}
        <Badge
          variant={available ? 'default' : 'filled'}
          radius="sm"
          tt="none"
          data-testid="media-folder-state"
          data-state={mount.state}
        >
          {available ? 'Available' : 'Unavailable'}
        </Badge>
        {mount.since !== null && (
          <Text size="sm" data-testid="media-folder-since">
            since {formatDateTime(mount.since, timeZone)}
          </Text>
        )}
      </Group>
      <Text size="sm">
        Folder: <code data-testid="media-folder-path">{mount.path}</code>
      </Text>
      {!available && (
        <div data-testid="media-unavailable">
          <Notice title="n8Tracks cannot read the media folder">
            <Text>
              Your audio files and their associations are kept: they come back as they were once the
              folder can be read again. Check that the folder is mounted at {mount.path}, then scan
              the library to try again.
            </Text>
          </Notice>
        </div>
      )}
    </Section>
  );
}

/** One row of a two-column count table; the count is a link when `to` names where the files are listed. */
function CountRow({
  label,
  value,
  testId,
  to,
}: {
  label: string;
  value: number;
  testId: string;
  to?: string;
}) {
  return (
    <Table.Tr>
      <Table.Th scope="row" fw={400}>
        {label}
      </Table.Th>
      <Table.Td ta="right" data-testid={testId}>
        {to === undefined ? (
          countText(value)
        ) : (
          <Anchor
            component={Link}
            to={to}
            underline="always"
            aria-label={`${countText(value)} unmatched: open Unmatched Files`}
          >
            {countText(value)}
          </Anchor>
        )}
      </Table.Td>
    </Table.Tr>
  );
}

/** The cataloged audio files by status and by association. */
function FilesSection({ status }: { status: MediaStatus }) {
  const { counts } = status;
  return (
    <Section id="media-files-heading" title="Audio files">
      <Text size="sm">
        {countText(counts.total)} {counts.total === 1 ? 'file' : 'files'} in the catalog. Associated
        and Unmatched include Missing files.
      </Text>
      <Table maw={360} withRowBorders={false} aria-labelledby="media-files-heading">
        <Table.Tbody>
          <CountRow label="Available" value={counts.available} testId="files-available" />
          <CountRow label="Missing" value={counts.missing} testId="files-missing" />
          <CountRow label="Associated" value={counts.associated} testId="files-associated" />
          <CountRow
            label="Unmatched"
            value={counts.unmatched}
            testId="files-unmatched"
            to={UNMATCHED_PATH}
          />
        </Table.Tbody>
      </Table>
    </Section>
  );
}

/** What a scan counted. */
function ScanCountsTable({ counts, label }: { counts: ScanCounts; label: string }) {
  return (
    <Table maw={360} withRowBorders={false} aria-label={label} data-testid="scan-counts">
      <Table.Tbody>
        {SCAN_COUNT_LABELS.map(([key, text]) => (
          <CountRow key={key} label={text} value={counts[key]} testId={`scan-count-${key}`} />
        ))}
      </Table.Tbody>
    </Table>
  );
}

/** When a scan finished, how long it took, and what started it. */
function ScanTimes({ report, timeZone }: { report: ScanReport; timeZone: string }) {
  const trigger = triggerText(report.trigger);
  return (
    <Text size="sm" data-testid="scan-finished">
      Finished {formatDateTime(report.finishedAt, timeZone)}, after{' '}
      {durationText(report.durationSeconds)}.{trigger !== null ? ` ${trigger}.` : ''}
    </Text>
  );
}

/** The last scan: when, how long, what it counted; a failed one says why over the counts before it. */
function LastScanSection({ status, timeZone }: { status: MediaStatus; timeZone: string }) {
  const last = status.lastScan;
  const successful = status.lastSuccessfulScan;
  if (last === null) {
    return (
      <Section id="last-scan-heading" title="Last scan">
        <Text data-testid="not-scanned">Not scanned yet.</Text>
      </Section>
    );
  }

  const failure = failureText(last);
  return (
    <Section id="last-scan-heading" title="Last scan">
      <ScanTimes report={last} timeZone={timeZone} />
      {failure !== null && (
        <div data-testid="scan-failed">
          <Notice title="The last scan did not finish">
            <Text>{failure}</Text>
          </Notice>
        </div>
      )}
      {failure === null && last.counts !== null && (
        <ScanCountsTable counts={last.counts} label="What the last scan counted" />
      )}
      {failure !== null &&
        (successful !== null && successful.counts !== null ? (
          <Stack gap={4} data-testid="previous-scan">
            <Text size="sm">
              The last scan that finished, on {formatDateTime(successful.finishedAt, timeZone)},
              counted:
            </Text>
            <ScanCountsTable
              counts={successful.counts}
              label="What the last successful scan counted"
            />
          </Stack>
        ) : (
          <Text size="sm" data-testid="no-previous-scan">
            No scan has finished yet.
          </Text>
        ))}
    </Section>
  );
}

/** The bar and the job's message while a scan is queued or running. */
function ScanProgress({ job }: { job: ScanJob | null }) {
  const indeterminate = isIndeterminate(job);
  const text = progressText(job);
  const progress = job?.progress ?? 0;
  return (
    <Stack gap={4} data-testid="scan-progress">
      <Progress.Root size="lg">
        {/* Mantine's own ARIA always states a value; an indeterminate bar must not, so it is written here. */}
        <Progress.Section
          value={indeterminate ? 100 : progress}
          striped={indeterminate}
          animated={indeterminate}
          withAria={false}
          role="progressbar"
          aria-label="Scan progress"
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={indeterminate ? undefined : progress}
          aria-valuetext={indeterminate ? text : `${String(progress)}%: ${text}`}
        />
      </Progress.Root>
      <Text size="sm" data-testid="scan-progress-text">
        {text}
      </Text>
    </Stack>
  );
}

/** When the next scheduled scan is due, or that they are off, with the link to change it. */
function ScheduleText({
  status,
  readAt,
  timeZone,
}: {
  status: MediaStatus;
  readAt: number;
  timeZone: string;
}) {
  const next = status.nextScheduledScan;
  const settings = (
    <Anchor component={Link} to="/settings/library" underline="always">
      Settings → Library
    </Anchor>
  );
  if (!status.schedule.enabled || next === null) {
    return (
      <Text size="sm" data-testid="next-scheduled-scan" data-scheduled="off">
        Scheduled scans are off. Turn them on in {settings}.
      </Text>
    );
  }
  const due = new Date(next).getTime() <= readAt;
  return (
    <Text size="sm" data-testid="next-scheduled-scan" data-scheduled="on">
      {due
        ? 'A scheduled scan is due now.'
        : `The next scheduled scan is at ${formatDateTime(next, timeZone)}.`}
      {status.mount.state === 'unavailable'
        ? ' Scheduled scans wait until the media folder can be read.'
        : ''}{' '}
      Change the schedule in {settings}.
    </Text>
  );
}

/**
 * Library → Media (#208): whether n8Tracks can read the media folder, the audio files by status and
 * association, the last scan, Scan Library with live progress, and when the next scheduled scan is
 * due. The status is read every 10 seconds, and every second while a scan is queued or running, so a
 * scheduled scan shows up too; the scan's job is followed every second until it ends.
 */
export function MediaPage() {
  const timeZone = useConfiguredTimeZone();
  // The scan this page started; otherwise the page follows the one the status says is in progress.
  const [started, setStarted] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);
  const [startFailed, setStartFailed] = useState(false);
  const [announcement, setAnnouncement] = useState('');
  const { state, reload } = useMediaStatus(started !== null || starting);
  const status = state.phase === 'ready' ? state.data : null;
  const jobId = started ?? status?.activeScanJobId ?? null;

  const onFinished = useCallback(
    (finished: ScanJob | null) => {
      setAnnouncement(finishedText(finished));
      setStarted(null);
      reload();
    },
    [reload],
  );
  const { job, gone } = useScanJob(jobId, onFinished);
  const scanning = jobId !== null && !gone && (job === null || isActive(job));

  const scan = async () => {
    setStarting(true);
    setStartFailed(false);
    const result = await startScan();
    setStarting(false);
    if (result.kind === 'started') {
      setAnnouncement('The scan has started.');
      setStarted(result.jobId);
    } else {
      setStartFailed(true);
    }
  };

  return (
    <Stack gap="lg">
      <Title order={2}>Media</Title>
      {state.phase === 'loading' && <Loader aria-label="Loading the media library" size="sm" />}
      {state.phase === 'error' && (
        <Notice title="The media library could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <div>
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
          </div>
        </Notice>
      )}
      {state.phase === 'ready' && status !== null && (
        <>
          {state.stale && (
            <Text size="sm" data-testid="status-stale">
              The media library could not be read just now; this is what it was last time.
            </Text>
          )}
          {status.majorityMissingWarning && (
            <div data-testid="majority-missing-warning">
              <Notice title="Most files went missing in the last scan">
                <Text>
                  {majorityMissingText(
                    status.lastSuccessfulScan?.counts ?? null,
                    status.mount.path,
                  )}
                </Text>
              </Notice>
            </div>
          )}
          <FolderSection status={status} timeZone={timeZone} />
          <Section id="scan-heading" title="Scanning">
            <Text size="sm">
              Scanning reads the media folder and catalogs its audio files. Nothing in the folder is
              ever changed.
            </Text>
            <Group>
              <Button
                onClick={() => {
                  void scan();
                }}
                loading={starting}
                disabled={starting || scanning}
              >
                Scan Library
              </Button>
            </Group>
            {scanning && <ScanProgress job={job} />}
            {startFailed && (
              <Notice title="The scan was not started">
                <Text>{FAILED_MESSAGE}</Text>
              </Notice>
            )}
            <div role="status" aria-live="polite" data-testid="scan-announcement">
              {announcement}
            </div>
            <ScheduleText status={status} readAt={state.readAt} timeZone={timeZone} />
          </Section>
          <FilesSection status={status} />
          <LastScanSection status={status} timeZone={timeZone} />
        </>
      )}
    </Stack>
  );
}
