import { useMediaQuery } from '@mantine/hooks';
import { useState } from 'react';

/** The ID of the panel, which the header's Details control names while it is open. */
export const DETAILS_PANEL_ID = 'song-details';

/** How wide the Details panel is, in pixels. */
export const DETAILS_PANEL_WIDTH = 340;

/** Below this window width (pixels) the panel overlays the editor instead of sitting beside it. */
export const DETAILS_OVERLAY_BELOW = 1100;

export const DETAILS_OVERLAY_QUERY = `(max-width: ${String(DETAILS_OVERLAY_BELOW - 0.02)}px)`;

/** Where this browser remembers whether the panel is open (beside the editor only). */
export const DETAILS_OPEN_STORAGE_KEY = 'n8tracks.songs.detailsOpen';

function readOpen(): boolean {
  try {
    return window.localStorage.getItem(DETAILS_OPEN_STORAGE_KEY) === 'true';
  } catch {
    return false;
  }
}

function storeOpen(open: boolean) {
  try {
    window.localStorage.setItem(DETAILS_OPEN_STORAGE_KEY, String(open));
  } catch {
    // Storage may be unavailable (private mode); the panel then simply starts closed next time.
  }
}

/**
 * Whether the Song page's Details panel is open, and whether the window is narrow enough that it
 * overlays the editor. Beside the editor, the open state is remembered per browser (closed for a
 * first-time visitor); as an overlay it always starts closed and is not remembered. Crossing the
 * breakpoint starts again from that rule.
 */
export function useDetailsPanel() {
  const narrow = useMediaQuery(DETAILS_OVERLAY_QUERY, false, { getInitialValueInEffect: false });
  const [open, setOpen] = useState(() => !narrow && readOpen());
  const [laidOutNarrow, setLaidOutNarrow] = useState(narrow);
  if (laidOutNarrow !== narrow) {
    setLaidOutNarrow(narrow);
    setOpen(!narrow && readOpen());
  }

  const set = (next: boolean) => {
    setOpen(next);
    if (!narrow) {
      storeOpen(next);
    }
  };

  return {
    open,
    narrow,
    toggle: () => {
      set(!open);
    },
    close: () => {
      set(false);
    },
  };
}
