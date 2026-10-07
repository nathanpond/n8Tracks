// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { ObservationFeed } from '../adapter/observations.ts';
import { OBSERVER_SOURCE, type ObservedMessage } from '../adapter/observed.ts';
import { nameOf, Page } from '../adapter/primitives.ts';
import { AdapterSession, WorkflowRegistry } from '../adapter/registry.ts';
import { ADAPTER_WORKFLOWS } from '../adapter/workflows/index.ts';
import type { GenerateJob, GenerateReply, Request } from '../messages.ts';
import type { GenerateViewState } from '../panel/GenerateView.ts';
import { fakeClock, snapshotHtml } from '../testing/snapshots.ts';
import { sunoObject } from '../testing/sunoResponses.ts';
import {
  LIST_NOT_READ,
  NO_CREATE_PAGE,
  NOT_SIGNED_IN,
  SAME_NAME,
  SunoGenerate,
} from './sunoGenerate.ts';

/**
 * The workspaces Suno lists in these tests. The rows of `page.workspace-selector.html` are named
 * "My Workspace" and redacted names, two of them "<redacted 13 chars>".
 */
const PROJECTS = [
  { id: 'default', name: 'My Workspace', clip_count: 81 },
  { id: 'w-13a', name: '<redacted 13 chars>' },
  { id: 'w-13b', name: '<redacted 13 chars>' },
  { id: 'w-same', name: 'Night Drive' },
];

const WORKSPACE_FILTERS = (
  sunoObject('feed-v3.workspace-last-page.request') as {
    filters: { workspace: { workspaceId: string } };
  }
).filters;

function observed(kind: ObservedMessage['kind'], body: unknown, filters: unknown = null) {
  const message: ObservedMessage = {
    source: OBSERVER_SOURCE,
    type: 'observed',
    kind,
    request: { cursor: null, page: null, filters, feedId: null },
    body,
  };
  return message;
}

/** The library pane asking for a workspace's songs, as Suno does once it is selected. */
function feedFor(id: string) {
  return observed(
    'library-feed',
    { clips: [], has_more: false },
    {
      ...WORKSPACE_FILTERS,
      workspace: { presence: 'True', workspaceId: id },
    },
  );
}

function job(change: Partial<GenerateJob> = {}): GenerateJob {
  return {
    requestId: '0199b1a0-7000-7000-9000-0000000000aa',
    songTitle: 'Night Drive',
    workspace: null,
    loads: 1,
    ...change,
  };
}

interface Options {
  job?: GenerateJob | null;
  address?: string;
  /** The workspace-list pages already seen when the tab starts. */
  pages?: unknown[];
  /** The service worker's answer to a step. */
  progress?: (request: Extract<Request, { type: 'generate-progress' }>) => GenerateReply;
  /** Whether Suno selects a workspace it just created by itself. */
  selectsCreated?: boolean;
}

afterEach(() => {
  document.body.innerHTML = '';
});

/**
 * The Suno tab of a generation over the workspace-selector snapshot, with a stand-in for Suno: a
 * pressed row makes the library pane ask for that workspace's songs, "Create new workspace" opens
 * the inline row (the create-workspace-dialog snapshot), and Confirm answers with the new workspace.
 */
function start(options: Options = {}) {
  document.body.innerHTML = snapshotHtml('workspace-selector');
  const clock = fakeClock();
  const visited: string[] = [];
  const page = new Page(document, {
    address: () => options.address ?? 'https://suno.com/create',
    navigate: (address) => visited.push(address),
    clock,
  });
  const feed = new ObservationFeed(
    {
      origin: 'https://suno.com',
      postMessage: () => undefined,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    },
    clock,
  );
  for (const body of options.pages ?? [
    { num_total_results: PROJECTS.length, current_page: 1, projects: PROJECTS },
  ]) {
    feed.take(observed('workspaces', body));
  }
  const pressed: string[] = [];
  document.addEventListener(
    'click',
    (event) => {
      const target = (event.target as Element).closest('[role="button"], button');
      if (target === null) {
        return;
      }
      const name = nameOf(target);
      pressed.push(name);
      if (name === 'Create new workspace') {
        document.body.innerHTML = snapshotHtml('create-workspace-dialog');
        return;
      }
      if (name === 'Confirm') {
        const typed =
          document.querySelector<HTMLInputElement>('input[aria-label="New workspace name"]')
            ?.value ?? '';
        feed.take(
          observed('workspace-created', {
            ...sunoObject('project.create.response'),
            id: 'w-new',
            name: typed,
          }),
        );
        if (options.selectsCreated === true) {
          feed.take(feedFor('w-new'));
        }
        return;
      }
      const project = PROJECTS.find((item) => name.startsWith(`${item.name} `));
      if (project !== undefined) {
        feed.take(feedFor(project.id));
      }
    },
    { capture: true },
  );

  const asked: Request[] = [];
  const shown: GenerateViewState[] = [];
  const send = (request: Request): Promise<unknown> => {
    asked.push(structuredClone(request));
    switch (request.type) {
      case 'generate-resume':
        return Promise.resolve({ job: options.job === undefined ? job() : options.job });
      case 'generate-progress':
        return Promise.resolve(options.progress?.(request) ?? { ok: true });
      case 'generate-workspaces':
        return Promise.resolve({ ok: true, songCounts: { default: 2, 'w-same': 0 } });
      case 'generate-resolve':
        return Promise.resolve({ ok: true });
      default:
        return Promise.resolve(undefined);
    }
  };
  const session = new AdapterSession(new WorkflowRegistry(ADAPTER_WORKFLOWS), page);
  const generate = new SunoGenerate({
    page,
    session,
    observations: feed,
    send,
    show: (state) => shown.push(state),
    clock,
  });
  return {
    generate,
    feed,
    asked,
    shown,
    pressed,
    visited,
    steps: () =>
      asked.flatMap((request) =>
        request.type === 'generate-progress' ? [`${request.state} ${request.step}`] : [],
      ),
    types: () => asked.map((request) => request.type),
    last: () => shown.at(-1),
  };
}

/** Lets the tab run until it waits for the user's choice. */
async function untilChoosing(generate: SunoGenerate): Promise<void> {
  for (let tries = 0; tries < 200 && !generate.choosing; tries += 1) {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
  expect(generate.choosing).toBe(true);
}

describe('Generate on Suno in the Suno tab: the Song’s workspace', () => {
  it('selects the Song’s workspace by the name Suno lists for its ID, after reporting the complete list', async () => {
    const tab = start({
      job: job({ workspace: { sunoId: 'default', name: 'Old name', state: 'available' } }),
    });

    await tab.generate.resume();

    expect(tab.steps()).toEqual([
      'workspace check sign-in',
      'workspace read workspace list',
      'workspace select workspace',
      'workspace workspace selected',
    ]);
    expect(tab.asked.find((request) => request.type === 'generate-workspaces')).toEqual({
      type: 'generate-workspaces',
      workspaces: PROJECTS,
    });
    expect(tab.pressed).toHaveLength(1);
    expect(tab.pressed[0]).toMatch(/^My Workspace /);
    expect(tab.last()).toEqual({ kind: 'selected', name: 'My Workspace' });
    // Complement: nothing was created, and no choice was asked for or recorded.
    expect(tab.types()).not.toContain('generate-resolve');
    expect(tab.shown.some((state) => state.kind === 'choose')).toBe(false);
  });

  it('presses nothing when the page already shows the Song’s workspace', async () => {
    const tab = start({
      job: job({ workspace: { sunoId: 'default', name: 'My Workspace', state: 'available' } }),
    });
    tab.feed.take(feedFor('default'));

    await tab.generate.resume();

    expect(tab.pressed).toEqual([]);
    expect(tab.last()).toEqual({ kind: 'selected', name: 'My Workspace' });
  });

  it('never selects a workspace of the same name and another ID: it stops and says why', async () => {
    const tab = start({
      job: job({ workspace: { sunoId: 'w-13a', name: 'x', state: 'available' } }),
    });

    await tab.generate.resume();

    expect(tab.pressed).toEqual([]);
    expect(tab.last()).toEqual({ kind: 'stopped', message: SAME_NAME });
    expect(tab.asked.at(-1)).toEqual({
      type: 'generate-progress',
      state: 'stopped',
      step: 'select workspace',
      message: SAME_NAME,
    });
  });
});

describe('Generate on Suno in the Suno tab: the choice', () => {
  it('offers the choice when the Song has none, same-name workspaces with no Song first, and creates nothing until the user chooses', async () => {
    const tab = start();

    const running = tab.generate.resume();
    await untilChoosing(tab.generate);

    expect(tab.last()).toMatchObject({ kind: 'choose', title: 'Night Drive', reason: 'none' });
    const offered = tab.last();
    expect(offered?.kind === 'choose' && offered.options.map((option) => option.id)).toEqual([
      'w-same',
      'w-13a',
      'w-13b',
      'default',
    ]);
    expect(tab.steps().at(-1)).toBe('workspace choose workspace');
    // Waiting: nothing pressed, nothing recorded.
    expect(tab.pressed).toEqual([]);
    expect(tab.types()).not.toContain('generate-resolve');

    tab.generate.create();
    await running;

    expect(tab.pressed).toEqual(['Create new workspace', 'Confirm']);
    expect(tab.asked.find((request) => request.type === 'generate-resolve')).toEqual({
      type: 'generate-resolve',
      workspace: { sunoId: 'w-new', name: 'Night Drive', how: 'created' },
    });
    expect(tab.steps()).toEqual([
      'workspace check sign-in',
      'workspace read workspace list',
      'workspace choose workspace',
      'workspace create workspace',
      'workspace select workspace',
      'stopped select workspace',
    ]);
    // The snapshot has no row of the new workspace, and Suno did not select it by itself here.
    expect(tab.last()).toMatchObject({ kind: 'stopped' });
  });

  it('takes the new workspace as selected when Suno selects it by itself', async () => {
    const tab = start({ selectsCreated: true });

    const running = tab.generate.resume();
    await untilChoosing(tab.generate);
    tab.generate.create();
    await running;

    expect(tab.pressed).toEqual(['Create new workspace', 'Confirm']);
    expect(tab.steps().at(-1)).toBe('workspace workspace selected');
    expect(tab.last()).toEqual({ kind: 'selected', name: 'Night Drive' });
  });

  it('records a workspace the user picks, then selects it', async () => {
    const tab = start();

    const running = tab.generate.resume();
    await untilChoosing(tab.generate);
    const offered = tab.last();
    const mine =
      offered?.kind === 'choose' ? offered.options.find((o) => o.id === 'default') : undefined;
    expect(mine).toMatchObject({ songCount: 2, sameName: false });
    if (mine === undefined) {
      throw new Error('My Workspace was not offered.');
    }
    tab.generate.pick(mine);
    await running;

    expect(tab.asked.find((request) => request.type === 'generate-resolve')).toEqual({
      type: 'generate-resolve',
      workspace: { sunoId: 'default', name: 'My Workspace', how: 'picked' },
    });
    expect(tab.pressed).toHaveLength(1);
    expect(tab.pressed[0]).toMatch(/^My Workspace /);
    expect(tab.last()).toEqual({ kind: 'selected', name: 'My Workspace' });
  });

  it('offers a replacement when Suno’s complete list lacks the Song’s workspace, after reporting the list', async () => {
    const tab = start({
      job: job({ workspace: { sunoId: 'w-gone', name: 'Gone', state: 'available' } }),
    });

    const running = tab.generate.resume();
    await untilChoosing(tab.generate);

    expect(tab.last()).toMatchObject({ kind: 'choose', reason: 'unavailable' });
    // The report of the complete list is what makes n8Tracks mark it Unavailable.
    expect(tab.types().indexOf('generate-workspaces')).toBeLessThan(
      tab.asked.findIndex(
        (request) => request.type === 'generate-progress' && request.step === 'choose workspace',
      ),
    );
    expect(tab.pressed).toEqual([]);
    void running;
  });
});

describe('Generate on Suno in the Suno tab: stopping', () => {
  it('stops and says so when the user is not signed in to Suno', async () => {
    const tab = start();
    document.querySelector('[data-testid="profile-menu-button"]')?.remove();

    await tab.generate.resume();

    expect(tab.last()).toEqual({ kind: 'stopped', message: NOT_SIGNED_IN });
    expect(tab.asked.at(-1)).toEqual({
      type: 'generate-progress',
      state: 'stopped',
      step: 'check sign-in',
      message: NOT_SIGNED_IN,
    });
    expect(tab.types()).not.toContain('generate-workspaces');
  });

  it('stops before the next step when the request has ended in n8Tracks, reporting nothing more', async () => {
    const tab = start({
      progress: (request) =>
        request.step === 'read workspace list'
          ? { ok: false, ended: true, message: 'Cancelled in n8Tracks.' }
          : { ok: true },
    });

    await tab.generate.resume();

    expect(tab.last()).toEqual({ kind: 'stopped', message: 'Cancelled in n8Tracks.' });
    expect(tab.types().filter((type) => type !== 'generate-resume')).toEqual([
      'generate-progress',
      'generate-progress',
    ]);
    expect(tab.pressed).toEqual([]);
  });

  it('stops without marking anything when the workspace list does not load completely', async () => {
    const tab = start({ pages: [{ num_total_results: 45, current_page: 1, projects: PROJECTS }] });

    await tab.generate.resume();

    expect(tab.last()).toEqual({ kind: 'stopped', message: LIST_NOT_READ });
    expect(tab.types()).not.toContain('generate-workspaces');
  });

  it('opens Create from another Suno page, and stops if Create does not come', async () => {
    const away = start({ address: 'https://suno.com/me', job: job({ loads: 1 }) });
    await away.generate.resume();
    expect(away.visited).toEqual(['https://suno.com/create']);
    expect(away.steps()).toEqual([]);

    document.body.innerHTML = '';
    const lost = start({ address: 'https://suno.com/', job: job({ loads: 3 }) });
    await lost.generate.resume();
    expect(lost.visited).toEqual([]);
    expect(lost.last()).toEqual({ kind: 'stopped', message: NO_CREATE_PAGE });
  });

  it('does nothing in a tab that is not generating', async () => {
    const tab = start({ job: null });

    await tab.generate.resume();

    expect(tab.types()).toEqual(['generate-resume']);
    expect(tab.shown).toEqual([]);
    expect(tab.pressed).toEqual([]);
  });
});
