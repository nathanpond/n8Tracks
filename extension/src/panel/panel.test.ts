// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { WorkflowStatus } from '../adapter/registry.ts';
import { REPORT_STATEMENT, type DiagnosticReport } from '../diagnostics/report.ts';
import type { ConnectedState } from '../messages.ts';
import { expectNoAxeViolations } from '../testing/a11y.ts';
import { Panel, stateText } from './panel.ts';

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

function status(change: Partial<WorkflowStatus>): WorkflowStatus {
  return {
    id: 'recognise-suno',
    title: 'Recognise the Suno page',
    feature: 'page',
    state: 'ready',
    startsOn: 'any suno.com page',
    step: null,
    message: null,
    stopped: false,
    ...change,
  };
}

const WORKFLOWS: WorkflowStatus[] = [
  status({}),
  status({ id: 'sync-library', title: 'Read the library', feature: 'sync' }),
  status({
    id: 'fill-songs',
    title: 'Fill Songs form',
    feature: 'generate',
    state: 'not-working',
    step: 'styles',
    message: "Fill Songs form: step 'styles' expected a text box labelled Styles",
  }),
  status({
    id: 'observe',
    title: 'Observe Create',
    feature: 'generate',
    state: 'not-checked',
    startsOn: 'the Create page',
  }),
];

function mount() {
  document.body.innerHTML = '<main><h1>Suno</h1><button id="before">Play</button></main>';
  // Stands in for Suno's own page, which has a language and a title of its own.
  document.documentElement.lang = 'en';
  document.title = 'Suno';
  const onCheckAgain = vi.fn();
  const onTryAgain = vi.fn();
  const blobs: Blob[] = [];
  const revoked: string[] = [];
  const panel = new Panel(document, {
    versions: { extension: '0.1.0', adapter: 1 },
    onCheckAgain,
    onTryAgain,
    objectUrls: {
      create: (blob) => {
        blobs.push(blob);
        return `blob:https://suno.com/report-${String(blobs.length)}`;
      },
      revoke: (url) => revoked.push(url),
    },
  });
  const root = panel.host.shadowRoot;
  if (root === null) {
    throw new Error('The panel has no open shadow root.');
  }
  const text = (selector: string) => root.querySelector(selector)?.textContent ?? '';
  return { panel, root, text, onCheckAgain, onTryAgain, blobs, revoked };
}

afterEach(() => {
  document.body.innerHTML = '';
});

describe('the panel on Suno', () => {
  it('is closed until opened, and is anchored to the right edge in its own shadow root', () => {
    const { panel, root } = mount();

    expect(panel.isOpen).toBe(false);
    expect(panel.host.hidden).toBe(true);
    expect(root.querySelector('style')?.textContent).toMatch(/position: fixed;[\s\S]*right: 0;/);
  });

  it('shows the versions, the connection, the features, and each workflow by group with its state', () => {
    const { panel, root, text } = mount();
    panel.render({ connection: CONNECTED, workflows: WORKFLOWS });
    panel.open();

    expect(text('.versions')).toBe('Extension v0.1.0 · Suno adapter 1');
    expect(text('.connection')).toBe('Connected to https://n8tracks.example.com');
    expect(text('.detail')).toBe('Credential: Chrome at home');
    expect(root.querySelector<HTMLElement>('.warning')?.hidden).toBe(true);
    expect([...root.querySelectorAll('.features li')].map((item) => item.textContent)).toEqual([
      'Library sync: Ready',
      'Generate on Suno: This credential lacks suno.generate',
    ]);
    expect([...root.querySelectorAll('h4')].map((heading) => heading.textContent)).toEqual([
      'Suno page',
      'Library sync',
      'Generate on Suno',
    ]);
    expect([...root.querySelectorAll('li[data-workflow]')].map((item) => item.textContent)).toEqual(
      [
        'Recognise the Suno page: Ready',
        'Read the library: Ready',
        "Fill Songs form: Not working: Fill Songs form: step 'styles' expected a text box labelled Styles",
        'Observe Create: Not checked on this page: it starts on the Create page',
      ],
    );
    expect(root.querySelector('[data-workflow="fill-songs"]')?.getAttribute('data-state')).toBe(
      'not-working',
    );
  });

  it('shows the version warning naming both versions, and keeps working', async () => {
    const { panel, root, text } = mount();
    panel.render({
      connection: {
        ...CONNECTED,
        compatibility: {
          kind: 'mismatch',
          extensionVersion: '0.1.0',
          applicationVersion: '0.2.0',
          update: 'extension',
        },
      },
      workflows: WORKFLOWS,
    });
    panel.open();

    expect(root.querySelector<HTMLElement>('.warning')?.hidden).toBe(false);
    expect(text('.warning')).toBe(
      'Extension 0.1.0 is older than n8Tracks 0.2.0: update the extension.',
    );
    expect(text('.connection')).toBe('Connected to https://n8tracks.example.com');
    expect(root.querySelectorAll('li[data-workflow]')).toHaveLength(4);
    await expectNoAxeViolations(document);
  });

  it('says why it is not connected, with no features', async () => {
    const { panel, root, text } = mount();
    panel.render({ connection: { status: 'not-paired' }, workflows: [status({})] });
    panel.open();

    expect(text('.connection')).toBe('Not connected to n8Tracks');
    expect(root.querySelector<HTMLElement>('.features')?.hidden).toBe(true);
    await expectNoAxeViolations(document);
  });

  it('shows a stopped run with the page-may-be-changed note', () => {
    const { panel, root } = mount();
    panel.render({
      connection: CONNECTED,
      workflows: [
        status({
          state: 'not-working',
          stopped: true,
          message: "Fill: step 'styles' expected x. The page may be partly changed.",
        }),
      ],
    });

    expect(root.querySelector('.workflow-state')?.textContent).toBe(
      "Stopped: Fill: step 'styles' expected x. The page may be partly changed.",
    );
  });

  it('offers Try again on a run the forbidden-control matcher refused', async () => {
    const { panel, root, onTryAgain } = mount();
    panel.render({
      connection: CONNECTED,
      workflows: [
        status({
          id: 'fill-songs',
          title: 'Fill Songs form',
          state: 'not-working',
          stopped: true,
          step: 'create',
          message:
            "Fill Songs form: step 'create' refused: forbidden control (the Create button: its name starts with Create, Publish, Delete, Trash, or Remove)",
        }),
        status({}),
      ],
    });
    panel.open();

    expect(root.querySelector('[data-workflow="fill-songs"] .workflow-state')?.textContent).toBe(
      "Stopped: Fill Songs form: step 'create' refused: forbidden control (the Create button: its name starts with Create, Publish, Delete, Trash, or Remove)",
    );
    const again = root.querySelectorAll<HTMLButtonElement>('.try-again');
    // Complement: only the stopped workflow offers it.
    expect(again).toHaveLength(1);
    expect(again[0]?.getAttribute('aria-label')).toBe('Try again: Fill Songs form');
    await expectNoAxeViolations(document);

    again[0]?.click();
    expect(onTryAgain).toHaveBeenCalledWith('fill-songs');
  });

  it('passes the accessibility checks when open, with every group named', async () => {
    const { panel, root } = mount();
    panel.render({ connection: CONNECTED, workflows: WORKFLOWS });
    panel.open();

    for (const list of root.querySelectorAll('.workflows ul')) {
      const heading = root.getElementById(list.getAttribute('aria-labelledby') ?? '');
      expect(heading?.localName).toBe('h4');
    }
    await expectNoAxeViolations(document);
  });

  it('moves focus to its heading when opened, and Escape closes it and gives focus back', () => {
    const { panel, root } = mount();
    const before = document.getElementById('before') as HTMLButtonElement;
    before.focus();
    panel.render({ connection: CONNECTED, workflows: WORKFLOWS });

    panel.open();

    expect(root.activeElement?.id).toBe('n8-title');
    root
      .querySelector('.check')
      ?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(panel.isOpen).toBe(false);
    expect(document.activeElement).toBe(before);
  });

  it('is operated by its own buttons: Close closes, Check again asks for a new check', () => {
    const { panel, root, onCheckAgain } = mount();
    panel.render({ connection: CONNECTED, workflows: WORKFLOWS });
    panel.open();

    const buttons = [...root.querySelectorAll('button')];
    expect(buttons.map((button) => button.type)).toEqual(['button', 'button']);
    expect(
      buttons.map((button) => button.getAttribute('aria-label') ?? button.textContent),
    ).toEqual(['Close the n8Tracks panel', 'Check again']);

    root.querySelector<HTMLButtonElement>('.check')?.click();
    expect(onCheckAgain).toHaveBeenCalledTimes(1);
    root.querySelector<HTMLButtonElement>('button[aria-label]')?.click();
    expect(panel.isOpen).toBe(false);
  });

  it('says so when nothing is registered', () => {
    const { panel, root } = mount();
    panel.render({ connection: CONNECTED, workflows: [] });

    expect(root.querySelector('.workflows')?.textContent).toBe('No workflows are registered.');
  });

  it('words each state', () => {
    expect(stateText(status({}))).toBe('Ready');
    expect(stateText(status({ state: 'not-checked', startsOn: 'the Library' }))).toBe(
      'Not checked on this page: it starts on the Library',
    );
    expect(stateText(status({ state: 'waiting', message: 'Open the Songs form' }))).toBe(
      'Waits for an earlier step: Open the Songs form',
    );
  });

  it('offers Download diagnostic report as a Blob link, with the statement beside it', async () => {
    const { panel, root, text, blobs, revoked } = mount();
    panel.render({ connection: CONNECTED, workflows: WORKFLOWS });
    panel.open();
    const link = root.querySelector<HTMLAnchorElement>('a.download');

    // Until the report is assembled, the link waits and says so.
    expect(link?.hidden).toBe(true);
    expect(text('.preparing')).toBe('Preparing the diagnostic report…');
    expect(text('#n8-diagnostics-statement')).toBe(REPORT_STATEMENT);

    panel.setReport(REPORT);

    expect(link?.hidden).toBe(false);
    expect(link?.textContent).toBe('Download diagnostic report');
    expect(link?.getAttribute('href')).toBe('blob:https://suno.com/report-1');
    expect(link?.getAttribute('download')).toBe('n8tracks-extension-diagnostics-2026-10-06.json');
    expect(link?.getAttribute('aria-describedby')).toBe('n8-diagnostics-statement');
    expect(root.querySelector<HTMLElement>('.preparing')?.hidden).toBe(true);
    expect(blobs[0]?.type).toBe('application/json');
    expect(JSON.parse((await blobs[0]?.text()) ?? '')).toEqual(REPORT);
    await expectNoAxeViolations(document);

    // A fresh report replaces the address; the old one is let go.
    panel.setReport(REPORT);
    expect(link?.getAttribute('href')).toBe('blob:https://suno.com/report-2');
    expect(revoked).toEqual(['blob:https://suno.com/report-1']);
    panel.remove();
    expect(revoked).toEqual(['blob:https://suno.com/report-1', 'blob:https://suno.com/report-2']);
  });
});

const REPORT: DiagnosticReport = {
  reportVersion: 1,
  generatedAt: '2026-10-06T21:15:00.000Z',
  versions: { extension: '0.1.0', adapter: '1', application: null, compatible: null, update: null },
  connection: { status: 'not-paired', scheme: null },
  browser: 'unknown',
  workflows: [],
  steps: [],
  pageStructure: null,
};
