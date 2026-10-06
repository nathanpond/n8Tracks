import {
  Anchor,
  Button,
  FileButton,
  Group,
  Modal,
  Stack,
  Text,
  UnstyledButton,
} from '@mantine/core';
import { useId, useRef, useState } from 'react';
import {
  ARTWORK_ACCEPT,
  artworkAlt,
  UPLOAD_FAILED_MESSAGE,
  uploadArtwork,
  type Artwork,
} from '../api/artwork';
import { ArtworkImage } from './ArtworkImage';
import { saveError } from './useInPlaceEdit';
import type { SaveOutcome } from './useRevisionedSave';

/** The edit field every owner takes its artwork in. */
export const ARTWORK_KEY = 'artworkAssetId';

/** How big the picker shows the artwork, in CSS pixels (from the 320-pixel thumbnail). */
const SHOWN_PIXELS = 200;

/**
 * An owner's artwork, chosen by uploading an image: upload one, replace it, or remove it (after a
 * confirmation). The image is uploaded first (n8Tracks judges it by its content, so a file that is
 * not really an image is refused with the reason, and nothing changes); then `save` attaches its
 * asset ID as an edit of the owner under its revision. Removing saves null. The artwork shows at
 * 320 pixels; activating it opens the 1,024-pixel image with a "View original" link.
 * Replaced or removed artwork is kept for 30 days in deleted items.
 */
export function ArtworkPicker({
  title,
  noun,
  artwork,
  save,
}: {
  /** The owner's title or name, for the text alternative. */
  title: string;
  /** What the owner is, for the messages ("Song"). */
  noun: string;
  artwork: Artwork | null;
  /** Saves the owner's `artworkAssetId` (null to remove it). */
  save: (assetId: string | null) => Promise<SaveOutcome>;
}) {
  const headingId = useId();
  const reset = useRef<() => void>(null);
  const [busy, setBusy] = useState<'uploading' | 'saving' | undefined>();
  const [error, setError] = useState<string | undefined>();
  const [status, setStatus] = useState('');
  const [enlarged, setEnlarged] = useState(false);
  const [confirming, setConfirming] = useState(false);

  const finish = (outcome: SaveOutcome, done: string) => {
    if (outcome.kind === 'saved') {
      setStatus(done);
    } else {
      setStatus('');
      setError(saveError(outcome, ARTWORK_KEY));
    }
  };

  const upload = async (file: File | null) => {
    reset.current?.();
    if (file === null) {
      return;
    }
    setError(undefined);
    setStatus('Uploading the image…');
    setBusy('uploading');
    const uploaded = await uploadArtwork(file);
    if (uploaded.kind !== 'uploaded') {
      setBusy(undefined);
      setStatus('');
      setError(
        uploaded.kind === 'refused'
          ? `${uploaded.message} The artwork was not changed.`
          : UPLOAD_FAILED_MESSAGE,
      );
      return;
    }
    setBusy('saving');
    const replacing = artwork !== null;
    const outcome = await save(uploaded.artwork.id);
    setBusy(undefined);
    finish(outcome, replacing ? 'Artwork replaced.' : 'Artwork saved.');
  };

  const remove = async () => {
    setConfirming(false);
    setError(undefined);
    setBusy('saving');
    const outcome = await save(null);
    setBusy(undefined);
    finish(outcome, 'Artwork removed.');
  };

  return (
    <Stack gap={6} role="group" aria-labelledby={headingId} data-testid="artwork-picker">
      <Text fw={500} size="sm" id={headingId}>
        Artwork
      </Text>
      {artwork === null ? (
        <ArtworkImage artwork={null} title={title} size="320" pixels={SHOWN_PIXELS} />
      ) : (
        <UnstyledButton
          onClick={() => {
            setEnlarged(true);
          }}
          aria-label={`Show the artwork for ${title} larger`}
          style={{ alignSelf: 'flex-start', borderRadius: 'var(--mantine-radius-sm)' }}
        >
          <ArtworkImage artwork={artwork} title={title} size="320" pixels={SHOWN_PIXELS} />
        </UnstyledButton>
      )}
      <Group gap="xs">
        <FileButton
          accept={ARTWORK_ACCEPT}
          resetRef={reset}
          onChange={(file) => {
            void upload(file);
          }}
        >
          {(props) => (
            <Button
              variant="default"
              size="compact-sm"
              loading={busy === 'uploading'}
              disabled={busy !== undefined}
              {...props}
            >
              {artwork === null ? 'Upload artwork' : 'Replace artwork'}
            </Button>
          )}
        </FileButton>
        {artwork !== null && (
          <Button
            variant="default"
            size="compact-sm"
            disabled={busy !== undefined}
            onClick={() => {
              setConfirming(true);
            }}
          >
            Remove artwork
          </Button>
        )}
      </Group>
      <Text size="xs" c="var(--n8-color-secondary-text)">
        A JPEG, PNG, or WebP image of up to 25 MB.
      </Text>
      {error !== undefined && (
        <Text size="sm" c="var(--mantine-color-error)" role="alert" data-testid="artwork-error">
          {error}
        </Text>
      )}
      <Text size="sm" role="status" data-testid="artwork-status">
        {status}
      </Text>

      {artwork !== null && (
        <Modal
          opened={enlarged}
          onClose={() => {
            setEnlarged(false);
          }}
          title={artworkAlt(title)}
          size="auto"
          closeButtonProps={{ 'aria-label': 'Close' }}
        >
          <Stack gap="sm" align="flex-start">
            <img
              src={artwork.urls['1024']}
              alt={artworkAlt(title)}
              style={{ maxWidth: 'min(1024px, 80vw)', maxHeight: '70vh', display: 'block' }}
            />
            <Anchor href={artwork.urls.original} target="_blank" rel="noopener" underline="always">
              View original
            </Anchor>
          </Stack>
        </Modal>
      )}

      <Modal
        opened={confirming}
        onClose={() => {
          setConfirming(false);
        }}
        title="Remove the artwork?"
        closeButtonProps={{ 'aria-label': 'Close' }}
      >
        <Stack gap="md">
          <Text size="sm" data-testid="remove-artwork-summary">
            The {noun} will show a placeholder instead. The image is kept in deleted items for 30
            days.
          </Text>
          <Group justify="flex-end" gap="sm">
            <Button
              variant="default"
              onClick={() => {
                setConfirming(false);
              }}
            >
              Cancel
            </Button>
            <Button
              color="red"
              onClick={() => {
                void remove();
              }}
            >
              Remove
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
