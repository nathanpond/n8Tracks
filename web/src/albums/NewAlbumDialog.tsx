import { Button, Group, Modal, Stack, Text, TextInput } from '@mantine/core';
import { useState, type SyntheticEvent } from 'react';
import { ALBUM_TITLE_MAXIMUM_LENGTH, createAlbum, type Album } from '../api/albums';
import { Notice } from '../components/Notice';
import { albumTitleError } from './albumRules';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

/**
 * Asks for a new Album's title and creates it; everything else is set on the Album's page, which
 * `onCreated` opens. Titles need not be unique.
 */
export function NewAlbumDialog({
  opened,
  onClose,
  onCreated,
}: {
  opened: boolean;
  onClose: () => void;
  onCreated: (album: Album) => void;
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
    const local = albumTitleError(title);
    setFailed(false);
    if (local !== undefined) {
      setError(local);
      return;
    }

    setSubmitting(true);
    const result = await createAlbum(title);
    setSubmitting(false);
    switch (result.kind) {
      case 'created':
        reset();
        onCreated(result.album);
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
      title="New Album"
      centered
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <form
        noValidate
        aria-label="New Album"
        onSubmit={(event) => {
          void submit(event);
        }}
      >
        <Stack gap="md">
          <TextInput
            label="Title"
            description={`One line, up to ${String(ALBUM_TITLE_MAXIMUM_LENGTH)} characters. Titles do not have to be unique.`}
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
              <Notice title="Album not created">
                <Text>{FAILED_MESSAGE}</Text>
              </Notice>
            )}
          </div>
          <Group justify="end">
            <Button variant="default" onClick={close}>
              Cancel
            </Button>
            <Button type="submit" loading={submitting}>
              Create Album
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
