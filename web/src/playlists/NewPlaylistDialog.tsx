import { Button, Group, Modal, Stack, Text, TextInput } from '@mantine/core';
import { useState, type SyntheticEvent } from 'react';
import {
  createPlaylist,
  PLAYLIST_TITLE_MAXIMUM_LENGTH,
  playlistTitleError,
  type Playlist,
} from '../api/playlists';
import { Notice } from '../components/Notice';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

/**
 * Asks for a new Playlist's title and creates it, empty; `onCreated` opens it, where Songs are
 * added. Titles need not be unique.
 */
export function NewPlaylistDialog({
  opened,
  onClose,
  onCreated,
}: {
  opened: boolean;
  onClose: () => void;
  onCreated: (playlist: Playlist) => void;
}) {
  const [title, setTitle] = useState('');
  const [error, setError] = useState<string>();
  const [failed, setFailed] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  const reset = () => {
    setTitle('');
    setError(undefined);
    setFailed(false);
  };

  const close = () => {
    reset();
    onClose();
  };

  const submit = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    const local = playlistTitleError(title);
    setFailed(false);
    if (local !== undefined) {
      setError(local);
      return;
    }

    setSubmitting(true);
    const result = await createPlaylist(title);
    setSubmitting(false);
    switch (result.kind) {
      case 'created':
        reset();
        onCreated(result.playlist);
        return;
      case 'invalid':
        setError(result.errors.title?.join(' '));
        setFailed(result.errors.title === undefined);
        return;
      case 'failed':
        setFailed(true);
    }
  };

  return (
    <Modal
      opened={opened}
      onClose={close}
      title="New Playlist"
      centered
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <form
        noValidate
        aria-label="New Playlist"
        onSubmit={(event) => {
          void submit(event);
        }}
      >
        <Stack gap="md">
          <TextInput
            label="Title"
            description={`Up to ${String(PLAYLIST_TITLE_MAXIMUM_LENGTH)} characters. Titles do not have to be unique.`}
            required
            value={title}
            onChange={(event) => {
              setTitle(event.currentTarget.value);
              setError(undefined);
            }}
            error={error}
            aria-invalid={error !== undefined}
            data-autofocus
          />
          <div role="status">
            {failed && (
              <Notice title="Playlist not created">
                <Text>{FAILED_MESSAGE}</Text>
              </Notice>
            )}
          </div>
          <Group justify="end">
            <Button variant="default" onClick={close}>
              Cancel
            </Button>
            <Button type="submit" loading={submitting}>
              Create Playlist
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
