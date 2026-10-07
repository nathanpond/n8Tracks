// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ConnectedState, Request } from '../messages.ts';
import { startRelay } from './relay.ts';

const TOKEN = 'n8t_0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefg';

const CONNECTED: ConnectedState = {
  status: 'connected',
  address: 'http://localhost:3000',
  credentialName: 'Chrome at home',
  scopes: ['suno.sync'],
  applicationVersion: '0.1.0',
  compatibility: { kind: 'compatible' },
  features: [],
};

let stop: (() => void) | undefined;

afterEach(() => {
  stop?.();
  stop = undefined;
});

/** Starts the relay on jsdom's window with a fake service worker; collects what it posts. */
function relay(answer: (request: Request) => Promise<unknown>) {
  const posted: { message: unknown; origin: string }[] = [];
  vi.spyOn(window, 'postMessage').mockImplementation((message: unknown, origin?: unknown) => {
    posted.push({ message, origin: String(origin) });
  });
  const send = vi.fn(answer);
  stop = startRelay({
    window,
    send,
    versions: { extension: '0.1.0-rc.1', adapter: '1' },
    pingStateTimeoutMs: 50,
  });
  return { posted, send };
}

/** What the web app posting to its own window looks like to the relay. */
function fromPage(data: unknown, origin = window.location.origin, source: unknown = window) {
  window.dispatchEvent(new MessageEvent('message', { data, origin, source: source as Window }));
}

describe('the relay on the paired n8Tracks origin', () => {
  it('answers ping with the extension versions and the connection state, to the page origin only', async () => {
    const { posted, send } = relay(() => Promise.resolve(CONNECTED));

    fromPage({ source: 'n8tracks', type: 'ping', id: 'p1' });

    await vi.waitFor(() => {
      expect(posted).toHaveLength(1);
    });
    expect(send).toHaveBeenCalledWith({ type: 'state' });
    expect(posted[0]).toEqual({
      origin: window.location.origin,
      message: {
        source: 'n8tracks-extension',
        type: 'pong',
        replyTo: 'ping',
        id: 'p1',
        extensionVersion: '0.1.0-rc.1',
        adapterVersion: '1',
        connection: {
          status: 'connected',
          credentialName: 'Chrome at home',
          scopes: ['suno.sync'],
          applicationVersion: '0.1.0',
          compatible: true,
        },
      },
    });
  });

  it('still answers ping in time when the service worker is slow, as checking', async () => {
    const { posted } = relay(() => new Promise(() => undefined));

    fromPage({ source: 'n8tracks', type: 'ping' });

    await vi.waitFor(() => {
      expect(posted).toHaveLength(1);
    });
    expect(posted[0]?.message).toMatchObject({ type: 'pong', connection: { status: 'checking' } });
  });

  it('passes any other message to the service worker and posts its answer back with the id', async () => {
    const { posted, send } = relay(() =>
      Promise.resolve({ type: 'error', error: 'unknown_type', message: 'no' }),
    );
    const message = { source: 'n8tracks', type: 'export-status', id: 'e9', exportId: 'x' };

    fromPage(message);

    await vi.waitFor(() => {
      expect(posted).toHaveLength(1);
    });
    expect(send).toHaveBeenCalledWith({ type: 'relay', message });
    expect(posted[0]?.message).toEqual({
      source: 'n8tracks-extension',
      type: 'error',
      error: 'unknown_type',
      message: 'no',
      replyTo: 'export-status',
      id: 'e9',
    });
  });

  it('ignores a message from another origin, another window, or not from the web app', async () => {
    const { posted, send } = relay(() => Promise.resolve(CONNECTED));

    fromPage({ source: 'n8tracks', type: 'ping' }, 'https://evil.example');
    fromPage({ source: 'n8tracks', type: 'ping' }, window.location.origin, null);
    fromPage({ source: 'n8tracks-extension', type: 'pong' });
    fromPage({ type: 'ping' });
    fromPage('ping');

    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(send).not.toHaveBeenCalled();
    expect(posted).toHaveLength(0);
  });

  it('never gives the page the token, whatever the service worker answers', async () => {
    const { posted } = relay(() => Promise.resolve({ ...CONNECTED, token: TOKEN }));

    fromPage({ source: 'n8tracks', type: 'ping', id: 'p2' });

    await vi.waitFor(() => {
      expect(posted).toHaveLength(1);
    });
    expect(JSON.stringify(posted)).not.toContain(TOKEN);
  });
});
