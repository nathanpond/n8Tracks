import { describe, expect, it } from 'vitest';
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
});
