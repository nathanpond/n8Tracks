import { describe, expect, it } from 'vitest';
import { fakeClock } from '../testing/snapshots.ts';
import { ObservationFeed, type FeedWindow } from './observations.ts';
import { OBSERVER_SOURCE, type ObservedMessage } from './observed.ts';

const ORIGIN = 'https://suno.com';

/** Suno's page, standing in for `window`: `message` delivers a postMessage event to the feed. */
function page() {
  const listeners: ((event: MessageEvent) => void)[] = [];
  const view: FeedWindow = {
    origin: ORIGIN,
    postMessage: () => undefined,
    addEventListener: (_type, listener) => {
      listeners.push(listener);
    },
    removeEventListener: () => undefined,
  };
  const feed = new ObservationFeed(view, fakeClock());
  feed.start();
  const message = (data: unknown, from: { source?: unknown; origin?: string } = {}) => {
    for (const listener of listeners) {
      listener({
        data,
        source: 'source' in from ? from.source : view,
        origin: from.origin ?? ORIGIN,
      } as MessageEvent);
    }
  };
  return { feed, message };
}

const workspaces: ObservedMessage = {
  source: OBSERVER_SOURCE,
  type: 'observed',
  kind: 'workspaces',
  request: { cursor: null, page: 1, filters: null, feedId: null },
  body: { projects: [{ id: 'w-1', name: 'Night Drive' }] },
};

describe('the observation feed, listening for the page observer', () => {
  it('takes an observed message from this window and origin', () => {
    const { feed, message } = page();

    message(workspaces);

    expect(feed.listedWorkspaces()).toEqual([{ id: 'w-1', name: 'Night Drive' }]);
  });

  // #329: postMessage origins are checked on this side too, not only by the observer.
  it('ignores an observed message from another origin', () => {
    const { feed, message } = page();

    message(workspaces, { origin: 'https://evil.example' });

    expect(feed.listedWorkspaces()).toEqual([]);
  });

  it('ignores an observed message that no window sent (source null)', () => {
    const { feed, message } = page();

    message(workspaces, { source: null });

    expect(feed.listedWorkspaces()).toEqual([]);
  });

  it('ignores an observed message from another window on this origin (a frame)', () => {
    const { feed, message } = page();

    message(workspaces, { source: {} });

    expect(feed.listedWorkspaces()).toEqual([]);
  });
});

describe("the plan's download allowance (#215)", () => {
  const billing = (body: unknown): ObservedMessage => ({
    source: OBSERVER_SOURCE,
    type: 'observed',
    kind: 'billing',
    request: { cursor: null, page: null, filters: null, feedId: null },
    body,
  });

  it('keeps the latest counts the page read, tells the listener, and queues nothing', async () => {
    const { feed, message } = page();
    const heard: unknown[] = [];
    feed.onDownloadUsage((usage) => heard.push(usage));
    expect(feed.downloadUsage()).toBeNull();

    message(
      billing({
        download_usage: { current_period_downloads_used: 2, current_period_downloads_limit: 60 },
      }),
    );
    // Not counts: kept as before.
    message(billing({}));

    expect(feed.downloadUsage()).toEqual({ used: 2, limit: 60, additional: 0 });
    expect(heard).toEqual([{ used: 2, limit: 60, additional: 0 }]);
    const controller = new AbortController();
    expect(await feed.next('billing', () => true, 10, controller.signal)).toBeNull();
  });
});
