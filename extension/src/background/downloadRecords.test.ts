import { describe, expect, it } from 'vitest';
import type { DownloadFile, DownloadRun } from '../download/downloader.ts';
import { jsonResponse } from '../testing/fakeBrowser.ts';
import { DisconnectedError } from './connection.ts';
import {
  DOWNLOAD_RECORDS_KEY,
  DOWNLOADS_PATH,
  DownloadRecorder,
  REPORT_TRIES,
  REPORTS_KEPT,
  reportOf,
} from './downloadRecords.ts';

/**
 * Download records (#222), in the service worker: each saved file is reported to n8Tracks once,
 * a failed or cancelled one never, and a report that cannot be sent is tried three times and kept
 * for later without touching the download. A fake connection and storage: no browser, no network.
 */

const A = '0c90d621-e30c-4c76-814a-e1fdeb500582';
const B = '6f1e2d3c-4b5a-4987-a6b5-c4d3e2f1a0b9';
const HOME = 'https://n8tracks.example.test';

function file(sunoId: string, change: Partial<DownloadFile> = {}): DownloadFile {
  return {
    key: `${sunoId}:wav`,
    sunoId,
    title: 'Song',
    displayName: 'maker',
    artist: null,
    format: 'wav',
    unlocked: true,
    streamAddress: null,
    fileName: `Song (suno-${sunoId}).wav`,
    state: 'downloading',
    paused: null,
    downloadId: 1,
    received: 0,
    total: 2048,
    reason: null,
    savedName: null,
    renamed: false,
    renameToM4a: false,
    fetchedAgain: false,
    recordId: null,
    savedAt: null,
    ...change,
  };
}

function saved(sunoId: string, recordId: string, change: Partial<DownloadFile> = {}): DownloadFile {
  return file(sunoId, {
    state: 'saved',
    savedName: `Song (suno-${sunoId}) (1).wav`,
    received: 2048,
    recordId,
    savedAt: '2026-10-07T12:00:00.000Z',
    ...change,
  });
}

function run(files: DownloadFile[], spent: string[] = []): DownloadRun {
  return { files, tabId: 7, unlocks: { confirmed: spent, spent } };
}

function memory() {
  const stored: Record<string, unknown> = {};
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

/** A recorder over a fake connection whose answers the test gives, call by call. */
function setUp(
  answer: (call: number) => Response | Promise<Response> = () => jsonResponse(201, {}),
) {
  const calls: { path: string; body: Record<string, unknown> }[] = [];
  const pairing = { address: HOME as string | null };
  const sleeps: number[] = [];
  const connection = {
    pairedAddress: () => Promise.resolve(pairing.address),
    call: async (path: string, init: RequestInit = {}) => {
      calls.push({
        path,
        body: JSON.parse(typeof init.body === 'string' ? init.body : '{}') as Record<
          string,
          unknown
        >,
      });
      return answer(calls.length);
    },
  };
  const storage = memory();
  const recorder = new DownloadRecorder({
    connection,
    storage,
    sleep: (ms) => {
      sleeps.push(ms);
      return Promise.resolve();
    },
  });
  /** Observes `value`, then waits for the sending it started. */
  const observe = async (value: DownloadRun) => {
    await recorder.observe(value);
    await recorder.flush();
  };
  return { recorder, calls, pairing, sleeps, storage, observe };
}

describe('download records', () => {
  it('reports a finished file once, with what the record needs and nothing else', async () => {
    const { calls, observe, recorder } = setUp();
    const value = run([saved(A, 'record-a')], [A]);

    await observe(value);
    await observe(value);
    await observe(run([...value.files, file(B)], [A]));

    expect(calls).toHaveLength(1);
    expect(calls[0]?.path).toBe(DOWNLOADS_PATH);
    expect(calls[0]?.body).toEqual({
      id: 'record-a',
      sunoId: A,
      format: 'wav',
      fileName: `Song (suno-${A}) (1).wav`,
      completedAt: '2026-10-07T12:00:00.000Z',
      sizeBytes: 2048,
      spentUnlock: true,
    });
    expect(await recorder.status()).toEqual({
      connected: true,
      pending: 0,
      refused: 0,
      unrecorded: 0,
    });
  });

  it('says whether the run spent an unlock on the clip, and sends no address or path', async () => {
    const { calls, observe } = setUp();

    await observe(
      run([
        saved(B, 'record-b', {
          streamAddress: 'https://cdn1.suno.ai/b.m4a',
          format: 'm4a-stream',
          received: 0,
          total: null,
        }),
      ]),
    );

    expect(calls[0]?.body).toMatchObject({
      spentUnlock: false,
      format: 'm4a-stream',
      sizeBytes: null,
    });
    expect(JSON.stringify(calls[0]?.body)).not.toMatch(/https?:|\/home\/|Downloads/);
  });

  it('never reports a file that failed, was cancelled, or is still downloading', async () => {
    const { calls, observe } = setUp();

    await observe(
      run([
        file(A, { state: 'failed', reason: 'the browser stopped the download (NETWORK_FAILED)' }),
        file(B, { state: 'cancelled' }),
        file(A, { key: `${A}:mp3`, format: 'mp3' }),
      ]),
    );

    expect(calls).toHaveLength(0);
  });

  it('a file downloaded again is a new record', async () => {
    const { calls, observe } = setUp();

    await observe(run([saved(A, 'first')]));
    await observe(run([saved(A, 'second')]));

    expect(calls.map((call) => call.body.id)).toEqual(['first', 'second']);
  });

  it('tries a failing report three times, keeps it without touching the download, and sends it when the connection works', async () => {
    let failing = true;
    const { calls, observe, recorder, sleeps, storage } = setUp(() => {
      if (failing) {
        throw new TypeError('Failed to fetch');
      }
      return jsonResponse(201, {});
    });
    const value = run([saved(A, 'record-a')]);
    const before = structuredClone(value);

    await observe(value);

    expect(calls).toHaveLength(REPORT_TRIES);
    expect(sleeps).toHaveLength(REPORT_TRIES - 1);
    expect(value).toEqual(before);
    expect((await recorder.status()).pending).toBe(1);
    expect(JSON.stringify(storage.stored[DOWNLOAD_RECORDS_KEY])).toContain('record-a');

    // Nothing new is saved: the next nudge (a page load, a service worker start) sends it.
    failing = false;
    await recorder.flush();
    expect(calls).toHaveLength(REPORT_TRIES + 1);
    expect((await recorder.status()).pending).toBe(0);

    // Seen already: observing the run again sends nothing more.
    await observe(value);
    expect(calls).toHaveLength(REPORT_TRIES + 1);
  });

  it('treats an answer of n8Tracks that is neither success nor refusal as a failure to retry', async () => {
    const { calls, observe, recorder } = setUp((call) =>
      jsonResponse(call < REPORT_TRIES ? 503 : 201, {}),
    );

    await observe(run([saved(A, 'record-a')]));

    expect(calls).toHaveLength(REPORT_TRIES);
    expect((await recorder.status()).pending).toBe(0);
  });

  it('drops a report n8Tracks refuses for good and counts it', async () => {
    const { calls, observe, recorder } = setUp((call) =>
      jsonResponse(call === 1 ? 422 : 403, { code: 'refused' }),
    );

    await observe(run([saved(A, 'record-a'), saved(B, 'record-b')]));

    expect(calls).toHaveLength(2);
    expect(await recorder.status()).toEqual({
      connected: true,
      pending: 0,
      refused: 2,
      unrecorded: 0,
    });
  });

  it('records nothing while not connected, and says so', async () => {
    const { calls, observe, recorder, pairing } = setUp();
    pairing.address = null;

    await observe(run([saved(A, 'record-a')]));

    expect(calls).toHaveLength(0);
    expect(await recorder.status()).toEqual({
      connected: false,
      pending: 0,
      refused: 0,
      unrecorded: 1,
    });

    // Connecting later does not send it: it was not recorded.
    pairing.address = HOME;
    await observe(run([saved(A, 'record-a')]));
    expect(calls).toHaveLength(0);
  });

  it('keeps the reports when n8Tracks forgets the token, for the same n8Tracks', async () => {
    let refused = true;
    const { calls, observe, recorder } = setUp(() => {
      if (refused) {
        throw new DisconnectedError({ status: 'revoked', address: HOME });
      }
      return jsonResponse(201, {});
    });

    await observe(run([saved(A, 'record-a')]));
    expect(calls).toHaveLength(1);
    expect((await recorder.status()).pending).toBe(1);

    refused = false;
    await recorder.flush();
    expect((await recorder.status()).pending).toBe(0);
  });

  it('discards the reports waiting when the extension is paired with another n8Tracks', async () => {
    const { observe, recorder, pairing, calls } = setUp(() => {
      throw new TypeError('Failed to fetch');
    });
    await observe(run([saved(A, 'record-a')]));
    expect((await recorder.status()).pending).toBe(1);

    pairing.address = 'https://other.example.test';
    expect((await recorder.status()).pending).toBe(0);
    await recorder.flush();
    await recorder.observe(run([saved(A, 'record-a')]));
    expect(calls.every((call) => call.body.id === 'record-a')).toBe(true);
    expect(calls).toHaveLength(REPORT_TRIES);
  });

  it(`keeps at most ${String(REPORTS_KEPT)} reports, dropping the oldest`, async () => {
    const { observe, recorder, storage } = setUp(() => {
      throw new TypeError('Failed to fetch');
    });
    const files = Array.from({ length: REPORTS_KEPT + 3 }, (_, index) =>
      saved(A, `record-${String(index)}`, { key: `${A}:wav:${String(index)}` }),
    );

    await observe(run(files));

    expect((await recorder.status()).pending).toBe(REPORTS_KEPT);
    const kept = (
      storage.stored[DOWNLOAD_RECORDS_KEY] as { pending: { id: string }[] }
    ).pending.map((report) => report.id);
    expect(kept[0]).toBe('record-3');
    expect(kept.at(-1)).toBe(`record-${String(REPORTS_KEPT + 2)}`);
  });

  it('makes no report of a file without its saved name or record ID', () => {
    expect(reportOf(saved(A, 'x', { savedName: null }), run([]))).toBeNull();
    expect(reportOf(saved(A, 'x', { recordId: null }), run([]))).toBeNull();
  });

  it('a stalled sending never makes observing wait', async () => {
    const { recorder } = setUp(() => new Promise<Response>(() => undefined));

    await expect(
      Promise.race([
        recorder.observe(run([saved(A, 'record-a')])).then(() => 'noted'),
        new Promise((resolve) => {
          setTimeout(() => {
            resolve('stalled');
          }, 500);
        }),
      ]),
    ).resolves.toBe('noted');
  });
});
