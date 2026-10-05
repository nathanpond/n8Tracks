import { Button, Group, List, Modal, Stack, Text } from '@mantine/core';
import type { ArtistMatch } from '../api/artists';

/** The other Artists that already have a name being given, one line each, saying which name matched. */
export function DuplicateMatches({ matches }: { matches: ArtistMatch[] }) {
  return (
    <List size="sm" data-testid="duplicate-matches">
      {matches.map((match) => (
        <List.Item key={`${match.id} ${match.matchedOn} ${match.matchedText}`}>
          {match.matchedOn === 'name'
            ? `${match.name} (its name)`
            : `${match.name} (its alias “${match.matchedText}”)`}
        </List.Item>
      ))}
    </List>
  );
}

/**
 * Asks whether to keep a name or alias other Artists already have: names stay allowed to repeat,
 * but the user confirms it each time a new one does. "Save anyway" confirms; Cancel, Escape, and
 * the close control go back to the form with what was typed.
 */
export function DuplicateArtistDialog({
  matches,
  onConfirm,
  onCancel,
}: {
  /** The matches to show; the dialog is open while there are some. */
  matches: ArtistMatch[] | null;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  return (
    <Modal
      opened={matches !== null}
      onClose={onCancel}
      title="Another Artist has this name"
      centered
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <Stack gap="md">
        <Text>Other Artists already have a name or alias you are giving this one:</Text>
        <DuplicateMatches matches={matches ?? []} />
        <Text>Artists can share names. Save it anyway?</Text>
        <Group justify="end">
          <Button variant="default" onClick={onCancel}>
            Cancel
          </Button>
          <Button onClick={onConfirm}>Save anyway</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
