import {
  Anchor,
  Button,
  Checkbox,
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
  type ArtworkCrop,
} from '../api/artwork';
import { ArtworkImage } from './ArtworkImage';
import { CropDialog } from './CropDialog';
import { ARTWORK_CROP_KEY, type ArtworkEdit } from './artworkField';
import { keptCrop } from './cropRules';
import { saveError } from './useInPlaceEdit';
import type { SaveOutcome } from './useRevisionedSave';

/** The edit field every owner takes its artwork in. */
export const ARTWORK_KEY = 'artworkAssetId';

/** How big the picker shows the artwork, in CSS pixels (from the 320-pixel thumbnail). */
const SHOWN_PIXELS = 200;

/**
 * An owner's artwork, chosen by uploading an image: upload one, replace it, crop it, or remove it
 * (after a confirmation). The image is uploaded first (n8Tracks judges it by its content, so a file
 * that is not really an image is refused with the reason, and nothing changes); then `save` attaches
 * its asset ID as an edit of the owner under its revision. Removing saves null. "Crop artwork"
 * positions a square crop ({@link CropDialog}) and "Reset crop" goes back to the centred square;
 * the image itself is never changed. Replacing resets the crop, unless "Keep crop" is ticked and the
 * crop fits the new image. The artwork shows at 320 pixels as its square; activating it opens the
 * whole 1,024-pixel image with a "View original" link. Replaced or removed artwork is kept for 30
 * days in deleted items.
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
  /** Saves the owner's artwork: its `artworkAssetId` (null to remove it) and its `artworkCrop`, each when given. */
  save: (edit: ArtworkEdit) => Promise<SaveOutcome>;
}) {
  const headingId = useId();
  const reset = useRef<() => void>(null);
  const [busy, setBusy] = useState<'uploading' | 'saving' | undefined>();
  const [error, setError] = useState<string | undefined>();
  const [status, setStatus] = useState('');
  const [enlarged, setEnlarged] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [cropping, setCropping] = useState(false);
  const [cropError, setCropError] = useState<string | undefined>();
  const [keepCrop, setKeepCrop] = useState(false);

  const finish = (outcome: SaveOutcome, done: string) => {
    if (outcome.kind === 'saved') {
      setStatus(done);
    } else {
      setStatus('');
      setError(saveError(outcome, ARTWORK_KEY) ?? saveError(outcome, ARTWORK_CROP_KEY));
    }
  };

  const saveCrop = async (crop: ArtworkCrop) => {
    setCropError(undefined);
    setBusy('saving');
    const outcome = await save({ crop });
    setBusy(undefined);
    if (outcome.kind === 'saved') {
      setCropping(false);
      setStatus('Crop saved.');
    } else if (outcome.kind === 'invalid' || outcome.kind === 'failed') {
      setCropError(saveError(outcome, ARTWORK_CROP_KEY));
    } else {
      setCropping(false);
    }
  };

  const resetCrop = async () => {
    setError(undefined);
    setBusy('saving');
    const outcome = await save({ crop: null });
    setBusy(undefined);
    finish(outcome, 'Crop reset to the centre.');
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
    const wanted = keepCrop ? (artwork?.crop ?? null) : null;
    const kept = keptCrop(wanted, uploaded.artwork.width, uploaded.artwork.height);
    const outcome = await save(
      kept === null
        ? { assetId: uploaded.artwork.id }
        : { assetId: uploaded.artwork.id, crop: kept },
    );
    setBusy(undefined);
    setKeepCrop(false);
    finish(
      outcome,
      !replacing
        ? 'Artwork saved.'
        : wanted !== null && kept === null
          ? 'Artwork replaced. The crop does not fit the new image, so it shows the centre.'
          : 'Artwork replaced.',
    );
  };

  const remove = async () => {
    setConfirming(false);
    setError(undefined);
    setBusy('saving');
    const outcome = await save({ assetId: null });
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
              setCropError(undefined);
              setCropping(true);
            }}
          >
            Crop artwork
          </Button>
        )}
        {artwork?.crop != null && (
          <Button
            variant="default"
            size="compact-sm"
            disabled={busy !== undefined}
            onClick={() => {
              void resetCrop();
            }}
          >
            Reset crop
          </Button>
        )}
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
      {artwork?.crop != null && (
        <Checkbox
          size="xs"
          label="Keep crop"
          description="When replacing the image, keep this crop if it fits the new one."
          checked={keepCrop}
          disabled={busy !== undefined}
          onChange={(event) => {
            setKeepCrop(event.currentTarget.checked);
          }}
        />
      )}
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
        <CropDialog
          opened={cropping}
          artwork={artwork}
          title={title}
          busy={busy === 'saving'}
          error={cropError}
          onClose={() => {
            setCropping(false);
          }}
          onSave={(crop) => {
            void saveCrop(crop);
          }}
        />
      )}

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
