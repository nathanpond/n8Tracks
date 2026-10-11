// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { legsFor } from '../adapter/libraryReader.ts';
import type { Clock } from '../adapter/clock.ts';
import type { FeedWindow } from '../adapter/observations.ts';
import { OBSERVER_SOURCE, type ObservedMessage } from '../adapter/observed.ts';
import type {
  ConnectedState,
  ConnectionState,
  ImageProgress,
  Request,
  SyncScope,
  SyncSession,
} from '../messages.ts';
import { expectNoAxeViolations } from '../testing/a11y.ts';
import { fakeClock, snapshotHtml } from '../testing/snapshots.ts';
import { sunoObject } from '../testing/sunoResponses.ts';
import { startSunoContent, type SunoContent } from './suno.ts';

const CONNECTED: ConnectedState = {
  status: 'connected',
  address: 'https://n8tracks.example.com',
  credentialName: 'Chrome at home',
  scopes: ['suno.sync'],
  applicationVersion: '0.1.0',
  compatibility: { kind: 'compatible' },
  features: [
    { feature: 'sync', label: 'Library sync', scope: 'suno.sync', available: true, reason: null },
    {
      feature: 'generate',
      label: 'Generate on Suno',
      scope: 'suno.generate',
      available: false,
      reason: 'This credential lacks suno.generate',
    },
  ],
};

/**
 * Leaves out what the panel asks each time it refreshes (the state and the diagnostic report,
 * #150), the page load's question whether its tab is generating (#145, never in a sync's tab), and
 * the Download view's questions (#215: no library load, and the formats last chosen; #216: the
 * download queue as it stands).
 */
const PANEL_REFRESH = new Set([
  'state',
  'diagnostics-record',
  'diagnostic-report',
  'generate-resume',
  'download-resume',
  'download-formats',
  'download-run',
]);
const notPanelRefresh = (type: string) => !PANEL_REFRESH.has(type);

const LIBRARY: SyncScope = { kind: 'library' };
const LIBRARY_FILTERS = (sunoObject('feed-v3.library-page-1.request') as { filters: unknown })
  .filters;

/** The filters of a workspace's own feed on `/create?wid=` (#422). */
function workspaceFilters(id: string): unknown {
  const filters = structuredClone(
    (sunoObject('feed-v3.workspace-create-wid.request') as { filters: Record<string, unknown> })
      .filters,
  );
  filters.workspace = { presence: 'True', workspaceId: id };
  return filters;
}

function observed(kind: ObservedMessage['kind'], body: unknown, filters: unknown = null) {
  const message: ObservedMessage = {
    source: OBSERVER_SOURCE,
    type: 'observed',
    kind,
    request: { cursor: null, page: null, filters, feedId: null },
    body,
  };
  return message;
}

function sessionAt(leg: number, change: Partial<SyncSession> = {}): SyncSession {
  return {
    tabId: 7,
    scope: LIBRARY,
    legs: legsFor(LIBRARY),
    leg,
    attempt: 0,
    partNumber: 0,
    counts: { clips: 0, trashed: 0, workspaces: 0, playlists: 0 },
    workspaces: [],
    exportId: null,
    updatedAt: 0,
    ...change,
  };
}

/** The service worker, as the content script sees it: it holds one sync and records every ask. */
function worker(
  session: SyncSession | null,
  connection: ConnectionState = CONNECTED,
  images: ImageProgress[] = [],
) {
  const asked: Request[] = [];
  let current = session;
  const send = (request: Request): Promise<unknown> => {
    asked.push(structuredClone(request));
    switch (request.type) {
      case 'state':
        return Promise.resolve(connection);
      case 'sync-resume':
        return Promise.resolve({ session: current });
      case 'sync-preview':
        return Promise.resolve({ replacesReady: true });
      case 'sync-begin':
        current = sessionAt(0, { scope: request.scope, legs: legsFor(request.scope) });
        return Promise.resolve({ ok: true, session: current });
      case 'sync-save':
        current = current === null ? null : { ...current, ...request.progress };
        return Promise.resolve({ ok: true });
      case 'sync-create':
        return Promise.resolve({ ok: true, exportId: 'e1' });
      case 'sync-part':
        return Promise.resolve({ ok: true });
      case 'sync-complete':
        current = null;
        return Promise.resolve({
          ok: true,
          reviewUrl: 'https://n8tracks.example.com/suno/imports/e1',
        });
      case 'sync-discard':
        current = null;
        return Promise.resolve({ ok: true });
      case 'sync-images':
        return Promise.resolve({ images: images.shift() ?? null });
      default:
        return Promise.resolve(undefined);
    }
  };
  return { asked, send, types: () => asked.map((request) => request.type) };
}

/**
 * Suno's page as the content script's window: the observer's replay of what it saw before the
 * content script started arrives when the script says it is ready, from this window and origin.
 */
function sunoWindow(seen: ObservedMessage[]): FeedWindow {
  const listeners: ((event: MessageEvent) => void)[] = [];
  const view: FeedWindow = {
    origin: 'https://suno.com',
    postMessage: () => {
      for (const message of seen) {
        for (const listener of listeners) {
          listener({ data: message, source: view, origin: 'https://suno.com' } as MessageEvent);
        }
      }
    },
    addEventListener: (_type, listener) => {
      listeners.push(listener);
    },
    removeEventListener: () => undefined,
  };
  return view;
}

let content: SunoContent | null = null;

function start(options: {
  address: string;
  snapshot?: string | null;
  worker: ReturnType<typeof worker>;
  seen?: ObservedMessage[];
  clock?: Clock;
}) {
  document.title = 'Suno';
  document.documentElement.lang = 'en';
  // `null`: a plain stand-in page, for the axe checks of the panel (Suno's own page has its own
  // findings, which are not the extension's).
  document.body.innerHTML =
    options.snapshot === null
      ? '<main><h1>Suno</h1><p>Library</p></main>'
      : snapshotHtml(options.snapshot ?? 'library-list');
  const visited: string[] = [];
  content = startSunoContent({
    document,
    send: options.worker.send,
    extensionVersion: '0.1.0',
    address: () => options.address,
    navigate: (address) => visited.push(address),
    window: sunoWindow(options.seen ?? []),
    clock: options.clock ?? fakeClock(),
  });
  const root = content.panel.host.shadowRoot;
  const text = (selector: string) => root?.querySelector(selector)?.textContent ?? '';
  const button = (name: string) =>
    [...(root?.querySelectorAll('button') ?? [])].find(
      (candidate) => (candidate.getAttribute('aria-label') ?? candidate.textContent) === name,
    );
  return { content, visited, text, button, root };
}

afterEach(() => {
  content?.stop();
  content = null;
  document.body.innerHTML = '';
});

describe('starting a sync from the panel', () => {
  it('never syncs by itself: a page load with no sync asks once and reads nothing', async () => {
    const sw = worker(null);
    const { content: started, visited } = start({
      address: 'https://suno.com/me',
      worker: sw,
      seen: [observed('library-feed', { clips: [], has_more: false }, LIBRARY_FILTERS)],
    });

    await started.resumed;

    expect(sw.types().filter(notPanelRefresh)).toEqual(['sync-resume']);
    expect(visited).toEqual([]);
    expect(started.panel.isOpen).toBe(false);
  });

  it('starts only after the scope, the summary, and Start sync, then opens the first list', async () => {
    const sw = worker(null);
    const {
      content: started,
      visited,
      text,
      button,
      root,
    } = start({
      address: 'https://suno.com/create',
      snapshot: null,
      worker: sw,
    });
    await started.resumed;
    await started.toggle();

    expect(root?.querySelector<HTMLInputElement>('input[value="library"]')?.checked).toBe(true);
    button('Next')?.click();
    await vi.waitFor(() => {
      expect(text('.sync-summary')).toContain('Reads your whole Suno library');
    });
    expect(text('.sync .warning')).toBe(
      'An export is waiting for review in n8Tracks; this sync replaces it when it finishes.',
    );
    // Nothing has started: the summary is not the confirmation.
    expect(sw.types()).not.toContain('sync-begin');
    expect(visited).toEqual([]);
    await expectNoAxeViolations(document);

    button('Start sync')?.click();
    // The workspace list is reached through Library's Workspaces tab, not by its address (#421).
    await vi.waitFor(() => {
      expect(visited).toEqual(['https://suno.com/me']);
    });
    expect(sw.asked).toContainEqual({ type: 'sync-begin', scope: LIBRARY });
    expect(text('.sync-progress')).toContain('Reading: Read the workspace list.');
  });

  it('is disabled, saying why, when the extension is not connected', async () => {
    const sw = worker(null, { status: 'not-paired' });
    const {
      content: started,
      text,
      button,
    } = start({ address: 'https://suno.com/me', worker: sw });
    await started.toggle();

    expect(button('Next')?.disabled).toBe(true);
    expect(text('.sync .detail')).toBe('Connect the extension to n8Tracks to sync.');
  });
});

/** Library's Workspaces tab in the loaded snapshot (#421). */
function workspacesTab(): Element {
  const tab = [...document.querySelectorAll('[role="tab"]')].find(
    (element) => element.textContent === 'Workspaces',
  );
  if (tab === undefined) {
    throw new Error('the snapshot has no Workspaces tab');
  }
  return tab;
}

/**
 * Suno's answer to pressing the Workspaces tab: the page asks for the list only then (#421). The
 * tab's own navigation is stopped, as jsdom cannot follow it.
 */
function answerTabPress(answer: () => void): Element[] {
  const pressed: Element[] = [];
  const tab = workspacesTab();
  tab.addEventListener('click', (event) => {
    event.preventDefault();
    pressed.push(tab);
    answer();
  });
  return pressed;
}

describe('reading a leg after its page loads', () => {
  it('reads the workspace list, keeps it, and opens the library', async () => {
    const projects = sunoObject('project-me.page-1.response');
    projects.num_total_results = 3;
    const sw = worker(sessionAt(0));
    const { content: started, visited } = start({
      address: 'https://suno.com/me',
      worker: sw,
    });
    answerTabPress(() => {
      started.observations.take(observed('workspaces', projects));
    });

    await started.resumed;

    expect(sw.asked).toContainEqual({
      type: 'sync-save',
      progress: {
        leg: 1,
        attempt: 0,
        partNumber: 0,
        counts: { clips: 0, trashed: 0, workspaces: 3, playlists: 0 },
        workspaces: projects.projects,
      },
    });
    expect(visited).toEqual(['https://suno.com/me']);
    expect(started.panel.isOpen).toBe(true);
  });

  it('gets the workspace list on a page load that brings no list request, by pressing the Workspaces tab (#421)', async () => {
    const projects = sunoObject('project-me.page-1.response');
    projects.num_total_results = 3;
    const sw = worker(sessionAt(0));
    // Loading Library sends Suno's feed request, never the workspace list's (live, 2026-10-11).
    const { content: started } = start({
      address: 'https://suno.com/me',
      worker: sw,
      seen: [observed('library-feed', { clips: [], has_more: false }, LIBRARY_FILTERS)],
    });
    const pressed = answerTabPress(() => {
      started.observations.take(observed('workspaces', projects));
    });

    await started.resumed;

    expect(pressed).toHaveLength(1);
    expect(sw.asked).toContainEqual(
      expect.objectContaining({
        type: 'sync-save',
        progress: expect.objectContaining({ leg: 1, workspaces: projects.projects }) as unknown,
      }),
    );
  });

  it('opens Library again when the tab press brings no list, and stops after the retries (#421)', async () => {
    const sw = worker(sessionAt(0));
    const first = start({ address: 'https://suno.com/me', worker: sw });
    const pressed = answerTabPress(() => undefined);
    await first.content.resumed;

    expect(pressed).toHaveLength(1);
    expect(sw.asked.find((request) => request.type === 'sync-save')).toMatchObject({
      progress: { leg: 0, attempt: 1 },
    });
    expect(first.visited).toEqual(['https://suno.com/me']);

    content?.stop();
    const again = worker(sessionAt(0, { attempt: 2 }));
    const last = start({ address: 'https://suno.com/me', worker: again });
    answerTabPress(() => undefined);
    await last.content.resumed;

    expect(last.visited).toEqual([]);
    expect(last.text('.sync-stopped')).toContain(
      "step 'Read the workspace list' expected a page of the workspace list within 20 seconds of opening the Workspaces tab",
    );
  });

  it('stops, pressing nothing, when Library shows no Workspaces tab (#421)', async () => {
    const sw = worker(sessionAt(0));
    const {
      content: started,
      text,
      visited,
    } = start({
      address: 'https://suno.com/me',
      snapshot: null,
      worker: sw,
    });

    await started.resumed;

    expect(sw.types()).toContain('sync-discard');
    expect(sw.types()).not.toContain('sync-save');
    expect(visited).toEqual([]);
    expect(text('.sync-stopped')).toBe(
      "Sync stopped: step 'Read the workspace list' expected the Workspaces tab in Library's tab row, leading to /me/workspaces. Nothing was sent for review.",
    );
  });

  it('reads the library into the export, then opens the Trash', async () => {
    const page = sunoObject('feed-v3.library-page-1.response');
    page.has_more = false;
    const sw = worker(sessionAt(1, { workspaces: [{ id: 'w1' }] }));
    const { content: started, visited } = start({
      address: 'https://suno.com/me',
      worker: sw,
      seen: [observed('library-feed', page, LIBRARY_FILTERS)],
    });

    await started.resumed;

    expect(sw.types().filter(notPanelRefresh)).toEqual([
      'sync-resume',
      'sync-create',
      'sync-part',
      'sync-save',
    ]);
    expect(sw.asked.find((request) => request.type === 'sync-create')).toMatchObject({
      type: 'sync-create',
      header: { libraryComplete: true, workspaces: [{ id: 'w1' }] },
    });
    expect(visited).toEqual(['https://suno.com/me/trash']);
  });

  it("reads a chosen workspace's own feed on /create?wid=, then opens the next workspace's page (#422)", async () => {
    const scope: SyncScope = { kind: 'workspaces', ids: ['w1', 'w2'], readTrash: false };
    const page = sunoObject('feed-v3.workspace-create-wid.page-1.response');
    page.has_more = false;
    delete page.next_cursor;
    const sw = worker(
      sessionAt(1, { scope, legs: legsFor(scope), workspaces: [{ id: 'w1' }, { id: 'w2' }] }),
    );
    const { content: started, visited } = start({
      address: 'https://suno.com/create?wid=w1',
      snapshot: 'create-workspace-songs',
      worker: sw,
      seen: [
        // The whole library's feed, and another workspace's, are not this leg's.
        observed('library-feed', { clips: [{ id: 'not-mine' }], has_more: false }, LIBRARY_FILTERS),
        observed('library-feed', page, workspaceFilters('w1')),
      ],
    });

    await started.resumed;

    expect(sw.types().filter(notPanelRefresh)).toEqual([
      'sync-resume',
      'sync-create',
      'sync-part',
      'sync-save',
    ]);
    expect(sw.asked.find((request) => request.type === 'sync-part')).toMatchObject({
      part: { partNumber: 1, clips: page.clips },
    });
    expect(visited).toEqual(['https://suno.com/create?wid=w2']);
  });

  it("stops as 'Suno tab lost' when the Create page is not the workspace's own (#422)", async () => {
    const scope: SyncScope = { kind: 'workspaces', ids: ['w1', 'w2'], readTrash: false };
    for (const address of ['https://suno.com/create?wid=w2', 'https://suno.com/create']) {
      const sw = worker(sessionAt(1, { scope, legs: legsFor(scope) }));
      const { content: started, text } = start({
        address,
        snapshot: 'create-workspace-songs',
        worker: sw,
      });

      await started.resumed;

      expect(sw.types(), address).toContain('sync-discard');
      expect(text('.sync-stopped'), address).toContain(
        "step 'Suno tab lost' expected the Suno tab to stay on the page being read (Read workspace 1 of 2)",
      );
      content?.stop();
      content = null;
    }
  });

  it('reads the Trash, completes the export, and says the review is open', async () => {
    const trash = sunoObject('clips-trashed-v2.response');
    delete trash.next_cursor;
    const sw = worker(sessionAt(2, { exportId: 'e1', partNumber: 1 }));
    const {
      content: started,
      visited,
      text,
    } = start({
      address: 'https://suno.com/me/trash',
      snapshot: 'library-trash',
      worker: sw,
      seen: [observed('trash', trash)],
    });

    await started.resumed;

    expect(sw.types()).toEqual(expect.arrayContaining(['sync-part', 'sync-complete']));
    expect(sw.asked.find((request) => request.type === 'sync-part')).toMatchObject({
      part: { partNumber: 2, clips: [], trashedClips: trash.clips },
    });
    expect(visited).toEqual([]);
    expect(text('.sync-done')).toBe(
      'Finished: read 0 workspaces, 0 clips, 2 clips in the Trash, 0 playlists. The review is open in n8Tracks.',
    );
  });

  it('follows the cover images after the export is complete, until they are done (#152)', async () => {
    const trash = sunoObject('clips-trashed-v2.response');
    delete trash.next_cursor;
    const progress = (change: Partial<ImageProgress>): ImageProgress => ({
      exportId: 'e1',
      tabId: 7,
      state: 'sending',
      total: 4,
      sent: 2,
      failed: 0,
      ignored: 0,
      ...change,
    });
    const sw = worker(sessionAt(2, { exportId: 'e1', partNumber: 1 }), CONNECTED, [
      progress({ state: 'waiting', sent: 0 }),
      progress({}),
      progress({ state: 'finished', sent: 3, failed: 1 }),
      progress({ state: 'finished', sent: 4 }),
    ]);
    const clock = fakeClock();
    const { content: started, text } = start({
      address: 'https://suno.com/me/trash',
      snapshot: 'library-trash',
      worker: sw,
      seen: [observed('trash', trash)],
      clock,
    });

    await started.resumed;

    // Asked until they were done, a second apart; never again after that.
    expect(sw.types().filter((type) => type === 'sync-images')).toHaveLength(3);
    expect(clock.slept.filter((ms) => ms === 1_000)).toHaveLength(2);
    expect(text('.sync-done')).toContain('The review is open in n8Tracks.');
    expect(text('.sync-images')).toBe(
      'Cover images: 3 of 4 sent. 1 image could not be read or sent; that Generation is imported without artwork.',
    );
  });

  it("stops as 'Suno tab lost' and discards the export when the tab is not on the leg's page", async () => {
    const sw = worker(sessionAt(1, { exportId: 'e1' }));
    const {
      content: started,
      text,
      visited,
    } = start({
      address: 'https://suno.com/create',
      worker: sw,
    });

    await started.resumed;

    expect(sw.types()).toContain('sync-discard');
    expect(sw.types()).not.toContain('sync-complete');
    expect(visited).toEqual([]);
    expect(text('.sync-stopped')).toBe(
      "Sync stopped: step 'Suno tab lost' expected the Suno tab to stay on the page being read (Read the library). Nothing was sent for review.",
    );
  });

  it('stops at a page that does not look as expected, naming the step, and offers nothing for review', async () => {
    const sw = worker(sessionAt(1));
    const {
      content: started,
      text,
      button,
    } = start({
      address: 'https://suno.com/me',
      snapshot: null,
      worker: sw,
      seen: [observed('library-feed', { clips: 'not a list', has_more: false }, LIBRARY_FILTERS)],
    });

    await started.resumed;

    expect(sw.types().filter(notPanelRefresh)).toEqual(['sync-resume', 'sync-discard']);
    expect(text('.sync-stopped')).toBe(
      "Sync stopped: step 'Read the library' expected a page of the library feed with a list of records, each with an ID. Nothing was sent for review.",
    );
    await expectNoAxeViolations(document);

    button('Try again: Sync to n8Tracks')?.click();
    expect(button('Next')).toBeDefined();
  });

  it('opens the page again when no first page came, twice at most', async () => {
    const sw = worker(sessionAt(1));
    const first = start({ address: 'https://suno.com/me', worker: sw });
    await first.content.resumed;

    expect(sw.asked.find((request) => request.type === 'sync-save')).toMatchObject({
      progress: { leg: 1, attempt: 1 },
    });
    expect(first.visited).toEqual(['https://suno.com/me']);

    content?.stop();
    const again = worker(sessionAt(1, { attempt: 2 }));
    const last = start({ address: 'https://suno.com/me', worker: again });
    await last.content.resumed;

    expect(last.visited).toEqual([]);
    expect(again.types()).toContain('sync-discard');
    expect(last.text('.sync-stopped')).toContain(
      "step 'Read the library' expected a page of the library feed within 20 seconds of opening the page",
    );
  });

  it('cancels: nothing more is read, the export is discarded, and it is never completed', async () => {
    // A clock that lets the test act while the reader waits for a page.
    let now = 0;
    const clock: Clock = {
      now: () => now,
      sleep: (ms) =>
        new Promise((resolve) => {
          setTimeout(() => {
            now += ms;
            resolve();
          }, 1);
        }),
    };
    const sw = worker(sessionAt(1, { exportId: 'e1' }));
    const {
      content: started,
      text,
      button,
      visited,
    } = start({
      address: 'https://suno.com/me',
      worker: sw,
      clock,
    });
    await vi.waitFor(() => {
      expect(text('.sync-progress')).toContain('Reading: Read the library.');
    });

    button('Cancel sync')?.click();
    await started.resumed;

    expect(sw.types()).toContain('sync-discard');
    expect(sw.types()).not.toContain('sync-complete');
    expect(sw.types()).not.toContain('sync-save');
    expect(visited).toEqual([]);
    expect(text('.sync-cancelled')).toBe(
      'Sync cancelled. Nothing more was read, and nothing was sent for review.',
    );
  });
});
