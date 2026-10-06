import { Button, Group, List, Loader, Modal, Stack, Text, TextInput } from '@mantine/core';
import { useEffect, useState } from 'react';
import { deleteSong, fetchSongDeletionImpact, type SongDeletionImpact } from '../api/songDeletion';
import type { Song } from '../api/songs';
import { songDeletionCounts, titleConfirms } from './songDeletion';

const FAILED_MESSAGE =
  'Not deleted: n8Tracks did not answer as expected. Check that it is running and try again.';

type Impact =
  | { kind: 'loading' }
  | { kind: 'found'; impact: SongDeletionImpact }
  | { kind: 'gone' }
  | { kind: 'failed' };

/** The Song a deletion removed, as the Songs table's notice names it. */
export interface DeletedSongNotice {
  title: string;
  shortcode: string;
}

/**
 * The confirmation for deleting a Song, opened while `opened`. It reads what the deletion would
 * take with it when it opens and lists the counts. When the server says the title is required (more
 * than one Version, any Generation, membership, or relationship), the Delete button stays disabled
 * until the title is typed; the server decides again at delete time, and when it has become required
 * since, the counts are refreshed from its answer. A Song changed elsewhere meanwhile is a conflict:
 * the counts are read again. `onDeleted` gets the Song's title and shortcode.
 */
export function DeleteSongDialog({
  song,
  opened,
  onClose,
  onDeleted,
}: {
  song: Pick<Song, 'id' | 'title' | 'shortcode'>;
  opened: boolean;
  onClose: () => void;
  onDeleted: (deleted: DeletedSongNotice) => void;
}) {
  const [read, setRead] = useState<{ key: string; impact: Impact } | undefined>();
  const [attempt, setAttempt] = useState(0);
  const [typed, setTyped] = useState('');
  const [deleting, setDeleting] = useState(false);
  const [message, setMessage] = useState<string | undefined>();
  // Each opening (and each read again after a conflict) has its own key, so an older answer is never shown.
  const key = opened ? `${song.id} ${String(attempt)}` : undefined;
  const impact: Impact = read !== undefined && read.key === key ? read.impact : { kind: 'loading' };

  useEffect(() => {
    if (key === undefined) {
      return undefined;
    }
    const controller = new AbortController();
    void fetchSongDeletionImpact(song.id, controller.signal).then((result) => {
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
  }, [song.id, key]);

  const close = () => {
    setMessage(undefined);
    setTyped('');
    setAttempt((previous) => previous + 1);
    onClose();
  };

  const found = impact.kind === 'found' ? impact.impact : undefined;
  const confirmed =
    found !== undefined && (!found.titleRequired || titleConfirms(typed, found.title));

  const remove = async (target: SongDeletionImpact) => {
    setDeleting(true);
    setMessage(undefined);
    const result = await deleteSong(target, target.titleRequired ? typed : undefined);
    setDeleting(false);
    switch (result.kind) {
      case 'deleted':
        setTyped('');
        onDeleted({ title: target.title, shortcode: target.shortcode });
        return;
      case 'confirmation':
        if (key !== undefined) {
          setRead({ key, impact: { kind: 'found', impact: result.impact } });
        }
        setMessage(
          'Not deleted: the Song changed since this opened, and deleting it now needs its title typed. Check what deleting it affects, then type its title.',
        );
        return;
      case 'conflict':
        setMessage(
          'Not deleted: the Song was changed elsewhere since this opened. Check what deleting it affects now, then choose Delete again.',
        );
        setAttempt((previous) => previous + 1);
        return;
      case 'gone':
        setMessage('This Song is no longer there: it may have been deleted elsewhere.');
        return;
      case 'failed':
        setMessage(FAILED_MESSAGE);
    }
  };

  return (
    <Modal
      opened={opened}
      onClose={close}
      title={`Delete “${song.title}”?`}
      centered
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <Stack gap="md">
        {impact.kind === 'loading' && (
          <Group gap="sm">
            <Loader size="xs" aria-hidden="true" />
            <Text size="sm" role="status">
              Checking what deleting it affects…
            </Text>
          </Group>
        )}
        {found !== undefined && (
          <Stack gap="xs" data-testid="delete-song-summary">
            <Text>
              Deleting {found.shortcode} “{found.title}” is permanent. These are deleted with it:
            </Text>
            <List size="sm" data-testid="delete-song-counts">
              {songDeletionCounts(found).map((line) => (
                <List.Item key={line}>{line}</List.Item>
              ))}
            </List>
            <Text size="sm">
              It is taken off its Albums and Playlists and out of its relationships; those Albums,
              Playlists, and other Songs remain. Its shortcode is never used for another Song.
            </Text>
          </Stack>
        )}
        {(impact.kind === 'gone' || impact.kind === 'failed') && (
          <Text role="alert" c="var(--mantine-color-error)">
            {impact.kind === 'gone'
              ? 'This Song is no longer there: it may have been deleted elsewhere.'
              : 'What deleting it affects could not be checked. Close this and try again.'}
          </Text>
        )}
        {found?.titleRequired === true && (
          <TextInput
            label="Type the Song’s title to confirm"
            description={`Type “${found.title}” exactly.`}
            value={typed}
            onChange={(event) => {
              setTyped(event.currentTarget.value);
            }}
            autoComplete="off"
            spellCheck={false}
          />
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
            disabled={!confirmed}
            onClick={() => {
              if (found !== undefined) {
                void remove(found);
              }
            }}
          >
            Delete Song
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
