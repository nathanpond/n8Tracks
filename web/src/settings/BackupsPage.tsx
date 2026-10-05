import {
  Button,
  Group,
  Loader,
  Modal,
  Paper,
  Progress,
  Stack,
  Table,
  Text,
  Title,
} from '@mantine/core';
import { useCallback, useEffect, useRef, useState } from 'react';
import {
  deleteBackup,
  downloadUrl,
  formatSize,
  startBackup,
  useBackupJob,
  useBackups,
  type Backup,
  type BackupJob,
  type BackupLocation,
  type BackupStatus,
} from '../api/backups';
import { formatDateTime, useConfiguredTimeZone } from '../api/timeZone';
import { Notice } from '../components/Notice';
import { BackupSchedulePanel } from './BackupSchedulePanel';

const FAILED_MESSAGE = 'Check your connection and try again.';

export const SHARED_DISK_TITLE = 'Backups share a disk with your data';

const LOCATION_LABELS: Record<BackupLocation, string> = {
  mount: 'Backup folder',
  data: 'Data folder',
};

const KIND_LABELS: Record<string, string> = {
  manual: 'Manual',
  scheduled: 'Scheduled',
  safety: 'Safety',
};

const STATUS_LABELS: Record<BackupStatus, string> = {
  valid: 'Valid',
  newer: 'Made by a newer version',
  invalid: 'Not a valid backup',
};

/** What the last "Back up now" came to, shown under the button. */
type Outcome =
  | { kind: 'succeeded'; name: string | null }
  | { kind: 'failed'; error: string }
  | { kind: 'already' }
  | { kind: 'start-failed' };

function resultName(job: BackupJob): string | null {
  const result: unknown = job.result;
  return typeof result === 'object' &&
    result !== null &&
    'name' in result &&
    typeof result.name === 'string'
    ? result.name
    : null;
}

/** The progress of the backup being followed, then its outcome. */
function BackupProgress({ job, outcome }: { job: BackupJob | null; outcome: Outcome | null }) {
  const active = job !== null && (job.status === 'queued' || job.status === 'running');
  return (
    <div role="status" data-testid="backup-status">
      <Stack gap="xs">
        {outcome?.kind === 'already' && <Text>A backup is already in progress.</Text>}
        {active && (
          <>
            <Text>
              {job.status === 'queued' ? 'Waiting to start' : (job.message ?? 'Starting')}…{' '}
              {job.progress}%
            </Text>
            <Progress value={job.progress} aria-label="Backup progress" />
          </>
        )}
        {outcome?.kind === 'succeeded' && (
          <Text>
            Backup finished{outcome.name ? `: ${outcome.name}` : ''}. It was verified before it was
            listed.
          </Text>
        )}
        {outcome?.kind === 'failed' && (
          <Notice title="Backup failed">
            <Text>{outcome.error}</Text>
          </Notice>
        )}
        {outcome?.kind === 'start-failed' && (
          <Notice title="The backup could not be started">
            <Text>{FAILED_MESSAGE}</Text>
          </Notice>
        )}
      </Stack>
    </div>
  );
}

/** Asks before deleting an archive: it cannot be undone. */
function DeleteDialog({
  backup,
  onClose,
  onChanged,
}: {
  backup: Backup | undefined;
  onClose: () => void;
  onChanged: () => void;
}) {
  const [failed, setFailed] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  const remove = async () => {
    if (!backup) {
      return;
    }
    setSubmitting(true);
    setFailed(false);
    const result = await deleteBackup(backup);
    setSubmitting(false);
    if (result === 'failed') {
      setFailed(true);
    } else {
      onChanged();
      onClose();
    }
  };

  return (
    <Modal
      opened={backup !== undefined}
      onClose={onClose}
      title="Delete backup?"
      centered
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <Stack gap="md">
        <Text>
          <strong>{backup?.name}</strong> is deleted from the{' '}
          {backup ? LOCATION_LABELS[backup.location].toLowerCase() : ''}. This cannot be undone.
        </Text>
        <div role="status">
          {failed && (
            <Notice title="Backup not deleted">
              <Text>{FAILED_MESSAGE}</Text>
            </Notice>
          )}
        </div>
        <Group justify="end">
          <Button variant="default" onClick={onClose} data-autofocus>
            Cancel
          </Button>
          <Button
            color="red"
            loading={submitting}
            onClick={() => {
              void remove();
            }}
          >
            Delete backup
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

function BackupTable({
  backups,
  timeZone,
  onDelete,
}: {
  backups: Backup[];
  timeZone: string;
  onDelete: (backup: Backup) => void;
}) {
  return (
    <Table.ScrollContainer minWidth={720}>
      <Table withTableBorder aria-label="Backups">
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col">Created</Table.Th>
            <Table.Th scope="col">Size</Table.Th>
            <Table.Th scope="col">Version</Table.Th>
            <Table.Th scope="col">Stored in</Table.Th>
            <Table.Th scope="col">Status</Table.Th>
            <Table.Th scope="col">Actions</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {backups.map((backup) => (
            <Table.Tr
              key={`${backup.location}/${backup.name}`}
              data-backup={backup.name}
              data-location={backup.location}
            >
              <Table.Th scope="row">
                <Stack gap={0}>
                  <Text size="sm">{formatDateTime(backup.createdAt, timeZone)}</Text>
                  <Text size="xs" c="var(--n8-color-secondary-text)" data-testid="backup-kind">
                    {backup.kind === null
                      ? 'Unknown kind'
                      : (KIND_LABELS[backup.kind] ?? backup.kind)}
                  </Text>
                  <Text size="xs" c="var(--n8-color-secondary-text)">
                    {backup.name}
                  </Text>
                </Stack>
              </Table.Th>
              <Table.Td>{formatSize(backup.size)}</Table.Td>
              <Table.Td>{backup.applicationVersion ?? '—'}</Table.Td>
              <Table.Td>{LOCATION_LABELS[backup.location]}</Table.Td>
              <Table.Td>{STATUS_LABELS[backup.status]}</Table.Td>
              <Table.Td>
                <Group gap="xs" wrap="nowrap">
                  {backup.status !== 'invalid' && (
                    <Button
                      component="a"
                      href={downloadUrl(backup)}
                      download={backup.name}
                      size="xs"
                      variant="default"
                      aria-label={`Download ${backup.name}`}
                    >
                      Download
                    </Button>
                  )}
                  <Button
                    size="xs"
                    variant="default"
                    color="red"
                    onClick={() => {
                      onDelete(backup);
                    }}
                    aria-label={`Delete ${backup.name}`}
                  >
                    Delete
                  </Button>
                </Group>
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

/**
 * Settings → Backups: back up the whole instance now, follow the job, see and change the schedule,
 * and download or delete the archives in the backup folder and the data folder. A backup still
 * being written is not listed.
 */
export function BackupsPage() {
  const { state, reload } = useBackups();
  const timeZone = useConfiguredTimeZone();
  const [jobId, setJobId] = useState<string | null>(null);
  const [outcome, setOutcome] = useState<Outcome | null>(null);
  const [starting, setStarting] = useState(false);
  const [deleting, setDeleting] = useState<Backup | undefined>();
  const followed = useRef(new Set<string>());

  const follow = useCallback((id: string) => {
    followed.current.add(id);
    setJobId(id);
  }, []);

  // A backup already queued or running when the page opens (or started elsewhere) is followed too.
  const activeJobId = state.phase === 'ready' ? state.list.activeJobId : null;
  useEffect(() => {
    if (activeJobId !== null && jobId === null && !followed.current.has(activeJobId)) {
      follow(activeJobId);
    }
  }, [activeJobId, jobId, follow]);

  const onFinished = useCallback(
    (job: BackupJob) => {
      setOutcome(
        job.status === 'succeeded'
          ? { kind: 'succeeded', name: resultName(job) }
          : { kind: 'failed', error: job.error ?? 'The backup failed.' },
      );
      setJobId(null);
      reload();
    },
    [reload],
  );
  const job = useBackupJob(jobId, onFinished);

  const start = async () => {
    setStarting(true);
    setOutcome(null);
    const result = await startBackup();
    setStarting(false);
    if (result.kind === 'failed') {
      setOutcome({ kind: 'start-failed' });
      return;
    }
    if (result.kind === 'in-progress') {
      setOutcome({ kind: 'already' });
    }
    follow(result.jobId);
  };

  return (
    <Stack gap="lg">
      <Title order={2}>Backups</Title>
      <Text>
        A backup is one archive of everything n8Tracks keeps: the database, with your account and
        credentials, the settings, and the files n8Tracks manages. Audio in the media folder is not
        included. Keep backups as safe as the data folder itself.
      </Text>

      {state.phase === 'ready' && state.list.sharesDiskWithData && (
        <Notice title={SHARED_DISK_TITLE}>
          <Text>
            No writable backup folder is mounted at /backup, so backups are saved in the data
            folder, on the same disk as the data they protect. Mount a folder on another disk at
            /backup to keep them apart.
          </Text>
        </Notice>
      )}

      <Group>
        <Button
          loading={starting}
          onClick={() => {
            void start();
          }}
        >
          Back up now
        </Button>
      </Group>
      <BackupProgress job={job} outcome={outcome} />

      <BackupSchedulePanel
        status={state.phase === 'ready' ? state.list.schedule : null}
        lastSuccessAt={state.phase === 'ready' ? state.list.lastSuccessAt : null}
        timeZone={timeZone}
        onSaved={reload}
      />

      {state.phase === 'loading' && <Loader aria-label="Loading backups" />}
      {state.phase === 'error' && (
        <Notice title="Backups could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <div>
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
          </div>
        </Notice>
      )}
      {state.phase === 'ready' &&
        (state.list.items.length === 0 ? (
          <Paper p="sm" withBorder>
            <Text>There are no backups yet.</Text>
          </Paper>
        ) : (
          <BackupTable backups={state.list.items} timeZone={timeZone} onDelete={setDeleting} />
        ))}

      <DeleteDialog
        key={`delete-${deleting ? `${deleting.location}/${deleting.name}` : 'none'}`}
        backup={deleting}
        onClose={() => {
          setDeleting(undefined);
        }}
        onChanged={reload}
      />
    </Stack>
  );
}
