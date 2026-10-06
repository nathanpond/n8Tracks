import { MantineProvider } from '@mantine/core';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { ArtworkCrop } from '../api/artwork';
import { testArtwork } from '../test/songServer';
import { CropDialog } from './CropDialog';
import {
  centredCrop,
  clampCrop,
  cropFits,
  cropStep,
  cropText,
  keptCrop,
  moveCrop,
  resizeCrop,
} from './cropRules';

const ASSET = '01a20000-0000-7000-8000-000000000001';

function renderDialog(
  change: Parameters<typeof testArtwork>[1] = {},
  onSave: (crop: ArtworkCrop) => void = () => undefined,
) {
  render(
    <MantineProvider>
      <CropDialog
        opened
        artwork={testArtwork(ASSET, change)}
        title="Night Drive"
        busy={false}
        error={undefined}
        onClose={() => undefined}
        onSave={onSave}
      />
    </MantineProvider>,
  );
}

async function selection() {
  return screen.findByRole('application', { name: 'Crop selection' });
}

function cropOf(element: HTMLElement): string | null {
  return element.getAttribute('data-crop');
}

describe('the crop rules', () => {
  it('centres the default square on the shorter side', () => {
    expect(centredCrop(1200, 600)).toEqual({ x: 300, y: 0, size: 600 });
    expect(centredCrop(600, 1200)).toEqual({ x: 0, y: 300, size: 600 });
    expect(centredCrop(301, 100)).toEqual({ x: 100, y: 0, size: 100 });
  });

  it('accepts a crop inside the image and at least 64 pixels, or the full shorter side of a small image', () => {
    expect(cropFits({ x: 0, y: 0, size: 600 }, 1200, 600)).toBe(true);
    expect(cropFits({ x: 1136, y: 536, size: 64 }, 1200, 600)).toBe(true);
    expect(cropFits({ x: 601, y: 0, size: 600 }, 1200, 600)).toBe(false);
    expect(cropFits({ x: -1, y: 0, size: 600 }, 1200, 600)).toBe(false);
    expect(cropFits({ x: 0, y: 0, size: 63 }, 1200, 600)).toBe(false);
    expect(cropFits({ x: 10, y: 0, size: 40 }, 50, 40)).toBe(true);
    expect(cropFits({ x: 0, y: 0, size: 30 }, 50, 40)).toBe(false);
  });

  it('keeps a crop for a new image only when it fits', () => {
    const crop = { x: 0, y: 0, size: 500 };
    expect(keptCrop(crop, 800, 600)).toEqual(crop);
    expect(keptCrop(crop, 400, 400)).toBeNull();
    expect(keptCrop(null, 800, 600)).toBeNull();
  });

  it('moves and resizes within the edges and the minimum', () => {
    expect(cropStep(1200, 600, false)).toBe(6);
    expect(cropStep(1200, 600, true)).toBe(60);
    expect(cropStep(50, 40, false)).toBe(1);
    expect(moveCrop({ x: 3, y: 0, size: 600 }, -6, -6, 1200, 600)).toEqual({
      x: 0,
      y: 0,
      size: 600,
    });
    expect(moveCrop({ x: 590, y: 0, size: 600 }, 60, 0, 1200, 600)).toEqual({
      x: 600,
      y: 0,
      size: 600,
    });
    expect(resizeCrop({ x: 300, y: 0, size: 600 }, -60, 1200, 600)).toEqual({
      x: 330,
      y: 30,
      size: 540,
    });
    expect(resizeCrop({ x: 330, y: 30, size: 540 }, 60, 1200, 600)).toEqual({
      x: 300,
      y: 0,
      size: 600,
    });
    expect(resizeCrop({ x: 0, y: 0, size: 70 }, -60, 1200, 600)).toEqual({ x: 3, y: 3, size: 64 });
    expect(clampCrop({ x: 2000, y: 2000, size: 5000 }, 1200, 600)).toEqual({
      x: 600,
      y: 0,
      size: 600,
    });
    expect(cropText({ x: 1, y: 2, size: 300 })).toBe('Left 1 px, top 2 px, size 300 px.');
  });
});

describe('the crop control', () => {
  it('opens on the centred square and states it', async () => {
    renderDialog();

    const square = await selection();

    expect(cropOf(square)).toBe('300,0,600');
    expect(screen.getByTestId('crop-position')).toHaveTextContent(
      'Left 300 px, top 0 px, size 600 px.',
    );
    expect(screen.getByTestId('crop-position')).toHaveAttribute('aria-live', 'polite');
    expect(square).toHaveAccessibleDescription(/arrow keys move it and plus and minus resize it/);
  });

  it('opens on the crop the owner set', async () => {
    renderDialog({ crop: { x: 10, y: 20, size: 200 } });

    expect(cropOf(await selection())).toBe('10,20,200');
  });

  it('moves by 1 percent with the arrow keys and 10 percent with Shift, stopping at the edges', async () => {
    const user = userEvent.setup();
    renderDialog();
    const square = await selection();
    square.focus();

    await user.keyboard('{ArrowLeft}');
    expect(cropOf(square)).toBe('294,0,600');
    await user.keyboard('{Shift>}{ArrowLeft}{/Shift}');
    expect(cropOf(square)).toBe('234,0,600');
    await user.keyboard('{ArrowDown}');
    expect(cropOf(square)).toBe('234,0,600');
    for (let press = 0; press < 5; press++) {
      await user.keyboard('{Shift>}{ArrowLeft}{/Shift}');
    }
    expect(cropOf(square)).toBe('0,0,600');
    await user.keyboard('{ArrowRight}');
    expect(cropOf(square)).toBe('6,0,600');
    expect(screen.getByTestId('crop-position')).toHaveTextContent(
      'Left 6 px, top 0 px, size 600 px.',
    );
  });

  it('resizes about the centre with plus and minus, 10 percent with Shift, never past the image or under the minimum', async () => {
    const user = userEvent.setup();
    renderDialog();
    const square = await selection();
    square.focus();

    await user.keyboard('-');
    expect(cropOf(square)).toBe('303,3,594');
    fireEvent.keyDown(square, { key: '_', code: 'Minus', shiftKey: true });
    expect(cropOf(square)).toBe('333,33,534');
    await user.keyboard('=');
    expect(cropOf(square)).toBe('330,30,540');
    fireEvent.keyDown(square, { key: '+', code: 'NumpadAdd', shiftKey: true });
    expect(cropOf(square)).toBe('300,0,600');
    fireEvent.keyDown(square, { key: '+', code: 'Equal', shiftKey: true });
    expect(cropOf(square)).toBe('300,0,600');
    for (let press = 0; press < 12; press++) {
      fireEvent.keyDown(square, { key: '-', code: 'NumpadSubtract', shiftKey: true });
    }
    expect(cropOf(square)?.endsWith(',64')).toBe(true);
  });

  it('states the selection when a key is released, not while it is held', async () => {
    renderDialog();
    const square = await selection();
    const position = screen.getByTestId('crop-position');

    fireEvent.keyDown(square, { key: 'ArrowRight' });
    fireEvent.keyDown(square, { key: 'ArrowRight' });
    expect(position).toHaveTextContent('Left 300 px');
    fireEvent.keyUp(square, { key: 'ArrowRight' });
    expect(position).toHaveTextContent('Left 312 px, top 0 px, size 600 px.');
  });

  it('fixes the size to the full shorter side of a small image', async () => {
    const user = userEvent.setup();
    renderDialog({ width: 50, height: 40 });
    const square = await selection();
    square.focus();

    await user.keyboard('-');
    expect(cropOf(square)).toBe('5,0,40');
    await user.keyboard('{ArrowLeft}');
    expect(cropOf(square)).toBe('4,0,40');
  });

  it('saves the selection, and Centre goes back to the default', async () => {
    const user = userEvent.setup();
    const onSave = vi.fn();
    renderDialog({ crop: { x: 0, y: 0, size: 300 } }, onSave);
    const square = await selection();

    await user.click(screen.getByRole('button', { name: 'Centre' }));
    expect(cropOf(square)).toBe('300,0,600');
    square.focus();
    await user.keyboard('{Shift>}{ArrowLeft}{/Shift}');
    await user.click(screen.getByRole('button', { name: 'Save crop' }));

    expect(onSave).toHaveBeenCalledWith({ x: 240, y: 0, size: 600 });
  });

  it('moves the square by dragging it', async () => {
    renderDialog();
    const square = await selection();
    // The test image is shown at 440 × 220, so a CSS pixel is 1,200 / 440 pixels of the original.
    square.setPointerCapture = () => undefined;

    fireEvent.pointerDown(square, { pointerId: 1, button: 0, clientX: 150, clientY: 100 });
    fireEvent.pointerMove(square, { pointerId: 1, clientX: 40, clientY: 100 });
    fireEvent.pointerUp(square, { pointerId: 1, clientX: 40, clientY: 100 });

    await waitFor(() => {
      expect(cropOf(square)).toBe('0,0,600');
    });
    expect(screen.getByTestId('crop-position')).toHaveTextContent(
      'Left 0 px, top 0 px, size 600 px.',
    );
  });
});
