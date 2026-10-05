import { Button, Group, Loader, Modal, Stack, Table, Text, TextInput } from '@mantine/core';
import { useEffect, useState } from 'react';
import {
  BACKUP_KIND_LABELS,
  formatSize,
  RESTORE_CONFIRMATION,
  startRestore,
  uploadBackup,
  validateBackup,
  type Backup,
  type RestoreValidation,
  type ValidationResult,
} from '../api/backups';
import { reportMaintenance } from '../api/maintenance';
import { formatDateTime } from '../api/timeZone';
import { Notice } from '../components/Notice';

const FAILED_MESSAGE = 'Check your connection and try again.';

/** What to restore from: a listed backup, or a file the administrator chose. */
export type RestoreRequest = { kind: 'listed'; backup: Backup } | { kind: 'upload'; file: File };

type Check =
  | { phase: 'checking' }
  | { phase: 'refused'; message: string }
  | { phase: 'ready'; validation: RestoreValidation };

function checkOf(result: ValidationResult): Check {
  switch (result.kind) {
    case 'valid':
      return { phase: 'ready', validation: result.validation };
    case 'refused':
      return { phase: 'refused', message: result.message };
    case 'gone':
      return { phase: 'refused', message: 'This backup is no longer there.' };
    default:
      return { phase: 'refused', message: `The backup could not be checked. ${FAILED_MESSAGE}` };
  }
}

function Summary({ validation, timeZone }: { validation: RestoreValidation; timeZone: string }) {
  const { archive } = validation;
  const rows: [string, string][] = [
    ['Created', formatDateTime(archive.createdAt, timeZone)],
    ['Made by', `n8Tracks ${archive.applicationVersion}`],
    ['Kind', BACKUP_KIND_LABELS[archive.kind] ?? archive.kind],
    ['File', archive.name],
    ['Size', formatSize(archive.size)],
  ];
  return (
    <Table withTableBorder aria-label="Backup to restore" data-testid="restore-summary">
      <Table.Tbody>
        {rows.map(([label, value]) => (
          <Table.Tr key={label}>
            <Table.Th scope="row" w="30%">
              {label}
            </Table.Th>
            <Table.Td style={{ overflowWrap: 'anywhere' }}>{value}</Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );
}

/**
 * Restoring from a backup: the archive is checked fully first (a listed one in place, a chosen file
 * by uploading it), and a refusal says why. A valid one shows its summary, what a restore replaces,
 * and a field where the administrator types RESTORE; confirming starts the restore, and the app
 * shows the maintenance page.
 */
export function RestoreDialog({
  request,
  timeZone,
  onClose,
}: {
  request: RestoreRequest | null;
  timeZone: string;
  onClose: () => void;
}) {
  const [check, setCheck] = useState<Check>({ phase: 'checking' });
  const [typed, setTyped] = useState('');
  const [starting, setStarting] = useState(false);
  const [startError, setStartError] = useState<string | null>(null);

  useEffect(() => {
    if (request === null) {
      return;
    }
    const controller = new AbortController();
    const run = async () => {
      const result =
        request.kind === 'listed'
          ? await validateBackup(request.backup)
          : await uploadBackup(request.file, controller.signal);
      if (!controller.signal.aborted) {
        setCheck(checkOf(result));
      }
    };
    void run();
    return () => {
      controller.abort();
    };
  }, [request]);

  const confirmed = typed === RESTORE_CONFIRMATION;

  const start = async (validation: RestoreValidation) => {
    setStarting(true);
    setStartError(null);
    const result = await startRestore(validation.validationId, typed);
    setStarting(false);
    switch (result.kind) {
      case 'started':
        reportMaintenance();
        return;
      case 'refused':
        setStartError(result.message);
        return;
      case 'expired':
        setStartError('This check has expired. Close this and choose Restore again.');
        return;
      default:
        setStartError(FAILED_MESSAGE);
    }
  };

  const title = request?.kind === 'upload' ? 'Restore from a file?' : 'Restore from this backup?';

  return (
    <Modal
      opened={request !== null}
      onClose={onClose}
      title={title}
      centered
      size="lg"
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <Stack gap="md">
        <div role="status" data-testid="restore-check">
          {check.phase === 'checking' && (
            <Group gap="sm">
              <Loader size="sm" aria-hidden="true" />
              <Text>
                {request?.kind === 'upload' ? 'Uploading and checking' : 'Checking'} the backup…
              </Text>
            </Group>
          )}
          {check.phase === 'refused' && (
            <Notice title="This backup cannot be restored">
              <Text data-testid="restore-refusal">{check.message}</Text>
              <Text>Nothing was changed.</Text>
            </Notice>
          )}
        </div>

        {check.phase === 'ready' && (
          <>
            <Summary validation={check.validation} timeZone={timeZone} />
            <Notice title="Everything is replaced">
              <Text>
                Restoring replaces everything n8Tracks keeps with what this backup holds: the
                catalog, the settings, the administrator account and its password, and the
                credentials. A safety backup of the current data is taken first. n8Tracks is
                unavailable while the restore runs, and everyone is signed out.
              </Text>
            </Notice>
            <TextInput
              label={`Type ${RESTORE_CONFIRMATION} to confirm`}
              value={typed}
              autoComplete="off"
              spellCheck={false}
              data-autofocus
              onChange={(event) => {
                setTyped(event.currentTarget.value);
              }}
            />
            <div role="status">
              {startError !== null && (
                <Notice title="The restore did not start">
                  <Text>{startError}</Text>
                </Notice>
              )}
            </div>
          </>
        )}

        <Group justify="end">
          <Button variant="default" onClick={onClose}>
            {check.phase === 'ready' ? 'Cancel' : 'Close'}
          </Button>
          {check.phase === 'ready' && (
            <Button
              color="red"
              disabled={!confirmed}
              loading={starting}
              onClick={() => {
                void start(check.validation);
              }}
            >
              Restore
            </Button>
          )}
        </Group>
      </Stack>
    </Modal>
  );
}
