import { Button, Group, Loader, Modal, Stack, Text, UnstyledButton } from '@mantine/core';
import { useState } from 'react';
import { pickGenerationArtwork, useSongGenerations, type Generation } from '../api/generations';
import type { Song } from '../api/songs';
import { ArtworkImage } from '../common/ArtworkImage';
import type { ArtworkChooseControls } from '../common/ArtworkPicker';

/** What the user is told when n8Tracks gives no usable answer to a pick. */
const PICK_FAILED_MESSAGE =
  'The artwork could not be changed. Check that n8Tracks is running and try again.';

/** The Song's Generations that have an image, in the list's order, for the chooser. */
function GenerationImages({
  song,
  onPick,
}: {
  song: Song;
  onPick: (generation: Generation) => void;
}) {
  const generations = useSongGenerations(song.id);
  if (generations.state.phase === 'loading') {
    return <Loader aria-label="Loading the Generations" />;
  }
  if (generations.state.phase !== 'ready') {
    return <Text role="alert">The Generations could not be loaded. Try again.</Text>;
  }
  const withImages = generations.state.data.filter((generation) => generation.artwork !== null);
  if (withImages.length === 0) {
    return (
      <Text size="sm" data-testid="no-generation-images">
        None of this Song’s Generations has an image yet.
      </Text>
    );
  }
  return (
    <Group gap="sm" role="list" aria-label="Generation images">
      {withImages.map((generation) => (
        <div role="listitem" key={generation.id}>
          <UnstyledButton
            aria-label={`Use the image of ${generation.shortcode}`}
            data-generation-image={generation.shortcode}
            onClick={() => {
              onPick(generation);
            }}
            style={{ borderRadius: 'var(--mantine-radius-sm)' }}
          >
            <Stack gap={4} align="center">
              <ArtworkImage
                artwork={generation.artwork}
                title={generation.shortcode}
                size="320"
                pixels={96}
              />
              <Text size="xs">{generation.shortcode}</Text>
            </Stack>
          </UnstyledButton>
        </div>
      ))}
    </Group>
  );
}

/**
 * "Choose from Generations" in a Song's artwork control (#121): the images of the Song's
 * Generations, one of which the user picks as the Song's own artwork. n8Tracks copies it, so the
 * Song keeps it whatever later happens to the Generation; artwork the Song had is kept in deleted
 * items, and the crop goes back to the centre. A conflict with another change of the Song is sent
 * again once with its current revision, since the user's choice does not depend on the rest of it.
 */
export function GenerationArtworkChooser({
  song,
  controls,
  onSong,
}: {
  song: Song;
  controls: ArtworkChooseControls;
  onSong: (song: Song) => void;
}) {
  const [opened, setOpened] = useState(false);

  const pick = async (generation: Generation) => {
    setOpened(false);
    controls.begin();
    let result = await pickGenerationArtwork(song.id, generation.id, song.revision);
    if (result.kind === 'conflict') {
      result = await pickGenerationArtwork(song.id, generation.id, result.current.revision);
    }
    switch (result.kind) {
      case 'saved':
        onSong(result.record);
        controls.end({ status: `Artwork set from Generation ${generation.shortcode}.` });
        break;
      case 'refused':
        controls.end({ error: `${result.message} The artwork was not changed.` });
        break;
      default:
        controls.end({ error: PICK_FAILED_MESSAGE });
    }
  };

  return (
    <>
      <Button
        variant="default"
        size="compact-sm"
        disabled={controls.disabled}
        onClick={() => {
          setOpened(true);
        }}
      >
        Choose from Generations
      </Button>
      <Modal
        opened={opened}
        onClose={() => {
          setOpened(false);
        }}
        title="Choose a Generation’s image"
        closeButtonProps={{ 'aria-label': 'Close' }}
      >
        <Stack gap="sm">
          <Text size="sm">
            The image is copied to the Song, so it stays even if the Generation changes or is
            deleted.
          </Text>
          {opened && (
            <GenerationImages
              song={song}
              onPick={(generation) => {
                void pick(generation);
              }}
            />
          )}
        </Stack>
      </Modal>
    </>
  );
}
