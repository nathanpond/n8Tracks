import { act } from '@testing-library/react';
import { vi } from 'vitest';

// Taken before any test fakes it, to bound the wait in endFakeTimeouts in real time.
const realSetTimeout = globalThis.setTimeout;

/** How long, in real time, the end of a test waits for its last clock step to finish. */
const SETTLE_LIMIT_MS = 5_000;

/**
 * Puts timeouts on a fake clock for a test that waits on the app's own timers (autosave, retries),
 * so how long a step takes depends on the app's timers and not on how loaded the machine is.
 * Testing Library is told, so its waits (`findBy…`, `waitFor`, the act drain after each step) move
 * the fake clock instead of waiting in real time: their `timeout` is then fake time too. Real
 * intervals and animation frames are left alone. A user-event session needs
 * `userEvent.setup({ advanceTimers })` with the returned {@link FakeClock.advanceTimers}. Undone
 * after each test ({@link endFakeTimeouts}).
 */
export function fakeTimeouts(): FakeClock {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
  const owner: Owner = { over: false, steps: new Set() };
  liveClock = owner;
  // A test that timed out is not stopped: its steps carry on into the tests after it. The
  // functions returned here belong to the test that put this clock on and fail once that test is
  // over, so a leftover user-event step or advance stops at its next move instead of moving a
  // later test's clock or touching its page. (Testing Library reads the global `jest` afresh on
  // each move, so a leftover wait of its own stops only if it moves between two tests.)
  const ensureLive = () => {
    if (owner.over) {
      throw new Error('This fake clock belongs to a test that is over.');
    }
  };
  const clock: FakeClock = {
    advanceTimers: (milliseconds) => {
      ensureLive();
      vi.advanceTimersByTime(milliseconds);
    },
    advanceTimersAsync: async (milliseconds) => {
      ensureLive();
      const step = act(async () => {
        await vi.advanceTimersByTimeAsync(milliseconds);
      });
      owner.steps.add(step);
      try {
        await step;
      } finally {
        owner.steps.delete(step);
      }
    },
  };
  // Testing Library recognises fake timers by a global `jest`; this is all of it that it calls.
  vi.stubGlobal('jest', { advanceTimersByTime: clock.advanceTimers });
  return clock;
}

/** The fake clock {@link fakeTimeouts} put on, moved only by the test that put it on. */
export interface FakeClock {
  /** Moves the fake clock on by `milliseconds`, running the timeouts that fall due. */
  advanceTimers: (milliseconds: number) => void;
  /**
   * Moves the fake clock on by `milliseconds` inside `act`, letting the promises each timeout
   * starts (a save, a retry) settle between, and the renders they cause happen.
   */
  advanceTimersAsync: (milliseconds: number) => Promise<void>;
}

interface Owner {
  over: boolean;
  /** The {@link FakeClock.advanceTimersAsync} steps still running, each inside its own `act`. */
  steps: Set<Promise<void>>;
}

let liveClock: Owner | undefined;

/**
 * Ends the running test's fake clock, if it put one on; run after each test (setup.ts), before the
 * real timers come back. A test that timed out part way through an
 * {@link FakeClock.advanceTimersAsync} step is still inside React's `act`, and while it is, the
 * next test's own `act` calls nest in it and never let React render, so its first wait fails at
 * once. That step is let finish on the clock it started on (bounded in real time) first.
 */
export async function endFakeTimeouts(): Promise<void> {
  const owner = liveClock;
  if (owner === undefined) {
    return;
  }
  owner.over = true;
  liveClock = undefined;
  if (owner.steps.size > 0) {
    await Promise.race([
      Promise.allSettled([...owner.steps]),
      new Promise((resolve) => realSetTimeout(resolve, SETTLE_LIMIT_MS)),
    ]);
  }
}
