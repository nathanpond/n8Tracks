// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ConnectedState, Request } from '../messages.ts';
import { snapshotHtml } from '../testing/snapshots.ts';
import { startSunoContent, type SunoContent } from './suno.ts';

const CONNECTED: ConnectedState = {
  status: 'connected',
  address: 'https://n8tracks.example.com',
  credentialName: 'Chrome at home',
  scopes: ['suno.sync', 'suno.generate'],
  applicationVersion: '0.2.0',
  compatibility: {
    kind: 'mismatch',
    extensionVersion: '0.1.0',
    applicationVersion: '0.2.0',
    update: 'extension',
  },
  features: [],
};

let content: SunoContent | null = null;

function start(address: { current: string }, answer: () => Promise<unknown>) {
  document.body.innerHTML = snapshotHtml('library-list');
  const sent: Request[] = [];
  content = startSunoContent({
    document,
    send: (request) => {
      sent.push(request);
      return answer();
    },
    extensionVersion: '0.1.0',
    address: () => address.current,
    watchMs: 50,
  });
  const root = content.panel.host.shadowRoot;
  const text = (selector: string) => root?.querySelector(selector)?.textContent ?? '';
  return { content, sent, text };
}

afterEach(() => {
  content?.stop();
  content = null;
  document.body.innerHTML = '';
  vi.useRealTimers();
});

describe('the Suno content script', () => {
  it('adds the panel closed, and asks nothing until it is opened', () => {
    const { content: started, sent } = start({ current: 'https://suno.com/me' }, () =>
      Promise.resolve(CONNECTED),
    );

    expect(started.panel.isOpen).toBe(false);
    expect(sent).toEqual([]);
  });

  it('opens from the toolbar with the connection, the version warning, and the self-check', async () => {
    const {
      content: started,
      sent,
      text,
    } = start({ current: 'https://suno.com/me' }, () => Promise.resolve(CONNECTED));

    await started.toggle();

    expect(started.panel.isOpen).toBe(true);
    expect(sent).toEqual([{ type: 'state' }]);
    expect(text('.connection')).toBe('Connected to https://n8tracks.example.com');
    expect(text('.warning')).toBe(
      'Extension 0.1.0 is older than n8Tracks 0.2.0: update the extension.',
    );
    expect(text('[data-workflow="recognise-suno"]')).toBe('Recognise the Suno page: Ready');

    await started.toggle();
    expect(started.panel.isOpen).toBe(false);
  });

  it('reads as not connected when the service worker does not answer', async () => {
    const { content: started, text } = start({ current: 'https://suno.com/me' }, () =>
      Promise.reject(new Error('Receiving end does not exist')),
    );

    await started.toggle();

    expect(text('.connection')).toBe('Not connected to n8Tracks');
    expect(text('[data-workflow="recognise-suno"]')).toBe('Recognise the Suno page: Ready');
  });

  it('checks again after a navigation while open, and stops watching once closed', async () => {
    vi.useFakeTimers();
    const address = { current: 'https://suno.com/me' };
    const { content: started, sent, text } = start(address, () => Promise.resolve(CONNECTED));
    await started.toggle();

    // Suno replaces the page as it navigates; the new one lacks the navigation.
    address.current = 'https://suno.com/song/00000000-0000-4000-8000-000000000101';
    document.body.querySelector('[data-testid="navbar-library-tab"]')?.remove();
    await vi.advanceTimersByTimeAsync(60);

    expect(sent).toHaveLength(2);
    expect(text('[data-workflow="recognise-suno"]')).toBe(
      "Recognise the Suno page: Not working: Recognise the Suno page: step 'navigation' expected Suno's navigation, with its Library link",
    );

    await started.toggle();
    address.current = 'https://suno.com/me';
    await vi.advanceTimersByTimeAsync(200);
    expect(sent).toHaveLength(2);
  });
});
