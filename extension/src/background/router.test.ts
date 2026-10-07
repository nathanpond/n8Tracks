import { describe, expect, it } from 'vitest';
import { ADAPTER_WORKFLOWS } from '../adapter/workflows/index.ts';
import { Diagnostics, DIAGNOSTICS_KEY } from '../diagnostics/report.ts';
import { fakeBrowser } from '../testing/fakeBrowser.ts';
import { Connection } from './connection.ts';
import { route } from './router.ts';

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
      workflows: [{ id: 'recognise-suno', state: 'ready' }],
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
