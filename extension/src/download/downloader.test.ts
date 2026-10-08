import { describe, expect, it, vi } from 'vitest';
import type { PrepareJob, PrepareOutcome } from '../adapter/downloadSteps.ts';
import {
  browserDownloads,
  DOWNLOAD_QUEUE_KEY,
  DOWNLOAD_WORDS,
  Downloader,
  type DownloadItem,
  type DownloadRun,
} from './downloader.ts';
import { isDownloadTabMessage } from '../messages.ts';
import { fileNameFor } from './fileName.ts';
import type { DownloadFormat, PlanEntry } from './selection.ts';

/**
 * The download queue (#216) with a fake downloads interface and fake adapter steps: no browser,
 * no page, no network.
 */

const A = '0c90d621-e30c-4c76-814a-e1fdeb500582';
const B = '6f1e2d3c-4b5a-4987-a6b5-c4d3e2f1a0b9';
const C = '3b2a1f0e-9d8c-4b7a-8695-a4b3c2d1e0f9';
const D = '9a8b7c6d-5e4f-4a3b-9c2d-1e0f9a8b7c6d';

const SIGNED = 'https://suno-data-uploads.s3.amazonaws.com/studio/uploads/';
const STREAM = 'https://d2lwuy8qc234o3.cloudfront.net/1/clip/';
const TAB = 7;

function entry(sunoId: string, format: DownloadFormat, change: Partial<PlanEntry> = {}): PlanEntry {
  return {
    sunoId,
    title: `Song ${sunoId.slice(0, 4)}`,
    displayName: 'maker',
    artist: null,
    format,
    unlocked: true,
    streamAddress: `${STREAM}${sunoId}.m4a`,
    ...change,
  };
}

/** The browser's downloads interface, as the queue sees it. */
function fakeDownloads() {
  let next = 1;
  const items = new Map<number, DownloadItem>();
  const started: { id: number; address: string; path: string }[] = [];
  const cancelled: number[] = [];
  return {
    items,
    started,
    cancelled,
    api: {
      start: (address: string, path: string) => {
        const id = next;
        next += 1;
        started.push({ id, address, path });
        items.set(id, {
          state: 'in_progress',
          filename: `/home/me/Downloads/${path}`,
          error: null,
          bytesReceived: 0,
          totalBytes: 1000,
        });
        return Promise.resolve(id);
      },
      cancel: (id: number) => {
        cancelled.push(id);
        const item = items.get(id);
        if (item !== undefined) {
          Object.assign(item, { state: 'interrupted', error: 'USER_CANCELED' });
        }
        return Promise.resolve();
      },
      item: (id: number) => Promise.resolve(structuredClone(items.get(id) ?? null)),
    },
  };
}

/** The Suno tab's preparation, answered by the test. */
function fakePage(
  answer: (job: PrepareJob, call: number) => PrepareOutcome | Promise<PrepareOutcome>,
) {
  const jobs: PrepareJob[] = [];
  return {
    jobs,
    prepare: async (tabId: number, job: PrepareJob) => {
      expect(tabId).toBe(TAB);
      jobs.push(job);
      return answer(job, jobs.length);
    },
  };
}

const prepared = (job: PrepareJob): PrepareOutcome => ({
  ok: true,
  address: `${SIGNED}${job.sunoId}.${job.format}?Expires=1&Signature=x`,
  pressed: true,
});

function memory(initial: Record<string, unknown> = {}) {
  const stored: Record<string, unknown> = structuredClone(initial);
  return {
    stored,
    get: (keys: string[]) =>
      Promise.resolve(Object.fromEntries(keys.map((key) => [key, structuredClone(stored[key])]))),
    set: (items: Record<string, unknown>) => {
      Object.assign(stored, structuredClone(items));
      return Promise.resolve();
    },
  };
}

function setUp(
  options: {
    page?: ReturnType<typeof fakePage>;
    storage?: ReturnType<typeof memory>;
    browserStarted?: boolean;
  } = {},
) {
  const downloads = fakeDownloads();
  const page = options.page ?? fakePage(prepared);
  const storage = options.storage ?? memory();
  const runs: DownloadRun[] = [];
  const downloader = new Downloader({
    downloads: downloads.api,
    prepare: page.prepare,
    storage,
    onChange: (run) => runs.push(run),
    browserStarted: () => Promise.resolve(options.browserStarted ?? false),
    // Progress is read when the browser reports a change; the timer never fires in a test.
    sleep: () => new Promise(() => undefined),
  });
  const run = () => downloader.current();
  const states = async () => (await run()).files.map((file) => `${file.key} ${file.state}`);
  /** The browser finishes download `id`, saving it under `filename` (the path asked for unless set). */
  const finish = (id: number, change: Partial<DownloadItem> = {}) => {
    const item = downloads.items.get(id);
    if (item === undefined) {
      throw new Error(`No download ${String(id)}.`);
    }
    Object.assign(item, { state: 'complete', bytesReceived: 1000, ...change });
    downloader.downloadChanged(id);
  };
  const interrupt = (id: number, error: string) => {
    const item = downloads.items.get(id);
    if (item !== undefined) {
      Object.assign(item, { state: 'interrupted', error });
    }
    downloader.downloadChanged(id);
  };
  return { downloader, downloads, page, storage, runs, run, states, finish, interrupt };
}

describe('the download queue', () => {
  it('downloads every file in plan order, two at a time, into n8Tracks/ under the standard name', async () => {
    const { downloader, downloads, states, finish } = setUp();
    const plan = [A, B, C, D].map((id) => entry(id, 'm4a-stream'));

    expect(await downloader.add(TAB, plan, 0)).toBeNull();

    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(2);
    });
    expect(downloads.started.map((started) => started.path)).toEqual([
      `n8Tracks/${fileNameFor(planEntry(plan, 0))}`,
      `n8Tracks/${fileNameFor(planEntry(plan, 1))}`,
    ]);
    expect(downloads.started[0]?.address).toBe(`${STREAM}${A}.m4a`);
    expect(await states()).toEqual([
      `${A}:m4a-stream downloading`,
      `${B}:m4a-stream downloading`,
      `${C}:m4a-stream queued`,
      `${D}:m4a-stream queued`,
    ]);

    finish(1);
    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(3);
    });
    expect(downloads.started[2]?.path).toContain(`(suno-${C})`);
    finish(2);
    finish(3);
    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(4);
    });
    finish(4);
    await vi.waitFor(async () => {
      expect((await states()).every((state) => state.endsWith('saved'))).toBe(true);
    });
  });

  it('prepares WAV, MP3, and M4A in the Suno tab one at a time, each just before its download', async () => {
    let release: (() => void) | null = null;
    const page = fakePage(
      (job, call) =>
        new Promise((resolve) => {
          if (call === 1) {
            release = () => {
              resolve(prepared(job));
            };
          } else {
            resolve(prepared(job));
          }
        }),
    );
    const { downloader, downloads } = setUp({ page });

    await downloader.add(TAB, [entry(A, 'wav'), entry(B, 'mp3'), entry(C, 'm4a')], 0);

    await vi.waitFor(() => {
      expect(page.jobs).toHaveLength(1);
    });
    // The second file waits for the page: there is one Download dialog.
    expect(downloads.started).toHaveLength(0);
    (release as (() => void) | null)?.();
    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(2);
    });
    expect(page.jobs).toEqual([
      { sunoId: A, format: 'wav' },
      { sunoId: B, format: 'mp3' },
    ]);
    // The third is not prepared while two are downloading: an address is never queued.
    expect(downloads.started[0]?.address).toBe(`${SIGNED}${A}.wav?Expires=1&Signature=x`);
  });

  it('reports a file that fails with its clip, format, and reason, and the rest continue', async () => {
    const page = fakePage((job) =>
      job.sunoId === A
        ? {
            ok: false,
            scope: 'file',
            reason: 'Suno did not prepare the file within 30 seconds',
            pressed: true,
          }
        : prepared(job),
    );
    const { downloader, downloads, run, finish } = setUp({ page });

    await downloader.add(TAB, [entry(A, 'wav'), entry(B, 'wav')], 0);

    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(1);
    });
    finish(1);
    await vi.waitFor(async () => {
      const files = (await run()).files;
      expect(files.map((file) => [file.sunoId, file.format, file.state, file.reason])).toEqual([
        [A, 'wav', 'failed', 'Suno did not prepare the file within 30 seconds'],
        [B, 'wav', 'saved', null],
      ]);
    });
  });

  it('gives a saved file a record ID and time of its own, new for each save, and a failed one none (#222)', async () => {
    const page = fakePage((job) =>
      job.sunoId === A
        ? { ok: false, scope: 'file', reason: 'Suno did not prepare the file', pressed: true }
        : prepared(job),
    );
    const { downloader, downloads, run, finish } = setUp({ page });

    await downloader.add(TAB, [entry(A, 'wav'), entry(B, 'wav')], 0);
    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(1);
    });
    finish(1);
    await vi.waitFor(async () => {
      expect((await run()).files.map((file) => file.state)).toEqual(['failed', 'saved']);
    });
    const [failed, first] = (await run()).files;
    expect(failed?.recordId).toBeNull();
    expect(failed?.savedAt).toBeNull();
    expect(first?.recordId).toMatch(/^[0-9a-f-]{36}$/);
    expect(Number.isNaN(Date.parse(first?.savedAt ?? ''))).toBe(false);

    // The same clip and format downloaded again is a new file with a new record ID.
    await downloader.add(TAB, [entry(B, 'wav')], 0);
    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(2);
    });
    finish(2);
    await vi.waitFor(async () => {
      expect((await run()).files.map((file) => file.state)).toEqual(['saved']);
    });
    const again = (await run()).files[0];
    expect(again?.recordId).toMatch(/^[0-9a-f-]{36}$/);
    expect(again?.recordId).not.toBe(first?.recordId);
  });

  it('stops every queued file of a format whose step no longer matches the page, naming the step; other formats go on', async () => {
    const step =
      "Prepare a file in the Download dialog: step 'format' expected the Download dialog's WAV choice";
    const page = fakePage((job) =>
      job.format === 'wav'
        ? { ok: false, scope: 'format', reason: step, pressed: false }
        : prepared(job),
    );
    const { downloader, downloads, run } = setUp({ page });

    await downloader.add(
      TAB,
      [entry(A, 'wav'), entry(B, 'wav'), entry(A, 'mp3'), entry(C, 'wav')],
      0,
    );

    await vi.waitFor(async () => {
      const files = (await run()).files;
      expect(files.map((file) => `${file.key} ${file.state}`)).toEqual([
        `${A}:wav failed`,
        `${B}:wav failed`,
        `${A}:mp3 downloading`,
        `${C}:wav failed`,
      ]);
      expect(
        files.filter((file) => file.format === 'wav').every((file) => file.reason === step),
      ).toBe(true);
    });
    // Only the first WAV was tried on the page.
    expect(page.jobs.filter((job) => job.format === 'wav')).toHaveLength(1);
    expect(downloads.started).toHaveLength(1);
  });

  it('Cancel stops queued files and cancels the files in flight through the downloads interface', async () => {
    const { downloader, downloads, states } = setUp();
    await downloader.add(
      TAB,
      [A, B, C].map((id) => entry(id, 'm4a-stream')),
      0,
    );
    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(2);
    });

    await downloader.cancel();

    expect(downloads.cancelled).toEqual([1, 2]);
    expect(await states()).toEqual([
      `${A}:m4a-stream cancelled`,
      `${B}:m4a-stream cancelled`,
      `${C}:m4a-stream cancelled`,
    ]);
    // Nothing more starts.
    await new Promise((resolve) => setTimeout(resolve, 10));
    expect(downloads.started).toHaveLength(2);
  });

  it('Retry runs only the files that failed, from the start', async () => {
    let failing = true;
    const page = fakePage((job) =>
      job.sunoId === B && failing
        ? {
            ok: false,
            scope: 'file',
            reason: 'Suno did not prepare the file within 30 seconds',
            pressed: true,
          }
        : prepared(job),
    );
    const { downloader, downloads, states, finish } = setUp({ page });
    await downloader.add(TAB, [entry(A, 'mp3'), entry(B, 'mp3')], 0);
    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(1);
    });
    finish(1);
    await vi.waitFor(async () => {
      expect(await states()).toEqual([`${A}:mp3 saved`, `${B}:mp3 failed`]);
    });

    failing = false;
    await downloader.retry();

    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(2);
    });
    expect(page.jobs.map((job) => job.sunoId)).toEqual([A, B, B]);
    finish(2);
    await vi.waitFor(async () => {
      expect(await states()).toEqual([`${A}:mp3 saved`, `${B}:mp3 saved`]);
    });
  });

  it('adds to a run under way without queueing a clip and format twice', async () => {
    const { downloader, downloads, states } = setUp();
    await downloader.add(TAB, [entry(A, 'm4a-stream'), entry(B, 'm4a-stream')], 0);
    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(2);
    });

    await downloader.add(TAB, [entry(B, 'm4a-stream'), entry(C, 'm4a-stream')], 0);

    expect(await states()).toEqual([
      `${A}:m4a-stream downloading`,
      `${B}:m4a-stream downloading`,
      `${C}:m4a-stream queued`,
    ]);
  });
});

describe('unlocks', () => {
  it('unlocks only the clips the user confirmed, once per clip, and refuses a count that does not match the plan', async () => {
    const page = fakePage(prepared);
    const { downloader, run, finish, downloads } = setUp({ page });
    const plan = [
      entry(A, 'wav', { unlocked: false }),
      entry(A, 'mp3', { unlocked: false }),
      entry(B, 'wav', { unlocked: true }),
      entry(C, 'm4a-stream', { unlocked: false }),
    ];

    // One clip not yet unlocked in a paid format: A. The stream needs none.
    expect(await downloader.add(TAB, plan, 2)).toBe(
      'The run needs 1 Suno download unlocks, but 2 were confirmed. Check the summary and start again.',
    );
    expect((await run()).files).toEqual([]);
    expect(await downloader.add(TAB, plan, 1)).toBeNull();

    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(2);
    });
    finish(1);
    finish(2);
    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(4);
    });
    expect((await run()).unlocks).toEqual({ confirmed: [A], spent: [A] });
  });

  it('never presses a page step for a clip whose unlock was not confirmed', async () => {
    const stored: DownloadRun = {
      files: [],
      tabId: TAB,
      unlocks: { confirmed: [], spent: [] },
    };
    const storage = memory({ [DOWNLOAD_QUEUE_KEY]: stored });
    const page = fakePage(prepared);
    const { downloader, run } = setUp({ page, storage });
    // A file of a locked clip arrives without its unlock confirmed (a stale stored queue).
    const file = {
      ...entry(A, 'wav', { unlocked: false }),
      key: `${A}:wav`,
      fileName: 'x',
      state: 'queued',
      paused: null,
      downloadId: null,
      received: 0,
      total: null,
      reason: null,
      savedName: null,
      renamed: false,
      renameToM4a: false,
      fetchedAgain: false,
    };
    storage.stored[DOWNLOAD_QUEUE_KEY] = { ...stored, files: [file] };

    await downloader.resume(TAB);

    await vi.waitFor(async () => {
      expect((await run()).files[0]?.reason).toBe(DOWNLOAD_WORDS.noUnlock);
    });
    expect(page.jobs).toEqual([]);
  });
});

describe('addresses', () => {
  it('hands the downloads interface only addresses on a listed Suno audio host', async () => {
    const page = fakePage((job) => ({
      ok: true,
      address: `https://evil.example.com/${job.sunoId}.wav`,
      pressed: true,
    }));
    const { downloader, downloads, run } = setUp({ page });

    await downloader.add(
      TAB,
      [
        entry(A, 'wav'),
        entry(B, 'm4a-stream', { streamAddress: 'https://cdn1.suno.ai/b.m4a' }),
        entry(C, 'm4a-stream', {
          streamAddress: `https://user:pw@d2lwuy8qc234o3.cloudfront.net/c.m4a`,
        }),
      ],
      0,
    );

    await vi.waitFor(async () => {
      expect((await run()).files.map((file) => file.reason)).toEqual([
        DOWNLOAD_WORDS.addressNotListed,
        DOWNLOAD_WORDS.streamNotListed,
        DOWNLOAD_WORDS.streamNotListed,
      ]);
    });
    expect(downloads.started).toEqual([]);
  });

  it('prepares an expired address afresh once, by running the page steps again', async () => {
    const { downloader, downloads, page, run, interrupt, finish } = setUp();
    await downloader.add(TAB, [entry(A, 'wav')], 0);
    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(1);
    });

    interrupt(1, 'SERVER_FORBIDDEN');
    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(2);
    });
    expect(page.jobs).toHaveLength(2);

    // Once only: a second expiry fails the file.
    interrupt(2, 'SERVER_FORBIDDEN');
    await vi.waitFor(async () => {
      expect((await run()).files[0]?.reason).toBe(
        'the browser stopped the download (SERVER_FORBIDDEN)',
      );
    });
    expect(page.jobs).toHaveLength(2);
    finish(2);
  });

  it('passes the browser the address and the relative name, and nothing else: no header, no credential', async () => {
    const calls: unknown[] = [];
    const api = {
      download: (options: unknown) => {
        calls.push(options);
        return Promise.resolve(3);
      },
      cancel: () => Promise.resolve(),
      search: () => Promise.resolve([]),
      onChanged: { addListener: () => undefined },
    } as unknown as typeof chrome.downloads;

    const id = await browserDownloads(api).start(
      `${SIGNED}${A}.wav?Signature=x` as never,
      `n8Tracks/Song (suno-${A}).wav`,
    );

    expect(id).toBe(3);
    expect(calls).toEqual([
      {
        url: `${SIGNED}${A}.wav?Signature=x`,
        filename: `n8Tracks/Song (suno-${A}).wav`,
        conflictAction: 'uniquify',
        saveAs: false,
      },
    ]);
  });
});

describe('the name the browser saved a file under', () => {
  it('is read back: the chosen name, the browser numbering it, or a different name, and an M4A saved as .mp4 is flagged', async () => {
    const { downloader, downloads, run, finish } = setUp();
    const plan = [
      entry(A, 'm4a-stream'),
      entry(B, 'm4a-stream'),
      entry(C, 'm4a-stream'),
      entry(D, 'm4a-stream'),
    ];
    await downloader.add(TAB, plan, 0);
    const name = (index: number) => fileNameFor(planEntry(plan, index));

    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(2);
    });
    finish(1);
    finish(2, {
      filename: `C:\\Users\\me\\Downloads\\n8Tracks\\${name(1).replace('.m4a', ' (1).m4a')}`,
    });
    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(4);
    });
    finish(3, { filename: `/home/me/Downloads/${C}.mp4` });
    finish(4, { filename: `/home/me/Downloads/n8Tracks/${D}_lyrics.m4a` });

    await vi.waitFor(async () => {
      const files = (await run()).files;
      expect(files.every((file) => file.state === 'saved')).toBe(true);
      expect(files.map((file) => [file.savedName, file.renamed, file.renameToM4a])).toEqual([
        [name(0), false, false],
        [name(1).replace('.m4a', ' (1).m4a'), false, false],
        [`${C}.mp4`, true, true],
        [`${D}_lyrics.m4a`, true, false],
      ]);
    });
  });
});

describe('tabs and restarts', () => {
  it('pauses the files that need the page when the Suno tab is closed, says so, and goes on with the stream', async () => {
    const { downloader, downloads, run, finish } = setUp();
    await downloader.add(
      TAB,
      [entry(A, 'm4a-stream'), entry(B, 'm4a-stream'), entry(C, 'wav'), entry(D, 'm4a-stream')],
      0,
    );
    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(2);
    });

    await downloader.tabClosed(TAB);
    finish(1);
    finish(2);

    await vi.waitFor(async () => {
      expect((await run()).files.map((file) => [file.state, file.paused])).toEqual([
        ['saved', null],
        ['saved', null],
        ['queued', DOWNLOAD_WORDS.tabClosed],
        ['downloading', null],
      ]);
    });

    await downloader.resume(TAB);
    await vi.waitFor(() => {
      expect(downloads.started).toHaveLength(4);
    });
  });

  it('keeps the queue in local storage, and a restarted service worker follows the downloads in flight', async () => {
    const first = setUp();
    await first.downloader.add(TAB, [entry(A, 'm4a-stream'), entry(B, 'm4a-stream')], 0);
    await vi.waitFor(() => {
      expect(first.downloads.started).toHaveLength(2);
    });
    const stored = storedQueue(first.storage);
    expect(stored.files.map((file) => [file.state, file.downloadId])).toEqual([
      ['downloading', 1],
      ['downloading', 2],
    ]);

    const second = setUp({ storage: memory(first.storage.stored) });
    second.downloads.items.set(1, itemOf(first.downloads, 1));
    second.downloads.items.set(2, itemOf(first.downloads, 2));
    await second.downloader.restore();
    second.finish(1);
    second.finish(2);

    await vi.waitFor(async () => {
      expect(await second.states()).toEqual([`${A}:m4a-stream saved`, `${B}:m4a-stream saved`]);
    });
    expect(second.downloads.started).toEqual([]);
  });

  it('after a browser restart waits for Resume, and files in flight start again from zero', async () => {
    const first = setUp();
    await first.downloader.add(TAB, [entry(A, 'm4a-stream'), entry(B, 'm4a-stream')], 0);
    await vi.waitFor(() => {
      expect(first.downloads.started).toHaveLength(2);
    });

    const second = setUp({ storage: memory(first.storage.stored), browserStarted: true });
    await second.downloader.restore();

    expect(
      (await second.run()).files.map((file) => [file.state, file.paused, file.received]),
    ).toEqual([
      ['queued', DOWNLOAD_WORDS.browserRestarted, 0],
      ['queued', DOWNLOAD_WORDS.browserRestarted, 0],
    ]);
    await new Promise((resolve) => setTimeout(resolve, 10));
    expect(second.downloads.started).toEqual([]);

    await second.downloader.resume(TAB);
    await vi.waitFor(() => {
      expect(second.downloads.started).toHaveLength(2);
    });
  });
});

function storedQueue(storage: ReturnType<typeof memory>): DownloadRun {
  return storage.stored[DOWNLOAD_QUEUE_KEY] as DownloadRun;
}

function planEntry(plan: readonly PlanEntry[], index: number): PlanEntry {
  const found = plan[index];
  if (found === undefined) {
    throw new Error(`No entry ${String(index)}.`);
  }
  return found;
}

function itemOf(downloads: ReturnType<typeof fakeDownloads>, id: number): DownloadItem {
  const item = downloads.items.get(id);
  if (item === undefined) {
    throw new Error(`No download ${String(id)}.`);
  }
  return { ...item };
}

describe("the queue's messages to the Suno tab", () => {
  it('are a file to prepare in a format Suno prepares, or the queue as it stands', () => {
    expect(
      isDownloadTabMessage({ type: 'download-prepare', job: { sunoId: A, format: 'wav' } }),
    ).toBe(true);
    expect(
      isDownloadTabMessage({
        type: 'download-progress',
        run: { files: [], tabId: 1, unlocks: {} },
      }),
    ).toBe(true);
    for (const other of [
      { type: 'download-prepare', job: { sunoId: A, format: 'm4a-stream' } },
      { type: 'download-prepare', job: { format: 'wav' } },
      { type: 'download-progress', run: null },
      { type: 'toggle-panel' },
      null,
    ]) {
      expect(isDownloadTabMessage(other), JSON.stringify(other)).toBe(false);
    }
  });
});
