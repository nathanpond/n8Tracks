import {
  Anchor,
  Button,
  CloseButton,
  Group,
  List,
  Modal,
  NativeSelect,
  Stack,
  Text,
} from '@mantine/core';
import { useState } from 'react';
import { Link } from 'react-router';
import {
  relateSongs,
  relationshipChoices,
  removeRelationship,
  useRelationshipTypes,
  type RelateResult,
} from '../api/relationships';
import { readSong, type Song, type SongRelationship } from '../api/songs';
import { SongSearch } from '../common/SongSearch';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

/** The Song's relationships grouped by name as seen from it, in the order the API gives (by name, then title). */
function grouped(relationships: readonly SongRelationship[]): [string, SongRelationship[]][] {
  const groups: [string, SongRelationship[]][] = [];
  for (const relationship of relationships) {
    const last = groups.at(-1);
    if (last?.[0] === relationship.name) {
      last[1].push(relationship);
    } else {
      groups.push([relationship.name, [relationship]]);
    }
  }
  return groups;
}

/** The confirmation before a relationship is removed; it goes from both Songs. */
function RemoveDialog({
  relationship,
  onClose,
  onRemove,
}: {
  relationship: SongRelationship | undefined;
  onClose: () => void;
  onRemove: (relationship: SongRelationship) => Promise<void>;
}) {
  const [removing, setRemoving] = useState(false);

  return (
    <Modal
      opened={relationship !== undefined}
      onClose={onClose}
      title="Remove relationship"
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <Stack gap="md">
        <Text data-testid="remove-relationship-summary">
          Remove{' '}
          <strong>
            {relationship?.name}: {relationship?.song.title}
          </strong>
          ? It is removed from {relationship?.song.title} as well. Neither Song is changed
          otherwise.
        </Text>
        <Group justify="end">
          <Button variant="default" onClick={onClose} data-autofocus>
            Cancel
          </Button>
          <Button
            color="red"
            loading={removing}
            onClick={() => {
              if (relationship === undefined) {
                return;
              }
              setRemoving(true);
              void onRemove(relationship).finally(() => {
                setRemoving(false);
              });
            }}
          >
            Remove
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/**
 * The Song's relationships to other Songs, grouped by the type's name as seen from this Song
 * (alphabetically), the Songs by title within each, each linking to that Song. The user relates
 * another Song by choosing how (a type, in the direction this Song reads it: "Sequel to" or "Has
 * sequel") and then finding the Song with the shared Song search, which leaves this Song out and
 * shows Songs already related that way as disabled. A relationship is removed, from both Songs,
 * after a confirmation. Neither change moves the Song's revision, so nothing here can conflict
 * with an edit; the Song as the API answers it replaces the page's copy.
 */
export function RelatedSection({ song, onSong }: { song: Song; onSong: (song: Song) => void }) {
  const { state, reload } = useRelationshipTypes();
  const [choice, setChoice] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const [removing, setRemoving] = useState<SongRelationship | undefined>();
  const choices = state.phase === 'ready' ? relationshipChoices(state.data) : [];
  const chosen = choices.find((option) => option.value === choice);
  const relatedUnderType =
    chosen === undefined
      ? []
      : song.relationships
          .filter((relationship) => relationship.typeId === chosen.typeId)
          .map((relationship) => relationship.song.id);

  const settle = (result: RelateResult, done: () => void) => {
    switch (result.kind) {
      case 'saved':
        onSong(result.song);
        done();
        break;
      case 'exists':
        onSong(result.song);
        setError('These Songs are already related this way.');
        break;
      case 'invalid':
        setError(Object.values(result.errors).flat().join(' ') || FAILED_MESSAGE);
        reload();
        break;
      case 'not-found':
        setError('That relationship no longer exists. The Song has been read again.');
        void readSong(song.id).then((current) => {
          if (current !== undefined) {
            onSong(current);
          }
        });
        break;
      default:
        setError(FAILED_MESSAGE);
    }
  };

  const relate = async (other: Song) => {
    if (chosen === undefined) {
      return;
    }
    setBusy(true);
    setError(undefined);
    const result = await relateSongs(song, chosen.typeId, chosen.direction, other.id);
    setBusy(false);
    settle(result, () => undefined);
  };

  const remove = async (relationship: SongRelationship) => {
    setError(undefined);
    const result = await removeRelationship(song, relationship.id);
    if (result.kind !== 'saved') {
      setRemoving(undefined);
    }
    settle(result, () => {
      setRemoving(undefined);
    });
  };

  return (
    <Stack gap={4} role="group" aria-labelledby="song-related">
      <Text fw={500} size="sm" id="song-related">
        Related
      </Text>
      {song.relationships.length === 0 ? (
        <Text size="sm" c="var(--n8-color-secondary-text)">
          Not related to any Song.
        </Text>
      ) : (
        <Stack gap={6} data-testid="song-relationships">
          {grouped(song.relationships).map(([name, relationships], index) => (
            <div key={name} data-relationship-group={name}>
              <Text size="sm" id={`song-related-${String(index)}`}>
                {name}:
              </Text>
              <List size="sm" aria-labelledby={`song-related-${String(index)}`}>
                {relationships.map((relationship) => (
                  <List.Item
                    key={relationship.id}
                    data-relationship-name={relationship.name}
                    data-song-title={relationship.song.title}
                  >
                    <Group gap={4} wrap="nowrap">
                      <Anchor
                        component={Link}
                        to={`/songs/${relationship.song.shortcode}`}
                        underline="always"
                      >
                        {relationship.song.title}
                      </Anchor>
                      <CloseButton
                        size="sm"
                        aria-label={`Remove ${relationship.name}: ${relationship.song.title}`}
                        onClick={() => {
                          setRemoving(relationship);
                        }}
                      />
                    </Group>
                  </List.Item>
                ))}
              </List>
            </div>
          ))}
        </Stack>
      )}
      <NativeSelect
        label="Relationship"
        description="How this Song relates to the one you find below."
        value={choice}
        disabled={state.phase !== 'ready'}
        onChange={(event) => {
          setChoice(event.currentTarget.value);
          setError(undefined);
        }}
        data={[
          { value: '', label: 'Choose how…' },
          {
            group: 'System types',
            items: choices
              .filter((option) => option.system)
              .map((option) => ({ value: option.value, label: option.label })),
          },
          ...(choices.some((option) => !option.system)
            ? [
                {
                  group: 'Your types',
                  items: choices
                    .filter((option) => !option.system)
                    .map((option) => ({ value: option.value, label: option.label })),
                },
              ]
            : []),
        ]}
      />
      <SongSearch
        label="Related Song"
        description={
          chosen === undefined
            ? 'Choose a relationship first.'
            : `This Song will show it as "${chosen.label}: <the Song you choose>".`
        }
        disabled={chosen === undefined}
        exclude={[song.id]}
        unavailable={relatedUnderType}
        unavailableNote="already related this way"
        busy={busy}
        error={error}
        onChoose={(other) => {
          void relate(other);
        }}
      />
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Group gap="xs">
          <Text size="sm" c="var(--mantine-color-error)">
            The relationship types could not be loaded.
          </Text>
          <Button variant="default" size="compact-xs" onClick={reload}>
            Try again
          </Button>
        </Group>
      )}
      <RemoveDialog
        key={removing?.id ?? 'none'}
        relationship={removing}
        onClose={() => {
          setRemoving(undefined);
        }}
        onRemove={remove}
      />
    </Stack>
  );
}
