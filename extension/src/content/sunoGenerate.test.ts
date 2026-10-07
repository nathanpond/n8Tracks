// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { ObservationFeed } from '../adapter/observations.ts';
import { OBSERVER_SOURCE, type ObservedMessage } from '../adapter/observed.ts';
import { nameOf, Page } from '../adapter/primitives.ts';
import { AdapterSession, WorkflowRegistry } from '../adapter/registry.ts';
import { ADAPTER_VERSION } from '../adapter/version.ts';
import { ADAPTER_WORKFLOWS } from '../adapter/workflows/index.ts';
import type { GenerateJob, GenerateReply, Request } from '../messages.ts';
import type { GenerateViewState } from '../panel/GenerateView.ts';
import { fakeClock, snapshotHtml } from '../testing/snapshots.ts';
import { standInForSuno } from '../testing/sunoForm.ts';
import type { FormJob, FormSource } from '../adapter/fill.ts';
import { sunoObject } from '../testing/sunoResponses.ts';
import {
  actionUnavailable,
  couldNotOpen,
  FORM_GONE,
  LIST_NOT_READ,
  NO_CREATE_PAGE,
  NO_FORM,
  NOT_EXPECTED,
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
    form: null,
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
  /** The service worker's answer to an observed Create (#149). */
  observedReply?: (request: Extract<Request, { type: 'generate-observed' }>) => unknown;
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
  let address = options.address ?? 'https://suno.com/create';
  const page = new Page(document, {
    address: () => address,
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
      case 'generate-source':
        return Promise.resolve({ ok: true });
      case 'generate-observed':
        return Promise.resolve(
          options.observedReply?.(request) ?? {
            ok: true,
            recorded: { outcome: 'attached', message: '2 Generations recorded on n8-1-v1.' },
          },
        );
      default:
        return Promise.resolve(undefined);
    }
  };
  const ran: string[] = [];
  const session = new AdapterSession(new WorkflowRegistry(ADAPTER_WORKFLOWS), page, (run) =>
    ran.push(run.workflowId),
  );
  const generate = new SunoGenerate({
    page,
    session,
    observations: feed,
    send,
    show: (state) => shown.push(state),
    clock,
    now: () => new Date('2026-10-07T12:00:00Z'),
  });
  return {
    generate,
    feed,
    asked,
    /** The workflows run, in order. */
    ran,
    shown,
    pressed,
    visited,
    steps: () =>
      asked.flatMap((request) =>
        request.type === 'generate-progress' ? [`${request.state} ${request.step}`] : [],
      ),
    types: () => asked.map((request) => request.type),
    last: () => shown.at(-1),
    goTo: (next: string) => {
      address = next;
    },
  };
}

/** Lets the tab run until `done` holds (the watch for Creates runs on after `resume`). */
async function until(done: () => boolean): Promise<void> {
  for (let tries = 0; tries < 500 && !done(); tries += 1) {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
  expect(done()).toBe(true);
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

/** An Advanced Song's form job, its values unlike the workspace-selector page's own. */
function advancedForm(change: Partial<FormJob> = {}): FormJob {
  return {
    kind: 'song',
    mode: 'advanced',
    entries: {
      'songs.advanced.model': 'v6-mini',
      'songs.advanced.lyrics': 'first line\nsecond line',
      'songs.advanced.styles': 'dream pop',
      'songs.advanced.exclude_styles': 'metal',
      'songs.advanced.vocal_gender': null,
      'songs.advanced.duration_mode': 'auto',
      'songs.advanced.duration_seconds': 180,
      'songs.advanced.max_mode': false,
      'songs.advanced.weirdness': 61,
      'songs.advanced.style_influence': 40,
      'songs.advanced.variety': 'high',
      'songs.advanced.personalize': false,
      'songs.advanced.title': 'Night Drive',
    },
    sources: [],
    fileInputs: [],
    unsupported: [],
    ...change,
  };
}

const ON_MY_WORKSPACE = { sunoId: 'default', name: 'My Workspace', state: 'available' } as const;

describe('Generate on Suno in the Suno tab: filling the form (#146)', () => {
  it('fills the Songs form once the workspace is selected, shows and reports the summary, and waits for the user’s Create', async () => {
    const tab = start({ job: job({ workspace: ON_MY_WORKSPACE, form: advancedForm() }) });
    const standIn = standInForSuno(document);
    tab.feed.take(feedFor('default'));

    try {
      await tab.generate.resume();
    } finally {
      standIn.stop();
    }

    expect(tab.steps()).toEqual([
      'workspace check sign-in',
      'workspace read workspace list',
      'workspace select workspace',
      'workspace workspace selected',
      'filling open Songs form',
      'filling fill form',
      'waiting review and create',
    ]);
    const shown = tab.last();
    expect(shown?.kind).toBe('verification');
    const results = shown?.kind === 'verification' ? shown.results : [];
    expect(
      results.filter((result) => result.outcome === 'failed' || result.outcome === 'unavailable'),
    ).toEqual([]);
    const report = tab.asked.at(-1);
    expect(report).toMatchObject({
      type: 'generate-progress',
      state: 'waiting',
      step: 'review and create',
      verification: {
        adapterVersion: ADAPTER_VERSION,
        mode: 'advanced',
        checkedAt: '2026-10-07T12:00:00.000Z',
      },
    });
    // The lyrics, styles, and title went as lengths and hashes only.
    expect(JSON.stringify(report)).not.toContain('first line');
    expect(JSON.stringify(report)).not.toContain('dream pop');
    // Invariant 4: the user clicks Create; the extension never does.
    expect(tab.pressed.filter((name) => /^create/i.test(name))).toEqual([]);
  });

  it('checks again without changing anything, replacing the summary; with the form gone, the request stops', async () => {
    const tab = start({ job: job({ workspace: ON_MY_WORKSPACE, form: advancedForm() }) });
    const standIn = standInForSuno(document);
    tab.feed.take(feedFor('default'));
    try {
      await tab.generate.resume();
      // The user changes Weirdness by hand in Suno.
      document.querySelectorAll('[role="slider"][aria-label="Weirdness"]').forEach((slider) => {
        slider.setAttribute('aria-valuenow', '90');
      });
      const pressedBefore = tab.pressed.length;

      await tab.generate.checkAgain();

      expect(tab.pressed).toHaveLength(pressedBefore);
      expect(tab.steps().at(-1)).toBe('waiting check form');
      const shown = tab.last();
      const weirdness =
        shown?.kind === 'verification'
          ? shown.results.find((result) => result.key === 'songs.advanced.weirdness')
          : undefined;
      expect(weirdness).toMatchObject({ outcome: 'failed', expected: 61, found: 90 });

      document.body.innerHTML = '';
      await tab.generate.checkAgain();
    } finally {
      standIn.stop();
    }

    expect(tab.last()).toEqual({ kind: 'stopped', message: FORM_GONE });
    expect(tab.asked.at(-1)).toMatchObject({ state: 'stopped', message: FORM_GONE });
  });

  it('fills nothing when the form is not as expected, naming the step', async () => {
    const tab = start({ job: job({ workspace: ON_MY_WORKSPACE, form: advancedForm() }) });
    tab.feed.take(feedFor('default'));
    // No stand-in: the More Options section the page shows closed never opens.

    await tab.generate.resume();

    const stopped = tab.last();
    expect(stopped?.kind).toBe('stopped');
    expect(stopped?.kind === 'stopped' ? stopped.message : '').toContain("step 'More Options'");
    expect(tab.pressed.filter((name) => !name.startsWith('More Options'))).toEqual([]);
    expect(
      tab.asked.some(
        (request) => request.type === 'generate-progress' && 'verification' in request,
      ),
    ).toBe(false);
  });
});

/** A Sound's form job, its values unlike the workspace-selector page's own (One-Shot, BPM 120). */
function soundForm(change: Partial<FormJob> = {}): FormJob {
  return {
    kind: 'sound',
    mode: 'single',
    entries: {
      'sounds.single.sound_description': 'rain on a tin roof',
      'sounds.single.sound_type': 'loop',
      'sounds.single.sound_bpm': 120,
      'sounds.single.sound_key': 'A',
      'sounds.single.sound_scale': 'minor',
    },
    sources: [],
    fileInputs: [],
    unsupported: [],
    ...change,
  };
}

/** An Advanced Speech's form job. */
function speechForm(change: Partial<FormJob> = {}): FormJob {
  return {
    kind: 'speech',
    mode: 'advanced',
    entries: {
      'speech.advanced.speech_script': 'Hello there.',
      'speech.advanced.speech_tone': 'warm',
      'speech.advanced.speech_vocal_gender': 'male',
      'speech.advanced.speech_background_music': true,
      'speech.advanced.speech_variety': 'max',
    },
    sources: [],
    fileInputs: [],
    unsupported: [],
    ...change,
  };
}

describe('Generate on Suno in the Suno tab: Speech and Sounds (#147)', () => {
  it('opens the Sounds tab at the step "choose form", fills the Sounds form, and waits for the user’s Create', async () => {
    const tab = start({ job: job({ workspace: ON_MY_WORKSPACE, form: soundForm() }) });
    const standIn = standInForSuno(document);
    tab.feed.take(feedFor('default'));

    try {
      await tab.generate.resume();
    } finally {
      standIn.stop();
    }

    expect(tab.steps()).toEqual([
      'workspace check sign-in',
      'workspace read workspace list',
      'workspace select workspace',
      'workspace workspace selected',
      'filling choose form',
      'filling fill form',
      'waiting review and create',
    ]);
    expect(tab.pressed).toContain('Sounds');
    const shown = tab.last();
    expect(shown).toMatchObject({ kind: 'verification', form: 'sound', mode: 'single' });
    const results = shown?.kind === 'verification' ? shown.results : [];
    expect(results.map((result) => [result.key, result.outcome])).toEqual([
      ['sounds.single.sounds_model', 'not_applicable'],
      ['sounds.single.sound_description', 'set'],
      ['sounds.single.sound_type', 'set'],
      ['sounds.single.sound_bpm', 'set'],
      ['sounds.single.sound_key', 'manual'],
      ['sounds.single.sound_scale', 'manual'],
    ]);
    const report = tab.asked.at(-1);
    expect(report).toMatchObject({
      state: 'waiting',
      step: 'review and create',
      verification: { adapterVersion: ADAPTER_VERSION, mode: 'single' },
    });
    expect(JSON.stringify(report)).not.toContain('tin roof');
    // Invariant 4: the user clicks Create; the extension never does.
    expect(tab.pressed.filter((name) => /^create/i.test(name))).toEqual([]);
  });

  it('fills the Speech form in its mode, and checks it again with the Speech check', async () => {
    const tab = start({ job: job({ workspace: ON_MY_WORKSPACE, form: speechForm() }) });
    const standIn = standInForSuno(document);
    tab.feed.take(feedFor('default'));
    try {
      await tab.generate.resume();
      const pressedBefore = tab.pressed.length;

      await tab.generate.checkAgain();

      expect(tab.pressed).toHaveLength(pressedBefore);
    } finally {
      standIn.stop();
    }

    expect(tab.steps().slice(-4)).toEqual([
      'filling choose form',
      'filling fill form',
      'waiting review and create',
      'waiting check form',
    ]);
    expect(tab.pressed).toEqual(expect.arrayContaining(['Speech', 'Male']));
    const shown = tab.last();
    expect(shown).toMatchObject({ kind: 'verification', form: 'speech', mode: 'advanced' });
    const results = shown?.kind === 'verification' ? shown.results : [];
    expect(results.every((result) => result.outcome === 'set')).toBe(true);
    expect(results).toHaveLength(5);
  });

  it('stops before filling when the form does not offer the kind’s tab, naming the step', async () => {
    const tab = start({ job: job({ workspace: ON_MY_WORKSPACE, form: speechForm() }) });
    const standIn = standInForSuno(document);
    // The user's account does not offer Speech: the tab is not there.
    for (const element of document.querySelectorAll('[role="tab"]')) {
      if (element.textContent.trim() === 'Speech') {
        element.remove();
      }
    }
    tab.feed.take(feedFor('default'));

    try {
      await tab.generate.resume();
    } finally {
      standIn.stop();
    }

    const stopped = tab.last();
    expect(stopped?.kind).toBe('stopped');
    expect(stopped?.kind === 'stopped' ? stopped.message : '').toContain("step 'Speech tab'");
    expect(tab.asked.at(-1)).toMatchObject({ state: 'stopped', step: 'choose form' });
    expect(tab.steps()).not.toContain('filling fill form');
    expect(
      tab.asked.some(
        (request) => request.type === 'generate-progress' && 'verification' in request,
      ),
    ).toBe(false);
  });

  it('stops at the step "choose form" for a kind and mode the adapter has no form for', async () => {
    const tab = start({
      job: job({ workspace: ON_MY_WORKSPACE, form: speechForm({ mode: 'single' }) }),
    });
    tab.feed.take(feedFor('default'));

    await tab.generate.resume();

    expect(tab.last()).toEqual({ kind: 'stopped', message: NO_FORM });
    expect(tab.asked.at(-1)).toMatchObject({ state: 'stopped', step: 'choose form' });
    expect(tab.pressed.filter((name) => name !== 'My Workspace')).toEqual([]);
  });
});

/** Suno's answer to a Create, as the page observer passes it on, with what the page sent. */
function createAnswer(
  id: string,
  clipIds: string[],
  submitted: Record<string, unknown> | null = { mv: 'chirp-goose' },
) {
  return {
    ...observed('create', {
      ...sunoObject('generate-v2-web.songs-advanced.response'),
      id,
      clips: clipIds.map((clip) => ({ id: clip, status: 'submitted' })),
    }),
    submitted,
  };
}

describe('Generate on Suno in the Suno tab: the user’s Create (#149)', () => {
  it('records the user’s Create after the fill, and the next one on the form too, never clicking Create', async () => {
    const tab = start({ job: job({ workspace: ON_MY_WORKSPACE, form: advancedForm() }) });
    const standIn = standInForSuno(document);
    tab.feed.take(feedFor('default'));
    tab.feed.take(createAnswer('request-1', ['clip-1', 'clip-2']));
    tab.feed.take(createAnswer('request-2', ['clip-3', 'clip-4'], null));

    try {
      await tab.generate.resume();
      await until(() => tab.types().filter((type) => type === 'generate-observed').length === 2);
    } finally {
      standIn.stop();
    }

    const creates = tab.asked.filter((request) => request.type === 'generate-observed');
    expect(creates.map((request) => (request.response as { id: string }).id)).toEqual([
      'request-1',
      'request-2',
    ]);
    expect(creates[0]?.submitted).toEqual({ mv: 'chirp-goose' });
    expect(creates[1]?.submitted).toBeNull();
    expect(tab.steps().at(-1)).toBe('waiting review and create');
    expect(tab.last()).toEqual({
      kind: 'recorded',
      recorded: true,
      message: '2 Generations recorded on n8-1-v1.',
    });
    // Invariant 4: the user clicks Create; the extension never does.
    expect(tab.pressed.filter((name) => /^create/i.test(name))).toEqual([]);
  });

  it('sends no clips for an answer not as expected, and says at its step that a sync will bring them in', async () => {
    const tab = start({ job: job({ created: 1 }) });
    tab.feed.take(createAnswer('request-1', []));
    tab.feed.take({
      ...createAnswer('request-2', ['clip-1']),
      body: { clips: [{ id: 'clip-1' }] },
    });

    await tab.generate.resume();
    await until(() => tab.steps().length === 2);

    expect(tab.types()).not.toContain('generate-observed');
    expect(tab.steps()).toEqual(['waiting record Create', 'waiting record Create']);
    expect(tab.asked.at(-1)).toMatchObject({ message: NOT_EXPECTED });
    expect(tab.last()).toEqual({ kind: 'recorded', recorded: false, message: NOT_EXPECTED });
  });

  it('after a recorded Create, never fills the form again, and leaving the Create page ends the request', async () => {
    const tab = start({
      job: job({ created: 2, form: advancedForm() }),
      address: 'https://suno.com/me',
    });

    await tab.generate.resume();

    expect(tab.steps()).toEqual(['done left the Create page']);
    expect(tab.visited).toEqual([]);
    expect(tab.last()).toMatchObject({ kind: 'recorded', recorded: true });

    // On the Create page, the tab only watches; leaving it then ends the request.
    const again = start({ job: job({ created: 1, form: advancedForm() }) });
    await again.generate.resume();
    expect(again.steps()).toEqual([]);
    again.goTo('https://suno.com/me');
    await until(() => again.steps().length === 1);
    expect(again.steps()).toEqual(['done left the Create page']);
    expect(again.pressed).toEqual([]);
  });

  it('records nothing in a tab with no active request, and stops watching when n8Tracks ends the request', async () => {
    const idle = start({ job: null });
    idle.feed.take(createAnswer('request-1', ['clip-1']));
    await idle.generate.resume();
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(idle.types()).toEqual(['generate-resume']);

    const ended = start({
      job: job({ created: 1 }),
      observedReply: () => ({
        ok: false,
        ended: true,
        message: 'The request has ended in n8Tracks.',
      }),
    });
    ended.feed.take(createAnswer('request-1', ['clip-1']));
    ended.feed.take(createAnswer('request-2', ['clip-2']));
    await ended.generate.resume();
    await until(() => ended.last()?.kind === 'stopped');
    expect(ended.types().filter((type) => type === 'generate-observed')).toHaveLength(1);
  });
});

// ---- Starting from a source (#148).

const SOURCE_CLIP = '00000000-0000-4000-8000-000000000104';
const SIMPLE_CLIP = '00000000-0000-4000-8000-000000000109';
const OTHER_CLIP = '00000000-0000-4000-8000-000000000199';
const SONG_PAGE = `https://suno.com/song/${SOURCE_CLIP}`;

function coverSource(change: Partial<FormSource> = {}): FormSource {
  return {
    key: 'songs.advanced.audio',
    title: 'Origin',
    sunoAction: 'cover',
    group: 'audio',
    position: 1,
    sunoId: SOURCE_CLIP,
    availability: 'ok',
    continueAtSeconds: null,
    ...change,
  };
}

interface SourceTabOptions {
  job: GenerateJob;
  address: string;
  html: string;
  /** What Suno does when a control of this name is pressed. */
  onPress?: (name: string, tab: { go(address: string, html: string): void }) => void;
}

/** A Suno tab partway through loading a source: the page, its address, and Suno's answers. */
function sourceTab(options: SourceTabOptions) {
  document.body.innerHTML = options.html;
  let address = options.address;
  const clock = fakeClock();
  const visited: string[] = [];
  const page = new Page(document, {
    address: () => address,
    navigate: (to) => visited.push(to),
    clock,
  });
  const suno = {
    go: (to: string, html: string) => {
      address = to;
      document.body.innerHTML = html;
    },
  };
  const pressed: string[] = [];
  document.addEventListener(
    'click',
    (event) => {
      const target = (event.target as Element).closest('[role], button');
      if (target === null) {
        return;
      }
      const name = nameOf(target);
      pressed.push(name);
      options.onPress?.(name, suno);
    },
    { capture: true },
  );
  const asked: Request[] = [];
  const shown: GenerateViewState[] = [];
  const send = (request: Request): Promise<unknown> => {
    asked.push(structuredClone(request));
    return Promise.resolve(
      request.type === 'generate-resume' ? { job: options.job } : { ok: true },
    );
  };
  const feed = new ObservationFeed(
    {
      origin: 'https://suno.com',
      postMessage: () => undefined,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    },
    clock,
  );
  const generate = new SunoGenerate({
    page,
    session: new AdapterSession(new WorkflowRegistry(ADAPTER_WORKFLOWS), page),
    observations: feed,
    send,
    show: (state) => shown.push(state),
    clock,
    now: () => new Date('2026-10-07T12:00:00Z'),
  });
  return {
    generate,
    asked,
    shown,
    pressed,
    visited,
    steps: () =>
      asked.flatMap((request) =>
        request.type === 'generate-progress' ? [`${request.state} ${request.step}`] : [],
      ),
    kept: () =>
      asked.flatMap((request) => (request.type === 'generate-source' ? [request.source] : [])),
    last: () => shown.at(-1),
  };
}

function audioResult(shown: GenerateViewState | undefined) {
  return shown?.kind === 'verification'
    ? shown.results.find((result) => result.key.endsWith('.audio'))
    : undefined;
}

/** Lets the tab run until the panel asks for the source to be loaded by hand. */
async function untilWaitingForSource(generate: SunoGenerate): Promise<void> {
  for (let tries = 0; tries < 500 && !generate.waitingForSource; tries += 1) {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
  expect(generate.waitingForSource).toBe(true);
}

const FORBIDDEN_WORDS = /^(create|publish|delete|trash|move to trash|remove)/i;

describe('Generate on Suno in the Suno tab: starting from a source (#148)', () => {
  it('chooses the Version’s mode first, then goes to the source clip’s page, filling nothing yet', async () => {
    const tab = start({
      job: job({ workspace: ON_MY_WORKSPACE, form: advancedForm({ sources: [coverSource()] }) }),
    });
    const standIn = standInForSuno(document);
    tab.feed.take(feedFor('default'));
    try {
      await tab.generate.resume();
    } finally {
      standIn.stop();
    }

    expect(tab.steps().slice(-2)).toEqual(['filling open Songs form', 'filling open source']);
    expect(tab.asked).toContainEqual({
      type: 'generate-source',
      source: { phase: 'opening', sunoId: SOURCE_CLIP },
    });
    expect(tab.visited).toEqual([SONG_PAGE]);
  });

  it('always switches to the kind’s form and the Version’s mode first; only a Song loads its source after it (#147, #148)', async () => {
    // A Song with a source: switch-form runs before the source (TS-002), and nothing is filled yet.
    const song = start({
      job: job({ workspace: ON_MY_WORKSPACE, form: advancedForm({ sources: [coverSource()] }) }),
    });
    let standIn = standInForSuno(document);
    song.feed.take(feedFor('default'));
    try {
      await song.generate.resume();
    } finally {
      standIn.stop();
    }
    expect(song.ran.slice(-1)).toEqual(['switch-form']);
    expect(song.ran.filter((id) => id.startsWith('fill-'))).toEqual([]);
    expect(song.visited).toEqual([SONG_PAGE]);
    document.body.innerHTML = '';

    // A Speech Version with a source the snapshot carries: its own form is switched to, and no
    // source is loaded, so the form is filled in place.
    const speech = start({
      job: job({ workspace: ON_MY_WORKSPACE, form: speechForm({ sources: [coverSource()] }) }),
    });
    standIn = standInForSuno(document);
    speech.feed.take(feedFor('default'));
    try {
      await speech.generate.resume();
    } finally {
      standIn.stop();
    }
    expect(speech.ran.slice(-2)).toEqual(['switch-speech-form', 'fill-speech-advanced']);
    expect(speech.visited).toEqual([]);
    expect(speech.steps().at(-1)).toBe('waiting review and create');
  });

  it('stops before changing the form when n8Tracks knows a source is in Suno’s Trash (#142)', async () => {
    const tab = start({
      job: job({
        workspace: ON_MY_WORKSPACE,
        form: advancedForm({ sources: [coverSource({ availability: 'trashed' })] }),
      }),
    });
    tab.feed.take(feedFor('default'));

    await tab.generate.resume();

    const stopped = tab.last();
    expect(stopped?.kind === 'stopped' ? stopped.message : '').toContain(
      '“Origin” is in Suno’s Trash',
    );
    expect(tab.asked.at(-1)).toMatchObject({ state: 'stopped', step: 'open Songs form' });
    expect(tab.pressed).toEqual([]);
    expect(tab.visited).toEqual([]);
  });

  it('on the clip’s page, chooses Remix › Cover, answers Overwrite, verifies the source, then fills the form', async () => {
    const tab = sourceTab({
      job: job({
        workspace: ON_MY_WORKSPACE,
        form: advancedForm({ sources: [coverSource()] }),
        source: { phase: 'opening', sunoId: SOURCE_CLIP },
      }),
      address: SONG_PAGE,
      html: snapshotHtml('clip-remix-menu'),
      onPress: (name, suno) => {
        if (name === 'Cover') {
          // Suno opens Create in the same page, asking first: the form has lyrics and styles.
          suno.go(
            'https://suno.com/create',
            snapshotHtml('overwrite-lyrics-styles-dialog') + snapshotHtml('create-source-advanced'),
          );
        }
        if (name === 'Overwrite') {
          document.querySelector('[role="dialog"]')?.remove();
        }
      },
    });
    const standIn = standInForSuno(document);
    try {
      await tab.generate.resume();
    } finally {
      standIn.stop();
    }

    expect(tab.steps()).toEqual([
      'filling choose source action',
      'filling verify source',
      'filling fill form',
      'waiting review and create',
    ]);
    // The phase is kept before the action, which may load Create anew, and cleared once verified.
    expect(tab.kept()).toEqual([{ phase: 'chosen', sunoId: SOURCE_CLIP }, null]);
    expect(tab.pressed.slice(0, 2)).toEqual(['Cover', 'Overwrite']);
    expect(tab.pressed.filter((name) => FORBIDDEN_WORDS.test(name))).toEqual([]);
    expect(audioResult(tab.last())).toMatchObject({
      key: 'songs.advanced.audio',
      outcome: 'verified',
      note: '“Origin” is on the form: the Audio section names Cover and its thumbnail is the source clip’s.',
    });
    expect(tab.asked.at(-1)).toMatchObject({
      state: 'waiting',
      verification: {
        entries: expect.arrayContaining([
          expect.objectContaining({ key: 'songs.advanced.audio', outcome: 'verified' }),
        ]) as unknown,
      },
    });
  });

  it('carries on after Suno loads Create anew: verifies the source and fills (Simple chip)', async () => {
    const tab = sourceTab({
      job: job({
        workspace: ON_MY_WORKSPACE,
        form: advancedForm({
          mode: 'simple',
          entries: { 'songs.simple.simple_prompt': 'a quiet song' },
          sources: [coverSource({ key: 'songs.simple.audio', sunoId: SIMPLE_CLIP })],
        }),
        source: { phase: 'chosen', sunoId: SIMPLE_CLIP },
      }),
      address: 'https://suno.com/create',
      html: snapshotHtml('create-source-simple'),
    });

    await tab.generate.resume();

    expect(tab.steps()).toEqual([
      'filling verify source',
      'filling fill form',
      'waiting review and create',
    ]);
    expect(audioResult(tab.last())).toMatchObject({ outcome: 'verified' });
    expect(tab.pressed).toEqual([]);
  });

  it('when the form shows another source, fills nothing until the user loads it by hand and presses Continue', async () => {
    const tab = sourceTab({
      job: job({
        workspace: ON_MY_WORKSPACE,
        form: advancedForm({ sources: [coverSource({ sunoId: OTHER_CLIP })] }),
        source: { phase: 'chosen', sunoId: OTHER_CLIP },
      }),
      address: 'https://suno.com/create',
      html: snapshotHtml('create-source-advanced'),
    });
    const standIn = standInForSuno(document);
    try {
      const running = tab.generate.resume();
      await untilWaitingForSource(tab.generate);

      expect(tab.steps()).toEqual(['filling verify source', 'filling load source by hand']);
      const waiting = tab.last();
      expect(waiting?.kind).toBe('source');
      expect(waiting?.kind === 'source' ? waiting.message : '').toContain("step 'source shown'");
      expect(tab.asked.at(-1)).toMatchObject({
        state: 'filling',
        step: 'load source by hand',
        message: expect.stringContaining('Load “Origin” with Remix › Cover') as string,
      });
      expect(tab.pressed).toEqual([]);

      // Continue with the source still wrong: it blocks again.
      tab.generate.continueSource();
      await untilWaitingForSource(tab.generate);
      expect(tab.steps().filter((step) => step === 'filling load source by hand')).toHaveLength(2);

      // The user loads the right clip by hand, and presses Continue.
      document.querySelectorAll('img').forEach((image) => {
        image.setAttribute('src', `https://cdn2.suno.ai/image_${OTHER_CLIP}.jpeg`);
      });
      tab.generate.continueSource();
      await running;
    } finally {
      standIn.stop();
    }

    expect(audioResult(tab.last())).toMatchObject({ outcome: 'verified' });
    expect(tab.steps().at(-1)).toBe('waiting review and create');
  });

  it('reports Reuse Prompt as set once the menu action is done: it leaves no source to see', async () => {
    const tab = sourceTab({
      job: job({
        workspace: ON_MY_WORKSPACE,
        form: advancedForm({ sources: [coverSource({ sunoAction: 'reuse_prompt' })] }),
        source: { phase: 'chosen', sunoId: SOURCE_CLIP },
      }),
      address: 'https://suno.com/create',
      html: snapshotHtml('create-songs-advanced-more-options'),
    });
    const standIn = standInForSuno(document);
    try {
      await tab.generate.resume();
    } finally {
      standIn.stop();
    }

    expect(audioResult(tab.last())).toMatchObject({ outcome: 'set' });
  });

  it('stops naming the source when its menu does not offer the action, choosing nothing', async () => {
    const tab = sourceTab({
      job: job({
        workspace: ON_MY_WORKSPACE,
        form: advancedForm({ sources: [coverSource()] }),
        source: { phase: 'opening', sunoId: SOURCE_CLIP },
      }),
      address: SONG_PAGE,
      html: snapshotHtml('clip-remix-menu'),
    });
    document
      .querySelector('[role="menuitem"][aria-label="Cover"]')
      ?.setAttribute('aria-disabled', 'true');

    await tab.generate.resume();

    expect(tab.last()).toEqual({
      kind: 'stopped',
      message: actionUnavailable('“Origin”', 'Cover'),
    });
    expect(tab.kept()).toEqual([]);
    expect(tab.pressed).toEqual([]);
  });

  it('stops naming the source when Suno shows another page instead of the clip’s', async () => {
    const tab = sourceTab({
      job: job({
        workspace: ON_MY_WORKSPACE,
        form: advancedForm({ sources: [coverSource()] }),
        source: { phase: 'opening', sunoId: SOURCE_CLIP },
      }),
      address: 'https://suno.com/create',
      html: snapshotHtml('create-songs-advanced-more-options'),
    });

    await tab.generate.resume();

    expect(tab.last()).toEqual({
      kind: 'stopped',
      message: couldNotOpen('“Origin”', 'Suno showed another page instead of the clip’s'),
    });
    expect(tab.asked.at(-1)).toMatchObject({ state: 'stopped', step: 'open source' });
    expect(tab.pressed).toEqual([]);
  });
});
