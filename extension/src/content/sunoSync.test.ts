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
  KnownWorkspace,
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
  workspaces: {
    known?: KnownWorkspace[];
    refresh?: boolean;
    reportRefused?: string;
    knownPlaylists?: KnownWorkspace[];
    refreshPlaylists?: boolean;
  } = {},
) {
  const asked: Request[] = [];
  let current = session;
  let refresh = workspaces.refresh === true;
  let refreshPlaylists = workspaces.refreshPlaylists === true;
  const send = (request: Request): Promise<unknown> => {
    asked.push(structuredClone(request));
    switch (request.type) {
      case 'state':
        return Promise.resolve(connection);
      case 'sync-resume': {
        const answer =
          refresh && current === null
            ? { session: null, refresh: true }
            : refreshPlaylists && current === null
              ? { session: null, refreshPlaylists: true }
              : null;
        refresh = false;
        refreshPlaylists = false;
        return Promise.resolve(answer ?? { session: current });
      }
      case 'sync-playlists-known':
        return Promise.resolve({
          ok: true,
          playlists: workspaces.knownPlaylists ?? [],
          message: '',
        });
      case 'sync-playlists-refresh':
        return Promise.resolve({ ok: true });
      case 'sync-playlists-report':
        return Promise.resolve({
          ok: true,
          playlists: request.playlists.map((playlist) => ({
            id: playlist.id,
            name: playlist.name,
          })),
          message: '',
        });
      case 'sync-workspaces-known':
        return Promise.resolve({ ok: true, workspaces: workspaces.known ?? [], message: '' });
      case 'sync-workspaces-refresh':
        return Promise.resolve({ ok: true });
      case 'sync-workspaces-report':
        return Promise.resolve(
          workspaces.reportRefused === undefined
            ? {
                ok: true,
                workspaces: request.workspaces.map((project) => ({
                  id: project.id,
                  name: project.name,
                })),
                message: '',
              }
            : { ok: false, workspaces: [], message: workspaces.reportRefused },
        );
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
      case 'sync-voices':
        return Promise.resolve({ ok: true, added: request.voices.length });
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

describe('the Workspaces choice (#448)', () => {
  /** The workspaces n8Tracks knows (made-up names). */
  const KNOWN: KnownWorkspace[] = [
    { id: 'w-1', name: 'Harbour Lights' },
    { id: 'w-2', name: 'Paper Moons' },
  ];

  /** The panel's workspace checkboxes, by the name each is labelled with. */
  function labels(root: ShadowRoot | null | undefined): string[] {
    return [...(root?.querySelectorAll('.sync-list label') ?? [])].map((label) =>
      label.textContent.trim(),
    );
  }

  async function chooseWorkspaces(root: ShadowRoot | null | undefined) {
    const radio = root?.querySelector<HTMLInputElement>('input[value="workspaces"]');
    if (radio === null || radio === undefined) {
      throw new Error('No Workspaces choice');
    }
    radio.checked = true;
    radio.dispatchEvent(new Event('change'));
    await Promise.resolve();
  }

  it('shows the workspaces n8Tracks knows straight away, with those Suno listed on this page, and opens nothing', async () => {
    const sw = worker(null, CONNECTED, [], { known: KNOWN });
    const {
      content: started,
      visited,
      root,
    } = start({
      address: 'https://suno.com/create',
      snapshot: null,
      worker: sw,
      // The tab saw a rename of one known workspace, and one n8Tracks does not know yet.
      seen: [
        observed('workspaces', {
          projects: [
            { id: 'w-2', name: 'Paper Moons, Revised' },
            { id: 'w-4', name: 'Night Ferry' },
          ],
        }),
      ],
    });
    await started.resumed;
    await started.toggle();

    await chooseWorkspaces(root);

    await vi.waitFor(() => {
      expect(labels(root)).toEqual(['Harbour Lights', 'Paper Moons, Revised', 'Night Ferry']);
    });
    expect(sw.types()).toContain('sync-workspaces-known');
    expect(visited).toEqual([]);
    await expectNoAxeViolations(document);
  });

  it('with none known, says so and offers Refresh from Suno, which opens Library to read the list', async () => {
    const sw = worker(null, CONNECTED, [], { known: [] });
    const {
      content: started,
      visited,
      root,
      text,
      button,
    } = start({ address: 'https://suno.com/create', snapshot: null, worker: sw });
    await started.resumed;
    await started.toggle();
    await chooseWorkspaces(root);

    await vi.waitFor(() => {
      expect(text('.sync-workspaces')).toBe(
        'n8Tracks knows no workspaces yet. Refresh from Suno to read your workspace list.',
      );
    });
    expect(labels(root)).toEqual([]);

    button('Refresh from Suno')?.click();

    await vi.waitFor(() => {
      expect(visited).toEqual(['https://suno.com/me']);
    });
    expect(sw.types()).toContain('sync-workspaces-refresh');
    expect(text('.sync-workspaces')).toBe('Reading your workspace list on Suno…');
    // A refresh is not a sync: nothing is begun.
    expect(sw.types()).not.toContain('sync-begin');
  });

  it("reads Suno's workspace list on Library's page load, reports it, and shows what n8Tracks then knows", async () => {
    const projects = sunoObject('project-me.page-1.response');
    projects.num_total_results = 3;
    const sw = worker(null, CONNECTED, [], { refresh: true });
    const {
      content: started,
      visited,
      root,
      text,
    } = start({ address: 'https://suno.com/me', worker: sw });
    const pressed = answerTabPress(() => {
      started.observations.take(observed('workspaces', projects));
    });

    await started.resumed;

    expect(pressed).toHaveLength(1);
    expect(sw.asked).toContainEqual({
      type: 'sync-workspaces-report',
      complete: true,
      workspaces: projects.projects,
    });
    expect(started.panel.isOpen).toBe(true);
    expect(root?.querySelector<HTMLInputElement>('input[value="workspaces"]')?.checked).toBe(true);
    expect(text('.sync-workspaces')).toBe('Read 3 workspaces from Suno.');
    expect(labels(root)).toHaveLength(3);
    // Nothing else: no export, no other page.
    expect(sw.types().filter((type) => type.startsWith('sync-') && type !== 'sync-resume')).toEqual(
      ['sync-workspaces-report'],
    );
    expect(visited).toEqual([]);
  });

  it('reports a list Suno stops short as not complete, and says some may be missing', async () => {
    const projects = sunoObject('project-me.page-1.response');
    projects.num_total_results = 101;
    const sw = worker(null, CONNECTED, [], { refresh: true });
    const { content: started, text } = start({ address: 'https://suno.com/me', worker: sw });
    answerTabPress(() => {
      started.observations.take(observed('workspaces', projects));
    });

    await started.resumed;

    expect(sw.asked).toContainEqual({
      type: 'sync-workspaces-report',
      complete: false,
      workspaces: projects.projects,
    });
    expect(text('.sync-workspaces')).toContain('Suno sent no further page');
  });

  it('says why when n8Tracks did not take the list', async () => {
    const projects = sunoObject('project-me.page-1.response');
    projects.num_total_results = 3;
    const sw = worker(null, CONNECTED, [], {
      refresh: true,
      reportRefused: 'n8Tracks refused the workspace list: 403 (insufficient_scope).',
    });
    const { content: started, text } = start({ address: 'https://suno.com/me', worker: sw });
    answerTabPress(() => {
      started.observations.take(observed('workspaces', projects));
    });

    await started.resumed;

    expect(text('.sync-workspaces')).toBe(
      'Your workspace list was read but not recorded: n8Tracks refused the workspace list: 403 (insufficient_scope).',
    );
  });

  it('stops, pressing nothing, when Library shows no Workspaces tab', async () => {
    const sw = worker(null, CONNECTED, [], { refresh: true });
    const { content: started, text } = start({
      address: 'https://suno.com/me',
      snapshot: null,
      worker: sw,
    });

    await started.resumed;

    expect(text('.sync-workspaces')).toContain("Not refreshed: step 'Read the workspace list'");
    expect(sw.types()).not.toContain('sync-workspaces-report');
  });
});

describe('reading a leg after its page loads', () => {
  it('goes on with a workspace list Suno stops short, keeping it as not complete (TS-007-15, #434)', async () => {
    // 101 workspaces, of which Suno answers one page; a scroll brings no next one.
    const projects = sunoObject('project-me.page-1.response');
    projects.num_total_results = 101;
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
        workspacesComplete: false,
      },
    });
    expect(sw.asked.some((request) => request.type === 'sync-discard')).toBe(false);
    // The next leg reads the user's voices on Create (#439).
    expect(visited).toEqual(['https://suno.com/create']);
  });

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
        workspacesComplete: true,
      },
    });
    expect(visited).toEqual(['https://suno.com/create']);
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
    const sw = worker(sessionAt(3, { workspaces: [{ id: 'w1' }] }));
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

  it("reads Library's songs across pages on the redesigned list, pressing only the pager's › (TS-007-23)", async () => {
    const first = sunoObject('feed-v3.library-page-1.response');
    const second = sunoObject('feed-v3.library-page-2.response');
    second.has_more = false;
    const next = observed('library-feed', second, LIBRARY_FILTERS);
    next.request = { ...next.request, cursor: 'page-2' };
    const sw = worker(sessionAt(3, { workspaces: [{ id: 'w1' }] }));
    const pressed: Element[] = [];
    document.addEventListener(
      'click',
      (event) => {
        pressed.push(event.target as Element);
      },
      true,
    );
    const { content: started, visited } = start({
      address: 'https://suno.com/me',
      snapshot: 'library-songs-groups',
      worker: sw,
      seen: [observed('library-feed', first, LIBRARY_FILTERS), next],
    });

    await started.resumed;

    // The one press is the pager's ›: the button after its page number (`#`).
    expect(pressed).toHaveLength(1);
    expect(pressed[0]?.previousElementSibling?.getAttribute('placeholder')).toBe('#');
    expect(sw.asked.find((request) => request.type === 'sync-part')).toMatchObject({
      part: {
        partNumber: 1,
        clips: [...(first.clips as unknown[]), ...(second.clips as unknown[])],
      },
    });
    expect(visited).toEqual(['https://suno.com/me/trash']);
  });

  it("reads a chosen workspace's own feed on /create?wid=, then opens the next workspace's page (#422)", async () => {
    const scope: SyncScope = { kind: 'workspaces', ids: ['w1', 'w2'], readTrash: false };
    const page = sunoObject('feed-v3.workspace-create-wid.page-1.response');
    page.has_more = false;
    delete page.next_cursor;
    const sw = worker(
      sessionAt(3, { scope, legs: legsFor(scope), workspaces: [{ id: 'w1' }, { id: 'w2' }] }),
    );
    const { content: started, visited } = start({
      address: 'https://suno.com/create?wid=w1',
      snapshot: 'create-workspace-songs-groups',
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

  it("asks for a workspace's next page by pressing the pager's Next page, and nothing else (TS-007-23)", async () => {
    const scope: SyncScope = { kind: 'workspaces', ids: ['w1'], readTrash: false };
    const first = sunoObject('feed-v3.workspace-create-wid.page-1.response');
    const second = sunoObject('feed-v3.workspace-create-wid.page-2.response');
    second.has_more = false;
    delete second.next_cursor;
    const next = observed('library-feed', second, workspaceFilters('w1'));
    next.request = { ...next.request, cursor: 'page-2' };
    const sw = worker(sessionAt(3, { scope, legs: legsFor(scope), workspaces: [{ id: 'w1' }] }));
    const pressed: string[] = [];
    document.addEventListener(
      'click',
      (event) => {
        const target = event.target as Element;
        pressed.push(target.getAttribute('aria-label') ?? target.localName);
      },
      true,
    );
    const { content: started } = start({
      address: 'https://suno.com/create?wid=w1',
      snapshot: 'create-workspace-songs-groups',
      worker: sw,
      seen: [observed('library-feed', first, workspaceFilters('w1')), next],
    });

    await started.resumed;

    expect(pressed).toEqual(['Next page']);
    expect(sw.asked.find((request) => request.type === 'sync-part')).toMatchObject({
      part: {
        partNumber: 1,
        clips: [...(first.clips as unknown[]), ...(second.clips as unknown[])],
      },
    });
  });

  it("stops as 'Suno tab lost' when the Create page is not the workspace's own (#422)", async () => {
    const scope: SyncScope = { kind: 'workspaces', ids: ['w1', 'w2'], readTrash: false };
    for (const address of ['https://suno.com/create?wid=w2', 'https://suno.com/create']) {
      const sw = worker(sessionAt(3, { scope, legs: legsFor(scope) }));
      const { content: started, text } = start({
        address,
        snapshot: 'create-workspace-songs-groups',
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
    const sw = worker(sessionAt(4, { exportId: 'e1', partNumber: 1 }));
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
    const sw = worker(sessionAt(4, { exportId: 'e1', partNumber: 1 }), CONNECTED, [
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
    const sw = worker(sessionAt(3, { exportId: 'e1' }));
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
    const sw = worker(sessionAt(3));
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
    const sw = worker(sessionAt(3));
    const first = start({ address: 'https://suno.com/me', worker: sw });
    await first.content.resumed;

    expect(sw.asked.find((request) => request.type === 'sync-save')).toMatchObject({
      progress: { leg: 3, attempt: 1 },
    });
    expect(first.visited).toEqual(['https://suno.com/me']);

    content?.stop();
    const again = worker(sessionAt(3, { attempt: 2 }));
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
    const sw = worker(sessionAt(3, { exportId: 'e1' }));
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

describe("reading the user's voices on Create (#439)", () => {
  const VOICE_LEG = 1;

  /** The voice list's answer as the observer forwards it, with these voices. */
  function voiceList(ids: string[], total = ids.length): ObservedMessage {
    return observed('personas', {
      current_page: 1,
      total_results: total,
      personas: ids.map((id) => ({
        id,
        name: `Voice ${id}`,
        description: '',
        is_trashed: false,
        is_owned: true,
      })),
    });
  }

  /**
   * Suno's answer to "+ Voice": the Voice picker (a dialog titled Voice with its Close) and the
   * voice list, which the page asks for only then. Close removes the picker. Returns what was pressed.
   */
  function answerVoicePress(answer: () => void): string[] {
    const pressed: string[] = [];
    const add = document.querySelector('[aria-label="Add Voice"]');
    if (add === null) {
      throw new Error('the snapshot has no + Voice');
    }
    add.addEventListener('click', () => {
      pressed.push('Add Voice');
      const picker = document.createElement('div');
      picker.setAttribute('role', 'dialog');
      picker.setAttribute('aria-label', 'Voice');
      const close = document.createElement('button');
      close.setAttribute('aria-label', 'Close');
      close.textContent = '×';
      close.addEventListener('click', () => {
        pressed.push('Close');
        picker.remove();
      });
      picker.append(close);
      document.body.append(picker);
      answer();
    });
    return pressed;
  }

  it('presses + Voice, reads the list, closes the picker, sends the voices, and opens the library', async () => {
    const sw = worker(sessionAt(VOICE_LEG));
    const { content: started, visited } = start({
      address: 'https://suno.com/create',
      snapshot: 'create-songs-advanced-more-options',
      worker: sw,
    });
    const pressed = answerVoicePress(() => {
      started.observations.take(voiceList(['v1', 'v2']));
    });

    await started.resumed;

    expect(pressed).toEqual(['Add Voice', 'Close']);
    expect(sw.asked).toContainEqual({
      type: 'sync-voices',
      complete: true,
      voices: [
        { id: 'v1', name: 'Voice v1', description: '', is_trashed: false, is_owned: true },
        { id: 'v2', name: 'Voice v2', description: '', is_trashed: false, is_owned: true },
      ],
    });
    expect(sw.asked).toContainEqual({
      type: 'sync-save',
      progress: expect.objectContaining({
        leg: VOICE_LEG + 1,
        counts: expect.objectContaining({ voices: 2 }) as unknown,
      }) as unknown,
    });
    expect(sw.types()).not.toContain('sync-create');
    expect(sw.types()).not.toContain('sync-discard');
    expect(visited).toEqual(['https://suno.com/me']);
  });

  it('goes on to the next leg, with a note, when + Voice is not there', async () => {
    const sw = worker(sessionAt(VOICE_LEG));
    const { content: started, visited } = start({
      address: 'https://suno.com/create',
      snapshot: null,
      worker: sw,
    });

    await started.resumed;

    expect(sw.types()).not.toContain('sync-voices');
    expect(sw.types()).not.toContain('sync-discard');
    const saved = sw.asked.find((request) => request.type === 'sync-save');
    expect(saved).toMatchObject({ progress: { leg: VOICE_LEG + 1 } });
    expect(saved?.type === 'sync-save' ? saved.progress.voicesNote : undefined).toMatch(
      /^Your voices were not read: /,
    );
    expect(visited).toEqual(['https://suno.com/me']);
  });

  it('shows the note about the voices when the sync finishes', async () => {
    const trash = sunoObject('clips-trashed-v2.response');
    delete trash.next_cursor;
    const sw = worker(
      sessionAt(4, {
        exportId: 'e1',
        partNumber: 1,
        counts: { clips: 0, trashed: 0, workspaces: 0, playlists: 0, voices: 2 },
        voicesNote:
          'Only some of your voices were read (Suno sent no further page), so none was marked unavailable.',
      }),
    );
    const { content: started, text } = start({
      address: 'https://suno.com/me/trash',
      snapshot: 'library-trash',
      worker: sw,
      seen: [observed('trash', trash)],
    });

    await started.resumed;

    expect(text('.sync-done')).toBe(
      'Finished: read 0 workspaces, 0 clips, 2 clips in the Trash, 0 playlists, 2 voices. The review is open in n8Tracks.',
    );
    expect(text('.sync-voices')).toBe(
      'Only some of your voices were read (Suno sent no further page), so none was marked unavailable.',
    );
  });
});

describe('playlists (#449)', () => {
  const PLAYLIST_SCOPE: SyncScope = {
    kind: 'playlists',
    playlists: [{ id: '00000000-0000-4000-8000-00000000050a', name: 'Harbour Lights' }],
    readTrash: false,
  };

  /** A page of the playlist list as the observer forwards it: made-up names. */
  function playlistList(ids: string[]): ObservedMessage {
    return observed('playlists', {
      current_page: 1,
      num_total_results: ids.length,
      playlists: ids.map((id) => ({ id, name: `Mix ${id}`, is_trashed: false, song_count: 3 })),
    });
  }

  /** Library's Playlists tab answers its press with `answer`, as Suno's request would. */
  function answerPlaylistsTab(answer: () => void): string[] {
    const pressed: string[] = [];
    const tab = [...document.querySelectorAll('[role="tab"]')].find(
      (element) => element.textContent === 'Playlists',
    );
    if (tab === undefined) {
      throw new Error('the snapshot has no Playlists tab');
    }
    document.addEventListener(
      'click',
      (event) => {
        const target = event.target as Element;
        pressed.push(target.textContent);
        if (target === tab) {
          event.preventDefault();
          answer();
        }
      },
      { capture: true },
    );
    return pressed;
  }

  it("reads the playlist list on Library by its Playlists tab, reports it, and goes on (the sync's leg)", async () => {
    const legs = legsFor(PLAYLIST_SCOPE);
    const sw = worker(sessionAt(2, { scope: PLAYLIST_SCOPE, legs }));
    const { content: started, visited } = start({ address: 'https://suno.com/me', worker: sw });
    const pressed = answerPlaylistsTab(() => {
      started.observations.take(playlistList(['p1', 'p2']));
    });

    await started.resumed;

    expect(pressed).toEqual(['Playlists']);
    expect(sw.asked).toContainEqual({
      type: 'sync-playlists-report',
      complete: true,
      playlists: [
        { id: 'p1', name: 'Mix p1', is_trashed: false, song_count: 3 },
        { id: 'p2', name: 'Mix p2', is_trashed: false, song_count: 3 },
      ],
    });
    expect(sw.types()).not.toContain('sync-create');
    expect(visited).toEqual(['https://suno.com/playlist/00000000-0000-4000-8000-00000000050a']);
  });

  /** The synthetic playlist of TS-007-25: 77 songs, 50 in its page's embedded data (#452). */
  const LONG_ID = '00000000-0000-4000-8000-000000000600';
  const LONG_SCOPE: SyncScope = {
    kind: 'playlists',
    playlists: [{ id: LONG_ID, name: 'Synthetic Playlist' }],
    readTrash: false,
  };

  /** The page's own `POST /api/unified/feed` answer for the songs after the first 50. */
  function secondPage(): ObservedMessage {
    const message = observed('playlist-feed', sunoObject('unified-feed.playlist-page-2.response'));
    message.request = { ...message.request, cursor: '50', feedId: `generic_playlist:${LONG_ID}` };
    return message;
  }

  it("imports a playlist's songs from its page's data and the page Suno sends on a scroll, with the membership", async () => {
    const legs = legsFor(LONG_SCOPE);
    const sw = worker(sessionAt(3, { scope: LONG_SCOPE, legs }));
    const { content: started, text } = start({
      address: `https://suno.com/playlist/${LONG_ID}`,
      snapshot: 'playlist-embedded',
      worker: sw,
      seen: [secondPage()],
    });
    const clicks: string[] = [];
    document.addEventListener(
      'click',
      (event) => {
        clicks.push((event.target as Element).textContent);
      },
      { capture: true },
    );

    await started.resumed;

    const part = sw.asked.find((request) => request.type === 'sync-part');
    const sentPart = part?.type === 'sync-part' ? part.part : null;
    const ids = Array.from(
      { length: 77 },
      (_, index) => `00000000-0000-4000-8000-${(0x601 + index).toString(16).padStart(12, '0')}`,
    );
    expect((sentPart?.clips as { id: string }[] | undefined)?.map((clip) => clip.id)).toEqual(ids);
    expect(sentPart?.playlists).toEqual([
      { id: LONG_ID, name: 'Synthetic Playlist', clipIds: ids, songCount: 77 },
    ]);
    // Scrolling presses nothing.
    expect(clicks).toEqual([]);
    expect(sw.types()).toContain('sync-complete');
    expect(text('.sync-note')).toBe('');
  });

  it('says a playlist was read in part when Suno sends no next page after the scrolls', async () => {
    const legs = legsFor(LONG_SCOPE);
    const sw = worker(sessionAt(3, { scope: LONG_SCOPE, legs }));
    const { content: started, text } = start({
      address: `https://suno.com/playlist/${LONG_ID}`,
      snapshot: 'playlist-embedded',
      worker: sw,
    });

    await started.resumed;

    expect(sw.types()).toContain('sync-complete');
    expect(text('.sync-note')).toBe(
      'Playlist “Synthetic Playlist” was read in part: 50 of 77 songs (Suno sent no further page). The review shows it so, and does not record it unless you choose to.',
    );
  });

  it('stops, saying the playlist page changed, when its embedded data holds no feed for it', async () => {
    const legs = legsFor(LONG_SCOPE);
    const sw = worker(sessionAt(3, { scope: LONG_SCOPE, legs }));
    const { content: started, text } = start({
      address: `https://suno.com/playlist/${LONG_ID}`,
      snapshot: 'playlist-embedded-no-feed',
      worker: sw,
    });

    await started.resumed;

    expect(sw.types()).not.toContain('sync-part');
    expect(sw.types()).toContain('sync-discard');
    expect(text('.sync-stopped')).toBe(
      `Sync stopped: step 'Read playlist 1 of 1' expected Suno's data for the playlist's songs in its page, but the playlist page changed (its embedded data holds no feed query for this playlist). Nothing was sent for review.`,
    );
  });

  it('offers the playlists n8Tracks knows, and Refresh from Suno reads the list on Library', async () => {
    const sw = worker(null, CONNECTED, [], {
      knownPlaylists: [{ id: 'p-known', name: 'Paper Moons' }],
    });
    const {
      content: started,
      root,
      visited,
    } = start({
      address: 'https://suno.com/create',
      snapshot: null,
      worker: sw,
    });
    await started.resumed;
    await started.toggle();
    const radio = root?.querySelector<HTMLInputElement>('input[value="playlists"]');
    if (radio === null || radio === undefined) {
      throw new Error('No Playlists choice');
    }
    radio.checked = true;
    radio.dispatchEvent(new Event('change'));
    await vi.waitFor(() => {
      expect(
        [...(root?.querySelectorAll('.sync-list label') ?? [])].map((label) =>
          label.textContent.trim(),
        ),
      ).toEqual(['Paper Moons']);
    });
    expect(sw.types()).toContain('sync-playlists-known');

    const refresh = [...(root?.querySelectorAll('button') ?? [])].find(
      (button) => button.textContent === 'Refresh from Suno',
    );
    refresh?.click();
    await vi.waitFor(() => {
      expect(visited).toEqual(['https://suno.com/me']);
    });
    expect(sw.types()).toContain('sync-playlists-refresh');
  });

  it("reads Suno's playlist list on Library's page load after Refresh from Suno, and reports it", async () => {
    const sw = worker(null, CONNECTED, [], { refreshPlaylists: true });
    const {
      content: started,
      visited,
      root,
    } = start({ address: 'https://suno.com/me', worker: sw });
    const pressed = answerPlaylistsTab(() => {
      started.observations.take(playlistList(['p1']));
    });

    await started.resumed;

    expect(pressed).toEqual(['Playlists']);
    expect(root?.querySelector<HTMLInputElement>('input[value="playlists"]')?.checked).toBe(true);
    expect(root?.querySelector('.sync-playlists')?.textContent).toBe('Read 1 playlist from Suno.');
    expect(sw.types().filter((type) => type.startsWith('sync-') && type !== 'sync-resume')).toEqual(
      ['sync-playlists-report'],
    );
    expect(visited).toEqual([]);
  });
});
