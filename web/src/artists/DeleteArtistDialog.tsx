import { Button, Group, Loader, Modal, Radio, Stack, Text } from '@mantine/core';
import { useEffect, useState } from 'react';
import { deleteArtist, searchArtists, type Artist, type ArtistCreditChoice } from '../api/artists';
import { readCatalogSettings } from '../api/catalogSettings';
import type { SongArtist } from '../api/songs';
import { ArtistPicker } from '../common/ArtistPicker';
import { creditCountText } from './artistDeletion';

const FAILED_MESSAGE =
  'Not deleted: n8Tracks did not answer as expected. Check that it is running and try again.';

/** What the dialog learns once it opens: whether other Artists exist, and whether this is the default. */
interface Context {
  othersExist: boolean;
  isDefault: boolean;
}

/**
 * The confirmation for deleting an Artist (#104), opened while `opened`. It states how many Songs
 * and Albums credit the Artist and, when any do, asks whether their credits go to another Artist
 * (chosen with the Artist picker, which can create one) or are removed; with no other Artist, only
 * removal is offered. An Artist nothing credits needs only the confirmation. It says when the
 * Artist is the default for new Songs, which the deletion clears, and that the deletion is
 * permanent. The delete carries the revision the page holds; when the Artist changed meanwhile
 * (credited elsewhere, say), the page takes in the Artist as it is now (`onCurrent`), so the counts
 * shown are current, and the user chooses again.
 */
export function DeleteArtistDialog({
  artist,
  opened,
  onClose,
  onCurrent,
  onDeleted,
}: {
  artist: Artist;
  opened: boolean;
  onClose: () => void;
  onCurrent: (current: Artist) => void;
  onDeleted: (choice: ArtistCreditChoice) => void;
}) {
  const [context, setContext] = useState<Context | undefined>();
  const [mode, setMode] = useState<'reassign' | 'remove' | undefined>();
  const [target, setTarget] = useState<SongArtist | undefined>();
  const [deleting, setDeleting] = useState(false);
  const [message, setMessage] = useState<string | undefined>();

  // Each time it opens, it asks whether another Artist exists and whether this one is the default.
  useEffect(() => {
    if (!opened) {
      return;
    }
    const controller = new AbortController();
    void Promise.all([
      searchArtists('', controller.signal, 2),
      readCatalogSettings(controller.signal),
    ]).then(([artists, settings]) => {
      if (!controller.signal.aborted) {
        setContext({
          othersExist: artists === undefined || artists.some((other) => other.id !== artist.id),
          isDefault: settings?.defaultArtist?.id === artist.id,
        });
      }
    });
    return () => {
      controller.abort();
    };
  }, [opened, artist.id]);

  const credited = artist.songCount > 0 || artist.albumCount > 0;
  const othersExist = context?.othersExist ?? false;
  const chosen: ArtistCreditChoice | undefined = !credited
    ? { kind: 'none' }
    : mode === 'remove' || (mode === undefined && context !== undefined && !othersExist)
      ? { kind: 'remove' }
      : mode === 'reassign' && target !== undefined
        ? { kind: 'reassign', to: target }
        : undefined;

  const close = () => {
    setMessage(undefined);
    setMode(undefined);
    setTarget(undefined);
    setContext(undefined);
    onClose();
  };

  const confirm = async () => {
    if (chosen === undefined) {
      return;
    }
    setDeleting(true);
    setMessage(undefined);
    const result = await deleteArtist(artist, chosen);
    setDeleting(false);
    switch (result.kind) {
      case 'deleted':
        onDeleted(chosen);
        return;
      case 'conflict':
        onCurrent(result.current);
        setMessage(
          'Not deleted: the Artist was changed elsewhere since this opened. Check the counts, then choose Delete again.',
        );
        return;
      case 'in-use':
        onCurrent(result.current);
        setContext((previous) => previous && { ...previous, isDefault: result.isDefaultArtist });
        setMessage(
          `Not deleted: ${creditCountText(result.current)} now ${result.current.songCount + result.current.albumCount === 1 ? 'credits' : 'credit'} this Artist. Choose what happens to the credits, then choose Delete again.`,
        );
        return;
      case 'invalid':
        setTarget(undefined);
        setMessage(`Not deleted: ${result.message}`);
        return;
      case 'gone':
        setMessage('This Artist is no longer there: it may have been deleted elsewhere.');
        return;
      case 'failed':
        setMessage(FAILED_MESSAGE);
    }
  };

  return (
    <Modal
      opened={opened}
      onClose={close}
      title={`Delete “${artist.name}”?`}
      centered
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <Stack gap="md">
        <Text data-testid="delete-artist-summary">
          Deleting the Artist “{artist.name}” is permanent. Its aliases, links, and artwork are
          deleted with it. No Song or Album is deleted.
        </Text>
        <Text data-testid="delete-artist-credits">
          {credited
            ? `It is credited on ${creditCountText(artist)}.`
            : 'No Song or Album credits it, so nothing else changes.'}
        </Text>
        {context === undefined && <Loader size="sm" aria-label="Checking the Artist" />}
        {context?.isDefault === true && (
          <Text data-testid="delete-artist-default">
            It is the default Artist for new Songs. The default will be cleared, so new Songs will
            have no primary Artist until another default is chosen in Settings → Catalog.
          </Text>
        )}
        {credited && context !== undefined && (
          <Stack gap="xs">
            {othersExist ? (
              <Radio.Group
                label="What happens to its credits"
                value={mode ?? null}
                onChange={(value) => {
                  setMode(value === 'remove' ? 'remove' : 'reassign');
                  setMessage(undefined);
                }}
              >
                <Stack gap="xs" mt="xs">
                  <Radio value="reassign" label="Reassign them to another Artist" />
                  <Radio value="remove" label="Remove them" />
                </Stack>
              </Radio.Group>
            ) : (
              <Text data-testid="delete-artist-only-removal">
                There is no other Artist to reassign them to, so they will be removed.
              </Text>
            )}
            {mode === 'reassign' && (
              <Stack gap={4}>
                <ArtistPicker
                  label="Reassign to"
                  description="A Song that already credits this Artist keeps one credit, in the more senior role."
                  exclude={[artist.id]}
                  allowCreate
                  onChoose={(chosenArtist) => {
                    setTarget(chosenArtist);
                    setMessage(undefined);
                  }}
                />
                {target !== undefined && (
                  <Text size="sm" data-testid="delete-artist-target">
                    The credits go to “{target.name}”.
                  </Text>
                )}
              </Stack>
            )}
            {chosen?.kind === 'remove' && (
              <Text size="sm" data-testid="delete-artist-removal">
                Songs it is the primary Artist of are left with no primary Artist, and Albums it is
                the Album Artist of are left with none.
              </Text>
            )}
          </Stack>
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
            disabled={chosen === undefined || context === undefined}
            onClick={() => {
              void confirm();
            }}
          >
            Delete Artist
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
