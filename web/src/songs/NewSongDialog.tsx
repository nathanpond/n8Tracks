import { Button, Group, Loader, Modal, Stack, Text, TextInput, Textarea } from '@mantine/core';
import { useEffect, useState, type ClipboardEvent, type SyntheticEvent } from 'react';
import { readCatalogSettings } from '../api/catalogSettings';
import {
  CONCEPT_MAXIMUM_LENGTH,
  createSong,
  TITLE_MAXIMUM_LENGTH,
  type Song,
  type SongArtist,
} from '../api/songs';
import { ArtistPicker } from '../common/ArtistPicker';
import { Notice } from '../components/Notice';
import { conceptError, singleLine, titleError } from './songRules';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

/** The primary Artist being chosen: loading the default, the default unknown (left to the API), or a choice. */
type PrimaryChoice =
  | { phase: 'loading' }
  | { phase: 'unknown' }
  | { phase: 'chosen'; artist: SongArtist | null; isDefault: boolean };

/**
 * Asks for a new Song's title, concept, and primary Artist, and creates it. The primary Artist
 * starts as the default Artist (Settings → Catalog); the user may choose another or none, and what
 * is shown is sent, so it overrides the default. If the default cannot be read, none is sent and
 * the API applies the default itself. The fields are checked as the API checks them before
 * anything is sent, and the API's own field errors are shown the same way. Anything else that goes
 * wrong keeps the dialog open with what was typed. `onCreated` gets the new Song.
 */
export function NewSongDialog({
  opened,
  onClose,
  onCreated,
}: {
  opened: boolean;
  onClose: () => void;
  onCreated: (song: Song) => void;
}) {
  const [title, setTitle] = useState('');
  const [concept, setConcept] = useState('');
  const [errors, setErrors] = useState<{
    title?: string;
    concept?: string;
    primaryArtist?: string;
  }>({});
  const [failed, setFailed] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [primary, setPrimary] = useState<PrimaryChoice>({ phase: 'loading' });

  // Each time the dialog opens, the primary Artist starts as the default as it is then.
  useEffect(() => {
    if (!opened) {
      return;
    }
    const controller = new AbortController();
    void readCatalogSettings(controller.signal).then((settings) => {
      if (!controller.signal.aborted) {
        setPrimary(
          settings === undefined
            ? { phase: 'unknown' }
            : {
                phase: 'chosen',
                artist: settings.defaultArtist,
                isDefault: settings.defaultArtist !== null,
              },
        );
      }
    });
    return () => {
      controller.abort();
      setPrimary({ phase: 'loading' });
    };
  }, [opened]);

  const close = () => {
    setTitle('');
    setConcept('');
    setErrors({});
    setFailed(false);
    onClose();
  };

  const pasteTitle = (event: ClipboardEvent<HTMLInputElement>) => {
    const pasted = event.clipboardData.getData('text');
    if (!/[\r\n]/.test(pasted)) {
      return;
    }
    // A text field would drop the line breaks, running the words together; spaces keep them apart.
    event.preventDefault();
    const input = event.currentTarget;
    const start = input.selectionStart ?? input.value.length;
    const end = input.selectionEnd ?? start;
    const replacement = singleLine(pasted);
    setTitle(input.value.slice(0, start) + replacement + input.value.slice(end));
  };

  const submit = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    const local = { title: titleError(title), concept: conceptError(concept) };
    setFailed(false);
    if (local.title !== undefined || local.concept !== undefined) {
      setErrors(local);
      return;
    }

    setSubmitting(true);
    const result = await createSong({
      title: singleLine(title),
      concept,
      ...(primary.phase === 'chosen' ? { primaryArtistId: primary.artist?.id ?? null } : {}),
    });
    setSubmitting(false);

    switch (result.kind) {
      case 'created':
        setTitle('');
        setConcept('');
        setErrors({});
        onCreated(result.song);
        return;
      case 'invalid':
        setErrors({
          title: result.errors.title?.join(' '),
          concept: result.errors.concept?.join(' '),
          primaryArtist: result.errors.primaryArtistId?.join(' '),
        });
        if (
          result.errors.title === undefined &&
          result.errors.concept === undefined &&
          result.errors.primaryArtistId === undefined
        ) {
          setFailed(true);
        }
        return;
      case 'failed':
        setErrors({});
        setFailed(true);
    }
  };

  return (
    <Modal
      opened={opened}
      onClose={close}
      title="New Song"
      centered
      size="lg"
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <form
        noValidate
        aria-label="New Song"
        onSubmit={(event) => {
          void submit(event);
        }}
      >
        <Stack gap="md">
          <TextInput
            label="Title"
            description={`One line, up to ${TITLE_MAXIMUM_LENGTH.toLocaleString('en-US')} characters. Titles do not have to be unique.`}
            required
            value={title}
            onChange={(event) => {
              setTitle(singleLine(event.currentTarget.value));
            }}
            onPaste={pasteTitle}
            error={errors.title}
            aria-invalid={errors.title !== undefined}
            data-autofocus
          />
          <Textarea
            label="Concept"
            description={`Optional: what the Song is about, up to ${CONCEPT_MAXIMUM_LENGTH.toLocaleString('en-US')} characters.`}
            rows={5}
            resize="vertical"
            value={concept}
            onChange={(event) => {
              setConcept(event.currentTarget.value);
            }}
            error={errors.concept}
            aria-invalid={errors.concept !== undefined}
          />
          <Stack gap={4} role="group" aria-labelledby="new-song-primary-artist">
            <Text fw={500} size="sm" id="new-song-primary-artist">
              Primary Artist
            </Text>
            {primary.phase === 'loading' && (
              <Loader size="xs" aria-label="Loading the default Artist" />
            )}
            {primary.phase === 'unknown' && (
              <Text size="sm" data-testid="new-song-primary">
                The default Artist, if one is set.
              </Text>
            )}
            {primary.phase === 'chosen' && (
              <Group gap="xs" data-testid="new-song-primary">
                <Text size="sm">
                  {primary.artist === null
                    ? 'None'
                    : `${primary.artist.name}${primary.isDefault ? ' (the default Artist)' : ''}`}
                </Text>
                {primary.artist !== null && (
                  <Button
                    variant="subtle"
                    size="compact-xs"
                    aria-label={`Remove primary Artist ${primary.artist.name}`}
                    onClick={() => {
                      setPrimary({ phase: 'chosen', artist: null, isDefault: false });
                    }}
                  >
                    Remove
                  </Button>
                )}
              </Group>
            )}
            <ArtistPicker
              label="Choose another primary Artist"
              exclude={
                primary.phase === 'chosen' && primary.artist !== null ? [primary.artist.id] : []
              }
              error={errors.primaryArtist}
              onChoose={(artist) => {
                setPrimary({ phase: 'chosen', artist, isDefault: false });
              }}
            />
          </Stack>
          <div role="status">
            {failed && (
              <Notice title="Song not created">
                <Text>{FAILED_MESSAGE}</Text>
              </Notice>
            )}
          </div>
          <Group justify="end">
            <Button variant="default" onClick={close}>
              Cancel
            </Button>
            <Button type="submit" loading={submitting}>
              Create Song
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
