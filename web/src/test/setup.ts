import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach, beforeEach, vi } from 'vitest';

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
  vi.useRealTimers();
  vi.unstubAllGlobals();
  window.localStorage.clear();
  document.head.querySelectorAll('base').forEach((element) => {
    element.remove();
  });
});
