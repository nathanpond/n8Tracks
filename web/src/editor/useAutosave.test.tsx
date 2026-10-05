import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { SaveOutcome } from '../common/useRevisionedSave';
import {
  AUTOSAVE_DELAY_MS,
  retryDelay,
  useAutosave,
  type AutosaveStatus,
  type Edit,
} from './useAutosave';

/**
 * A record held outside React, as the Version pane holds it in refs: what is stored, what is on
 * screen, and a `send` whose answers each test scripts (a stored save by default).
 */
function harness() {
  const world = {
    stored: { lyrics: '' } as Record<string, string | null>,
    drafts: { lyrics: '' } as Record<string, string | null>,
    problem: undefined as string | undefined,
    sent: [] as Edit[],
    /** Answers the next sends, in order; when empty, the save is stored. */
    answers: [] as (() => Promise<SaveOutcome>)[],
  };
  const pending = () => {
    const edit: Record<string, string | null> = {};
    for (const [key, value] of Object.entries(world.drafts)) {
      if (world.stored[key] !== value) {
        edit[key] = value;
      }
    }
    return edit;
  };
  const send = vi.fn((edit: Edit): Promise<SaveOutcome> => {
    world.sent.push(edit);
    const answer = world.answers.shift();
    if (answer) {
      return answer();
    }
    world.stored = { ...world.stored, ...edit };
    return Promise.resolve({ kind: 'saved' });
  });
  const onReloaded = vi.fn(() => {
    world.drafts = { ...world.stored };
  });
  const hook = renderHook(() =>
    useAutosave({ pending, problem: () => world.problem, send, onReloaded }),
  );
  const type = (lyrics: string) => {
    world.drafts = { ...world.drafts, lyrics };
    act(() => {
      hook.result.current.changed();
    });
  };
  const status = (): AutosaveStatus => hook.result.current.status;
  return { world, send, onReloaded, hook, type, status };
}

/** The reason a status gives, or '' when it gives none. */
function reasonOf(status: AutosaveStatus): string {
  return 'reason' in status ? status.reason : '';
}

/** Moves the fake clock on by `ms`, letting the promises it settles run. */
async function wait(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('useAutosave', () => {
  it('sends one save after the user pauses, not one per keystroke', async () => {
    const { world, send, type, status } = harness();
    expect(status()).toEqual({ kind: 'saved' });

    for (const text of ['R', 'Ru', 'Run', 'Run ', 'Run!']) {
      type(text);
      await wait(400);
    }
    expect(send).not.toHaveBeenCalled();
    expect(status()).toEqual({ kind: 'saving' });

    await wait(AUTOSAVE_DELAY_MS - 400 - 1);
    expect(send).not.toHaveBeenCalled();
    await wait(1);
    expect(world.sent).toEqual([{ lyrics: 'Run!' }]);
    expect(status()).toEqual({ kind: 'saved' });

    await wait(60_000);
    expect(send).toHaveBeenCalledTimes(1);
  });

  it('sends nothing when the text goes back to what is stored', async () => {
    const { send, type, status } = harness();
    type('x');
    type('');
    expect(status()).toEqual({ kind: 'saved' });
    await wait(AUTOSAVE_DELAY_MS * 2);
    expect(send).not.toHaveBeenCalled();
  });

  it('sends what was typed during a save once it returns, one save at a time', async () => {
    const { world, send, type, status } = harness();
    let finish: (outcome: SaveOutcome) => void = () => undefined;
    world.answers.push(
      () =>
        new Promise<SaveOutcome>((resolve) => {
          finish = (outcome) => {
            world.stored = { ...world.stored, lyrics: 'One' };
            resolve(outcome);
          };
        }),
    );

    type('One');
    await wait(AUTOSAVE_DELAY_MS);
    expect(world.sent).toEqual([{ lyrics: 'One' }]);

    // Typed while the first save is out; its pause ends before the save returns.
    type('One two');
    await wait(AUTOSAVE_DELAY_MS * 3);
    expect(send).toHaveBeenCalledTimes(1);
    expect(status()).toEqual({ kind: 'saving' });

    await act(async () => {
      finish({ kind: 'saved' });
      await Promise.resolve();
    });
    await wait(0);
    expect(world.sent).toEqual([{ lyrics: 'One' }, { lyrics: 'One two' }]);
    expect(status()).toEqual({ kind: 'saved' });
  });

  it('retries a failed save without limit, backing off from 2 to 30 seconds', async () => {
    const { world, send, type, status } = harness();
    const failing = () => Promise.resolve<SaveOutcome>({ kind: 'failed', reason: 'unreachable' });
    world.answers.push(...Array.from({ length: 8 }, () => failing));

    type('Offline');
    await wait(AUTOSAVE_DELAY_MS);
    expect(send).toHaveBeenCalledTimes(1);
    expect(status().kind).toBe('retrying');
    expect(reasonOf(status())).toMatch(/cannot be reached/);

    const delays = [2_000, 4_000, 8_000, 16_000, 30_000, 30_000, 30_000];
    for (const [index, delay] of delays.entries()) {
      await wait(delay - 1);
      expect(send).toHaveBeenCalledTimes(index + 1);
      await wait(1);
      expect(send).toHaveBeenCalledTimes(index + 2);
    }
    expect(status().kind).toBe('retrying');

    // The server is back: the next retry stores it.
    await wait(30_000);
    expect(send).toHaveBeenCalledTimes(9);
    expect(status()).toEqual({ kind: 'saved' });
    expect(world.stored.lyrics).toBe('Offline');
  });

  it('retries a server error, and its backoff starts again after a success', async () => {
    const { world, send, type, status } = harness();
    const error = () => Promise.resolve<SaveOutcome>({ kind: 'failed', reason: 'server' });
    world.answers.push(error, error);

    type('a');
    await wait(AUTOSAVE_DELAY_MS + 2_000 + 4_000);
    expect(send).toHaveBeenCalledTimes(3);
    expect(status()).toEqual({ kind: 'saved' });

    world.answers.push(error);
    type('ab');
    await wait(AUTOSAVE_DELAY_MS);
    expect(status().kind).toBe('retrying');
    await wait(2_000);
    expect(send).toHaveBeenCalledTimes(5);
    expect(status()).toEqual({ kind: 'saved' });
  });

  it('computes the backoff', () => {
    expect([1, 2, 3, 4, 5, 6, 50].map(retryDelay)).toEqual([
      2_000, 4_000, 8_000, 16_000, 30_000, 30_000, 30_000,
    ]);
  });

  it.each([
    ['frozen', /Create a new Version from it/],
    ['signed-out', /signed out/],
    ['gone', /no longer there/],
    ['refused', /refused/],
  ] as const)('stops on a %s refusal and says what to do', async (reason, message) => {
    const { world, send, type, status } = harness();
    world.answers.push(() => Promise.resolve({ kind: 'failed', reason }));

    type('x');
    await wait(AUTOSAVE_DELAY_MS);
    expect(status()).toMatchObject({ kind: 'stopped', canRetry: true });
    expect(reasonOf(status())).toMatch(message);
    await wait(120_000);
    expect(send).toHaveBeenCalledTimes(1);
  });

  it('stops on a validation refusal until the text changes, then saves again', async () => {
    const { world, send, type, status } = harness();
    world.answers.push(() =>
      Promise.resolve({ kind: 'invalid', errors: { lyrics: ['Use at most 5,000 characters.'] } }),
    );

    type('long');
    await wait(AUTOSAVE_DELAY_MS);
    expect(status()).toEqual({
      kind: 'stopped',
      reason: 'Use at most 5,000 characters. Change the text to save it.',
      canRetry: false,
    });
    await wait(60_000);
    expect(send).toHaveBeenCalledTimes(1);

    type('short');
    expect(status()).toEqual({ kind: 'saving' });
    await wait(AUTOSAVE_DELAY_MS);
    expect(world.sent).toEqual([{ lyrics: 'long' }, { lyrics: 'short' }]);
    expect(status()).toEqual({ kind: 'saved' });
  });

  it('does not send text over a limit, and says why', async () => {
    const { world, send, type, status } = harness();
    world.problem = 'The lyrics are over their limit. Shorten the text to save.';
    type('x'.repeat(10));
    expect(status()).toEqual({ kind: 'stopped', reason: world.problem, canRetry: false });
    await wait(AUTOSAVE_DELAY_MS * 4);
    expect(send).not.toHaveBeenCalled();

    world.problem = undefined;
    type('x');
    await wait(AUTOSAVE_DELAY_MS);
    expect(world.sent).toEqual([{ lyrics: 'x' }]);
  });

  it('pauses on a conflict left undecided until the user reapplies or reloads', async () => {
    const { world, send, onReloaded, hook, type, status } = harness();
    world.answers.push(() => Promise.resolve({ kind: 'keep-editing' }));

    type('Mine');
    await wait(AUTOSAVE_DELAY_MS);
    expect(status()).toEqual({ kind: 'conflict' });

    // Typing on does not save over the other change.
    type('Mine, more');
    await wait(60_000);
    expect(send).toHaveBeenCalledTimes(1);
    expect(world.drafts.lyrics).toBe('Mine, more');

    act(() => {
      hook.result.current.reapply();
    });
    await wait(0);
    expect(world.sent).toEqual([{ lyrics: 'Mine' }, { lyrics: 'Mine, more' }]);
    expect(status()).toEqual({ kind: 'saved' });

    world.answers.push(() => Promise.resolve({ kind: 'keep-editing' }));
    type('Again');
    await wait(AUTOSAVE_DELAY_MS);
    expect(status()).toEqual({ kind: 'conflict' });
    act(() => {
      hook.result.current.reload();
    });
    expect(onReloaded).toHaveBeenCalledTimes(1);
    expect(world.drafts.lyrics).toBe('Mine, more');
    expect(status()).toEqual({ kind: 'saved' });
  });

  it('takes the current record when the user reloads from the conflict dialog', async () => {
    const { world, onReloaded, type, status } = harness();
    world.answers.push(() => {
      world.stored = { lyrics: 'Theirs' };
      return Promise.resolve({ kind: 'reloaded' });
    });

    type('Mine');
    await wait(AUTOSAVE_DELAY_MS);
    expect(onReloaded).toHaveBeenCalledTimes(1);
    expect(world.drafts.lyrics).toBe('Theirs');
    expect(status()).toEqual({ kind: 'saved' });
  });

  it('flushes now: true once stored, false when the save does not go through', async () => {
    const { world, send, hook, type } = harness();

    type('Now');
    let stored: boolean | undefined;
    await act(async () => {
      stored = await hook.result.current.flush();
    });
    expect(stored).toBe(true);
    expect(world.sent).toEqual([{ lyrics: 'Now' }]);

    world.answers.push(() => Promise.resolve({ kind: 'failed', reason: 'unreachable' }));
    type('Now then');
    await act(async () => {
      stored = await hook.result.current.flush();
    });
    expect(stored).toBe(false);
    expect(send).toHaveBeenCalledTimes(2);
    expect(hook.result.current.status.kind).toBe('retrying');

    // The retry is still scheduled.
    await wait(2_000);
    expect(send).toHaveBeenCalledTimes(3);
    expect(hook.result.current.status).toEqual({ kind: 'saved' });
  });

  it('tries again at once when asked', async () => {
    const { world, send, hook, type, status } = harness();
    world.answers.push(() => Promise.resolve({ kind: 'failed', reason: 'signed-out' }));
    type('x');
    await wait(AUTOSAVE_DELAY_MS);
    expect(status().kind).toBe('stopped');

    act(() => {
      hook.result.current.retry();
    });
    await wait(0);
    expect(send).toHaveBeenCalledTimes(2);
    expect(status()).toEqual({ kind: 'saved' });
  });
});
