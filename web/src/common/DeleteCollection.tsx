import { Button, CloseButton, Group, Modal, Paper, Stack, Text } from '@mantine/core';
import { useState } from 'react';
import type { DeleteCollectionResult } from '../api/collectionDeletion';
import {
  songCountText,
  type CollectionNoun,
  type DeletedCollectionState,
} from './collectionDeletion';

const FAILED_MESSAGE =
  'Not deleted: n8Tracks did not answer as expected. Check that it is running and try again.';

/** What else goes with the record, by noun. */
const TAKEN_WITH: Record<CollectionNoun, string> = {
  Album: 'Its track order, links, and artwork are deleted with it.',
  Playlist: 'Its order and artwork are deleted with it.',
};

/**
 * The confirmation for deleting an Album or a Playlist (#103), opened while `opened`. It states how
 * many Songs the record holds, that those Songs are not deleted, and that the deletion is permanent.
 * The delete carries the revision the page holds; when the record changed elsewhere meanwhile, the
 * page takes in the record as it is now (`onCurrent`), so the count shown is current, and the user
 * chooses Delete again. `onDeleted` runs once it is gone.
 */
export function DeleteCollectionDialog<T extends { title: string; songCount: number }>({
  noun,
  record,
  opened,
  onClose,
  remove,
  onCurrent,
  onDeleted,
}: {
  noun: CollectionNoun;
  record: T;
  opened: boolean;
  onClose: () => void;
  remove: () => Promise<DeleteCollectionResult<T>>;
  onCurrent: (current: T) => void;
  onDeleted: () => void;
}) {
  const [deleting, setDeleting] = useState(false);
  const [message, setMessage] = useState<string | undefined>();

  const close = () => {
    setMessage(undefined);
    onClose();
  };

  const confirm = async () => {
    setDeleting(true);
    setMessage(undefined);
    const result = await remove();
    setDeleting(false);
    switch (result.kind) {
      case 'deleted':
        onDeleted();
        return;
      case 'conflict':
        onCurrent(result.current);
        setMessage(
          `Not deleted: the ${noun} was changed elsewhere since this opened. It now holds ${songCountText(result.current.songCount)}. Check, then choose Delete again.`,
        );
        return;
      case 'gone':
        setMessage(`This ${noun} is no longer there: it may have been deleted elsewhere.`);
        return;
      case 'failed':
        setMessage(FAILED_MESSAGE);
    }
  };

  return (
    <Modal
      opened={opened}
      onClose={close}
      title={`Delete “${record.title}”?`}
      centered
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <Stack gap="md">
        <Stack gap="xs" data-testid="delete-collection-summary">
          <Text>
            Deleting the {noun} “{record.title}” is permanent. {TAKEN_WITH[noun]}
          </Text>
          <Text data-testid="delete-collection-songs">
            It holds {songCountText(record.songCount)}.{' '}
            {record.songCount === 0
              ? 'No Song is affected.'
              : `The Songs themselves are not deleted: they stay in the catalog and are only taken off this ${noun}.`}
          </Text>
        </Stack>
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
            onClick={() => {
              void confirm();
            }}
          >
            Delete {noun}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** The dismissible notice naming the Album or Playlist just deleted. */
export function DeletedCollectionNotice({
  deleted,
  onDismiss,
}: {
  deleted: DeletedCollectionState['deletedCollection'];
  onDismiss: () => void;
}) {
  return (
    <Paper p="sm" withBorder data-testid="collection-deleted-notice">
      <Group justify="space-between" wrap="nowrap" gap="sm">
        <Text role="status" style={{ overflowWrap: 'anywhere' }}>
          Deleted the {deleted.noun} “{deleted.title}”. Its Songs were not deleted.
        </Text>
        <CloseButton aria-label="Dismiss" onClick={onDismiss} />
      </Group>
    </Paper>
  );
}
