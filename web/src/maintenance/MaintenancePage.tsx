import { Button, Group, Loader, Progress, Stack, Text, Title } from '@mantine/core';
import { useEffect } from 'react';
import {
  stageWhile,
  useMaintenanceStatus,
  type MaintenanceStage,
  type MaintenanceStatus,
} from '../api/maintenance';
import { Notice } from '../components/Notice';
import { PlainPage } from '../components/PlainPage';

/** Each stage as the page names it. */
const STAGE_LABELS: Record<MaintenanceStage, string> = {
  validating: 'Checking the backup',
  'safety-backup': 'Taking a safety backup of the current data',
  replacing: 'Replacing the data',
  migrating: 'Updating the database',
  finishing: 'Finishing',
};

function Progressing({ status }: { status: MaintenanceStatus }) {
  const label = status.stage === null ? 'Starting' : STAGE_LABELS[status.stage];
  return (
    <Stack gap="xs">
      <Text data-testid="maintenance-stage">
        {label}… {status.percent}%
      </Text>
      <Progress value={status.percent} aria-label="Restore progress" />
    </Stack>
  );
}

function Ended({ status, onDone }: { status: MaintenanceStatus; onDone: () => void }) {
  const rolledBack = status.outcome === 'rolled-back';
  const title = rolledBack ? 'The restore failed and was undone' : 'The restore did not complete';
  return (
    <Stack gap="md" align="flex-start">
      <Notice title={title}>
        <Text>
          {rolledBack
            ? `The restore failed while ${stageWhile(status.stage)}. n8Tracks put back the data it had before the restore began.`
            : 'Nothing was changed.'}{' '}
          Sign in and open Settings → Backups to see what happened.
        </Text>
      </Notice>
      <Button onClick={onDone}>Continue</Button>
    </Stack>
  );
}

/** The restore failed and its data could not be put back: maintenance stays on until it is restored by hand. */
function Stalled({ status }: { status: MaintenanceStatus }) {
  return (
    <Notice title="The restore failed and could not be undone">
      <Text>
        The restore failed while {stageWhile(status.stage)}, and n8Tracks could not put back the
        data it had before. It stays unavailable so that nothing changes further. The server log
        says where the safety backup is and how to restore it; this page updates once n8Tracks is
        running normally again.
      </Text>
    </Notice>
  );
}

/**
 * Shown instead of the app while the instance is in maintenance: the stage and its progress, read
 * from the maintenance status every second. When maintenance ends after a successful restore (or
 * there was none), `onDone` is called at once; after a failed one the page says so and waits for
 * the user to continue.
 */
export function MaintenancePage({ onDone }: { onDone: () => void }) {
  const status = useMaintenanceStatus(true);
  const failed =
    status !== null &&
    !status.active &&
    (status.outcome === 'failed' || status.outcome === 'rolled-back');
  const finished = status !== null && !status.active && !failed;

  useEffect(() => {
    if (finished) {
      onDone();
    }
  }, [finished, onDone]);

  return (
    <PlainPage>
      <Stack gap="md">
        <Title order={2}>Restoring a backup</Title>
        <Text>
          n8Tracks is unavailable while a backup is restored. This page updates by itself; you do
          not need to reload it.
        </Text>
        <div role="status" aria-live="polite" data-testid="maintenance-status">
          {status === null && (
            <Group gap="sm">
              <Loader size="sm" aria-hidden="true" />
              <Text>Checking the restore’s progress…</Text>
            </Group>
          )}
          {status?.active === true && status.outcome === 'rollback-failed' && (
            <Stalled status={status} />
          )}
          {status?.active === true && status.outcome !== 'rollback-failed' && (
            <Progressing status={status} />
          )}
          {failed && <Ended status={status} onDone={onDone} />}
          {finished && <Text>The restore has finished.</Text>}
        </div>
      </Stack>
    </PlainPage>
  );
}
