import { act } from '@testing-library/react';

/** What a fake audio element holds: jsdom loads and plays nothing, so the test says what happens. */
interface FakeMediaState {
  paused: boolean;
  currentTime: number;
  duration: number;
  error: MediaError | null;
  plays: number;
}

/** The fake audio elements: their states, and the moves a test makes on the one the player made. */
export interface FakeAudio {
  /** The player's audio element (the one in the page); throws when there is none. */
  element: () => HTMLAudioElement;
  /** Every audio element whose state was touched, in the order first touched. */
  touched: HTMLMediaElement[];
  /** How many times play() was called on the element. */
  plays: () => number;
  /** The element's current position, as the fake holds it. */
  time: () => number;
  /** The audio says how long it is (metadata loaded). */
  loaded: (duration: number) => void;
  /** The audio plays on by `seconds`. */
  advance: (seconds: number) => void;
  /** The audio reaches its end. */
  end: () => void;
  /** The audio cannot be loaded or decoded (MEDIA_ERR_SRC_NOT_SUPPORTED by default). */
  fail: (code?: number) => void;
  /** Puts jsdom's own media element back. */
  restore: () => void;
}

const PROPERTIES = ['paused', 'currentTime', 'duration', 'error', 'src'] as const;
const METHODS = ['play', 'pause', 'load'] as const;

/**
 * Replaces jsdom's media element (which plays nothing and reports "not implemented") with a fake:
 * `play()` starts it at once (a `play` event), `pause()` stops it (`pause`), a new `src` starts again
 * from nothing, and the test moves time, ends it, or fails it. Call `restore` after the test.
 */
export function installFakeAudio(): FakeAudio {
  const prototype = HTMLMediaElement.prototype;
  const saved = new Map<string, PropertyDescriptor | undefined>();
  for (const name of [...PROPERTIES, ...METHODS]) {
    saved.set(name, Object.getOwnPropertyDescriptor(prototype, name));
  }
  const states = new WeakMap<HTMLMediaElement, FakeMediaState>();
  const touched: HTMLMediaElement[] = [];
  const stateOf = (element: HTMLMediaElement): FakeMediaState => {
    let state = states.get(element);
    if (state === undefined) {
      state = { paused: true, currentTime: 0, duration: Number.NaN, error: null, plays: 0 };
      states.set(element, state);
      touched.push(element);
    }
    return state;
  };
  const fire = (element: HTMLMediaElement, type: string) => {
    element.dispatchEvent(new Event(type));
  };

  Object.defineProperty(prototype, 'paused', {
    configurable: true,
    get(this: HTMLMediaElement) {
      return stateOf(this).paused;
    },
  });
  Object.defineProperty(prototype, 'currentTime', {
    configurable: true,
    get(this: HTMLMediaElement) {
      return stateOf(this).currentTime;
    },
    set(this: HTMLMediaElement, value: number) {
      stateOf(this).currentTime = value;
    },
  });
  Object.defineProperty(prototype, 'duration', {
    configurable: true,
    get(this: HTMLMediaElement) {
      return stateOf(this).duration;
    },
  });
  Object.defineProperty(prototype, 'error', {
    configurable: true,
    get(this: HTMLMediaElement) {
      return stateOf(this).error;
    },
  });
  Object.defineProperty(prototype, 'src', {
    configurable: true,
    get(this: HTMLMediaElement) {
      return this.getAttribute('src') ?? '';
    },
    set(this: HTMLMediaElement, value: string) {
      const state = stateOf(this);
      if (!state.paused) {
        state.paused = true;
        fire(this, 'pause');
      }
      state.currentTime = 0;
      state.duration = Number.NaN;
      state.error = null;
      this.setAttribute('src', value);
    },
  });
  Object.defineProperty(prototype, 'play', {
    configurable: true,
    value(this: HTMLMediaElement) {
      const state = stateOf(this);
      state.plays += 1;
      if (state.paused) {
        state.paused = false;
        fire(this, 'play');
      }
      return Promise.resolve();
    },
  });
  Object.defineProperty(prototype, 'pause', {
    configurable: true,
    value(this: HTMLMediaElement) {
      const state = stateOf(this);
      if (!state.paused) {
        state.paused = true;
        fire(this, 'pause');
      }
    },
  });
  Object.defineProperty(prototype, 'load', {
    configurable: true,
    value(this: HTMLMediaElement) {
      const state = stateOf(this);
      state.paused = true;
      state.currentTime = 0;
      state.duration = Number.NaN;
      state.error = null;
    },
  });

  const element = () => {
    const found = document.querySelector('audio');
    if (found === null) {
      throw new Error('The player has no audio element in the page.');
    }
    return found;
  };

  return {
    element,
    touched,
    plays: () => stateOf(element()).plays,
    time: () => stateOf(element()).currentTime,
    loaded: (duration) => {
      act(() => {
        const audio = element();
        stateOf(audio).duration = duration;
        fire(audio, 'durationchange');
        fire(audio, 'loadedmetadata');
      });
    },
    advance: (seconds) => {
      act(() => {
        const audio = element();
        const state = stateOf(audio);
        state.currentTime = Math.min(
          Number.isFinite(state.duration) ? state.duration : Infinity,
          state.currentTime + seconds,
        );
        fire(audio, 'timeupdate');
      });
    },
    end: () => {
      act(() => {
        const audio = element();
        const state = stateOf(audio);
        state.currentTime = state.duration;
        state.paused = true;
        fire(audio, 'pause');
        fire(audio, 'ended');
      });
    },
    fail: (code = 4) => {
      act(() => {
        const audio = element();
        const state = stateOf(audio);
        state.error = { code, message: 'fake failure' } as MediaError;
        state.paused = true;
        fire(audio, 'error');
      });
    },
    restore: () => {
      for (const [name, descriptor] of saved) {
        if (descriptor === undefined) {
          Reflect.deleteProperty(prototype, name);
        } else {
          Object.defineProperty(prototype, name, descriptor);
        }
      }
    },
  };
}
