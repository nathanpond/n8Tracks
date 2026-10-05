import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { jsonResponse, requestPath, stubFetch } from '../test/helpers';
import {
  heldSnapshots,
  SNAPSHOT_IDLE_MS,
  SNAPSHOT_QUEUE_LIMIT,
  SNAPSHOT_RETRY_MS,
  useSnapshots,
  type EditorText,
} from './useSnapshots';

const VERSION = '0199b1a0-0000-7000-9000-000000000001';

/** The snapshot requests sent so far, as their bodies. */
function sentSnapshots(mock: ReturnType<typeof stubFetch>) {
  return mock.mock.calls
    .filter(([input, init]) => requestPath(input).endsWith('/snapshots') && init?.method === 'POST')
    .map(([, init]) => JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as unknown);
}

function stored(body: Record<string, unknown>) {
  return jsonResponse(201, { id: 'snap', versionId: VERSION, createdAt: body.capturedAt, ...body });
}

/** A snapshot server: stores everything unless `answer` says otherwise. */
function snapshotServer(answer?: () => Promise<Response>) {
  const mock = stubFetch();
  mock.mockImplementation((_input, init) => {
    if (answer) {
      return answer();
    }
    const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as Record<
      string,
      unknown
    >;
    return Promise.resolve(stored(body));
  });
  return mock;
}

/** The hook over text the test changes, as the editor's drafts. */
function renderSnapshots(opening: EditorText) {
  let text: EditorText | undefined = opening;
  const onStored = vi.fn();
  const hook = renderHook(() => useSnapshots({ versionId: VERSION, read: () => text, onStored }));
  return {
    ...hook,
    onStored,
    type: (next: EditorText | undefined) => {
      text = next;
      hook.result.current.changed();
    },
  };
}

/** Lets the sends started by a timer finish. */
async function settle() {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(0);
  });
}

describe('snapshots', () => {
  beforeEach(() => {
    vi.useFakeTimers({
      now: new Date('2026-10-01T09:00:00Z'),
      toFake: ['setTimeout', 'clearTimeout', 'Date'],
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('are taken 30 seconds after the last change, with the opening text first', async () => {
    const mock = snapshotServer();
    const { type, onStored } = renderSnapshots({ lyrics: 'Opening', styles: 'pop' });

    type({ lyrics: 'Opening, then a verse', styles: 'pop' });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(SNAPSHOT_IDLE_MS - 1_000);
    });
    // Another change starts the pause again.
    type({ lyrics: 'Opening, then a second verse', styles: 'pop' });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(SNAPSHOT_IDLE_MS - 1);
    });
    expect(sentSnapshots(mock)).toEqual([]);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(1);
    });
    await settle();
    expect(sentSnapshots(mock)).toEqual([
      { lyrics: 'Opening', styles: 'pop', capturedAt: '2026-10-01T09:00:00.000Z' },
      {
        lyrics: 'Opening, then a second verse',
        styles: 'pop',
        capturedAt: '2026-10-01T09:00:59.000Z',
      },
    ]);
    expect(onStored).toHaveBeenCalledTimes(2);

    // Another pause with no change takes nothing; a later change takes only the new text.
    type({ lyrics: 'Opening, then a second verse', styles: 'pop' });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(SNAPSHOT_IDLE_MS);
    });
    expect(sentSnapshots(mock)).toHaveLength(2);
    type({ lyrics: 'Rewritten', styles: 'rock' });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(SNAPSHOT_IDLE_MS);
    });
    await settle();
    expect(sentSnapshots(mock).slice(2)).toEqual([
      expect.objectContaining({ lyrics: 'Rewritten', styles: 'rock' }),
    ]);
  });

  it('leave out empty opening text, text typed back to what is in history, and text over a limit', async () => {
    const mock = snapshotServer();
    const { type } = renderSnapshots({ lyrics: '', styles: '' });

    type({ lyrics: 'Back', styles: '' });
    type({ lyrics: '', styles: '' });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(SNAPSHOT_IDLE_MS * 2);
    });
    type(undefined);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(SNAPSHOT_IDLE_MS * 2);
    });
    expect(sentSnapshots(mock)).toEqual([]);

    type({ lyrics: 'A verse', styles: '' });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(SNAPSHOT_IDLE_MS);
    });
    await settle();
    expect(sentSnapshots(mock)).toEqual([expect.objectContaining({ lyrics: 'A verse' })]);
  });

  it('are taken when the user leaves the Version with changes not yet snapshotted', async () => {
    const mock = snapshotServer();
    const quiet = renderSnapshots({ lyrics: 'Untouched', styles: '' });
    quiet.unmount();
    await settle();
    expect(sentSnapshots(mock)).toEqual([]);

    const busy = renderSnapshots({ lyrics: '', styles: '' });
    busy.type({ lyrics: 'Typed, then left at once', styles: '' });
    busy.unmount();
    await settle();
    expect(sentSnapshots(mock)).toEqual([
      expect.objectContaining({ lyrics: 'Typed, then left at once' }),
    ]);
  });

  it('are taken at once by capture (before Reload discards the text), and not after a restore', async () => {
    const mock = snapshotServer();
    const { result, type } = renderSnapshots({ lyrics: '', styles: '' });

    type({ lyrics: 'About to be discarded', styles: '' });
    act(() => {
      result.current.capture();
    });
    await settle();
    expect(sentSnapshots(mock)).toEqual([
      expect.objectContaining({ lyrics: 'About to be discarded' }),
    ]);

    // Restored text came from history: it is not sent back, nor the pause it would have armed.
    act(() => {
      result.current.rebase({ lyrics: 'Restored', styles: '' }, false);
    });
    type({ lyrics: 'Restored', styles: '' });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(SNAPSHOT_IDLE_MS);
    });
    expect(sentSnapshots(mock)).toHaveLength(1);

    // Reloaded text may be new to history: it goes in before the next change.
    act(() => {
      result.current.rebase({ lyrics: 'Reloaded', styles: '' }, true);
    });
    type({ lyrics: 'Reloaded, edited', styles: '' });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(SNAPSHOT_IDLE_MS);
    });
    await settle();
    expect(sentSnapshots(mock).slice(1)).toEqual([
      expect.objectContaining({ lyrics: 'Reloaded' }),
      expect.objectContaining({ lyrics: 'Reloaded, edited' }),
    ]);
  });

  it('that cannot be sent are held, up to ten, and sent with their capture time when the connection returns', async () => {
    let online = false;
    const mock = stubFetch();
    mock.mockImplementation((_input, init) => {
      if (!online) {
        return Promise.reject(new TypeError('Failed to fetch'));
      }
      const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as Record<
        string,
        unknown
      >;
      return Promise.resolve(stored(body));
    });
    const { result, type } = renderSnapshots({ lyrics: '', styles: '' });

    for (let draft = 1; draft <= SNAPSHOT_QUEUE_LIMIT + 2; draft++) {
      type({ lyrics: `Draft ${String(draft)}`, styles: '' });
      act(() => {
        result.current.capture();
      });
      await settle();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(1_000);
      });
    }
    expect(heldSnapshots().map((item) => item.text.lyrics)).toEqual(
      Array.from({ length: SNAPSHOT_QUEUE_LIMIT }, (_, index) => `Draft ${String(index + 3)}`),
    );
    const capturedAt = heldSnapshots()[0]?.text.capturedAt;
    const tried = mock.mock.calls.length;

    online = true;
    act(() => {
      window.dispatchEvent(new Event('online'));
    });
    await settle();
    const sent = sentSnapshots(mock).slice(tried);
    expect(sent).toHaveLength(SNAPSHOT_QUEUE_LIMIT);
    expect(sent[0]).toEqual({ lyrics: 'Draft 3', styles: '', capturedAt });
    expect(heldSnapshots()).toEqual([]);
  });

  it('that failed are retried on their own, and one the API refuses for good is dropped', async () => {
    let answer = () => Promise.resolve(jsonResponse(503, { code: 'unavailable' }));
    const mock = snapshotServer(() => answer());
    const { result, type } = renderSnapshots({ lyrics: '', styles: '' });

    type({ lyrics: 'Kept for later', styles: '' });
    act(() => {
      result.current.capture();
    });
    await settle();
    expect(heldSnapshots()).toHaveLength(1);

    answer = () => Promise.resolve(jsonResponse(422, { code: 'validation_failed', errors: {} }));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(SNAPSHOT_RETRY_MS);
    });
    await settle();
    expect(sentSnapshots(mock)).toHaveLength(2);
    expect(heldSnapshots()).toEqual([]);
  });
});
