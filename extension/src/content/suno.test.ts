// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ANY_SUNO_PAGE } from '../adapter/addresses.ts';
import { WorkflowRegistry } from '../adapter/registry.ts';
import { OK, present, type Workflow } from '../adapter/workflow.ts';
import type { ConnectedState, Request } from '../messages.ts';
import { recogniseSuno } from '../adapter/workflows/recognise.ts';
import { fakeClock, snapshotHtml } from '../testing/snapshots.ts';
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

function start(
  address: { current: string },
  answer: (request: Request) => Promise<unknown>,
  page = { snapshot: 'library-list', registry: undefined as WorkflowRegistry | undefined },
) {
  document.body.innerHTML = snapshotHtml(page.snapshot);
  const sent: Request[] = [];
  content = startSunoContent({
    document,
    send: (request) => {
      sent.push(request);
      return answer(request);
    },
    extensionVersion: '0.1.0',
    address: () => address.current,
    watchMs: 50,
    objectUrls: { create: () => 'blob:https://suno.com/report', revoke: () => undefined },
    ...(page.registry ? { registry: page.registry } : {}),
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
    expect(sent.map((request) => request.type)).toEqual([
      'state',
      'diagnostics-record',
      'diagnostic-report',
    ]);
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

    const states = () => sent.filter((request) => request.type === 'state');
    expect(states()).toHaveLength(2);
    expect(text('[data-workflow="recognise-suno"]')).toBe(
      "Recognise the Suno page: Not working: Recognise the Suno page: step 'navigation' expected Suno's navigation, with its Library link",
    );

    await started.toggle();
    address.current = 'https://suno.com/me';
    await vi.advanceTimersByTimeAsync(200);
    expect(states()).toHaveLength(2);
  });

  it('shows a refused press as stopped, and Try again checks the page afresh', async () => {
    const create = {
      role: 'button',
      name: 'Create song',
      description: 'the Create button',
    } as const;
    const pressCreate: Workflow = {
      id: 'press-create',
      title: 'Press Create (test only)',
      feature: 'generate',
      startsOn: ANY_SUNO_PAGE,
      needs: [{ step: 'create', check: (page) => present(page, create) }],
      steps: [
        {
          name: 'create',
          expect: ({ page }) => present(page, create),
          act: ({ page }) => {
            const result = page.find(create);
            if (result.kind === 'found') {
              page.click(result.found);
            }
          },
          verify: () => OK,
        },
      ],
      fixtures: ['create-songs-simple'],
    };
    const registry = new WorkflowRegistry([pressCreate]);
    const { content: started, text } = start(
      { current: 'https://suno.com/create' },
      () => Promise.resolve(CONNECTED),
      { snapshot: 'create-songs-simple', registry },
    );
    const clicks: EventTarget[] = [];
    document.addEventListener('click', (event) => clicks.push(event.target ?? document), {
      capture: true,
    });

    const result = await started.session.run(pressCreate, {});
    await started.toggle();

    expect(result.ok).toBe(false);
    expect(clicks).toEqual([]);
    expect(text('[data-workflow="press-create"] .workflow-state')).toBe(
      "Stopped: Press Create (test only): step 'create' refused: forbidden control (the Create button: it is the Create button (Songs and Sounds)). The page may be partly changed.",
    );

    started.panel.host.shadowRoot?.querySelector<HTMLButtonElement>('.try-again')?.click();
    await vi.waitFor(() => {
      expect(text('[data-workflow="press-create"] .workflow-state')).toBe('Ready');
    });
    // Try again pressed nothing on the page.
    expect(clicks.filter((target) => target !== started.panel.host)).toEqual([]);
  });

  it('hands a stopped run to the step log, and offers the report in the panel', async () => {
    const report = {
      reportVersion: 1,
      generatedAt: '2026-10-06T09:00:00.000Z',
      versions: {},
      connection: { status: 'connected', scheme: 'https' },
      browser: 'unknown',
      workflows: [],
      steps: [],
      pageStructure: null,
    };
    const { content: started, sent } = start(
      { current: 'https://suno.com/create' },
      (request) => Promise.resolve(request.type === 'diagnostic-report' ? report : CONNECTED),
      { snapshot: 'create-songs-simple', registry: undefined },
    );

    // The Create page snapshot has no navigation: recognising the page stops.
    const result = await started.session.run(recogniseSuno, {}, { clock: fakeClock() });

    expect(result.ok).toBe(false);
    const recorded = sent.find((request) => request.type === 'diagnostics-record');
    expect(recorded).toMatchObject({
      run: {
        workflowId: 'recognise-suno',
        failure: { step: 'navigation', kind: 'check' },
        structure: { anchoredOn: 'page' },
      },
    });
    expect(JSON.stringify(recorded)).not.toMatch(/Lyrics|Styles|Create/);

    await started.toggle();
    const link = started.panel.host.shadowRoot?.querySelector<HTMLAnchorElement>('a.download');
    await vi.waitFor(() => {
      expect(link?.hidden).toBe(false);
    });
    expect(link?.getAttribute('href')).toBe('blob:https://suno.com/report');
    expect(link?.getAttribute('download')).toBe('n8tracks-extension-diagnostics-2026-10-06.json');
  });
});
