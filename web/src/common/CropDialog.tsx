import { Button, Group, Modal, Stack, Text } from '@mantine/core';
import {
  useId,
  useRef,
  useState,
  type KeyboardEvent,
  type PointerEvent as ReactPointerEvent,
} from 'react';
import type { Artwork, ArtworkCrop } from '../api/artwork';
import {
  centredCrop,
  clampCrop,
  cropStep,
  cropText,
  moveCrop,
  resizeCrop,
  smallestCrop,
} from './cropRules';

/** The most room the image takes in the dialog, in CSS pixels on its long side. */
const SHOWN_LONG_SIDE = 440;

/** What a pointer is doing to the selection: moving it from where it was grabbed, or resizing it from its top-left corner. */
type Drag =
  | { kind: 'move'; pointerId: number; grabX: number; grabY: number; from: ArtworkCrop }
  | { kind: 'resize'; pointerId: number; from: ArtworkCrop };

/** The arrow keys, and the direction each moves the selection. */
const ARROWS: Readonly<Record<string, readonly [number, number]>> = {
  ArrowLeft: [-1, 0],
  ArrowRight: [1, 0],
  ArrowUp: [0, -1],
  ArrowDown: [0, 1],
};

/** Whether a key grows (1) or shrinks (-1) the selection, or neither (0). */
function resizeDirection(event: KeyboardEvent): number {
  if (event.key === '+' || event.key === '=' || event.code === 'NumpadAdd') {
    return 1;
  }
  if (event.key === '-' || event.key === '_' || event.code === 'NumpadSubtract') {
    return -1;
  }
  return 0;
}

/**
 * Whether Shift asks for the large step. On most keyboards "+" is itself Shift with "=", so a "+"
 * typed that way is the small step; Shift with the keypad's plus is the large one.
 */
function largeStep(event: KeyboardEvent): boolean {
  return event.shiftKey && !(event.key === '+' && event.code === 'Equal');
}

/**
 * Positions a square crop over an owner's artwork, without changing the image: the whole image is
 * shown with a square selection over it, which a pointer moves (drag inside it) or resizes (drag
 * its corner handle). The selection is a keyboard control too: the arrow keys move it by 1 percent
 * of the image's shorter side (10 percent with Shift), and plus and minus resize it by 1 percent
 * about its centre (10 percent with Shift), always within the image and never under the minimum.
 * Its left, top, and size in pixels of the original are stated in a polite live region when a key
 * is released or a drag ends. It opens on the crop the owner set, or the centred square.
 */
export function CropDialog({
  opened,
  artwork,
  title,
  busy,
  error,
  onClose,
  onSave,
}: {
  opened: boolean;
  artwork: Artwork;
  /** The owner's title or name, for the dialog's title. */
  title: string;
  /** Whether the crop is being saved: the controls wait. */
  busy: boolean;
  /** Why the last save failed, if it did. */
  error: string | undefined;
  onClose: () => void;
  onSave: (crop: ArtworkCrop) => void;
}) {
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={`Crop the artwork for ${title}`}
      size="auto"
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      {opened && (
        <CropControl
          artwork={artwork}
          busy={busy}
          error={error}
          onCancel={onClose}
          onSave={onSave}
        />
      )}
    </Modal>
  );
}

function CropControl({
  artwork,
  busy,
  error,
  onCancel,
  onSave,
}: {
  artwork: Artwork;
  busy: boolean;
  error: string | undefined;
  onCancel: () => void;
  onSave: (crop: ArtworkCrop) => void;
}) {
  const { width, height } = artwork;
  const helpId = useId();
  const frame = useRef<HTMLDivElement>(null);
  const drag = useRef<Drag | null>(null);
  const [crop, setCrop] = useState<ArtworkCrop>(() => artwork.crop ?? centredCrop(width, height));
  const [stated, setStated] = useState(() => cropText(artwork.crop ?? centredCrop(width, height)));

  // Shown no larger than the original, and no larger than the room the dialog has.
  const scale = Math.min(1, SHOWN_LONG_SIDE / Math.max(width, height));
  const shown = (pixels: number) => pixels * scale;

  const keyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    const step = cropStep(width, height, largeStep(event));
    const arrow = ARROWS[event.key];
    if (arrow !== undefined) {
      event.preventDefault();
      setCrop((current) => moveCrop(current, arrow[0] * step, arrow[1] * step, width, height));
      return;
    }
    const direction = resizeDirection(event);
    if (direction !== 0) {
      event.preventDefault();
      setCrop((current) => resizeCrop(current, direction * step, width, height));
    }
  };

  /** Where the pointer is, in pixels of the original. */
  const pointerAt = (event: ReactPointerEvent) => {
    const box = frame.current?.getBoundingClientRect();
    return box === undefined
      ? { x: 0, y: 0 }
      : { x: (event.clientX - box.left) / scale, y: (event.clientY - box.top) / scale };
  };

  const startDrag = (event: ReactPointerEvent<HTMLDivElement>, kind: Drag['kind']) => {
    if (busy || event.button !== 0) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    event.currentTarget.setPointerCapture(event.pointerId);
    const at = pointerAt(event);
    drag.current =
      kind === 'move'
        ? { kind, pointerId: event.pointerId, grabX: at.x, grabY: at.y, from: crop }
        : { kind, pointerId: event.pointerId, from: crop };
  };

  const moveDrag = (event: ReactPointerEvent<HTMLDivElement>) => {
    const current = drag.current;
    if (current?.pointerId !== event.pointerId) {
      return;
    }
    const at = pointerAt(event);
    if (current.kind === 'move') {
      setCrop(
        moveCrop(
          current.from,
          Math.round(at.x - current.grabX),
          Math.round(at.y - current.grabY),
          width,
          height,
        ),
      );
    } else {
      const { x, y } = current.from;
      const size = Math.min(
        Math.max(Math.round(Math.max(at.x - x, at.y - y)), smallestCrop(width, height)),
        width - x,
        height - y,
      );
      setCrop(clampCrop({ x, y, size }, width, height));
    }
  };

  const endDrag = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (drag.current?.pointerId !== event.pointerId) {
      return;
    }
    drag.current = null;
    setStated(cropText(crop));
  };

  return (
    <Stack gap="sm">
      <Text size="sm" id={helpId}>
        Drag the square, or its corner to resize it. With the square focused, the arrow keys move it
        and plus and minus resize it; hold Shift for larger steps. The image itself is not changed.
      </Text>
      <div
        ref={frame}
        style={{
          position: 'relative',
          width: shown(width),
          height: shown(height),
          overflow: 'hidden',
          alignSelf: 'center',
          touchAction: 'none',
          userSelect: 'none',
        }}
      >
        <img
          src={artwork.urls['1024']}
          alt=""
          draggable={false}
          width={shown(width)}
          height={shown(height)}
          style={{ display: 'block', width: shown(width), height: shown(height) }}
        />
        {/* eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions -- a 2-D crop selection has no ARIA widget role; role=application hands it the arrow and plus/minus keys (AC: keyboard operable) */}
        <div
          role="application"
          aria-roledescription="crop selection"
          aria-label="Crop selection"
          aria-describedby={helpId}
          // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- the crop selection must take keyboard focus to be moved and resized by keys (AC)
          tabIndex={0}
          data-testid="crop-selection"
          data-crop={`${String(crop.x)},${String(crop.y)},${String(crop.size)}`}
          onKeyDown={keyDown}
          onKeyUp={() => {
            setStated(cropText(crop));
          }}
          onPointerDown={(event) => {
            startDrag(event, 'move');
          }}
          onPointerMove={moveDrag}
          onPointerUp={endDrag}
          onPointerCancel={endDrag}
          style={{
            position: 'absolute',
            left: shown(crop.x),
            top: shown(crop.y),
            width: shown(crop.size),
            height: shown(crop.size),
            boxSizing: 'border-box',
            border: '2px solid #fff',
            outline: '1px solid #000',
            boxShadow: '0 0 0 9999px rgba(0, 0, 0, 0.55)',
            cursor: 'move',
          }}
        >
          <div
            aria-hidden="true"
            data-testid="crop-resize-handle"
            onPointerDown={(event) => {
              startDrag(event, 'resize');
            }}
            onPointerMove={moveDrag}
            onPointerUp={endDrag}
            onPointerCancel={endDrag}
            style={{
              position: 'absolute',
              right: -7,
              bottom: -7,
              width: 14,
              height: 14,
              background: '#fff',
              border: '1px solid #000',
              cursor: 'nwse-resize',
            }}
          />
        </div>
      </div>
      <Text size="sm" role="status" aria-live="polite" data-testid="crop-position">
        {stated}
      </Text>
      {error !== undefined && (
        <Text size="sm" c="var(--mantine-color-error)" role="alert" data-testid="crop-error">
          {error}
        </Text>
      )}
      <Group justify="space-between" gap="sm">
        <Button
          variant="default"
          disabled={busy}
          onClick={() => {
            const centred = centredCrop(width, height);
            setCrop(centred);
            setStated(cropText(centred));
          }}
        >
          Centre
        </Button>
        <Group gap="sm">
          <Button variant="default" disabled={busy} onClick={onCancel}>
            Cancel
          </Button>
          <Button
            loading={busy}
            onClick={() => {
              onSave(crop);
            }}
          >
            Save crop
          </Button>
        </Group>
      </Group>
    </Stack>
  );
}
