// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { FeedWindow } from '../adapter/observations.ts';
import { OBSERVER_SOURCE, type ObservedMessage } from '../adapter/observed.ts';
import type { ClipLookupReply, ClipLookupRow, ConnectedState, Request } from '../messages.ts';
import { START_NOT_YET } from '../panel/DownloadView.ts';
import { expectNoAxeViolations } from '../testing/a11y.ts';
import { fakeClock, snapshotHtml } from '../testing/snapshots.ts';
import { sunoObject } from '../testing/sunoResponses.ts';
import { startSunoContent, type SunoContent } from './suno.ts';

/**
 * The Download view on Suno (#215), from Load library to the summary, over the TS-003 and TS-004
 * fixtures. The complement throughout: the only messages are the view's own (`download-*`) and the
 * panel's refresh; the only n8Tracks call is the lookup; nothing on Suno's page is pressed.
 */

const CONNECTED: ConnectedState = {
  status: 'connected',
  address: 'https://n8tracks.example.com',
  credentialName: 'Chrome at home',
  scopes: ['suno.sync'],
  applicationVersion: '0.1.0',
  compatibility: { kind: 'compatible' },
  features: [
    { feature: 'sync', label: 'Library sync', scope: 'suno.sync', available: true, reason: null },
  ],
};

/** The panel's own questions, and the page load's questions of the sync and Generate on Suno. */
const OTHERS = new Set([
  'state',
  'diagnostics-record',
  'diagnostic-report',
  'sync-resume',
  'generate-resume',
]);

const LIBRARY_FILTERS = (sunoObject('feed-v3.library-page-1.request') as { filters: unknown })
  .filters;

const FIRST = '00000000-0000-4000-8000-000000000118';
const SECOND = '00000000-0000-4000-8000-00000000011d';
const THIRD = '00000000-0000-4000-8000-000000000121';
const FOURTH = '00000000-0000-4000-8000-000000000122';
const GENERATING = '00000000-0000-4000-8000-000000000002';
const TRASHED = '00000000-0000-4000-8000-000000000123';
const OTHER_WORKSPACE = '00000000-0000-4000-8000-000000000116';

function observed(kind: ObservedMessage['kind'], body: unknown, cursor: string | null = null) {
  const message: ObservedMessage = {
    source: OBSERVER_SOURCE,
    type: 'observed',
    kind,
    request: {
      cursor,
      page: null,
      filters: kind === 'library-feed' ? LIBRARY_FILTERS : null,
      feedId: null,
    },
    body,
  };
  return message;
}

function titled(clip: Record<string, unknown>, title: string) {
  return { ...clip, title };
}

/** One page of the library holding every kind of clip: two workspaces, generating, and trashed. */
function everyKindPage() {
  const first = sunoObject('feed-v3.library-page-1.response').clips as Record<string, unknown>[];
  const second = sunoObject('feed-v3.library-page-2.response').clips as Record<string, unknown>[];
  const workspace = sunoObject('feed-v3.workspace-last-page.response').clips as Record<
    string,
    unknown
  >[];
  const generating = (sunoObject('generate-v2-web.response').clips as Record<string, unknown>[])[0];
  const trashed = (sunoObject('clips-trashed-v2.response').clips as Record<string, unknown>[])[0];
  return {
    clips: [
      titled(first[0] ?? {}, 'Morning light'),
      titled(first[1] ?? {}, 'Night drive'),
      titled(second[0] ?? {}, 'Morning rain'),
      { ...titled(second[1] ?? {}, ''), is_download_unlocked: true },
      titled(workspace[0] ?? {}, 'Other place'),
      titled(generating ?? {}, 'Still cooking'),
      titled(trashed ?? {}, 'Thrown away'),
    ],
    has_more: false,
  };
}

function lookupRow(sunoId: string, change: Partial<ClipLookupRow> = {}): ClipLookupRow {
  return {
    sunoId,
    generation: null,
    artist: null,
    deleted: false,
    downloadedFormats: [],
    ...change,
  };
}

/** The service worker as the content script sees it. */
function worker(
  options: {
    load?: { selected: string[] } | null;
    begin?: { ok: true } | { ok: false; message: string };
    lookup?: (sunoIds: string[]) => ClipLookupReply;
    formats?: string[];
  } = {},
) {
  const asked: Request[] = [];
  let load = options.load ?? null;
  let formats = options.formats ?? [];
  const send = (request: Request): Promise<unknown> => {
    asked.push(structuredClone(request));
    switch (request.type) {
      case 'state':
        return Promise.resolve(CONNECTED);
      case 'sync-resume':
        return Promise.resolve({ session: null });
      case 'generate-resume':
        return Promise.resolve({ job: null });
      case 'download-resume': {
        const taken = load;
        load = null;
        return Promise.resolve({ load: taken });
      }
      case 'download-begin':
        return Promise.resolve(options.begin ?? { ok: true });
      case 'download-formats':
        if (request.formats !== undefined) {
          formats = request.formats;
        }
        return Promise.resolve({ formats });
      case 'download-lookup':
        return Promise.resolve(
          (options.lookup ?? ((ids) => ({ ok: true, rows: ids.map((id) => lookupRow(id)) })))(
            request.sunoIds,
          ),
        );
      default:
        return Promise.resolve(undefined);
    }
  };
  return {
    asked,
    send,
    own: () => asked.map((request) => request.type).filter((type) => !OTHERS.has(type)),
  };
}

function sunoWindow(seen: ObservedMessage[]): FeedWindow & { deliver(m: ObservedMessage): void } {
  const listeners: ((event: MessageEvent) => void)[] = [];
  const view = {
    origin: 'https://suno.com',
    postMessage: () => {
      for (const message of seen) {
        view.deliver(message);
      }
    },
    addEventListener: (_type: 'message', listener: (event: MessageEvent) => void) => {
      listeners.push(listener);
    },
    removeEventListener: () => undefined,
    deliver: (message: ObservedMessage) => {
      for (const listener of listeners) {
        listener({
          data: message,
          source: view,
          origin: 'https://suno.com',
        } as unknown as MessageEvent);
      }
    },
  };
  return view;
}

let content: SunoContent | null = null;

function start(options: {
  address: string;
  snapshot?: string | null;
  worker: ReturnType<typeof worker>;
  seen?: ObservedMessage[];
}) {
  document.title = 'Suno';
  document.documentElement.lang = 'en';
  document.body.innerHTML =
    options.snapshot === null
      ? '<main><h1>Suno</h1><p>Library</p></main>'
      : snapshotHtml(options.snapshot ?? 'library-list');
  const visited: string[] = [];
  const window = sunoWindow(options.seen ?? []);
  content = startSunoContent({
    document,
    send: options.worker.send,
    extensionVersion: '0.1.0',
    address: () => options.address,
    navigate: (address) => visited.push(address),
    window,
    clock: fakeClock(),
  });
  const root = content.panel.host.shadowRoot;
  const view = () => root?.querySelector('.download-view');
  const text = (selector: string) => view()?.querySelector(selector)?.textContent ?? '';
  const button = (name: string) =>
    [...(view()?.querySelectorAll('button') ?? [])].find(
      (candidate) => (candidate.getAttribute('aria-label') ?? candidate.textContent) === name,
    );
  const rows = () =>
    [...(view()?.querySelectorAll<HTMLElement>('.dl-row') ?? [])].map((row) => ({
      id: row.dataset.sunoId ?? '',
      text: row.textContent,
      box: row.querySelector<HTMLInputElement>('input[type="checkbox"]'),
    }));
  const box = (id: string) =>
    view()?.querySelector<HTMLInputElement>(`input[type="checkbox"][data-suno-id="${id}"]`);
  const format = (value: string) =>
    view()?.querySelector<HTMLInputElement>(`.dl-formats input[value="${value}"]`);
  const summary = () =>
    [...(view()?.querySelectorAll('.dl-summary p') ?? [])].map((line) => line.textContent);
  return { content, visited, text, button, rows, box, format, summary, view, window };
}

/** Ticks a checkbox the way a user does: its state changes, and it says so. */
function tick(input: HTMLInputElement | null | undefined, checked = true) {
  if (input === null || input === undefined) {
    throw new Error('No such checkbox.');
  }
  input.checked = checked;
  input.dispatchEvent(new Event('change'));
}

afterEach(() => {
  content?.stop();
  content = null;
  document.body.innerHTML = '';
});

describe('Load library', () => {
  it('never reads by itself: a page load with no Load library waiting asks once and reads nothing', async () => {
    const sw = worker();
    const { content: started, visited } = start({
      address: 'https://suno.com/me',
      worker: sw,
      seen: [observed('library-feed', everyKindPage())],
    });

    await started.downloading;

    expect(sw.own()).toEqual(['download-resume']);
    expect(visited).toEqual([]);
    expect(started.panel.isOpen).toBe(false);
  });

  it('asks the service worker, then opens the library page again, carrying the selection', async () => {
    const sw = worker();
    const {
      content: started,
      visited,
      button,
    } = start({
      address: 'https://suno.com/create',
      snapshot: null,
      worker: sw,
    });
    await started.toggle();

    button('Load library')?.click();

    await vi.waitFor(() => {
      expect(visited).toEqual(['https://suno.com/me']);
    });
    expect(sw.asked).toContainEqual({ type: 'download-begin', selected: [] });
  });

  it('is refused, saying why, while a sync or Generate on Suno runs, and opens nothing', async () => {
    const sw = worker({
      begin: { ok: false, message: 'A sync to n8Tracks is running.' },
    });
    const {
      content: started,
      visited,
      button,
      text,
    } = start({
      address: 'https://suno.com/me',
      worker: sw,
    });
    await started.toggle();

    button('Load library')?.click();

    await vi.waitFor(() => {
      expect(text('.dl-status')).toBe('The library was not loaded: A sync to n8Tracks is running.');
    });
    expect(visited).toEqual([]);
  });
});

describe('reading the library on the page Load library opened', () => {
  it('reads every page the way a sync does, lists the clips, and asks n8Tracks once', async () => {
    const first = sunoObject('feed-v3.library-page-1.response');
    const second = sunoObject('feed-v3.library-page-2.response');
    second.has_more = false;
    const sw = worker({
      load: { selected: [] },
      lookup: (ids) => ({
        ok: true,
        rows: ids.map((id) =>
          id === FIRST
            ? lookupRow(id, { generation: { id: 'g1', shortcode: 'n8-1-v1-g1' }, artist: 'Art' })
            : id === THIRD
              ? lookupRow(id, { deleted: true })
              : lookupRow(id),
        ),
      }),
    });
    const clicks: EventTarget[] = [];
    document.addEventListener(
      'click',
      (event) => {
        clicks.push(event.target ?? document);
      },
      true,
    );
    const {
      content: started,
      rows,
      text,
      visited,
    } = start({
      address: 'https://suno.com/me',
      worker: sw,
      seen: [observed('library-feed', first), observed('library-feed', second, 'c2')],
    });

    await started.downloading;

    expect(started.panel.isOpen).toBe(true);
    expect(rows().map((row) => row.id)).toEqual([FIRST, SECOND, THIRD, FOURTH]);
    expect(text('.dl-status')).toBe('4 clips listed from every workspace.');
    expect(rows()[0]?.text).toContain('4:04');
    expect(rows()[0]?.text).toContain('2026-10-01');
    expect(rows()[0]?.text).toContain('Not unlocked');
    expect(rows()[0]?.text).toContain('In n8Tracks: n8-1-v1-g1');
    expect(rows()[1]?.text).toContain('Not in n8Tracks');
    expect(rows()[2]?.text).toContain('Deleted in n8Tracks');

    // Complement: the view's own messages only, one lookup of what was read, no navigation, and
    // the one run on the page is the load-more scroll, which presses nothing.
    expect(sw.own()).toEqual(['download-resume', 'download-formats', 'download-lookup']);
    expect(sw.asked.find((request) => request.type === 'download-lookup')).toEqual({
      type: 'download-lookup',
      sunoIds: [FIRST, SECOND, THIRD, FOURTH],
    });
    expect(visited).toEqual([]);
    const runs = sw.asked.flatMap((request) =>
      request.type === 'diagnostics-record' && request.run !== undefined
        ? [(request.run as { workflowId: string }).workflowId]
        : [],
    );
    expect(runs).toEqual(['load-more']);
    expect(clicks).toEqual([]);
  });

  it('keeps the clips read so far under a banner when the read stops, one by one only', async () => {
    const first = sunoObject('feed-v3.library-page-1.response');
    const sw = worker({ load: { selected: [SECOND] } });
    const {
      content: started,
      rows,
      text,
      button,
      box,
    } = start({
      address: 'https://suno.com/me',
      worker: sw,
      seen: [observed('library-feed', first)],
    });

    await started.downloading;

    expect(rows().map((row) => row.id)).toEqual([FIRST, SECOND]);
    expect(text('.dl-incomplete')).toBe(
      "The list is incomplete: step 'Read the library' expected a page of the library feed within 20 seconds (asked 3 times). The clips read so far can be selected one by one; Select all is off until the library is read to its end (Refresh).",
    );
    expect(button('Select all shown')?.disabled).toBe(true);
    // The selection carried over the Refresh is kept, and others can be added one by one.
    expect(box(SECOND)?.checked).toBe(true);
    tick(box(FIRST));
    expect(box(FIRST)?.checked).toBe(true);
    expect(button('Refresh: read the library again')?.disabled).toBe(false);
  });

  it('refuses to read when the tab has left the library page', async () => {
    const sw = worker({ load: { selected: [] } });
    const {
      content: started,
      text,
      rows,
    } = start({
      address: 'https://suno.com/create',
      snapshot: null,
      worker: sw,
      seen: [observed('library-feed', everyKindPage())],
    });

    await started.downloading;

    expect(text('.dl-status')).toBe(
      'The library was not loaded: the Suno tab left the library before it was read. Load it again.',
    );
    expect(rows()).toEqual([]);
  });
});

describe('the list, its filters, and the selection', () => {
  async function loaded(options: Parameters<typeof worker>[0] = {}) {
    const sw = worker({ load: { selected: [] }, ...options });
    const started = start({
      address: 'https://suno.com/me',
      snapshot: null,
      worker: sw,
      seen: [observed('library-feed', everyKindPage())],
    });
    await started.content.downloading;
    return { ...started, sw };
  }

  it('shows generating and trashed clips disabled with the reason, and is accessible', async () => {
    const { rows, box } = await loaded();

    expect(rows()).toHaveLength(7);
    expect(box(GENERATING)?.disabled).toBe(true);
    expect(box(TRASHED)?.disabled).toBe(true);
    expect(rows().find((row) => row.id === GENERATING)?.text).toContain('Still generating in Suno');
    expect(rows().find((row) => row.id === TRASHED)?.text).toContain("In Suno's Trash");
    expect(box(FIRST)?.disabled).toBe(false);
    expect(rows().find((row) => row.id === FOURTH)?.text).toContain('Untitled');
    expect(rows().find((row) => row.id === FOURTH)?.text).toContain('Unlocked on Suno');
    await expectNoAxeViolations(document);
  });

  it('narrows by workspace and by text in the title, and selects all shown under the filter', async () => {
    const { rows, view, button, summary, box } = await loaded();
    const workspace = view()?.querySelector<HTMLSelectElement>('select');
    const search = view()?.querySelector<HTMLInputElement>('input[type="search"]');

    expect([...(workspace?.options ?? [])].map((option) => option.value)).toContain(
      '00000000-0000-4000-8000-000000000105',
    );
    if (workspace !== null && workspace !== undefined) {
      workspace.value = '00000000-0000-4000-8000-000000000105';
      workspace.dispatchEvent(new Event('change'));
    }
    expect(rows().map((row) => row.id)).toEqual([OTHER_WORKSPACE]);

    if (workspace !== null && workspace !== undefined) {
      workspace.value = '';
      workspace.dispatchEvent(new Event('change'));
    }
    if (search !== null && search !== undefined) {
      search.value = 'morning';
      search.dispatchEvent(new Event('input'));
    }
    expect(rows().map((row) => row.id)).toEqual([FIRST, THIRD]);

    button('Select all shown')?.click();
    expect(box(FIRST)?.checked).toBe(true);
    expect(box(THIRD)?.checked).toBe(true);

    // The selection survives the filter; the summary says how many the filter hides.
    if (search !== null && search !== undefined) {
      search.value = 'night';
      search.dispatchEvent(new Event('input'));
    }
    expect(rows().map((row) => row.id)).toEqual([SECOND]);
    expect(summary()).toContain('2 selected clips are hidden by the current filter.');
    expect(summary()[0]).toBe('Selected: 2 clips.');
  });

  it('summarises clips, formats, files, unlocks, and where the files go, and Start stays disabled', async () => {
    const { button, summary, format, text, sw, window } = await loaded();

    button('Select all shown')?.click();
    expect(summary()[0]).toBe('Selected: 5 clips.');
    expect(text('.dl-start-why')).toBe('Choose at least one format.');

    tick(format('wav'));
    tick(format('mp3'));
    expect(summary()).toEqual([
      'Selected: 5 clips.',
      'Formats: WAV, MP3.',
      'Files: 10.',
      "This run uses 4 Suno download unlocks (selected clips not yet unlocked). How many remain this period is not known yet: Suno sends it when a clip's Download dialog opens. Open any clip's More options › Download on this page, then close the dialog without downloading.",
      "The files go to the browser's download folder. Copy them into the n8Tracks media folder yourself.",
    ]);
    expect(text('.dl-start-why')).toBe(
      'This run needs 4 Suno download unlocks, and how many remain is not known yet.',
    );
    expect(sw.asked).toContainEqual({ type: 'download-formats', formats: ['wav', 'mp3'] });

    // The page reads the allowance when a Download dialog opens (TS-004).
    window.deliver(observed('billing', sunoObject('billing-info.download-excerpt.response')));
    expect(summary()[3]).toBe(
      'This run uses 4 Suno download unlocks (selected clips not yet unlocked). 60 remain this period.',
    );
    expect(text('.dl-start-why')).toBe(START_NOT_YET);
    expect(button('Start download')?.disabled).toBe(true);

    window.deliver(
      observed('billing', {
        download_usage: {
          current_period_downloads_used: 57,
          current_period_downloads_limit: 60,
          additional_download_remaining: 0,
        },
      }),
    );
    expect(text('.dl-start-why')).toBe(
      'This run needs 4 Suno download unlocks, but only 3 remain this period.',
    );

    // The stream alone needs no unlock.
    tick(format('wav'), false);
    tick(format('mp3'), false);
    tick(format('m4a-stream'));
    expect(summary()).not.toContain(expect.stringContaining('unlock'));
    expect(text('.dl-start-why')).toBe(START_NOT_YET);

    button('Clear selection')?.click();
    expect(summary()[0]).toBe('Selected: 0 clips.');
    expect(text('.dl-start-why')).toBe('Select at least one clip.');
    expect(button('Start download')?.disabled).toBe(true);
  });

  it('remembers the formats: none the first time, then the last chosen', async () => {
    const first = await loaded();
    expect(first.format('wav')?.checked).toBe(false);
    first.content.stop();
    content = null;

    const again = await loaded({ formats: ['mp3', 'm4a-stream'] });

    expect(again.format('mp3')?.checked).toBe(true);
    expect(again.format('m4a-stream')?.checked).toBe(true);
    expect(again.format('wav')?.checked).toBe(false);
  });

  it('works when not connected, saying why "already in n8Tracks" is unavailable', async () => {
    const { text, rows, button, box } = await loaded({
      lookup: () => ({
        ok: false,
        unavailable: true,
        message: 'The extension is not connected to n8Tracks.',
      }),
    });

    expect(text('.dl-lookup')).toBe(
      'Already in n8Tracks: unavailable. The extension is not connected to n8Tracks. The clips can still be downloaded.',
    );
    expect(rows()[0]?.text).toContain('In n8Tracks: unknown');
    expect(button('Retry: check which clips are in n8Tracks')?.hidden).toBe(true);
    tick(box(FIRST));
    expect(box(FIRST)?.checked).toBe(true);
  });

  it('offers Retry when the lookup fails while connected, and the list stays usable', async () => {
    let calls = 0;
    const { text, rows, button, sw } = await loaded({
      lookup: (ids) => {
        calls += 1;
        return calls === 1
          ? { ok: false, unavailable: false, message: 'Cannot reach n8Tracks.' }
          : { ok: true, rows: ids.map((id) => lookupRow(id)) };
      },
    });

    expect(text('.dl-lookup')).toBe('Already in n8Tracks: unknown. Cannot reach n8Tracks.');
    const retry = button('Retry: check which clips are in n8Tracks');
    expect(retry?.hidden).toBe(false);

    retry?.click();

    await vi.waitFor(() => {
      expect(rows()[0]?.text).toContain('Not in n8Tracks');
    });
    expect(sw.own().filter((type) => type === 'download-lookup')).toHaveLength(2);
  });
});
