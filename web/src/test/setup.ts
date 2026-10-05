import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach, beforeEach, vi } from 'vitest';
import { clearMaintenance } from '../api/maintenance';
import { resetSnapshots } from '../editor/useSnapshots';

// jsdom has neither of these, and Mantine needs both.
class ResizeObserverStub implements ResizeObserver {
  observe(): void {
    // Nothing is laid out in jsdom, so there is nothing to observe.
  }

  unobserve(): void {
    // See observe.
  }

  disconnect(): void {
    // See observe.
  }
}

// jsdom's Range has no geometry at all. CodeMirror measures its text through a Range when an
// animation frame comes round, and without these that throws as an uncaught error against whichever
// test is running; on a slow machine the frame comes round often enough to fail the run. Nothing
// is laid out in jsdom, so a Range has what an element has there: no boxes, and an empty rectangle.
Range.prototype.getClientRects = function getClientRects() {
  return document.createElement('span').getClientRects();
};
Range.prototype.getBoundingClientRect = function getBoundingClientRect() {
  return document.createElement('span').getBoundingClientRect();
};

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', ResizeObserverStub);
  vi.stubGlobal('matchMedia', (query: string): MediaQueryList => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn(() => false),
  }));
});

afterEach(() => {
  cleanup();
  // Snapshots not yet sent are held for the whole page, so one test's would reach the next.
  resetSnapshots();
  // Maintenance, once reported, is held for the whole page until the maintenance page clears it.
  clearMaintenance();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  window.localStorage.clear();
  document.head.querySelectorAll('base').forEach((element) => {
    element.remove();
  });
});
