/** Time for the adapter: real in the extension, controlled in tests. */
export interface Clock {
  /** Milliseconds, only ever compared with another reading. */
  now(): number;
  sleep(ms: number): Promise<void>;
}

export const realClock: Clock = {
  now: () => performance.now(),
  sleep: (ms) =>
    new Promise((resolve) => {
      setTimeout(resolve, ms);
    }),
};

/** How often a condition is read again while waiting for it (the adapter story: 100 ms). */
export const POLL_MS = 100;

/**
 * Reads `condition` every `pollMs` until it holds or `timeoutMs` has passed, and returns the last
 * reading. The condition is always read at least once, and once more at the deadline.
 */
export async function poll<T extends { ok: boolean }>(
  condition: () => T,
  timeoutMs: number,
  clock: Clock = realClock,
  pollMs: number = POLL_MS,
): Promise<T> {
  const deadline = clock.now() + timeoutMs;
  let reading = condition();
  while (!reading.ok && clock.now() < deadline) {
    await clock.sleep(Math.min(pollMs, Math.max(0, deadline - clock.now())));
    reading = condition();
  }
  return reading;
}
