import { describe, expect, it } from 'vitest';
import type { ExportPart, SyncProgress, SyncScope } from '../messages.ts';
import { fakeClock } from '../testing/snapshots.ts';
import { sunoObject } from '../testing/sunoResponses.ts';
import {
  exportHeader,
  legsFor,
  PART_CLIPS,
  readLeg,
  readLibrary,
  ReadCancelled,
  ReadStop,
  stepOf,
  type LegContext,
} from './libraryReader.ts';
import { ObservationFeed } from './observations.ts';
import {
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

/**
 * A fake Suno page: `onLoad` are the responses of the page's own first requests; each `more()`
 * (the load-more workflow: a scroll) makes Suno answer with the next entry of `onMore`, if any.
 */
function suno(onLoad: ObservedMessage[], onMore: ObservedMessage[][] = []) {
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
  const counts: unknown[] = [];
  const controller = new AbortController();
  let created = false;
  const context: LegContext = {
    observations: feed,
    more: (step) => {
      moreSteps.push(step);
      for (const message of onMore.shift() ?? []) {
        feed.take(message);
      }
      return Promise.resolve();
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
  return { feed, context, headers, parts, moreSteps, counts, controller, clock };
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
      { list: 'library' },
      { list: 'trash' },
    ]);
    // A workspace read filters the library feed by workspace (it has every clip's `project`).
    expect(legsFor({ kind: 'workspaces', ids: ['w1'] })).toEqual(legsFor(LIBRARY));
    const playlists = legsFor({
      kind: 'playlists',
      playlists: [
        { id: 'p1', name: 'One' },
        { id: 'p2', name: 'Two' },
      ],
    });
    expect(playlists.map((leg) => leg.list)).toEqual([
      'workspaces',
      'playlist',
      'playlist',
      'trash',
    ]);
    expect(playlists.map((leg) => stepOf(leg, playlists))).toEqual([
      'Read the workspace list',
      'Read playlist 1 of 2',
      'Read playlist 2 of 2',
      'Read the Trash',
    ]);
  });
});

describe('reading the workspace list', () => {
  it('follows the numbered pages until they hold num_total_results, uploading nothing yet', async () => {
    const first = sunoObject('project-me.page-1.response');
    const last = sunoObject('project-me.last-page.response');
    // The fixtures are trimmed to three workspaces a page: say there are 23, so 2 pages.
    first.num_total_results = 23;
    last.num_total_results = 23;
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

  it('is not complete while pages are missing: a page that never comes stops the read', async () => {
    const first = sunoObject('project-me.page-1.response');
    const page = suno([observed('workspaces', first, { page: 1 })]);

    const stop = await stopOf(readLeg(LIBRARY, legsFor(LIBRARY), START, page.context));

    expect(stop.step).toBe('Read the workspace list');
    expect(stop.expected).toBe('a page of the workspace list within 20 seconds (asked 3 times)');
    expect(page.moreSteps).toHaveLength(3);
  });
});

describe('reading the whole library', () => {
  const atLibrary: SyncProgress = {
    ...START,
    leg: 1,
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
      leg: 2,
      attempt: 0,
      partNumber: 1,
      counts: { clips: 4, trashed: 0, workspaces: 3, playlists: 0 },
      workspaces: [],
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

  it('stops when Suno sends the same page twice', async () => {
    const page = suno(
      [libraryPage(clips(2, 'a'), true, null)],
      [[libraryPage(clips(2, 'b'), true, null)]],
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

describe('reading chosen workspaces', () => {
  it('keeps only the clips of the chosen workspaces, and says the library is not complete', async () => {
    const scope: SyncScope = { kind: 'workspaces', ids: ['w1', 'w3'] };
    const mixed = [...clips(2, 'one', 'w1'), ...clips(2, 'two', 'w2'), ...clips(1, 'three', 'w3')];
    const page = suno([libraryPage(mixed, false, null)]);

    const next = await readLeg(scope, legsFor(scope), { ...START, leg: 1 }, page.context);

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

    const next = await readLeg(scope, legs, { ...START, leg: 1 }, page.context);

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
    });
  });

  it('stops, naming the step, when fewer songs come than the playlist has (paging past 50 is unverified)', async () => {
    const page = suno([playlistFeed(60)]);

    expect(await stopOf(readLeg(scope, legs, { ...START, leg: 1 }, page.context))).toMatchObject({
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
    const scope: SyncScope = { kind: 'workspaces', ids: ['elsewhere'] };
    const page = suno([observed('trash', first)], [[observed('trash', last, { cursor: 'n1' })]]);

    const next = await readLeg(
      scope,
      legsFor(scope),
      { ...START, leg: 2, partNumber: 2 },
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
    expect(next).toMatchObject({ leg: 3, partNumber: 3, counts: { trashed: 5 } });
  });
});

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
