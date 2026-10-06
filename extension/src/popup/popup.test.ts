// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ConnectedState, ConnectionState, Request, ResponseFor } from '../messages.ts';
import { expectNoAxeViolations, loadPage } from '../testing/a11y.ts';
import popupHtml from './popup.html?raw';
import { startPopup } from './popup.ts';

const CONNECTED: ConnectedState = {
  status: 'connected',
  address: 'https://n8tracks.example.com',
  credentialName: 'Chrome at home',
  scopes: ['suno.sync'],
  applicationVersion: '0.1.3',
  compatibility: { kind: 'compatible' },
  features: [
    { feature: 'sync', label: 'Library sync', scope: 'suno.sync', available: true, reason: null },
    {
      feature: 'generate',
      label: 'Generate on Suno',
      scope: 'suno.generate',
      available: false,
      reason: 'This credential lacks suno.generate',
    },
  ],
};

function text(id: string): string {
  return document.getElementById(id)?.textContent ?? '';
}

function hidden(id: string): boolean {
  return document.getElementById(id)?.hidden !== false;
}

function button(id: string): HTMLButtonElement {
  return document.getElementById(id) as HTMLButtonElement;
}

/** Starts the popup over a fake service worker that answers `states` in turn (the last repeats). */
async function open(...states: ConnectionState[]) {
  const answers = [...states];
  const sent: Request[] = [];
  const send = <T extends Request>(request: T): Promise<ResponseFor[T['type']]> => {
    sent.push(request);
    const next = answers.length > 1 ? answers.shift() : answers[0];
    return Promise.resolve(next as ResponseFor[T['type']]);
  };
  const requestPermissions = vi.fn(() => Promise.resolve(true));
  const openOptions = vi.fn();
  await startPopup(document, {
    manifest: { name: 'n8Tracks', version: '0.1.0' },
    send,
    requestPermissions,
    openOptions,
  });
  return { sent, requestPermissions, openOptions };
}

describe('the popup', () => {
  beforeEach(() => {
    loadPage(popupHtml);
  });

  it('shows Connected to the address with the credential name and each feature', async () => {
    await open(CONNECTED);

    expect(text('connection')).toBe('Connected to https://n8tracks.example.com');
    expect(text('detail')).toBe('Credential: Chrome at home');
    expect(hidden('warning')).toBe(true);
    const items = [...document.querySelectorAll('#features li')];
    expect(items.map((item) => item.textContent)).toEqual([
      'Library sync: Ready',
      'Generate on Suno: This credential lacks suno.generate',
    ]);
    expect(items[1]?.getAttribute('aria-disabled')).toBe('true');
    expect(hidden('disconnect')).toBe(false);
    await expectNoAxeViolations(document);
  });

  it('warns about a version mismatch, naming both versions and which side to update', async () => {
    await open({
      ...CONNECTED,
      compatibility: {
        kind: 'mismatch',
        extensionVersion: '0.1.0',
        applicationVersion: '0.2.0',
        update: 'extension',
      },
    });

    expect(hidden('warning')).toBe(false);
    expect(text('warning')).toBe(
      'Extension 0.1.0 is older than n8Tracks 0.2.0: update the extension.',
    );
    // The extension keeps working: still connected, features still listed.
    expect(text('connection')).toBe('Connected to https://n8tracks.example.com');
    await expectNoAxeViolations(document);
  });

  it('shows Disconnected after the credential was revoked', async () => {
    await open({ status: 'revoked', address: 'https://n8tracks.example.com' });

    expect(text('connection')).toBe('Disconnected: credential revoked or invalid');
    expect(hidden('features')).toBe(true);
    expect(button('open-options').textContent).toBe('Connect…');
    await expectNoAxeViolations(document);
  });

  it('Disconnect asks the service worker to forget the pairing', async () => {
    const { sent } = await open(CONNECTED, { status: 'not-paired' });

    button('disconnect').click();

    await vi.waitFor(() => {
      expect(text('connection')).toBe('Not connected to n8Tracks');
    });
    expect(sent.at(-1)).toEqual({ type: 'disconnect' });
    expect(hidden('disconnect')).toBe(true);
  });

  it('shows Permission removed with a Reconnect that asks the browser again', async () => {
    const removed: ConnectionState = {
      status: 'permission-removed',
      address: 'https://n8tracks.example.com',
      origins: ['https://suno.com/*', 'https://n8tracks.example.com/*'],
    };
    const { requestPermissions, sent } = await open(removed, CONNECTED);

    expect(text('connection')).toBe('Permission removed');
    expect(hidden('reconnect')).toBe(false);
    await expectNoAxeViolations(document);
    button('reconnect').click();

    expect(requestPermissions).toHaveBeenCalledWith(removed.origins);
    await vi.waitFor(() => {
      expect(text('connection')).toBe('Connected to https://n8tracks.example.com');
    });
    expect(sent.at(-1)).toEqual({ type: 'state', fresh: true });
  });

  it('opens the settings from the button, and every control is a reachable button', async () => {
    const { openOptions } = await open({ status: 'not-paired' });

    const control = button('open-options');
    control.focus();
    expect(document.activeElement).toBe(control);
    control.click();

    expect(openOptions).toHaveBeenCalledTimes(1);
    for (const element of document.querySelectorAll('button')) {
      expect(element.type).toBe('button');
    }
  });
});
