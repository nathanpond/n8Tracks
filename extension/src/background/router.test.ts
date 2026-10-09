import { describe, expect, it } from 'vitest';
import { ADAPTER_WORKFLOWS } from '../adapter/workflows/index.ts';
import { Diagnostics, DIAGNOSTICS_KEY } from '../diagnostics/report.ts';
import { fakeBrowser } from '../testing/fakeBrowser.ts';
import { Connection } from './connection.ts';
import { route } from './router.ts';
import type { DownloadCoordinator } from './download.ts';
import type { SyncCoordinator } from './sync.ts';

const ID = 'abcdefghijklmnopabcdefghijklmnop';
const PAGE = { id: ID, url: `chrome-extension://${ID}/options/options.html` };
const CONTENT_SCRIPT = { id: ID, url: 'https://n8tracks.example.com/songs', tab: { id: 4 } };

function connection() {
  return new Connection({
    browser: fakeBrowser().browser,
    versions: { extension: '0.1.0', adapter: '1' },
    fetch: () => Promise.reject(new Error('no network in this test')),
  });
}

describe('the service worker router', () => {
  it('answers the state to an extension page and to a content script', async () => {
    expect(await route(connection(), { type: 'state' }, PAGE, ID)).toEqual({
      status: 'not-paired',
    });
    expect(await route(connection(), { type: 'state' }, CONTENT_SCRIPT, ID)).toEqual({
      status: 'not-paired',
    });
  });

  it('lets only the extension pages connect and disconnect', async () => {
    for (const message of [
      { type: 'connect', address: 'https://n8tracks.example.com', token: 'n8t_x' },
      { type: 'disconnect' },
    ]) {
      expect(await route(connection(), message, CONTENT_SCRIPT, ID)).toHaveProperty('refused');
    }
    // Complement: the options page may.
    expect(await route(connection(), { type: 'disconnect' }, PAGE, ID)).toEqual({
      status: 'not-paired',
    });
  });

  it('lets the options page connect when it is open in a tab, as the manifest opens it', async () => {
    const inTab = { ...PAGE, tab: { id: 12 } };
    const reply = await route(
      connection(),
      { type: 'connect', address: 'https://n8tracks.example.com', token: 'n8t_x' },
      inTab,
      ID,
    );

    expect(reply).not.toHaveProperty('refused');
    expect(await route(connection(), { type: 'disconnect' }, inTab, ID)).toEqual({
      status: 'not-paired',
    });
  });

  it('still refuses connect from a content script whose tab shows an extension-like path', async () => {
    const lookalike = {
      id: ID,
      url: `https://n8tracks.example.com/chrome-extension://${ID}/options`,
      tab: { id: 4 },
    };

    expect(
      await route(
        connection(),
        { type: 'connect', address: 'https://n8tracks.example.com', token: 'n8t_x' },
        lookalike,
        ID,
      ),
    ).toHaveProperty('refused');
  });

  it('refuses another extension and anything that is not a request', async () => {
    expect(
      await route(connection(), { type: 'state' }, { ...PAGE, id: 'other' }, ID),
    ).toHaveProperty('refused');
    expect(await route(connection(), { type: 'steal' }, PAGE, ID)).toHaveProperty('refused');
    expect(await route(connection(), 'state', PAGE, ID)).toHaveProperty('refused');
  });

  it('answers a page message it does not handle with unknown_type', async () => {
    const message = { source: 'n8tracks', type: 'export', id: '7' };

    expect(await route(connection(), { type: 'relay', message }, CONTENT_SCRIPT, ID)).toEqual({
      type: 'error',
      error: 'unknown_type',
      message: 'The extension does not handle "export".',
    });
  });

  it('passes a sync message from the Suno content script to the sync, with its tab', async () => {
    const handled: [unknown, number][] = [];
    const sync = {
      handle: (message: unknown, tabId: number) => {
        handled.push([message, tabId]);
        return Promise.resolve({ session: null });
      },
    } as unknown as SyncCoordinator;
    const suno = { id: ID, url: 'https://suno.com/me', tab: { id: 9 } };

    expect(await route(connection(), { type: 'sync-resume' }, suno, ID, undefined, sync)).toEqual({
      session: null,
    });
    expect(handled).toEqual([[{ type: 'sync-resume' }, 9]]);

    // Not from the relay on n8Tracks, an extension page, or a sender without a tab.
    for (const sender of [CONTENT_SCRIPT, PAGE, { id: ID, url: 'https://suno.com/me' }]) {
      expect(
        await route(connection(), { type: 'sync-resume' }, sender, ID, undefined, sync),
      ).toHaveProperty('refused');
    }
    expect(
      await route(
        connection(),
        { type: 'sync-begin', scope: { kind: 'all' } },
        suno,
        ID,
        undefined,
        sync,
      ),
    ).toHaveProperty('refused');
    expect(handled).toHaveLength(1);

    // The cover images of a sync (#152) are asked for the same way, and only that way.
    expect(await route(connection(), { type: 'sync-images' }, suno, ID, undefined, sync)).toEqual({
      session: null,
    });
    expect(handled.at(-1)).toEqual([{ type: 'sync-images' }, 9]);
    for (const sender of [CONTENT_SCRIPT, PAGE]) {
      expect(
        await route(connection(), { type: 'sync-images' }, sender, ID, undefined, sync),
      ).toHaveProperty('refused');
    }
    expect(handled).toHaveLength(2);
  });

  it('passes a Download message from the Suno content script to the Download view, with its tab (#215)', async () => {
    const handled: [unknown, number][] = [];
    const download = {
      handle: (message: unknown, tabId: number) => {
        handled.push([message, tabId]);
        return Promise.resolve({ load: null });
      },
    } as unknown as DownloadCoordinator;
    const suno = { id: ID, url: 'https://suno.com/me', tab: { id: 9 } };
    const send = (message: unknown, sender: typeof suno | typeof PAGE | typeof CONTENT_SCRIPT) =>
      route(connection(), message, sender, ID, undefined, undefined, undefined, download);

    expect(await send({ type: 'download-resume' }, suno)).toEqual({ load: null });
    expect(await send({ type: 'download-lookup', sunoIds: ['a'] }, suno)).toEqual({ load: null });
    expect(handled).toEqual([
      [{ type: 'download-resume' }, 9],
      [{ type: 'download-lookup', sunoIds: ['a'] }, 9],
    ]);

    // Not from the relay on n8Tracks or an extension page; a malformed one is not a request.
    for (const sender of [CONTENT_SCRIPT, PAGE]) {
      expect(await send({ type: 'download-resume' }, sender)).toHaveProperty('refused');
    }
    expect(await send({ type: 'download-lookup', sunoIds: [] }, suno)).toHaveProperty('refused');
    expect(await send({ type: 'download-begin', selected: [3] }, suno)).toHaveProperty('refused');
    expect(handled).toHaveLength(2);

    // The download run's messages (#216): checked member by member, from the Suno tab only.
    const file = {
      sunoId: 'a',
      title: 'A',
      displayName: 'maker',
      artist: null,
      format: 'wav',
      unlocked: false,
      streamAddress: null,
    };
    await send({ type: 'download-start', files: [file], unlocks: 1 }, suno);
    await send({ type: 'download-control', action: 'retry' }, suno);
    await send({ type: 'download-run' }, suno);
    expect(handled.slice(2).map(([message]) => (message as { type: string }).type)).toEqual([
      'download-start',
      'download-control',
      'download-run',
    ]);
    for (const bad of [
      { type: 'download-start', files: [], unlocks: 0 },
      { type: 'download-start', files: [{ ...file, format: 'flac' }], unlocks: 0 },
      { type: 'download-start', files: [{ ...file, streamAddress: 3 }], unlocks: 0 },
      { type: 'download-start', files: [file], unlocks: -1 },
      { type: 'download-control', action: 'delete' },
    ]) {
      expect(await send(bad, suno), JSON.stringify(bad)).toHaveProperty('refused');
    }
    expect(await send({ type: 'download-run' }, CONTENT_SCRIPT)).toHaveProperty('refused');
    expect(handled).toHaveLength(5);
  });

  it('takes the step log from a content script, answers the report, and clears it on disconnect', async () => {
    const session = fakeBrowser();
    const diagnostics = new Diagnostics({
      storage: session.browser.storage,
      workflows: ADAPTER_WORKFLOWS,
      versions: { extension: '0.1.0', adapter: '1' },
      connectionState: () => Promise.resolve({ status: 'not-paired' }),
      browser: () => 'unknown',
    });
    const record = {
      type: 'diagnostics-record',
      run: {
        workflowId: 'recognise-suno',
        log: [{ step: 'navigation', phase: 'expect', outcome: 'ok', atMs: 3 }],
        failure: null,
      },
      statuses: [{ id: 'recognise-suno', state: 'ready', step: null, stopped: false }],
    };

    expect(await route(connection(), record, CONTENT_SCRIPT, ID, diagnostics)).toEqual({
      recorded: true,
    });
    const report = await route(
      connection(),
      { type: 'diagnostic-report' },
      CONTENT_SCRIPT,
      ID,
      diagnostics,
    );
    expect(report).toMatchObject({
      workflows: [
        { id: 'recognise-suno', state: 'ready' },
        { id: 'load-more', state: 'not_checked' },
        { id: 'open-workspaces', state: 'not_checked' },
        { id: 'more-workspaces', state: 'not_checked' },
        { id: 'select-workspace', state: 'not_checked' },
        { id: 'create-workspace', state: 'not_checked' },
        { id: 'switch-form', state: 'not_checked' },
        { id: 'fill-songs-simple', state: 'not_checked' },
        { id: 'fill-songs-advanced', state: 'not_checked' },
        { id: 'check-songs-form', state: 'not_checked' },
        { id: 'switch-speech-form', state: 'not_checked' },
        { id: 'fill-speech-simple', state: 'not_checked' },
        { id: 'fill-speech-advanced', state: 'not_checked' },
        { id: 'check-speech-form', state: 'not_checked' },
        { id: 'switch-sounds-form', state: 'not_checked' },
        { id: 'fill-sounds', state: 'not_checked' },
        { id: 'check-sounds-form', state: 'not_checked' },
        { id: 'open-source-menu', state: 'not_checked' },
        { id: 'choose-source-action', state: 'not_checked' },
        { id: 'answer-overwrite', state: 'not_checked' },
        { id: 'verify-source-advanced', state: 'not_checked' },
        { id: 'verify-source-simple', state: 'not_checked' },
        { id: 'set-extend-from', state: 'not_checked' },
        { id: 'choose-voice', state: 'not_checked' },
        { id: 'close-voice-picker', state: 'not_checked' },
        { id: 'refresh-library', state: 'not_checked' },
        { id: 'open-clip-menu', state: 'not_checked' },
        { id: 'choose-download', state: 'not_checked' },
        { id: 'choose-download-format', state: 'not_checked' },
        { id: 'close-download-dialog', state: 'not_checked' },
      ],
      steps: [{ workflow: 'recognise-suno', step: 'navigation', ms: 3 }],
    });
    // A malformed record is not a request at all.
    expect(
      await route(
        connection(),
        { type: 'diagnostics-record', statuses: 'all' },
        CONTENT_SCRIPT,
        ID,
        diagnostics,
      ),
    ).toHaveProperty('refused');

    await route(connection(), { type: 'disconnect' }, PAGE, ID, diagnostics);
    expect(session.stored.has(DIAGNOSTICS_KEY)).toBe(false);
  });
});
