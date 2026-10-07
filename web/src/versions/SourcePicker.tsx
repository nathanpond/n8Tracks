import {
  Badge,
  Button,
  Divider,
  Group,
  Loader,
  Modal,
  Stack,
  Table,
  Text,
  TextInput,
  VisuallyHidden,
} from '@mantine/core';
import { useEffect, useState } from 'react';
import {
  generationDuration,
  ratingText,
  readSongGenerations,
  type Generation,
} from '../api/generations';
import { pastedSunoId, type LineageSource } from '../api/lineage';
import type { Song } from '../api/songs';
import { SongSearch } from '../common/SongSearch';
import { externalSource, generationSource } from './sourcesRules';

/**
 * Chooses one source (#125): a Song found with the shared search (every live Song, this Version's
 * own included), then one of its Generations, every state shown and labelled, the Version's own
 * left out and those already in the group not offered again; or a pasted Suno song address or clip
 * ID, which n8Tracks resolves to its Generation when it has one and otherwise keeps as a Suno clip,
 * Not imported. Open while `target` is set.
 */
export function SourcePicker({
  target,
  title,
  versionId,
  chosen,
  onPick,
  onClose,
}: {
  target: unknown;
  title: string;
  versionId: string;
  /** The Generations the group already holds (by ID). */
  chosen: readonly string[];
  onPick: (source: LineageSource) => void;
  onClose: () => void;
}) {
  return (
    <Modal
      opened={target !== undefined}
      onClose={onClose}
      title={title}
      closeButtonProps={{ 'aria-label': 'Close' }}
      size="lg"
    >
      {target !== undefined && <PickerForm versionId={versionId} chosen={chosen} onPick={onPick} />}
    </Modal>
  );
}

type Loaded =
  { phase: 'loading' } | { phase: 'failed' } | { phase: 'ready'; generations: Generation[] };

function PickerForm({
  versionId,
  chosen,
  onPick,
}: {
  versionId: string;
  chosen: readonly string[];
  onPick: (source: LineageSource) => void;
}) {
  const [song, setSong] = useState<Song | undefined>();
  const [loaded, setLoaded] = useState<Loaded>({ phase: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const [pasted, setPasted] = useState('');
  const [pasteError, setPasteError] = useState<string | undefined>();

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

  const offered =
    loaded.phase === 'ready'
      ? loaded.generations.filter((generation) => generation.version.id !== versionId)
      : [];

  return (
    <Stack gap="md">
      <SongSearch
        label="Song"
        description="Find the Song, then choose one of its Generations."
        onChoose={(found) => {
          setLoaded({ phase: 'loading' });
          setSong(found);
        }}
      />
      {song !== undefined && (
        <Stack gap="xs" role="group" aria-labelledby="picker-generations-heading">
          <Text fw={500} id="picker-generations-heading">
            Generations of {song.shortcode} {song.title}
          </Text>
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
          ) : offered.length === 0 ? (
            <Text size="sm" data-testid="picker-no-generations">
              This Song has no Generation to choose
              {loaded.generations.length > 0 ? ' besides this Version’s own' : ''}.
            </Text>
          ) : (
            <Table.ScrollContainer minWidth={420}>
              <Table data-testid="picker-generations">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Generation</Table.Th>
                    <Table.Th>Title</Table.Th>
                    <Table.Th>State</Table.Th>
                    <Table.Th>Rating</Table.Th>
                    <Table.Th>Length</Table.Th>
                    <Table.Th>
                      <VisuallyHidden>Choose</VisuallyHidden>
                    </Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {offered.map((generation) => {
                    const taken = chosen.includes(generation.id);
                    return (
                      <Table.Tr key={generation.id} data-picker-generation={generation.shortcode}>
                        <Table.Td ff="monospace">{generation.shortcode}</Table.Td>
                        <Table.Td>{generation.title ?? '—'}</Table.Td>
                        <Table.Td>
                          <Group gap={4} wrap="wrap">
                            <Badge size="sm" variant="default" radius="sm" tt="none">
                              {generation.state === 'archived' ? 'Archived' : 'Active'}
                            </Badge>
                            {generation.remoteState === 'trashed' && (
                              <Badge size="sm" variant="default" radius="sm" tt="none">
                                In Suno Trash
                              </Badge>
                            )}
                            {generation.remoteState === 'missing' && (
                              <Badge size="sm" variant="default" radius="sm" tt="none">
                                Remote Missing
                              </Badge>
                            )}
                          </Group>
                        </Table.Td>
                        <Table.Td>{ratingText(generation.rating)}</Table.Td>
                        <Table.Td>{generationDuration(generation)}</Table.Td>
                        <Table.Td>
                          {taken ? (
                            <Text size="sm">Already a source</Text>
                          ) : (
                            <Button
                              size="compact-sm"
                              variant="default"
                              aria-label={`Choose ${generation.shortcode}`}
                              onClick={() => {
                                onPick(generationSource(generation, song.title));
                              }}
                            >
                              Choose
                            </Button>
                          )}
                        </Table.Td>
                      </Table.Tr>
                    );
                  })}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          )}
        </Stack>
      )}
      <Divider />
      <Text size="sm" fw={500}>
        Or a clip n8Tracks has not imported
      </Text>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          const sunoId = pastedSunoId(pasted);
          if (sunoId === undefined) {
            setPasteError('Paste a Suno song address (https://suno.com/song/…) or a clip ID.');
            return;
          }
          onPick(externalSource(sunoId));
        }}
      >
        <Group gap="sm" align="flex-end">
          <TextInput
            label="Suno song address or clip ID"
            description="For a clip n8Tracks has not imported: it is kept by its Suno ID."
            value={pasted}
            error={pasteError}
            style={{ flex: 1 }}
            onChange={(event) => {
              setPasted(event.currentTarget.value);
              setPasteError(undefined);
            }}
          />
          <Button type="submit" variant="default">
            Use this clip
          </Button>
        </Group>
      </form>
    </Stack>
  );
}
