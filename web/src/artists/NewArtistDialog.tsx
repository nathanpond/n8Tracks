import { Button, Group, Modal, Stack, Text, TextInput } from '@mantine/core';
import { useState, type SyntheticEvent } from 'react';
import {
  ARTIST_NAME_MAXIMUM_LENGTH,
  createArtist,
  type Artist,
  type ArtistMatch,
} from '../api/artists';
import { Notice } from '../components/Notice';
import { artistNameError } from './artistRules';
import { DuplicateMatches } from './DuplicateMatches';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

/**
 * Asks for a new Artist's name and creates it; aliases, notes, and links are added on the Artist's
 * page, which `onCreated` opens. A name another Artist already has (ignoring case) is shown with the
 * matching Artists and created only once the user confirms it.
 */
export function NewArtistDialog({
  opened,
  onClose,
  onCreated,
}: {
  opened: boolean;
  onClose: () => void;
  onCreated: (artist: Artist) => void;
}) {
  const [name, setName] = useState('');
  const [error, setError] = useState<string>();
  const [matches, setMatches] = useState<ArtistMatch[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  const reset = () => {
    setName('');
    setError(undefined);
    setMatches(null);
    setFailed(false);
  };

  const close = () => {
    reset();
    onClose();
  };

  const create = async (confirmDuplicate: boolean) => {
    const local = artistNameError(name);
    setFailed(false);
    if (local !== undefined) {
      setError(local);
      return;
    }

    setSubmitting(true);
    const result = await createArtist({ name }, confirmDuplicate);
    setSubmitting(false);

    switch (result.kind) {
      case 'created':
        reset();
        onCreated(result.artist);
        return;
      case 'duplicate':
        setError(undefined);
        setMatches(result.matches);
        return;
      case 'invalid':
        setError(result.errors.name?.join(' '));
        setFailed(result.errors.name === undefined);
        return;
      case 'failed':
        setFailed(true);
    }
  };

  const submit = (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    void create(matches !== null);
  };

  return (
    <Modal
      opened={opened}
      onClose={close}
      title="New Artist"
      centered
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <form noValidate aria-label="New Artist" onSubmit={submit}>
        <Stack gap="md">
          <TextInput
            label="Name"
            description={`The name shown for the Artist, up to ${String(ARTIST_NAME_MAXIMUM_LENGTH)} characters.`}
            required
            value={name}
            onChange={(event) => {
              setName(event.currentTarget.value);
              setMatches(null);
            }}
            error={error}
            aria-invalid={error !== undefined}
            data-autofocus
          />
          <div role="status">
            {matches !== null && (
              <Notice title="Another Artist has this name">
                <Text size="sm">Other Artists already have this name or alias:</Text>
                <DuplicateMatches matches={matches} />
                <Text size="sm">Artists can share names. Create this one anyway?</Text>
              </Notice>
            )}
            {failed && (
              <Notice title="Artist not created">
                <Text>{FAILED_MESSAGE}</Text>
              </Notice>
            )}
          </div>
          <Group justify="end">
            <Button variant="default" onClick={close}>
              Cancel
            </Button>
            <Button type="submit" loading={submitting}>
              {matches === null ? 'Create Artist' : 'Create anyway'}
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
