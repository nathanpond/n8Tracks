import { Badge, Button, Group, Loader, Modal, Radio, Stack, Text } from '@mantine/core';
import { useEffect, useState } from 'react';
import { associateFile, removeAssociation, type UnmatchedFile } from '../api/audioFiles';
import { generationDuration, readSongGenerations, type Generation } from '../api/generations';
import type { FailureReason } from '../api/saves';
import type { Song } from '../api/songs';
import { ARCHIVED_STATE_ID } from '../api/workflow';
import { SongSearch } from '../common/SongSearch';
import { Notice } from '../components/Notice';
import { associationText, folderText, originText } from './unmatchedRules';

/** How many Songs the dialog's search offers at once (#210). */
export const ASSOCIATE_SEARCH_RESULTS = 20;

/** The value of the Generation choice that associates the file with the Song alone. */
const SONG_ONLY = 'song-only';

const isArchived = (song: Pick<Song, 'state'>) => song.state.id === ARCHIVED_STATE_ID;

function failureText(reason: FailureReason | undefined): string {
  switch (reason) {
    case 'gone':
      return 'The file, the Song, or the Generation is no longer there. Close this and look again.';
    case 'signed-out':
      return 'You are signed out. Sign in again and try once more.';
    case 'unreachable':
    case 'server':
      return 'n8Tracks did not answer as expected. Check that it is running and try again.';
    default:
      return 'n8Tracks did not accept this. Close this and look again.';
  }
}

const STALE_TEXT =
  'This file changed since the list was loaded. It is shown here as it is now: check it and try again.';

type Loaded =
  { phase: 'loading' } | { phase: 'failed' } | { phase: 'ready'; generations: Generation[] };

/**
 * Associates an audio file with a Song and, when wanted, one of its Generations (#210): any Song can
 * be found by title or shortcode (twenty at a time, Archived ones included and marked), then its
 * Generations are offered, every state included, beside "the Song only". A Generation is preferred
 * when the file came from one. For a file that is associated, the dialog names the current
 * association, says choosing another replaces it, and offers to remove it. Nothing in the media folder
 * changes. Open while `file` is set; `onChanged` is told what changed (an announcement), or nothing
 * when the file was found changed, so the list can be read again.
 */
export function AssociateFileDialog({
  file,
  onClose,
  onChanged,
}: {
  file: UnmatchedFile | undefined;
  onClose: () => void;
  onChanged: (announcement?: string) => void;
}) {
  return (
    <Modal
      opened={file !== undefined}
      onClose={onClose}
      title={file?.song === null ? 'Associate file' : 'Change association'}
      closeButtonProps={{ 'aria-label': 'Close' }}
      size="lg"
    >
      {file !== undefined && (
        <AssociateForm key={file.id} file={file} onClose={onClose} onChanged={onChanged} />
      )}
    </Modal>
  );
}

function AssociateForm({
  file,
  onClose,
  onChanged,
}: {
  file: UnmatchedFile;
  onClose: () => void;
  onChanged: (announcement?: string) => void;
}) {
  const [current, setCurrent] = useState(file);
  const [song, setSong] = useState<Song | undefined>();
  const [loaded, setLoaded] = useState<Loaded>({ phase: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const [choice, setChoice] = useState(SONG_ONLY);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | undefined>();

  useEffect(() => {
    if (song === undefined) {
      return undefined;
    }
    const controller = new AbortController();
    void readSongGenerations(song.id, controller.signal).then((generations) => {
      if (!controller.signal.aborted) {
        setLoaded(
          generations === undefined ? { phase: 'failed' } : { phase: 'ready', generations },
        );
      }
    });
    return () => {
      controller.abort();
    };
  }, [song, attempt]);

  const generations = loaded.phase === 'ready' ? loaded.generations : [];

  const associate = async () => {
    if (song === undefined) {
      return;
    }
    const generation = generations.find((candidate) => candidate.id === choice);
    setBusy(true);
    setProblem(undefined);
    const result = await associateFile(current, song.id, generation?.id ?? null);
    setBusy(false);
    if (result.kind === 'saved') {
      onChanged(`Associated ${current.fileName} with ${associationText(result.record)}.`);
      onClose();
    } else if (result.kind === 'conflict') {
      setCurrent(result.current);
      setProblem(STALE_TEXT);
      onChanged();
    } else {
      setProblem(failureText(result.kind === 'failed' ? result.reason : 'refused'));
    }
  };

  const remove = async () => {
    setBusy(true);
    setProblem(undefined);
    const result = await removeAssociation(current);
    setBusy(false);
    if (result.kind === 'saved') {
      onChanged(`Removed the association of ${current.fileName}. It is back in Unmatched Files.`);
      onClose();
    } else if (result.kind === 'conflict') {
      if (result.current !== null) {
        setCurrent(result.current);
      }
      setProblem(STALE_TEXT);
      onChanged();
    } else {
      setProblem(failureText(result.kind === 'failed' ? result.reason : 'refused'));
    }
  };

  return (
    <Stack gap="md">
      <Text>
        File:{' '}
        <Text span fw={600} style={{ wordBreak: 'break-word' }}>
          {current.fileName}
        </Text>{' '}
        ({folderText(current)}). Associating changes nothing in the media folder.
      </Text>
      {current.song !== null && (
        <Stack gap="xs" data-testid="current-association">
          <Text>
            It is associated with {associationText(current)},{' '}
            {originText(current.associationOrigin)}. Choosing another Song or Generation replaces
            that association.
          </Text>
          <div>
            <Button
              variant="default"
              color="red"
              disabled={busy}
              onClick={() => {
                void remove();
              }}
            >
              Remove association
            </Button>
          </div>
        </Stack>
      )}
      <SongSearch
        label="Song"
        description="Type a title or a shortcode. Archived Songs are included and marked."
        limit={ASSOCIATE_SEARCH_RESULTS}
        noteOf={(found) => (isArchived(found) ? 'Archived' : undefined)}
        busy={busy}
        onChoose={(found) => {
          setLoaded({ phase: 'loading' });
          setChoice(SONG_ONLY);
          setSong(found);
        }}
      />
      {song !== undefined && (
        <Stack gap="xs" data-testid="chosen-song">
          <Group gap="xs">
            <Text>
              Song:{' '}
              <Text span fw={600}>
                {song.title}
              </Text>{' '}
              ({song.shortcode})
            </Text>
            {isArchived(song) && (
              <Badge size="sm" variant="default" radius="sm" tt="none">
                Archived
              </Badge>
            )}
          </Group>
          {loaded.phase === 'loading' ? (
            <Group gap="xs">
              <Loader size="xs" aria-hidden="true" />
              <Text size="sm" role="status">
                Loading its Generations…
              </Text>
            </Group>
          ) : loaded.phase === 'failed' ? (
            <Group gap="sm">
              <Text role="alert">Its Generations could not be loaded.</Text>
              <Button
                variant="default"
                size="compact-sm"
                onClick={() => {
                  setLoaded({ phase: 'loading' });
                  setAttempt((previous) => previous + 1);
                }}
              >
                Try again
              </Button>
            </Group>
          ) : (
            <Radio.Group
              label="Generation"
              description="When the file came from one of this Song’s Generations, choose it: a Generation is preferred. The Song alone is allowed."
              value={choice}
              onChange={setChoice}
            >
              <Stack gap="xs" mt="xs">
                <Radio value={SONG_ONLY} label="None: the Song only" />
                {generations.map((generation) => (
                  <Radio
                    key={generation.id}
                    value={generation.id}
                    label={generation.shortcode}
                    description={[
                      generation.title ?? 'Untitled',
                      generationDuration(generation),
                      generation.state === 'archived' ? 'Archived' : undefined,
                      generation.remoteState === 'trashed' ? 'In Suno Trash' : undefined,
                      generation.remoteState === 'missing' ? 'Remote Missing' : undefined,
                    ]
                      .filter((part) => part !== undefined)
                      .join(' · ')}
                  />
                ))}
              </Stack>
            </Radio.Group>
          )}
        </Stack>
      )}
      {problem !== undefined && (
        <Notice title="Not saved">
          <Text role="alert">{problem}</Text>
        </Notice>
      )}
      <Group justify="flex-end" gap="sm">
        <Button variant="default" onClick={onClose}>
          Cancel
        </Button>
        <Button
          disabled={song === undefined || loaded.phase !== 'ready'}
          loading={busy}
          onClick={() => {
            void associate();
          }}
        >
          Associate
        </Button>
      </Group>
    </Stack>
  );
}
