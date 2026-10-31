import { describe, expect, it } from 'vitest';
import type { ExportPart, SyncLeg, SyncProgress, SyncScope } from '../messages.ts';
import { fakeClock } from '../testing/snapshots.ts';
import { sunoObject } from '../testing/sunoResponses.ts';
import {
  exportHeader,
  legsFor,
  readsTrash,
  PAGE_WAIT_MS,
  PART_CLIPS,
  readLeg,
  readLibrary,
  ReadCancelled,
  ReadStop,
  stepOf,
  VOICES_STEP,
  type LegContext,
} from './libraryReader.ts';
import { ObservationFeed } from './observations.ts';
import {
  forwardedBody,
  OBSERVER_SOURCE,
  type ObservedKind,
  type ObservedMessage,
  type ObservedRequest,
} from './observed.ts';

type Clip = Record<string, unknown> & { id: string };

const LIBRARY_FILTERS = (sunoObject('feed-v3.library-page-1.request') as { filters: unknown })
  .filters;
const WORKSPACE_FILTERS = (
  sunoObject('feed-v3.workspace-last-page.request') as { filters: unknown }
).filters;

function observed(
  kind: ObservedKind,
  body: unknown,
  request: Partial<ObservedRequest> = {},
): ObservedMessage {
  return {
    source: OBSERVER_SOURCE,
    type: 'observed',
    kind,
    request: { cursor: null, page: null, filters: null, feedId: null, ...request },
    body,
  };
}

function clipsOf(name: string): Clip[] {
  return sunoObject(name).clips as Clip[];
}

/** `count` copies of a fixture clip with IDs of their own, in a workspace. */
function clips(count: number, prefix: string, workspace = 'w1'): Clip[] {
  const [model] = clipsOf('feed-v3.library-page-1.response');
  return Array.from({ length: count }, (_, index) => ({
    ...model,
    id: `${prefix}-${String(index)}`,
    project: { id: workspace },
  }));
}

/** A page of the library feed: the TS-003 shape with these clips. */
function libraryPage(pageClips: Clip[], hasMore: boolean, cursor: string | null) {
  const body: Record<string, unknown> = {
    ...sunoObject('feed-v3.library-page-1.response'),
    clips: pageClips,
  };
  body.has_more = hasMore;
  if (!hasMore) {
    delete body.next_cursor;
  }
  return observed('library-feed', body, { cursor, filters: LIBRARY_FILTERS });
}

const START: SyncProgress = {
  leg: 0,
  attempt: 0,
  partNumber: 0,
  counts: { clips: 0, trashed: 0, workspaces: 0, playlists: 0 },
  workspaces: [],
};

/** How the fake page answers the voice leg (#439). */
interface VoiceSetup {
  /** What Suno answers when "+ Voice" opens the picker. */
  onOpen?: ObservedMessage[];
  /** What each scroll of the picker brings. */
  onMore?: ObservedMessage[][];
  /** Opening the picker stops at this step (a ReadStop), as when "+ Voice" is not found. */
  openFails?: boolean;
  /** Closing the picker stops. */
  closeFails?: boolean;
  /** n8Tracks' answer to the voices sent: null when it took them, else why not. */
  refusal?: string | null;
}

/**
 * A fake Suno page: `onLoad` are the responses of the page's own first requests; each `more()`
 * (the load-more workflow: a scroll) makes Suno answer with the next entry of `onMore`, if any;
 * pressing Library's Workspaces tab (`openWorkspaceList`, #421) makes it answer with `onOpen`; and
 * opening the Voice picker (#439) answers as `voice` says.
 */
function suno(
  onLoad: ObservedMessage[],
  onMore: ObservedMessage[][] = [],
  onOpen: ObservedMessage[] = [],
  voice: VoiceSetup = {},
) {
  const clock = fakeClock();
  const feed = new ObservationFeed(
    {
      origin: 'https://suno.com',
      postMessage: () => undefined,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    },
    clock,
  );
  for (const message of onLoad) {
    feed.take(message);
  }
  const headers: Record<string, unknown>[] = [];
  const parts: ExportPart[] = [];
  const moreSteps: string[] = [];
  const openSteps: string[] = [];
  const voiceSteps: string[] = [];
  const sentVoices: { complete: boolean; voices: Record<string, unknown>[] }[] = [];
  const voiceMore = voice.onMore ?? [];
  const counts: unknown[] = [];
  const controller = new AbortController();
  let created = false;
  const context: LegContext = {
    observations: feed,
    openWorkspaceList: (step) => {
      openSteps.push(step);
      for (const message of onOpen.splice(0)) {
        feed.take(message);
      }
      return Promise.resolve();
    },
    more: (step) => {
      moreSteps.push(step);
      for (const message of onMore.shift() ?? []) {
        feed.take(message);
      }
      return Promise.resolve();
    },
    openVoices: (step) => {
      voiceSteps.push(`open ${step}`);
      if (voice.openFails === true) {
        return Promise.reject(new ReadStop('voice picker', 'the Voice picker'));
      }
      for (const message of (voice.onOpen ?? []).splice(0)) {
        feed.take(message);
      }
      return Promise.resolve();
    },
    moreVoices: (step) => {
      voiceSteps.push(`more ${step}`);
      for (const message of voiceMore.shift() ?? []) {
        feed.take(message);
      }
      return Promise.resolve();
    },
    closeVoices: (step) => {
      voiceSteps.push(`close ${step}`);
      return voice.closeFails === true
        ? Promise.reject(new ReadStop('picker closed', 'the Voice picker, closed'))
        : Promise.resolve();
    },
    sendVoices: (complete, voices) => {
      sentVoices.push({ complete, voices: structuredClone(voices) });
      return Promise.resolve(voice.refusal ?? null);
    },
    sink: {
      create: (header) => {
        if (!created) {
          headers.push(header);
          created = true;
        }
        return Promise.resolve();
      },
      part: (part) => {
        parts.push(structuredClone(part));
        return Promise.resolve();
      },
    },
    signal: controller.signal,
    progress: (now) => counts.push(now),
    versions: { extension: '0.1.0', adapter: 2 },
    now: () => '2026-10-06T12:00:00.000Z',
  };
  return {
    feed,
    context,
    headers,
    parts,
    moreSteps,
    openSteps,
    voiceSteps,
    sentVoices,
    counts,
    controller,
    clock,
  };
}

async function stopOf(reading: Promise<unknown>): Promise<ReadStop> {
  try {
    await reading;
  } catch (error) {
    if (error instanceof ReadStop) {
      return error;
    }
    throw error;
  }
  throw new Error('The read did not stop.');
}

const LIBRARY: SyncScope = { kind: 'library' };

describe('the legs of a sync', () => {
  it('reads the workspace list, then what was chosen, then the Trash', () => {
    expect(legsFor(LIBRARY)).toEqual([
      { list: 'workspaces' },
      { list: 'voices' },
      { list: 'library' },
      { list: 'trash' },
    ]);
    // A workspace sync reads each chosen workspace's own feed, not the whole library (#422).
    const workspaces = legsFor({ kind: 'workspaces', ids: ['w1', 'w2'], readTrash: true });
    expect(workspaces).toEqual([
      { list: 'workspaces' },
      { list: 'voices' },
      { list: 'workspace', id: 'w1' },
      { list: 'workspace', id: 'w2' },
      { list: 'trash' },
    ]);
    expect(workspaces.map((leg) => stepOf(leg, workspaces))).toEqual([
      'Read the workspace list',
      'Read your voices',
      'Read workspace 1 of 2',
      'Read workspace 2 of 2',
      'Read the Trash',
    ]);
    const playlists = legsFor({
      kind: 'playlists',
      playlists: [
        { id: 'p1', name: 'One' },
        { id: 'p2', name: 'Two' },
      ],
      readTrash: true,
    });
    expect(playlists.map((leg) => leg.list)).toEqual([
      'workspaces',
      'voices',
      'playlist',
      'playlist',
      'trash',
    ]);
    expect(playlists.map((leg) => stepOf(leg, playlists))).toEqual([
      'Read the workspace list',
      'Read your voices',
      'Read playlist 1 of 2',
      'Read playlist 2 of 2',
      'Read the Trash',
    ]);
  });

  it('skips the Trash on a workspace or playlist sync unless asked; a whole-library sync always reads it (#420)', () => {
    expect(legsFor({ kind: 'workspaces', ids: ['w1'], readTrash: false })).toEqual([
      { list: 'workspaces' },
      { list: 'voices' },
      { list: 'workspace', id: 'w1' },
    ]);
    expect(
      legsFor({
        kind: 'playlists',
        playlists: [{ id: 'p1', name: 'One' }],
        readTrash: false,
      }),
    ).toEqual([
      { list: 'workspaces' },
      { list: 'voices' },
      { list: 'playlist', id: 'p1', name: 'One' },
    ]);
    expect(readsTrash(LIBRARY)).toBe(true);
    expect(readsTrash({ kind: 'workspaces', ids: ['w1'], readTrash: false })).toBe(false);
    expect(readsTrash({ kind: 'workspaces', ids: ['w1'], readTrash: true })).toBe(true);
    expect(
      readsTrash({ kind: 'playlists', playlists: [{ id: 'p1', name: 'One' }], readTrash: false }),
    ).toBe(false);
    expect(
      readsTrash({ kind: 'playlists', playlists: [{ id: 'p1', name: 'One' }], readTrash: true }),
    ).toBe(true);
  });
});

describe('reading the workspace list', () => {
  it('follows the numbered pages until they hold num_total_results, uploading nothing yet', async () => {
    const first = sunoObject('project-me.page-1.response');
    const last = sunoObject('project-me.last-page.response');
    // The fixtures hold three workspaces a page: say there are 6, so 2 pages.
    first.num_total_results = 6;
    last.num_total_results = 6;
    last.current_page = 2;
    const page = suno(
      [observed('workspaces', first, { page: 1 })],
      [[observed('workspaces', last, { page: 2 })]],
    );

    const next = await readLeg(LIBRARY, legsFor(LIBRARY), START, page.context);

    expect(page.moreSteps).toEqual(['Read the workspace list']);
    expect(next.leg).toBe(1);
    expect(next.counts.workspaces).toBe(6);
    expect(next.workspaces).toEqual([
      ...(first.projects as unknown[]),
      ...(last.projects as unknown[]),
    ]);
    expect(page.headers).toEqual([]);
    expect(page.parts).toEqual([]);
  });

  it('ends on one page that holds the whole list, asking for no more (TS-007-14)', async () => {
    const first = sunoObject('project-me.page-1.response');
    first.num_total_results = (first.projects as unknown[]).length;
    const page = suno([observed('workspaces', first, { page: 1 })]);

    const next = await readLeg(LIBRARY, legsFor(LIBRARY), START, page.context);

    expect(page.moreSteps).toEqual([]);
    expect(next.leg).toBe(1);
    expect(next.counts.workspaces).toBe((first.projects as unknown[]).length);
  });

  it('presses the Workspaces tab first: a page load that brings no list request still gets the list (#421)', async () => {
    const first = sunoObject('project-me.page-1.response');
    first.num_total_results = 3;
    // Loading the page asks Suno for nothing (live, 2026-10-11); the tab press does.
    const page = suno([], [], [observed('workspaces', first, { page: 1 })]);

    const next = await readLeg(LIBRARY, legsFor(LIBRARY), START, page.context);

    expect(page.openSteps).toEqual(['Read the workspace list']);
    expect(page.moreSteps).toEqual([]);
    expect(next.leg).toBe(1);
    expect(next.workspaces).toEqual(first.projects);
  });

  it('asks for Library to be opened again when the tab press brings no list (#421)', async () => {
    const page = suno([]);

    const stop = await stopOf(readLeg(LIBRARY, legsFor(LIBRARY), START, page.context));

    expect(page.openSteps).toEqual(['Read the workspace list']);
    expect(stop.step).toBe('Read the workspace list');
    expect(stop.expected).toBe(
      'a page of the workspace list within 20 seconds of opening the Workspaces tab',
    );
    expect(stop.reopen).toBe(true);
  });

  it("stops with the press's own failure when the tab cannot be pressed, waiting for nothing (#421)", async () => {
    const page = suno([observed('workspaces', sunoObject('project-me.page-1.response'))]);
    page.context.openWorkspaceList = (step) =>
      Promise.reject(new ReadStop(step, "the Workspaces tab in Library's tab row"));

    const stop = await stopOf(readLeg(LIBRARY, legsFor(LIBRARY), START, page.context));

    expect(stop.expected).toBe("the Workspaces tab in Library's tab row");
    expect(stop.reopen).toBe(false);
    expect(page.clock.slept).toEqual([]);
  });

  it('presses nothing for any other leg (#421)', async () => {
    const page = suno([libraryPage([], false, null)]);

    await readLeg(LIBRARY, legsFor(LIBRARY), { ...START, leg: 2 }, page.context);

    expect(page.openSteps).toEqual([]);
  });

  it('goes on with the pages Suno gave when no next page comes after a scroll, as an incomplete list (TS-007-15, #434)', async () => {
    // The owner's list: 101 workspaces, 20 a page; Suno loads pages 1 and 2, and a scripted scroll
    // never brings page 3 (only a trusted scroll does).
    const pageOf = (number: number) =>
      observed(
        'workspaces',
        {
          num_total_results: 101,
          current_page: number,
          projects: Array.from({ length: 20 }, (_, index) => ({
            id: `w-${String(number)}-${String(index)}`,
            name: `Workspace ${String(index)}`,
          })),
        },
        { page: number },
      );
    const page = suno([pageOf(1), pageOf(2)]);

    const next = await readLeg(LIBRARY, legsFor(LIBRARY), START, page.context);

    // One scroll asked for page 3, and one wait of 20 seconds for it: then the leg goes on.
    expect(page.moreSteps).toEqual(['Read the workspace list', 'Read the workspace list']);
    expect(page.clock.slept.reduce((total, ms) => total + ms, 0)).toBeLessThanOrEqual(
      PAGE_WAIT_MS + 1_000,
    );
    expect(next.leg).toBe(1);
    expect(next.counts.workspaces).toBe(40);
    expect(next.workspaces).toHaveLength(40);
    expect(next.workspacesComplete).toBe(false);
  });

  it('says the workspace list is not complete in the header the next leg creates (#434)', async () => {
    const short: SyncProgress = {
      ...START,
      leg: 2,
      workspaces: sunoObject('project-me.page-1.response').projects as unknown[],
      workspacesComplete: false,
    };
    const page = suno([libraryPage([], false, null)]);

    const next = await readLeg(LIBRARY, legsFor(LIBRARY), short, page.context);

    expect(page.headers[0]).toMatchObject({ workspacesComplete: false, libraryComplete: true });
    expect(page.headers[0]?.workspaces).toEqual(short.workspaces);
    expect(next.workspacesComplete).toBe(false);
    // Complement: a list read to its end, or a sync begun before #434, is complete.
    const whole = suno([libraryPage([], false, null)]);
    await readLeg(
      LIBRARY,
      legsFor(LIBRARY),
      { ...short, workspacesComplete: undefined },
      whole.context,
    );
    expect(whole.headers[0]).toMatchObject({ workspacesComplete: true });
  });
});

describe('reading the whole library', () => {
  const atLibrary: SyncProgress = {
    ...START,
    leg: 2,
    counts: { ...START.counts, workspaces: 3 },
    workspaces: sunoObject('project-me.page-1.response').projects as unknown[],
  };

  it('follows the pages to the end, then creates the export and sends the clips', async () => {
    const page1 = clipsOf('feed-v3.library-page-1.response');
    const page2 = clipsOf('feed-v3.library-page-2.response');
    const page = suno([libraryPage(page1, true, null)], [[libraryPage(page2, false, 'c1')]]);

    const next = await readLeg(LIBRARY, legsFor(LIBRARY), atLibrary, page.context);

    expect(page.moreSteps).toEqual(['Read the library']);
    expect(page.headers).toEqual([
      exportHeader(
        LIBRARY,
        true,
        atLibrary.workspaces,
        LIBRARY_FILTERS,
        { extension: '0.1.0', adapter: 2 },
        '2026-10-06T12:00:00.000Z',
      ),
    ]);
    expect(page.headers[0]).toMatchObject({
      format: 'n8tracks.suno-export',
      formatVersion: 1,
      adapterVersion: '2',
      scope: { kind: 'library', ids: [] },
      libraryComplete: true,
      trashedComplete: true,
      workspacesComplete: true,
      libraryFilters: LIBRARY_FILTERS,
    });
    expect(page.parts).toEqual([
      { partNumber: 1, clips: [...page1, ...page2], trashedClips: [], playlists: [] },
    ]);
    expect(next).toEqual({
      leg: 3,
      attempt: 0,
      partNumber: 1,
      counts: { clips: 4, trashed: 0, workspaces: 3, playlists: 0 },
      workspaces: [],
      workspacesComplete: true,
    });
  });

  it('uploads parts of at most 200 clips as it reads, dropping a clip seen twice', async () => {
    const [a, b, c] = [clips(150, 'a'), clips(150, 'b'), clips(150, 'c')];
    const page = suno(
      [libraryPage(a, true, null)],
      [[libraryPage(b, true, 'c1')], [libraryPage([...c, ...a.slice(0, 5)], false, 'c2')]],
    );

    const next = await readLeg(LIBRARY, legsFor(LIBRARY), atLibrary, page.context);

    expect(page.parts.map((part) => [part.partNumber, part.clips.length])).toEqual([
      [1, PART_CLIPS],
      [2, PART_CLIPS],
      [3, 50],
    ]);
    expect(next.counts.clips).toBe(450);
    expect(next.partNumber).toBe(3);
  });

  it('numbers its parts after those of earlier legs, so a leg read again replaces its own', async () => {
    const page = suno([libraryPage(clips(3, 'a'), false, null)]);

    const next = await readLeg(
      LIBRARY,
      legsFor(LIBRARY),
      { ...atLibrary, partNumber: 4 },
      page.context,
    );

    expect(page.parts.map((part) => part.partNumber)).toEqual([5]);
    expect(next.partNumber).toBe(5);
  });

  it('takes an empty page as the end only when Suno says there is no more', async () => {
    const page = suno([libraryPage([], true, null)], [[libraryPage(clips(2, 'a'), false, 'c1')]]);

    const next = await readLeg(LIBRARY, legsFor(LIBRARY), atLibrary, page.context);

    expect(page.moreSteps).toHaveLength(1);
    expect(next.counts.clips).toBe(2);
  });

  it('is never complete without its last page: no more pages stops the read, naming the step', async () => {
    const page = suno([libraryPage(clips(2, 'a'), true, null)]);

    const stop = await stopOf(readLeg(LIBRARY, legsFor(LIBRARY), atLibrary, page.context));

    expect(stop).toMatchObject({
      step: 'Read the library',
      expected: 'a page of the library feed within 20 seconds (asked 3 times)',
      reopen: false,
    });
    // The first scroll, then the two retries of the same action.
    expect(page.moreSteps).toEqual(['Read the library', 'Read the library', 'Read the library']);
  });

  it('asks for the page to be opened again when no first page came at all', async () => {
    const page = suno([]);

    const stop = await stopOf(readLeg(LIBRARY, legsFor(LIBRARY), atLibrary, page.context));

    expect(stop).toMatchObject({ step: 'Read the library', reopen: true });
    expect(page.moreSteps).toEqual([]);
    expect(page.headers).toEqual([]);
  });

  it('stops at a malformed page, naming the step and what it expected', async () => {
    const broken = libraryPage([{ title: 'no id' } as unknown as Clip], false, null);
    const noMore = observed('library-feed', { clips: [] }, { filters: LIBRARY_FILTERS });

    expect(
      await stopOf(readLeg(LIBRARY, legsFor(LIBRARY), atLibrary, suno([broken]).context)),
    ).toMatchObject({
      step: 'Read the library',
      expected: 'a page of the library feed with a list of records, each with an ID',
    });
    expect(
      await stopOf(readLeg(LIBRARY, legsFor(LIBRARY), atLibrary, suno([noMore]).context)),
    ).toMatchObject({ expected: 'a page of the library feed that says whether there is more' });
  });

  it('skips the page Suno fetched ahead when › asks for it again (TS-007-5)', async () => {
    // Opening Library › Songs: page 1, then page 2 fetched ahead. The first › asks for page 2
    // again; the next › asks for page 3.
    const page = suno(
      [libraryPage(clips(2, 'a'), true, null), libraryPage(clips(2, 'b'), true, 'c1')],
      [[libraryPage(clips(2, 'b'), true, 'c1')], [libraryPage(clips(2, 'c'), false, 'c2')]],
    );

    const next = await readLeg(LIBRARY, legsFor(LIBRARY), atLibrary, page.context);

    expect(next.counts.clips).toBe(6);
    expect(page.parts.flatMap((part) => part.clips.map((clip) => (clip as Clip).id))).toEqual([
      'a-0',
      'a-1',
      'b-0',
      'b-1',
      'c-0',
      'c-1',
    ]);
  });

  it('stops when Suno keeps sending a page it sent before', async () => {
    const again = () => [libraryPage(clips(2, 'b'), true, null)];
    const page = suno(
      [libraryPage(clips(2, 'a'), true, null)],
      [again(), again(), again(), again()],
    );

    expect(await stopOf(readLeg(LIBRARY, legsFor(LIBRARY), atLibrary, page.context))).toMatchObject(
      {
        step: 'Read the library',
        expected: 'each page of the list once (Suno sent a page of the library feed again)',
      },
    );
  });

  it("ignores another list's feed: a workspace's feed is not the library", async () => {
    const workspaceFeed = observed(
      'library-feed',
      { clips: clips(2, 'x'), has_more: false },
      { filters: WORKSPACE_FILTERS },
    );
    const page = suno([workspaceFeed, libraryPage(clips(1, 'a'), false, null)]);

    const next = await readLeg(LIBRARY, legsFor(LIBRARY), atLibrary, page.context);

    expect(next.counts.clips).toBe(1);
    expect(page.parts[0]?.clips.map((clip) => (clip as Clip).id)).toEqual(['a-0']);
  });

  it('stops reading once cancelled: nothing more is asked of the page', async () => {
    const page = suno([libraryPage(clips(2, 'a'), true, null)]);
    page.context.more = (step) => {
      page.moreSteps.push(step);
      page.controller.abort();
      return Promise.resolve();
    };

    await expect(readLeg(LIBRARY, legsFor(LIBRARY), atLibrary, page.context)).rejects.toThrow(
      ReadCancelled,
    );
    expect(page.moreSteps).toHaveLength(1);
  });
});

/** Filters of the workspace feed on `/create?wid=` (#422), for workspace `id`. */
function workspaceFilters(id: string): unknown {
  const filters = structuredClone(
    (sunoObject('feed-v3.workspace-create-wid.request') as { filters: Record<string, unknown> })
      .filters,
  );
  filters.workspace = { presence: 'True', workspaceId: id };
  return filters;
}

/** A page of workspace `id`'s own feed (#422): the captured shape with these clips. */
function workspacePage(id: string, pageClips: Clip[], hasMore: boolean, cursor: string | null) {
  const body: Record<string, unknown> = {
    ...sunoObject('feed-v3.workspace-create-wid.page-1.response'),
    clips: pageClips,
  };
  body.has_more = hasMore;
  if (!hasMore) {
    delete body.next_cursor;
  }
  return observed('library-feed', body, { cursor, filters: workspaceFilters(id) });
}

describe("reading a chosen workspace's own feed (#422)", () => {
  const scope: SyncScope = { kind: 'workspaces', ids: ['w1', 'w3'], readTrash: false };
  const legs = legsFor(scope);

  it("reads the captured pages of the workspace feed, asking for each (the pager's ›, TS-007-23), until has_more is false", async () => {
    const workspace = '00000000-0000-4000-8000-000000000427';
    const captured: SyncScope = { kind: 'workspaces', ids: [workspace], readTrash: false };
    const first = sunoObject('feed-v3.workspace-create-wid.page-1.response');
    const second = sunoObject('feed-v3.workspace-create-wid.page-2.response');
    const last: Record<string, unknown> = {
      ...second,
      clips: clips(1, 'last', workspace),
      has_more: false,
    };
    delete last.next_cursor;
    const filters = (sunoObject('feed-v3.workspace-create-wid.request') as { filters: unknown })
      .filters;
    const page = suno(
      [observed('library-feed', first, { filters })],
      [
        [observed('library-feed', second, { cursor: 'c2', filters })],
        [observed('library-feed', last, { cursor: 'c3', filters })],
      ],
    );

    const next = await readLeg(captured, legsFor(captured), { ...START, leg: 2 }, page.context);

    expect(page.moreSteps).toEqual(['Read workspace 1 of 1', 'Read workspace 1 of 1']);
    expect(page.parts.flatMap((part) => part.clips.map((clip) => (clip as Clip).id))).toEqual([
      ...(first.clips as Clip[]).map((clip) => clip.id),
      ...(second.clips as Clip[]).map((clip) => clip.id),
      'last-0',
    ]);
    expect(next.counts.clips).toBe(5);
    expect(page.headers[0]).toMatchObject({
      scope: { kind: 'workspaces', ids: [workspace] },
      libraryComplete: false,
      trashedComplete: false,
      workspacesComplete: true,
      libraryFilters: filters,
    });
  });

  it("takes only the feed filtered to this leg's workspace: never the library's, nor another workspace's", async () => {
    const page = suno([
      libraryPage(clips(2, 'library', 'w1'), false, null),
      workspacePage('w3', clips(2, 'other', 'w3'), false, null),
      workspacePage('w1', clips(2, 'mine', 'w1'), false, null),
    ]);

    const next = await readLeg(scope, legs, { ...START, leg: 2 }, page.context);

    expect(page.parts.flatMap((part) => part.clips.map((clip) => (clip as Clip).id))).toEqual([
      'mine-0',
      'mine-1',
    ]);
    // The next leg is the second workspace's own page.
    expect(legs[next.leg]).toEqual({ list: 'workspace', id: 'w3' });
  });

  it('reads each chosen workspace on its own leg, numbering the parts on from the last', async () => {
    const page = suno([workspacePage('w3', clips(1, 'three', 'w3'), false, null)]);

    const next = await readLeg(
      scope,
      legs,
      {
        ...START,
        leg: 3,
        partNumber: 1,
        counts: { clips: 2, trashed: 0, workspaces: 3, playlists: 0 },
      },
      page.context,
    );

    expect(page.parts).toMatchObject([{ partNumber: 2, trashedClips: [], playlists: [] }]);
    expect(next.counts.clips).toBe(3);
    expect(next.leg).toBe(legs.length);
  });

  it("stops, naming the workspace's step, when no first page of its feed comes", async () => {
    const page = suno([libraryPage(clips(2, 'library', 'w1'), false, null)]);

    const stop = await stopOf(readLeg(scope, legs, { ...START, leg: 2 }, page.context));

    expect(stop.step).toBe('Read workspace 1 of 2');
    expect(stop.expected).toBe(
      "a page of the workspace's songs within 20 seconds of opening the page",
    );
    expect(stop.reopen).toBe(true);
  });

  it('is never complete without its last page: a page that does not come after › stops the read', async () => {
    const page = suno([workspacePage('w1', clips(2, 'mine', 'w1'), true, null)]);

    const stop = await stopOf(readLeg(scope, legs, { ...START, leg: 2 }, page.context));

    expect(stop.expected).toBe("a page of the workspace's songs within 20 seconds (asked 3 times)");
    expect(page.moreSteps).toHaveLength(3);
  });

  it('skips a page of the workspace feed it has read when › asks for it again, as on Library (TS-007-23)', async () => {
    // The workspace's pager pages as Library's does (`feed/v3/offset`, then `feed/v3`): should
    // Suno fetch page 2 ahead there too, the first › asks for it again.
    const page = suno(
      [
        workspacePage('w1', clips(2, 'a', 'w1'), true, null),
        workspacePage('w1', clips(2, 'b', 'w1'), true, 'c1'),
      ],
      [
        [workspacePage('w1', clips(2, 'b', 'w1'), true, 'c1')],
        [workspacePage('w1', clips(2, 'c', 'w1'), false, 'c2')],
      ],
    );

    const next = await readLeg(scope, legs, { ...START, leg: 2 }, page.context);

    expect(next.counts.clips).toBe(6);
    expect(page.moreSteps).toEqual(['Read workspace 1 of 2', 'Read workspace 1 of 2']);
  });

  it('creates the export even for an empty workspace, so it is still reviewed', async () => {
    const page = suno([workspacePage('w1', [], false, null)]);

    await readLeg(scope, legs, { ...START, leg: 2 }, page.context);

    expect(page.headers).toHaveLength(1);
    expect(page.parts).toEqual([]);
  });

  it('promises nothing about the Trash, and sends no trashed clips, when the Trash is not read (#420)', async () => {
    const scope: SyncScope = { kind: 'workspaces', ids: ['w1'], readTrash: false };
    const legs = legsFor(scope);
    const page = suno([workspacePage('w1', clips(2, 'one', 'w1'), false, null)]);

    const next = await readLeg(scope, legs, { ...START, leg: 2 }, page.context);

    expect(page.headers).toHaveLength(1);
    expect(page.headers[0]).toMatchObject({
      scope: { kind: 'workspaces', ids: ['w1'] },
      libraryComplete: false,
      trashedComplete: false,
      workspacesComplete: true,
    });
    expect(page.parts.every((part) => part.trashedClips.length === 0)).toBe(true);
    // The workspace was the last leg: the sync is finished.
    expect(next.leg).toBe(legs.length);
  });
});

describe('reading chosen workspaces in a sync begun before #422', () => {
  it('keeps only the clips of the chosen workspaces, and says the library is not complete', async () => {
    const scope: SyncScope = { kind: 'workspaces', ids: ['w1', 'w3'], readTrash: true };
    const mixed = [...clips(2, 'one', 'w1'), ...clips(2, 'two', 'w2'), ...clips(1, 'three', 'w3')];
    const page = suno([libraryPage(mixed, false, null)]);
    // The legs such a sync was begun with: the whole library, kept by workspace.
    const before: SyncLeg[] = [{ list: 'workspaces' }, { list: 'library' }, { list: 'trash' }];

    const next = await readLeg(scope, before, { ...START, leg: 1 }, page.context);

    expect(page.parts[0]?.clips.map((clip) => (clip as Clip).id)).toEqual([
      'one-0',
      'one-1',
      'three-0',
    ]);
    expect(next.counts.clips).toBe(3);
    expect(page.headers[0]).toMatchObject({
      scope: { kind: 'workspaces', ids: ['w1', 'w3'] },
      libraryComplete: false,
      trashedComplete: true,
      workspacesComplete: true,
    });
  });
});

describe('reading a playlist', () => {
  const scope: SyncScope = {
    kind: 'playlists',
    playlists: [{ id: '00000000-0000-4000-8000-00000000012b', name: 'Morning' }],
    readTrash: true,
  };
  const legs = legsFor(scope);

  function playlistFeed(songCount: number) {
    const body = sunoObject('unified-feed.playlist.response');
    const feed = body.feed as { feed_metadata: { song_count: number } };
    // The fixture is trimmed to two of its eight songs.
    feed.feed_metadata.song_count = songCount;
    return observed('playlist-feed', body, {
      feedId: 'generic_playlist:00000000-0000-4000-8000-00000000012b',
    });
  }

  it("reads the playlist's songs until there are as many as its song count, with its clip IDs", async () => {
    const other = observed('playlist-feed', sunoObject('unified-feed.playlist.response'), {
      feedId: 'generic_playlist:someone-else',
    });
    const page = suno([other, playlistFeed(2)]);

    const next = await readLeg(scope, legs, { ...START, leg: 2 }, page.context);

    const items = (
      sunoObject('unified-feed.playlist.response').feed as {
        items: { content_item: Clip }[];
      }
    ).items.map((item) => item.content_item);
    expect(page.parts).toEqual([
      {
        partNumber: 1,
        clips: items,
        trashedClips: [],
        playlists: [
          {
            id: '00000000-0000-4000-8000-00000000012b',
            name: 'Morning',
            clipIds: items.map((item) => item.id),
          },
        ],
      },
    ]);
    expect(next.counts).toMatchObject({ clips: 2, playlists: 1 });
    expect(page.headers[0]).toMatchObject({
      scope: { kind: 'playlists', ids: ['00000000-0000-4000-8000-00000000012b'] },
      libraryComplete: false,
      trashedComplete: true,
    });
  });

  it('says the Trash is not complete when a playlist sync does not read it (#420)', async () => {
    const unread: SyncScope = { ...scope, readTrash: false };
    const page = suno([playlistFeed(2)]);

    const next = await readLeg(unread, legsFor(unread), { ...START, leg: 2 }, page.context);

    expect(page.headers[0]).toMatchObject({ libraryComplete: false, trashedComplete: false });
    expect(page.parts.every((part) => part.trashedClips.length === 0)).toBe(true);
    expect(next.leg).toBe(legsFor(unread).length);
  });

  it('stops, naming the step, when fewer songs come than the playlist has (paging past 50 is unverified)', async () => {
    const page = suno([playlistFeed(60)]);

    expect(await stopOf(readLeg(scope, legs, { ...START, leg: 2 }, page.context))).toMatchObject({
      step: 'Read playlist 1 of 1',
    });
    expect(page.parts).toEqual([]);
  });
});

describe('reading the Trash', () => {
  it('follows the cursor until no next_cursor comes back, whatever the scope', async () => {
    const first = sunoObject('clips-trashed-v2.response');
    const last: Record<string, unknown> = {
      ...sunoObject('clips-trashed-v2.response'),
      clips: clips(3, 't'),
    };
    delete last.next_cursor;
    const scope: SyncScope = { kind: 'workspaces', ids: ['elsewhere'], readTrash: true };
    const page = suno([observed('trash', first)], [[observed('trash', last, { cursor: 'n1' })]]);

    const next = await readLeg(
      scope,
      legsFor(scope),
      { ...START, leg: 3, partNumber: 2 },
      page.context,
    );

    expect(page.parts).toEqual([
      {
        partNumber: 3,
        clips: [],
        trashedClips: [...(first.clips as Clip[]), ...(last.clips as Clip[])],
        playlists: [],
      },
    ]);
    expect(next).toMatchObject({ leg: 4, partNumber: 3, counts: { trashed: 5 } });
  });
});

describe('reading your voices (#439)', () => {
  const scope: SyncScope = { kind: 'workspaces', ids: ['w1'], readTrash: false };
  const legs = legsFor(scope);
  const atVoices: SyncProgress = { ...START, leg: 1, counts: { ...START.counts, voices: 0 } };

  /** A page of the voice list as the observer forwards it: `count` personas from the capture. */
  function voicePage(page: number, total: number, ids: string[]): ObservedMessage {
    const captured = sunoObject('persona-get-personas.response');
    const [model] = captured.personas as Record<string, unknown>[];
    return observed(
      'personas',
      forwardedBody('personas', {
        ...captured,
        current_page: page,
        total_results: total,
        personas: ids.map((id) => ({ ...model, id, name: `Voice ${id}` })),
      }),
      { page },
    );
  }

  it('reads the list Suno sends when the picker opens, closes it, and sends it whole, uploading nothing', async () => {
    const page = suno([], [], [], { onOpen: [voicePage(1, 2, ['v1', 'v2'])] });

    expect(legs[1]).toEqual({ list: 'voices' });
    expect(stepOf(legs[1] ?? { list: 'voices' }, legs)).toBe(VOICES_STEP);
    const next = await readLeg(scope, legs, atVoices, page.context);

    expect(page.voiceSteps).toEqual([`open ${VOICES_STEP}`, `close ${VOICES_STEP}`]);
    expect(page.sentVoices).toEqual([
      {
        complete: true,
        voices: [
          { id: 'v1', name: 'Voice v1', description: '', is_trashed: false, is_owned: true },
          { id: 'v2', name: 'Voice v2', description: '', is_trashed: false, is_owned: true },
        ],
      },
    ]);
    // Nothing of the persona beyond what n8Tracks keeps reaches the reader.
    expect(JSON.stringify(page.sentVoices)).not.toMatch(/user_handle|user_display_name|clip|image/);
    expect(page.headers).toEqual([]);
    expect(page.parts).toEqual([]);
    expect(page.moreSteps).toEqual([]);
    expect(page.openSteps).toEqual([]);
    expect(next).toEqual({
      ...atVoices,
      leg: 2,
      counts: { ...atVoices.counts, voices: 2 },
      workspacesComplete: true,
    });
    expect(next.voicesNote).toBeUndefined();
  });

  it('scrolls the picker for each next page until it holds total_results', async () => {
    const page = suno([], [], [], {
      onOpen: [voicePage(1, 3, ['v1', 'v2'])],
      onMore: [[voicePage(2, 3, ['v3'])]],
    });

    const next = await readLeg(scope, legs, atVoices, page.context);

    expect(page.voiceSteps).toEqual([
      `open ${VOICES_STEP}`,
      `more ${VOICES_STEP}`,
      `close ${VOICES_STEP}`,
    ]);
    expect(page.sentVoices[0]?.complete).toBe(true);
    expect(page.sentVoices[0]?.voices.map((voice) => voice.id)).toEqual(['v1', 'v2', 'v3']);
    expect(next.counts.voices).toBe(3);
    expect(page.moreSteps).toEqual([]);
  });

  it('sends a short list as incomplete when no next page comes after one scroll, and says so', async () => {
    const page = suno([], [], [], { onOpen: [voicePage(1, 5, ['v1', 'v2'])] });

    const next = await readLeg(scope, legs, atVoices, page.context);

    expect(page.voiceSteps.filter((step) => step.startsWith('more'))).toHaveLength(1);
    expect(page.sentVoices.map((sent) => sent.complete)).toEqual([false]);
    expect(page.sentVoices[0]?.voices.map((voice) => voice.id)).toEqual(['v1', 'v2']);
    expect(next.leg).toBe(2);
    expect(next.voicesNote).toBe(
      'Only some of your voices were read (Suno sent no further page), so none was marked unavailable.',
    );
  });

  it('goes on with the sync, sending nothing, when the picker cannot be opened', async () => {
    const page = suno([], [], [], { openFails: true });

    const next = await readLeg(scope, legs, atVoices, page.context);

    expect(page.sentVoices).toEqual([]);
    expect(page.voiceSteps).toEqual([`open ${VOICES_STEP}`, `close ${VOICES_STEP}`]);
    expect(next.leg).toBe(2);
    expect(next.voicesNote).toBe(
      "Your voices were not read: step 'voice picker' expected the Voice picker.",
    );
  });

  it('goes on with the sync, sending nothing, when no voice list comes within 20 seconds', async () => {
    const page = suno([], [], [], { onOpen: [] });

    const next = await readLeg(scope, legs, atVoices, page.context);

    expect(page.sentVoices).toEqual([]);
    expect(page.clock.slept.reduce((sum, ms) => sum + ms, 0)).toBeGreaterThanOrEqual(PAGE_WAIT_MS);
    expect(next.leg).toBe(2);
    expect(next.attempt).toBe(0);
    expect(next.voicesNote).toBe(
      "Your voices were not read: step 'Read your voices' expected a page of your voice list within 20 seconds of opening the Voice picker.",
    );
  });

  it('goes on with the sync when a page cannot be read, or the picker cannot be closed', async () => {
    const odd = observed('personas', { personas: [] }, { page: 1 });
    const page = suno([], [], [], { onOpen: [odd], closeFails: true });

    const next = await readLeg(scope, legs, atVoices, page.context);

    expect(page.sentVoices).toEqual([]);
    expect(next.leg).toBe(2);
    expect(next.voicesNote).toBe(
      "Your voices were not read: step 'Read your voices' expected a page of your voice list with its page number and total.",
    );
  });

  it('says the voices were not recorded when n8Tracks refuses them, and goes on', async () => {
    const page = suno([], [], [], {
      onOpen: [voicePage(1, 1, ['v1'])],
      refusal: 'n8Tracks refused your voices: 403 (insufficient_scope).',
    });

    const next = await readLeg(scope, legs, atVoices, page.context);

    expect(next.leg).toBe(2);
    expect(next.voicesNote).toBe(
      'Your voices were read but not recorded: n8Tracks refused your voices: 403 (insufficient_scope).',
    );
  });

  it('carries the note on to the end of the sync, and stops only when the user cancels', async () => {
    const later = suno([workspacePageOf('w1', [], false)]);
    const next = await readLeg(
      scope,
      legs,
      { ...atVoices, leg: 2, voicesNote: 'Your voices were not read: why.' },
      later.context,
    );
    expect(next.voicesNote).toBe('Your voices were not read: why.');

    const page = suno([], [], [], { onOpen: [] });
    page.controller.abort();
    await expect(readLeg(scope, legs, atVoices, page.context)).rejects.toBeInstanceOf(
      ReadCancelled,
    );
    expect(page.sentVoices).toEqual([]);
  });
});

/** A last page of one workspace's own feed (#422), with these clips. */
function workspacePageOf(id: string, pageClips: Clip[], hasMore: boolean): ObservedMessage {
  return observed(
    'library-feed',
    { clips: pageClips, has_more: hasMore },
    { filters: { workspace: { presence: 'True', workspaceId: id } } },
  );
}

describe('reading the library for the Download view (#215)', () => {
  it('reads Library › Songs to its end the way the library leg does, sending nothing anywhere', async () => {
    const first = sunoObject('feed-v3.library-page-1.response');
    const second = sunoObject('feed-v3.library-page-2.response');
    second.has_more = false;
    const { context, moreSteps, parts, headers } = suno(
      [observed('library-feed', first, { filters: LIBRARY_FILTERS })],
      [[observed('library-feed', second, { filters: LIBRARY_FILTERS, cursor: 'c2' })]],
    );
    const taken: string[] = [];

    const count = await readLibrary(context, (records) => {
      taken.push(...records.map((record) => String(record.id)));
    });

    expect(count).toBe(4);
    expect(taken).toEqual([
      ...(first.clips as Clip[]).map((clip) => clip.id),
      ...(second.clips as Clip[]).map((clip) => clip.id),
    ]);
    expect(moreSteps).toEqual(['Read the library']);
    // Complement: no export is made or sent.
    expect(headers).toEqual([]);
    expect(parts).toEqual([]);
  });

  it('stops naming the step when a page does not come; what was read stays with the caller', async () => {
    const { context } = suno([
      observed('library-feed', sunoObject('feed-v3.library-page-1.response'), {
        filters: LIBRARY_FILTERS,
      }),
    ]);
    const taken: unknown[] = [];

    const stop = await stopOf(readLibrary(context, (records) => taken.push(...records)));

    expect(stop.step).toBe('Read the library');
    expect(taken).toHaveLength(2);
  });

  it('ignores a workspace feed, which is not the whole library', async () => {
    const { context } = suno([
      observed('library-feed', sunoObject('feed-v3.workspace-last-page.response'), {
        filters: WORKSPACE_FILTERS,
      }),
    ]);

    const stop = await stopOf(readLibrary(context, () => undefined));

    expect(stop.reopen).toBe(true);
  });
});
