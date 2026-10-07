// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Connection } from '../background/connection.ts';
import { route } from '../background/router.ts';
import { ADAPTER_WORKFLOWS } from '../adapter/workflows/index.ts';
import { Diagnostics, DIAGNOSTICS_KEY, REPORT_STATEMENT } from '../diagnostics/report.ts';
import type { Request, ResponseFor } from '../messages.ts';
import { expectNoAxeViolations, loadPage } from '../testing/a11y.ts';
import { fakeBrowser, jsonResponse } from '../testing/fakeBrowser.ts';
import optionsHtml from './options.html?raw';
import { PERMISSION_DECLINED_MESSAGE, startOptions } from './options.ts';

const ID = 'abcdefghijklmnopabcdefghijklmnop';
const TOKEN = 'n8t_0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefg';
const PATTERN = 'https://n8tracks.example.com/*';

function input(id: string): HTMLInputElement {
  return document.getElementById(id) as HTMLInputElement;
}

function text(id: string): string {
  return document.getElementById(id)?.textContent ?? '';
}

function submit() {
  (document.getElementById('pair') as HTMLFormElement).requestSubmit();
}

/**
 * The options page over the real service-worker connection and router, with a fake browser and a
 * fake n8Tracks: what the user sees is what pairing really does, short of the browser's prompt.
 */
async function open(options: { grant?: boolean; answer?: () => Promise<Response> } = {}) {
  const fake = fakeBrowser();
  const fetchImpl = vi.fn(
    options.answer ??
      (() =>
        Promise.resolve(
          jsonResponse(200, {
            applicationVersion: '0.1.0',
            credentialName: 'Chrome at home',
            scopes: ['catalog.read', 'suno.sync', 'suno.generate'],
            compatible: true,
          }),
        )),
  );
  const connection = new Connection({
    browser: fake.browser,
    versions: { extension: '0.1.0', adapter: '1' },
    fetch: fetchImpl,
  });
  // The step log's session storage, apart from the pairing's local storage.
  const session = fakeBrowser();
  const diagnostics = new Diagnostics({
    storage: session.browser.storage,
    workflows: ADAPTER_WORKFLOWS,
    versions: { extension: '0.1.0', adapter: '1' },
    connectionState: () => connection.state(),
    browser: () => 'Google Chrome 140',
    now: () => new Date('2026-10-06T08:00:00Z'),
  });
  const send = <T extends Request>(request: T) =>
    route(
      connection,
      request,
      { id: ID, url: `chrome-extension://${ID}/options/options.html` },
      ID,
      diagnostics,
    ) as Promise<ResponseFor[T['type']]>;
  const saveFile = vi.fn<(fileName: string, text: string) => void>();
  const requestPermissions = vi.fn((origins: string[]) => {
    if (options.grant !== false) {
      fake.grant(origins);
    }
    return Promise.resolve(options.grant !== false);
  });
  await startOptions(document, {
    manifest: { name: 'n8Tracks', version: '0.1.0' },
    send,
    requestPermissions,
    saveFile,
  });
  return { ...fake, fetchImpl, requestPermissions, connection, diagnostics, session, saveFile };
}

function fill(address: string, token: string) {
  input('address').value = address;
  input('address').dispatchEvent(new Event('input'));
  input('token').value = token;
}

describe('the options page', () => {
  beforeEach(() => {
    loadPage(optionsHtml);
  });

  it('starts disconnected with a labelled form', async () => {
    await open();

    expect(text('connection')).toBe('Not connected to n8Tracks');
    expect(text('version')).toBe('v0.1.0');
    expect(document.getElementById('disconnect')?.hidden).toBe(true);
    await expectNoAxeViolations(document);
  });

  it('connects: asks the browser for exactly suno.com and the one origin, checks the token, then saves it', async () => {
    const context = await open();
    fill('https://n8tracks.example.com/', ` ${TOKEN} `);

    submit();

    expect(context.requestPermissions).toHaveBeenCalledWith(['https://suno.com/*', PATTERN]);
    await vi.waitFor(() => {
      expect(text('connection')).toBe('Connected to https://n8tracks.example.com');
    });
    expect(text('detail')).toBe('Credential: Chrome at home');
    expect(text('result')).toBe('Connected to https://n8tracks.example.com as Chrome at home.');
    expect(input('token').value).toBe('');
    expect(context.stored.get('pairing')).toEqual({
      address: 'https://n8tracks.example.com',
      token: TOKEN,
    });
    await expectNoAxeViolations(document);
  });

  it('stays disconnected, says why, and stores no token when the permission is declined', async () => {
    const context = await open({ grant: false });
    fill('https://n8tracks.example.com', TOKEN);

    submit();

    await vi.waitFor(() => {
      expect(text('result')).toBe(PERMISSION_DECLINED_MESSAGE);
    });
    expect(context.fetchImpl).not.toHaveBeenCalled();
    expect(context.stored.size).toBe(0);
    expect(text('connection')).toBe('Not connected to n8Tracks');
  });

  it.each([
    [
      'a wrong address',
      () => Promise.reject(new TypeError('Failed to fetch')),
      'address-error',
      'Cannot reach n8Tracks at this address',
    ],
    [
      'another server',
      () => Promise.resolve(new Response('<html></html>', { status: 200 })),
      'address-error',
      'not an n8Tracks server',
    ],
    [
      'a rejected token',
      () => Promise.resolve(jsonResponse(401, { code: 'invalid_token' })),
      'token-error',
      'n8Tracks rejected the token',
    ],
    [
      'a token without the Suno scopes',
      () =>
        Promise.resolve(
          jsonResponse(200, {
            applicationVersion: '0.1.0',
            credentialName: 'reader',
            scopes: ['catalog.read'],
            compatible: true,
          }),
        ),
      'token-error',
      'neither suno.sync nor suno.generate',
    ],
  ])('reports %s next to its field and saves nothing', async (_, answer, field, words) => {
    const context = await open({ answer });
    fill('https://n8tracks.example.com', TOKEN);

    submit();

    await vi.waitFor(() => {
      expect(text(field)).toContain(words);
    });
    const target = field === 'address-error' ? 'address' : 'token';
    expect(input(target).getAttribute('aria-invalid')).toBe('true');
    expect(document.activeElement).toBe(input(target));
    expect(context.stored.size).toBe(0);
    await expectNoAxeViolations(document);
  });

  it('checks the fields before asking the browser for anything', async () => {
    const context = await open();
    fill('n8tracks.example.com', '');

    submit();

    expect(text('address-error')).toBe(
      'Enter a full address that starts with https:// or http://.',
    );
    expect(text('token-error')).toContain('Enter the token');
    expect(context.requestPermissions).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(input('address'));
  });

  it('notes that an http:// address sends the token unencrypted', async () => {
    await open();

    fill('http://192.168.1.20:8080', TOKEN);
    expect(document.getElementById('insecure')?.hidden).toBe(false);

    fill('https://n8tracks.example.com', TOKEN);
    expect(document.getElementById('insecure')?.hidden).toBe(true);
  });

  it('asks before replacing a connection, and replacing gives back the old origin', async () => {
    const context = await open();
    fill('https://n8tracks.example.com', TOKEN);
    submit();
    await vi.waitFor(() => {
      expect(text('connection')).toBe('Connected to https://n8tracks.example.com');
    });

    fill('http://192.168.1.20:8080', 'n8t_other');
    submit();

    expect(document.getElementById('confirm')?.hidden).toBe(false);
    expect(text('confirm-text')).toContain(
      'replaces the connection to https://n8tracks.example.com',
    );
    expect(context.requestPermissions).toHaveBeenCalledTimes(1);
    await expectNoAxeViolations(document);

    (document.getElementById('replace') as HTMLButtonElement).click();

    await vi.waitFor(() => {
      expect(text('connection')).toBe('Connected to http://192.168.1.20:8080');
    });
    expect(context.origins.has(PATTERN)).toBe(false);
  });

  it('keeps the connection when the user does not confirm the replacement', async () => {
    const context = await open();
    fill('https://n8tracks.example.com', TOKEN);
    submit();
    await vi.waitFor(() => {
      expect(text('connection')).toBe('Connected to https://n8tracks.example.com');
    });

    fill('http://192.168.1.20:8080', 'n8t_other');
    submit();
    (document.getElementById('keep') as HTMLButtonElement).click();

    expect(document.getElementById('confirm')?.hidden).toBe(true);
    expect(context.requestPermissions).toHaveBeenCalledTimes(1);
    expect(context.stored.get('pairing')).toMatchObject({ token: TOKEN });
  });

  it('Disconnect forgets the token and gives back the n8Tracks permission', async () => {
    const context = await open();
    fill('https://n8tracks.example.com', TOKEN);
    submit();
    await vi.waitFor(() => {
      expect(document.getElementById('disconnect')?.hidden).toBe(false);
    });

    (document.getElementById('disconnect') as HTMLButtonElement).click();

    await vi.waitFor(() => {
      expect(text('connection')).toBe('Not connected to n8Tracks');
    });
    expect(context.stored.size).toBe(0);
    expect(context.browser.permissions.remove).toHaveBeenCalledWith({ origins: [PATTERN] });
  });

  it('shows the version warning on a mismatch and keeps the connection', async () => {
    await open({
      answer: () =>
        Promise.resolve(
          jsonResponse(200, {
            applicationVersion: '0.2.4',
            credentialName: 'Chrome at home',
            scopes: ['suno.sync'],
            compatible: false,
          }),
        ),
    });
    fill('https://n8tracks.example.com', TOKEN);

    submit();

    await vi.waitFor(() => {
      expect(text('warning')).toBe(
        'Extension 0.1.0 is older than n8Tracks 0.2.4: update the extension.',
      );
    });
    expect(text('connection')).toBe('Connected to https://n8tracks.example.com');
  });

  it('offers Download diagnostic report with the statement of what it holds beside it', async () => {
    const context = await open();
    const button = document.getElementById('download-report') as HTMLButtonElement;

    expect(button.textContent.trim()).toBe('Download diagnostic report');
    expect(text('diagnostics-statement')).toBe(REPORT_STATEMENT);
    expect(button.getAttribute('aria-describedby')).toBe('diagnostics-statement');
    await expectNoAxeViolations(document);

    button.click();

    await vi.waitFor(() => {
      expect(context.saveFile).toHaveBeenCalledTimes(1);
    });
    const [fileName, json] = context.saveFile.mock.calls[0] ?? ['', ''];
    expect(fileName).toBe('n8tracks-extension-diagnostics-2026-10-06.json');
    expect(JSON.parse(json)).toMatchObject({
      reportVersion: 1,
      versions: { extension: '0.1.0', adapter: '1', application: null },
      connection: { status: 'not-paired', scheme: null },
      pageStructure: null,
    });
    expect(text('diagnostics-result')).toBe('The diagnostic report is saved to your downloads.');
    await expectNoAxeViolations(document);
  });

  it('keeps the token and the address out of the report, and Disconnect clears the step log', async () => {
    const context = await open();
    fill('https://n8tracks.example.com/', TOKEN);
    submit();
    await vi.waitFor(() => {
      expect(text('connection')).toBe('Connected to https://n8tracks.example.com');
    });
    await context.diagnostics.record({
      run: {
        workflowId: 'recognise-suno',
        log: [{ step: 'navigation', phase: 'expect', outcome: 'ok', atMs: 12 }],
        failure: null,
      },
    });

    document.getElementById('download-report')?.click();
    await vi.waitFor(() => {
      expect(context.saveFile).toHaveBeenCalledTimes(1);
    });
    const json = context.saveFile.mock.calls[0]?.[1] ?? '';
    expect(json).not.toContain(TOKEN);
    expect(json).not.toContain('n8tracks.example.com');
    expect(json).not.toContain('Chrome at home');
    expect(JSON.parse(json)).toMatchObject({
      versions: { application: '0.1.0', compatible: true },
      connection: { status: 'connected', scheme: 'https' },
      steps: [{ workflow: 'recognise-suno', step: 'navigation', ms: 12 }],
    });
    expect(context.session.stored.has(DIAGNOSTICS_KEY)).toBe(true);

    document.getElementById('disconnect')?.click();

    await vi.waitFor(() => {
      expect(context.session.stored.has(DIAGNOSTICS_KEY)).toBe(false);
    });
  });
});
