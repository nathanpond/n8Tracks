import { Button, Group, Loader, Modal, Stack, Text } from '@mantine/core';
import { useEffect, useState } from 'react';
import {
  deleteVersion,
  fetchDeletionImpact,
  type DeletionImpact,
  type Version,
  type VersionDetail,
} from '../api/versions';
import { DeletionAudioFiles } from '../media/DeletionAudioFiles';
import { deletionSummary } from './versionDeletion';

const FAILED_MESSAGE =
  'Not deleted: n8Tracks did not answer as expected. Check that it is running and try again.';

type Impact =
  | { kind: 'loading' }
  | { kind: 'found'; impact: DeletionImpact }
  | { kind: 'gone' }
  | { kind: 'failed' };

/**
 * The confirmation for deleting a Version, opened for `version` (closed when undefined). It reads
 * what the deletion would do when it opens (Generations deleted with it, descendants that remain,
 * whether it is the Song's last Version) and deletes on the revision that read found: a count that
 * changed since is not a conflict, but an edit of the Version is, and the counts are read again.
 * `onDeleted` gets the Song's current Version afterwards.
 */
export function DeleteVersionDialog({
  version,
  onClose,
  onDeleted,
}: {
  version: Version | undefined;
  onClose: () => void;
  onDeleted: (deleted: Version, current: VersionDetail) => void;
}) {
  const [read, setRead] = useState<{ key: string; impact: Impact } | undefined>();
  const [attempt, setAttempt] = useState(0);
  const [deleting, setDeleting] = useState(false);
  const [message, setMessage] = useState<string | undefined>();
  const id = version?.id;
  // Each opening (and each read again after a conflict) has its own key, so an older answer is never shown.
  const key = id === undefined ? undefined : `${id} ${String(attempt)}`;
  const impact: Impact = read !== undefined && read.key === key ? read.impact : { kind: 'loading' };

  useEffect(() => {
    if (id === undefined || key === undefined) {
      return undefined;
    }
    const controller = new AbortController();
    void fetchDeletionImpact(id, controller.signal).then((result) => {
      if (!controller.signal.aborted) {
        setRead({
          key,
          impact: result.kind === 'found' ? { kind: 'found', impact: result.impact } : result,
        });
      }
    });
    return () => {
      controller.abort();
    };
  }, [id, key]);

  const close = () => {
    setMessage(undefined);
    setAttempt((previous) => previous + 1);
    onClose();
  };

  const remove = async (target: Version, found: DeletionImpact) => {
    setDeleting(true);
    setMessage(undefined);
    const result = await deleteVersion({ id: target.id, revision: found.revision });
    setDeleting(false);
    switch (result.kind) {
      case 'deleted':
        onDeleted(target, result.current);
        return;
      case 'conflict':
        setMessage(
          `Version ${target.number} was changed elsewhere since this opened, so it was not deleted. Check what deleting it does now, then choose Delete again.`,
        );
        setAttempt((previous) => previous + 1);
        return;
      case 'gone':
        setMessage(
          `Version ${target.number} is no longer there: it may have been deleted elsewhere.`,
        );
        return;
      case 'failed':
        setMessage(FAILED_MESSAGE);
    }
  };

  return (
    <Modal
      opened={version !== undefined}
      onClose={close}
      title={version === undefined ? 'Delete' : `Delete Version ${version.number}?`}
      centered
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      {version !== undefined && (
        <Stack gap="md">
          {impact.kind === 'loading' && (
            <Group gap="sm">
              <Loader size="xs" aria-hidden="true" />
              <Text size="sm" role="status">
                Checking what deleting it affects…
              </Text>
            </Group>
          )}
          {impact.kind === 'found' && (
            <Stack gap="xs" data-testid="delete-version-summary">
              {deletionSummary(version.number, impact.impact).map((line) => (
                <Text key={line}>{line}</Text>
              ))}
              <DeletionAudioFiles counts={impact.impact.localAudioFiles} owner="version" />
            </Stack>
          )}
          {(impact.kind === 'gone' || impact.kind === 'failed') && (
            <Text role="alert" c="var(--mantine-color-error)">
              {impact.kind === 'gone'
                ? `Version ${version.number} is no longer there: it may have been deleted elsewhere.`
                : 'What deleting it affects could not be checked. Close this and try again.'}
            </Text>
          )}
          {message !== undefined && (
            <Text size="sm" role="alert" c="var(--mantine-color-error)">
              {message}
            </Text>
          )}
          <Group gap="sm" justify="flex-end">
            <Button variant="default" data-autofocus onClick={close}>
              Cancel
            </Button>
            <Button
              color="red"
              loading={deleting}
              disabled={impact.kind !== 'found'}
              onClick={() => {
                if (impact.kind === 'found') {
                  void remove(version, impact.impact);
                }
              }}
            >
              Delete Version {version.number}
            </Button>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}
