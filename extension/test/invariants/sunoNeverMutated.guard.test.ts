// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ANY_SUNO_PAGE } from '../../src/adapter/addresses.ts';
import { FILLERS, MODEL_LABELS, type FormJob } from '../../src/adapter/fill.ts';
import {
  INSPIRATION_ROUTE,
  planSources,
  SOURCE_ROUTES,
  type LoadedSource,
} from '../../src/adapter/sources.ts';
import { ObservationFeed } from '../../src/adapter/observations.ts';
import { OBSERVER_SOURCE } from '../../src/adapter/observed.ts';
import { Page, type Target } from '../../src/adapter/primitives.ts';
import { AdapterSession, WorkflowRegistry } from '../../src/adapter/registry.ts';
import { OK, present, type Step, type Workflow } from '../../src/adapter/workflow.ts';
import { ADAPTER_WORKFLOWS } from '../../src/adapter/workflows/index.ts';
import { chooseSourceAction, openSourceMenu } from '../../src/adapter/workflows/sources.ts';
import { COMPLETION_LIMIT_MS, SunoGenerate } from '../../src/content/sunoGenerate.ts';
import { fakeClock, SNAPSHOT_NAMES, snapshotHtml } from '../../src/testing/snapshots.ts';
import { EXTENSION_ROOT, scanExtension } from './sourceScan.ts';
import {
  exerciseWorkflows,
  forbiddenControls,
  registryProblems,
  type RunRecipe,
  WORKSPACE_SONGS_ADDRESS,
} from './workflowGuard.ts';

/**
 * Invariant 4 (CLAUDE.md): Suno is never mutated destructively, and the extension never clicks
 * Create, Publish, or Delete on its own. This guard runs in `npm test` on every pull request:
 *
 * 1. at run time, every registered workflow against every TS-003 snapshot, with spies on the
 *    controls the forbidden-control matcher (`src/adapter/forbidden.ts`) recognises;
 * 2. statically, the extension's shipped source (`sourceScan.ts`): no request to Suno, nothing
 *    reaching Suno's page except through `adapter/primitives.ts`, no workflow it cannot see.
 *
 * Not covered: a forbidden control Suno adds under a new name is caught only when the snapshots
 * and the matcher are updated; the live-site smoke checks in the generate stories supplement it.
 * `sourceScan.ts` lists what the static scan does not see.
 */

/**
 * How the guard runs each registered workflow. A story that adds a workflow adds its recipe
 * here: a workflow without one fails the guard.
 */
const RUN_RECIPES: Readonly<Record<string, RunRecipe>> = {
  'recognise-suno': { values: {} },
  // A sync's workspace list (#421): Library's Workspaces tab, which only changes what Library
  // shows; the Library snapshots' Publish and Trash controls must never be reached.
  'open-workspace-list': { values: {} },
  // Scrolls the list on screen; it presses nothing on any snapshot.
  'load-more': { values: {} },
  // Generate on Suno's workspace (#145, TS-007-15). Checking sign-in reads only; opening the list
  // presses only the breadcrumb (no snapshot shows it); searching types into the list's search box
  // and presses nothing. A workspace is opened by its address, never by its row.
  'signed-in': { values: {} },
  'open-workspaces': { values: {} },
  'search-workspaces': { values: { query: 'Night Drive' } },
  // The one permitted change to Suno: its two controls, through their own primitive only.
  'create-workspace': {
    values: { workspaceName: 'A new Song', created: () => OK },
    exceptions: ['create-workspace'],
  },
  // Filling the Songs form (#146): tabs, section headers, More Options' toggles, Duration's Custom,
  // slider keys (never Enter), and Simple's "+" menu with its Lyrics and Styles dialogs (TS-005);
  // Create is never pressed. Every entry is given a value other than the
  // snapshot's, so every filler acts.
  'switch-form': { values: { mode: 'advanced' } },
  'fill-songs-simple': { values: fillValues('simple') },
  'fill-songs-advanced': { values: fillValues('advanced') },
  'check-songs-form': { values: fillValues('advanced') },
  // Filling the Speech and Sounds forms (#147): their tabs, section headers, choice buttons, the
  // Variety slider's keys, the BPM box, and the Key popover's note, Major/Minor, and Apply (TS-005);
  // Create is never pressed.
  'switch-speech-form': { values: { mode: 'advanced' } },
  'fill-speech-simple': { values: kindValues('speech', 'simple') },
  'fill-speech-advanced': { values: kindValues('speech', 'advanced') },
  'check-speech-form': { values: kindValues('speech', 'advanced') },
  'switch-sounds-form': { values: { mode: 'single' } },
  'fill-sounds': { values: kindValues('sound', 'single') },
  'check-sounds-form': { values: kindValues('sound', 'single') },
  // Starting from a source (#148): the clip's More options, the Remix submenu, and Cover; the
  // Remix and Edit menus hold Publish and Move to Trash, which must never be reached. On the
  // Create form, Overwrite in Suno's question, and the source read without pressing anything.
  'open-source-menu': { values: { route: SOURCE_ROUTES.cover }, address: () => SONG_PAGE },
  'choose-source-action': { values: { route: SOURCE_ROUTES.cover }, address: () => SONG_PAGE },
  'answer-overwrite': { values: {} },
  'verify-source-advanced': { values: { load: coverOf('00000000-0000-4000-8000-000000000104') } },
  'verify-source-simple': { values: { load: coverOf('00000000-0000-4000-8000-000000000109') } },
  // An Extend's "Extend from" time (TS-005), typed into the Audio section; it presses nothing. The
  // snapshot's own time is given, so the guard's run (with no editing commands in jsdom) completes.
  'set-extend-from': { values: { seconds: 54 } },
  // The Voice (TS-005): "+ Voice", then a voice's title in the picker, and its Close; on the snapshot
  // of the voice chosen, nothing is pressed. The picker's Play buttons must never be reached.
  'close-voice-picker': { values: {} },
  'choose-voice': {
    values: {
      voice: { name: '<redacted 13 chars>', personaId: '00000000-0000-4000-8000-000000000203' },
    },
  },
  // Create's Save to (#435): the Save to button, the workspace's name typed into "Search or
  // create..." (never Enter), its one row, and Escape. The picker's "+", which may create a
  // workspace, must never be reached; on the snapshot of the picker open, nothing but the row is
  // pressed. Closing it sends Escape only.
  'choose-save-to': { values: { workspaceName: 'My Workspace' } },
  'close-save-to': { values: {} },
  // Preparing a download (#216): a library row's More options, the menu's Download, then a format
  // and "Unlock & Download" in the Download dialog, which are the second permitted change and are
  // pressed only through their own primitive. The menu's Publish and Move to Trash, and the
  // dialog's "MP4 video asset" and Manage, must never be reached.
  'open-clip-menu': { values: { sunoId: '00000000-0000-4000-8000-000000000102' } },
  'choose-download': { values: {} },
  'choose-download-format': { values: { format: 'wav' }, exceptions: ['download-clip'] },
  'close-download-dialog': { values: {}, exceptions: ['download-clip'] },
};

/** A Speech or Sound fill's values, other than the snapshots' own, so every filler acts. */
function kindValues(kind: string, mode: string): Record<string, unknown> {
  const job: FormJob = {
    kind,
    mode,
    entries: {
      'speech.simple.speech_prompt': 'a calm voice',
      'speech.advanced.speech_script': 'Hello there.',
      'speech.advanced.speech_tone': 'warm',
      'speech.advanced.speech_vocal_gender': 'male',
      'speech.advanced.speech_background_music': true,
      'speech.advanced.speech_variety': 'max',
      // Not a snapshot's own model, so the model picker is opened and a model chosen (TS-006, TS-007-10).
      'sounds.single.sounds_model': 'v6-wild',
      'sounds.single.sound_description': 'rain on a tin roof',
      'sounds.single.sound_type': 'one_shot',
      'sounds.single.sound_bpm': null,
      'sounds.single.sound_key': 'C',
      'sounds.single.sound_scale': 'major',
    },
    sources: [],
    fileInputs: [],
    unsupported: [],
  };
  return { job, workspace: 'My Workspace', results: [] };
}

const SONG_PAGE = 'https://suno.com/song/00000000-0000-4000-8000-000000000104';

/** A Cover of the clip `sunoId`, as the source plan loads it. */
function coverOf(sunoId: string): LoadedSource | null {
  return planSources({
    kind: 'song',
    mode: 'advanced',
    entries: {},
    sources: [
      { key: 'songs.advanced.audio', title: 'Origin', sunoAction: 'cover', sunoId, group: 'audio' },
    ],
    fileInputs: [],
    unsupported: [],
  }).load;
}

function fillValues(mode: string): Record<string, unknown> {
  const prefix = `songs.${mode}.`;
  const job: FormJob = {
    kind: 'song',
    mode,
    entries: {
      // The model button and a model in its Model dialog (a row, TS-007-10) or menu (a
      // `menuitemradio`, TS-006); "Create Custom Model" (which spends credits) must never be reached.
      [`${prefix}model`]: 'v6-wild',
      [`${prefix}simple_prompt`]: 'a quiet song',
      // Simple's sections (TS-005): the "+" menu, its Lyrics and Styles items, Write new, Close.
      [`${prefix}simple_add_lyrics`]: 'line one\nline two',
      [`${prefix}simple_add_styles`]: 'dream pop',
      [`${prefix}lyrics`]: 'line one\nline two',
      [`${prefix}styles`]: 'dream pop',
      [`${prefix}exclude_styles`]: '',
      [`${prefix}vocal_gender`]: null,
      [`${prefix}duration_mode`]: 'custom',
      [`${prefix}duration_seconds`]: 120,
      [`${prefix}max_mode`]: false,
      [`${prefix}weirdness`]: 40,
      [`${prefix}style_influence`]: 60,
      [`${prefix}variety`]: 'normal',
      [`${prefix}personalize`]: false,
      [`${prefix}title`]: 'A title',
    },
    sources: [],
    fileInputs: [],
    unsupported: [],
  };
  return { job, workspace: 'My Workspace', results: [] };
}

/** Every workflow module under `src/adapter/workflows/`, except the list itself. */
const WORKFLOW_MODULES = import.meta.glob<Record<string, unknown>>(
  ['../../src/adapter/workflows/*.ts', '!**/index.ts', '!**/*.test.ts'],
  { eager: true },
);

afterEach(() => {
  document.body.innerHTML = '';
});

describe('invariant 4: the extension never presses a forbidden control on Suno', () => {
  it('runs every registered workflow on every snapshot without activating a forbidden control', async () => {
    expect(ADAPTER_WORKFLOWS.length).toBeGreaterThan(0);
    expect(await exerciseWorkflows(ADAPTER_WORKFLOWS, { recipes: RUN_RECIPES })).toEqual([]);
    // Every workflow on every snapshot, each failing step polled to its timeout on a fake clock.
    // The adapter's lookups read jsdom's computed styles, which is slow: about 12 s here and an
    // expected 40 s on a GitHub runner, so the limit is three times that.
  }, 120_000);

  it('takes every Suno action through the clip menus without reaching Publish or Move to Trash (#148)', async () => {
    // Each action's route, on the snapshot of the menu it is in: every step must run there.
    const workflows: Workflow[] = [];
    const recipes: Record<string, RunRecipe> = {};
    for (const [action, route] of [
      ...Object.entries(SOURCE_ROUTES),
      ['inspiration', INSPIRATION_ROUTE] as const,
    ]) {
      // The Remix menu on the clip's own page (TS-005) and in a list (TS-003); Edit in a list.
      const fixtures =
        route.menu === 'Remix' ? ['clip-page-remix-menu', 'clip-remix-menu'] : ['clip-edit-menu'];
      for (const workflow of [openSourceMenu, chooseSourceAction]) {
        const id = `${workflow.id}-${action}`;
        workflows.push({ ...(workflow as unknown as Workflow), id, fixtures });
        recipes[id] = { values: { route }, address: () => SONG_PAGE };
      }
    }

    expect(
      await exerciseWorkflows(workflows, {
        recipes,
        snapshots: [
          'clip-page',
          'clip-page-remix-menu',
          'clip-remix-menu',
          'clip-edit-menu',
          'clip-download-menu',
        ],
      }),
    ).toEqual([]);
  }, 60_000);

  it('knows every workflow: each module is registered, and each registered one has a recipe', () => {
    expect(Object.keys(WORKFLOW_MODULES).length).toBeGreaterThan(0);
    expect(registryProblems(WORKFLOW_MODULES, ADAPTER_WORKFLOWS, RUN_RECIPES)).toEqual([]);
  });

  it('spies on the forbidden controls of every snapshot it runs on', () => {
    // Complement: the spies are really there. Every snapshot but the playlist page and the clip's
    // page with its menu closed (TS-005), which have no Create, Publish, Delete, Trash, or Remove
    // control and no dialog, has at least one.
    const spiedOn = SNAPSHOT_NAMES.filter((name) => {
      document.body.innerHTML = snapshotHtml(name);
      return forbiddenControls(document).length > 0;
    });
    expect(spiedOn).toEqual(
      SNAPSHOT_NAMES.filter((name) => name !== 'playlist' && name !== 'clip-page'),
    );
  });

  it('finds no request to Suno and no way to the page around the primitives', () => {
    const report = scanExtension({ root: EXTENSION_ROOT });

    expect(report.findings).toEqual([]);
    // Complement: the scan read the source it guards, and saw the Suno content script's code as
    // page context.
    expect(report.scanned).toEqual(
      expect.arrayContaining([
        'src/adapter/primitives.ts',
        'src/background/apiClient.ts',
        'src/content/suno.ts',
        'src/panel/panel.ts',
        'src/page/observe.ts',
        'src/background/sync.ts',
        'src/adapter/imageReader.ts',
        'src/background/images.ts',
      ]),
    );
    expect(report.pageContext).toEqual(
      expect.arrayContaining([
        'src/adapter/workflows/recognise.ts',
        'src/adapter/workflows/loadMore.ts',
        'src/adapter/libraryReader.ts',
        'src/content/suno-main.ts',
        'src/content/sunoSync.ts',
        'src/content/sunoGenerate.ts',
        'src/adapter/workflows/workspace.ts',
        'src/panel/GenerateView.ts',
        'src/page/observe.ts',
        'src/page/observe-main.ts',
        'src/panel/panel.ts',
        'src/panel/SyncView.ts',
        'src/ui/connectionView.ts',
      ]),
    );
    expect(report.pageContext).not.toContain('src/background/apiClient.ts');
    expect(report.pageContext).not.toContain('src/popup/popup.ts');
  });

  it('watches for finished clips by listening only: no press, scroll, workflow, or load (#429)', async () => {
    const { pressesDuringWatch, completions } = await watchCompletionOn('create-workspace-songs', [
      // The user's own browsing brings a library page: the clip is still generating.
      { at: 3 * 60_000, clips: [{ id: 'clip-1', status: 'streaming' }] },
    ]);
    expect(pressesDuringWatch).toEqual([]);
    // The page heard was reported; then the watch ran to its limit hearing nothing more.
    expect(completions).toEqual([[]]);
  }, 60_000);

  it('opens the Song’s workspace by its address and types into the list’s search box, pressing no control (TS-007-15, #434)', async () => {
    // A Song with a workspace, on Create: one load, by address, and nothing else.
    const byAddress = await generateOn('workspace-selector', 'https://suno.com/create', {
      sunoId: 'default',
      name: 'My Workspace',
      state: 'available',
    });
    expect(byAddress.happened).toEqual(['load https://suno.com/create?wid=default']);
    document.body.innerHTML = '';

    // That load: the page holds the Create button and each song's Publish; the workspace is taken
    // as selected by the address, and nothing is pressed.
    const arrived = await generateOn(
      'create-workspace-songs',
      WORKSPACE_SONGS_ADDRESS,
      {
        sunoId: WORKSPACE_SONGS_ADDRESS.split('wid=')[1] ?? '',
        name: 'Country Farming',
        state: 'available',
      },
      2,
    );
    expect(arrived.forbidden).toEqual(
      expect.arrayContaining(['button "Create song"', 'button "Publish clip"']),
    );
    expect(arrived.happened).toEqual([]);
    expect(arrived.shown.at(-1)).toEqual({ kind: 'selected', name: 'Country Farming' });
    document.body.innerHTML = '';

    // No workspace: the list's search box is typed into (the Song's title, then cleared), and the
    // choice waits for the user. No press, no key, no load.
    const searching = await generateOn('workspace-selector', 'https://suno.com/create', null);
    expect(searching.happened).toEqual([
      'typed "Night Drive" into Search workspaces',
      'typed "" into Search workspaces',
    ]);
    expect(searching.shown.at(-1)).toMatchObject({ kind: 'choose' });
  }, 60_000);
});

/**
 * Runs Generate on Suno in a Suno tab on `snapshot` at `address`, for a Song whose workspace is
 * `workspace`, until it selects the workspace, loads a page, or waits for the user's choice. Reports
 * every press, key, form submission, and load, and what was typed where; the forbidden controls on
 * the page are spied on as the workflow guard does.
 */
async function generateOn(
  snapshot: string,
  address: string,
  workspace: { sunoId: string; name: string; state: 'available' | 'unavailable' } | null,
  loads = 1,
): Promise<{ happened: string[]; forbidden: string[]; shown: unknown[] }> {
  document.body.innerHTML = snapshotHtml(snapshot);
  const happened: string[] = [];
  const controls = forbiddenControls(document);
  for (const control of controls) {
    for (const type of ['click', 'pointerdown', 'mousedown', 'keydown', 'submit']) {
      control.element.addEventListener(type, () => happened.push(`${type} on ${control.words}`), {
        capture: true,
      });
    }
  }
  for (const type of ['click', 'pointerdown', 'mousedown', 'keydown', 'submit']) {
    document.addEventListener(type, () => happened.push(`${type} on the page`), { capture: true });
  }
  document.addEventListener(
    'input',
    (event) => {
      const box = event.target as HTMLInputElement;
      happened.push(`typed "${box.value}" into ${box.getAttribute('aria-label') ?? box.localName}`);
    },
    { capture: true },
  );
  const clock = fakeClock();
  const page = new Page(document, {
    address: () => address,
    navigate: (to) => happened.push(`load ${to}`),
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
  feed.take({
    source: OBSERVER_SOURCE,
    type: 'observed',
    kind: 'workspaces',
    request: { cursor: null, page: 1, filters: null, feedId: null },
    body: { num_total_results: 101, current_page: 1, projects: [{ id: 'default', name: 'Mine' }] },
  });
  const shown: unknown[] = [];
  const generate = new SunoGenerate({
    page,
    session: new AdapterSession(new WorkflowRegistry(ADAPTER_WORKFLOWS), page, () => undefined),
    observations: feed,
    send: (request) => {
      switch (request.type) {
        case 'generate-resume':
          return Promise.resolve({
            job: {
              requestId: '0199b1a0-7000-7000-9000-0000000000aa',
              songTitle: 'Night Drive',
              workspace,
              loads,
              form: null,
            },
          });
        case 'generate-workspaces':
          return Promise.resolve({ ok: true, songCounts: {} });
        default:
          return Promise.resolve({ ok: true });
      }
    },
    show: (state) => {
      shown.push(state);
    },
    clock,
  });
  const running = generate.resume();
  for (let tries = 0; tries < 500 && !generate.choosing; tries += 1) {
    const settled = await Promise.race([
      running.then(() => true),
      new Promise((resolve) => {
        setTimeout(() => {
          resolve(false);
        }, 0);
      }),
    ]);
    if (settled === true) {
      break;
    }
  }
  return { happened, forbidden: controls.map((control) => control.words), shown };
}

/**
 * Runs the completion watch (#154) in a Suno tab on `snapshot`, on the Create page with a recorded
 * Create and the Song's workspace known, while page answers `heard` come on their own. Reports every
 * press, scroll, workflow, and load that happened during it, and the completion reports sent.
 */
async function watchCompletionOn(
  snapshot: string,
  heard: { at: number; clips: Record<string, unknown>[] }[],
): Promise<{ pressesDuringWatch: string[]; completions: unknown[] }> {
  document.body.innerHTML = snapshotHtml(snapshot);
  const happened: string[] = [];
  // The forbidden controls, spied on as the workflow guard does, and any press at all on the page.
  for (const control of forbiddenControls(document)) {
    for (const type of ['click', 'pointerdown', 'mousedown', 'keydown', 'submit']) {
      control.element.addEventListener(type, () => happened.push(`${type} on ${control.words}`), {
        capture: true,
      });
    }
  }
  for (const type of ['click', 'pointerdown', 'mousedown', 'keydown', 'submit']) {
    document.addEventListener(type, () => happened.push(`${type} on the page`), { capture: true });
  }
  for (const primitive of [
    'click',
    'createWorkspaceClick',
    'downloadDialogClick',
    'go',
    'scrollToEnd',
    'scrollListToEnd',
    'set',
    'slide',
    'dismiss',
    'typeText',
    'choose',
  ] as const) {
    vi.spyOn(Page.prototype, primitive).mockImplementation(() => {
      happened.push(`Page.${primitive}`);
      return undefined as never;
    });
  }
  vi.spyOn(AdapterSession.prototype, 'run').mockImplementation((workflow) => {
    happened.push(`workflow ${workflow.id}`);
    return Promise.resolve({ ok: true } as never);
  });

  const clock = fakeClock();
  const page = new Page(document, {
    address: () => 'https://suno.com/create',
    navigate: (address) => happened.push(`load ${address}`),
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
  const later = [...heard];
  const sleep = clock.sleep.bind(clock);
  clock.sleep = async (ms) => {
    await sleep(ms);
    for (const due of later.filter((item) => item.at <= clock.now())) {
      later.splice(later.indexOf(due), 1);
      feed.take({
        source: OBSERVER_SOURCE,
        type: 'observed',
        kind: 'library-feed',
        request: { cursor: null, page: null, filters: null, feedId: null },
        body: { clips: due.clips, has_more: false },
      });
    }
  };
  const completions: unknown[] = [];
  const generate = new SunoGenerate({
    page,
    session: new AdapterSession(new WorkflowRegistry(ADAPTER_WORKFLOWS), page, () => undefined),
    observations: feed,
    send: (request) => {
      switch (request.type) {
        case 'generate-resume':
          return Promise.resolve({
            job: {
              requestId: '0199b1a0-7000-7000-9000-0000000000aa',
              songTitle: 'Night Drive',
              workspace: { sunoId: 'default', name: 'My Workspace', state: 'available' },
              loads: 1,
              form: null,
              created: 1,
            },
            watching: ['clip-1'],
          });
        case 'generate-completion':
          completions.push(request.clips);
          return Promise.resolve({ ok: true, watching: ['clip-1'] });
        default:
          return Promise.resolve({ ok: true });
      }
    },
    show: () => undefined,
    clock,
  });

  await generate.resume();
  for (let tries = 0; tries < 500 && generate.watchingCompletion; tries += 1) {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
  expect(generate.watchingCompletion).toBe(false);
  expect(clock.now()).toBeGreaterThanOrEqual(COMPLETION_LIMIT_MS);
  return { pressesDuringWatch: happened, completions };
}

const CREATE_SONG: Target = {
  role: 'button',
  name: 'Create song',
  description: 'the Create button',
};

/**
 * A test-only workflow of one step that does `act` on the snapshots it lists, once `expectOn` is
 * on the page (at once when null).
 */
function testWorkflow(
  id: string,
  fixtures: readonly string[],
  act: Step['act'],
  expectOn: Target | null = CREATE_SONG,
): Workflow {
  const ready = (page: Page) => (expectOn === null ? OK : present(page, expectOn));
  return {
    id,
    title: `Test only: ${id}`,
    feature: 'generate',
    startsOn: ANY_SUNO_PAGE,
    needs: [{ step: 'act', check: ready }],
    steps: [
      {
        name: 'act',
        expect: ({ page }) => ready(page),
        act,
        verify: () => OK,
      },
    ],
    fixtures,
  };
}

/** Clicks the one element `target` finds, through the click primitive. */
function clicking(target: Target): Step['act'] {
  return ({ page }) => {
    const result = page.find(target);
    if (result.kind === 'found') {
      page.click(result.found);
    }
  };
}

describe('the guard bites', () => {
  const recipes = (...ids: string[]) =>
    Object.fromEntries(ids.map((id) => [id, { values: {} }])) as Record<string, RunRecipe>;

  it('fails a workflow that clicks Create, in each mode', async () => {
    const modes = [
      ['create-songs-simple', 'Create song'],
      ['create-songs-advanced-more-options', 'Create song'],
      ['create-speech-simple', 'Create speech'],
      ['create-speech-advanced', 'Create speech'],
      ['create-sounds-advanced-options', 'Create song'],
    ] as const;
    for (const [snapshot, name] of modes) {
      const target: Target = { role: 'button', name, description: 'the Create button' };
      const rogue = testWorkflow('press-create', [snapshot], clicking(target), target);

      const findings = await exerciseWorkflows([rogue], {
        recipes: recipes('press-create'),
        snapshots: [snapshot],
      });

      expect(findings, snapshot).toEqual([
        `'press-create' on ${snapshot}: step 'act' tried a forbidden control (the Create button: it is the Create button (${name === 'Create song' ? 'Songs and Sounds' : 'Speech'}))`,
        `'press-create' on ${snapshot}: not every step ran on its own fixture (missed 'act')`,
      ]);
    }
  });

  it("guards a workspace's songs on /create?wid=, where the Create form sits beside the list (#422)", async () => {
    const snapshot = 'create-workspace-songs';
    document.body.innerHTML = snapshotHtml(snapshot);
    // The page holds the Create button and each row's Publish button: all spied on.
    expect(forbiddenControls(document).map((control) => control.words)).toEqual(
      expect.arrayContaining(['button "Create song"', 'button "Publish clip"']),
    );

    // The reader's scroll for more runs there in full and reaches none of them.
    const loadMore = ADAPTER_WORKFLOWS.find((workflow) => workflow.id === 'load-more');
    expect(loadMore?.fixtures).toContain(snapshot);
    expect(
      await exerciseWorkflows(loadMore === undefined ? [] : [loadMore], {
        recipes: { 'load-more': RUN_RECIPES['load-more'] ?? { values: {} } },
        snapshots: [snapshot],
      }),
    ).toEqual([]);

    // A workflow that tried to press Create there is refused, and a click that went around the
    // primitives to a row's Publish is seen by the spies.
    const rogue = testWorkflow('press', [], clicking(CREATE_SONG), null);
    expect(
      await exerciseWorkflows([rogue], { recipes: recipes('press'), snapshots: [snapshot] }),
    ).toEqual([
      `'press' on ${snapshot}: step 'act' tried a forbidden control (the Create button: it is the Create button (Songs and Sounds))`,
    ]);
    const bypass = testWorkflow(
      'bypass',
      [],
      () => {
        // Test-only: reaches the page directly, as the static scan forbids shipped code to.
        document
          .querySelector('[aria-label="Publish clip"]')
          ?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
      },
      null,
    );
    expect(
      await exerciseWorkflows([bypass], { recipes: recipes('bypass'), snapshots: [snapshot] }),
    ).toEqual([`'bypass' on ${snapshot}: click reached button "Publish clip"`]);
  }, 60_000);

  it("fails a workflow that presses the Model dialog's Create Custom Model row, and sees a press around the primitives (#427)", async () => {
    const snapshot = 'create-songs-advanced-model-dialog';
    document.body.innerHTML = snapshotHtml(snapshot);
    // The row is spied on; the models' rows are not (they may be pressed).
    const spied = forbiddenControls(document).map((control) => control.words);
    expect(spied).toContain('row ""');
    expect(
      forbiddenControls(document).filter(
        (control) => control.element.getAttribute('role') === 'row',
      ).length,
    ).toBe(1);

    // Choosing each model through the dialog, as the model filler does, passes the guard: a model's
    // row, and the grid it sits in, are not spied on.
    for (const model of MODEL_LABELS) {
      const choose = testWorkflow(
        'choose-model',
        [snapshot],
        async ({ page }) => {
          const filler = FILLERS.find((candidate) => candidate.entry === 'songs.advanced.model');
          if (filler !== undefined) {
            await filler.write(page, model, fillValues('advanced').job as FormJob);
          }
        },
        null,
      );
      expect(
        await exerciseWorkflows([choose], {
          recipes: recipes('choose-model'),
          snapshots: [snapshot],
        }),
        model,
      ).toEqual([]);
    }

    const target: Target = {
      role: 'row',
      leadText: 'Create Custom Model',
      description: 'the Create Custom Model row',
    };
    const rogue = testWorkflow('press', [snapshot], clicking(target), target);
    expect(
      await exerciseWorkflows([rogue], { recipes: recipes('press'), snapshots: [snapshot] }),
    ).toEqual([
      `'press' on ${snapshot}: step 'act' tried a forbidden control (the Create Custom Model row: its name starts with Create, Publish, Delete, Trash, or Remove)`,
      `'press' on ${snapshot}: not every step ran on its own fixture (missed 'act')`,
    ]);

    const bypass = testWorkflow(
      'bypass',
      [],
      () => {
        // Test-only: reaches the page directly, as the static scan forbids shipped code to.
        document
          .querySelector('[role="dialog"] [role="row"]')
          ?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
      },
      null,
    );
    expect(
      await exerciseWorkflows([bypass], { recipes: recipes('bypass'), snapshots: [snapshot] }),
    ).toEqual([`'bypass' on ${snapshot}: click reached row ""`]);
  }, 60_000);

  it('refuses the Save to picker’s “+” and a “Create …” row, as a dialog or not, and sees a press around the primitives (#435)', async () => {
    const snapshot = 'create-songs-advanced-save-to-picker';
    document.body.innerHTML = snapshotHtml(snapshot);
    // "+" (an icon with no name) is spied on; the workspaces' rows, and the box, are not.
    const spied = forbiddenControls(document).filter((control) =>
      control.element.closest('[role="dialog"]'),
    );
    expect(spied.map((control) => control.words)).toEqual(['button ""']);

    // Setting Save to, as the content script does, passes the guard: the row only.
    const saveTo = ADAPTER_WORKFLOWS.find((workflow) => workflow.id === 'choose-save-to');
    expect(saveTo?.fixtures).toEqual([snapshot]);
    expect(
      await exerciseWorkflows(saveTo === undefined ? [] : [saveTo], {
        recipes: { 'choose-save-to': RUN_RECIPES['choose-save-to'] ?? { values: {} } },
        snapshots: [snapshot],
      }),
    ).toEqual([]);

    const box: Target = {
      role: 'textbox',
      name: 'Search or create...',
      description: 'the Save to box',
    };
    const plus: Target = {
      role: 'button',
      name: '',
      within: { around: box, levels: 1, description: 'the box’s row' },
      description: 'the Save to picker’s “+”',
    };
    const refusedPlus = `'press' on ${snapshot}: step 'act' tried a forbidden control (the Save to picker’s “+”: the Save to picker allows only a workspace’s row: its “+” may create a workspace)`;
    const rogue = testWorkflow('press', [], clicking(plus), null);
    expect(
      await exerciseWorkflows([rogue], { recipes: recipes('press'), snapshots: [snapshot] }),
    ).toEqual([refusedPlus]);

    // The same picker without a dialog's role: known by the box beside "+".
    const inline = testWorkflow(
      'press',
      [],
      (context) => {
        // Test-only: reshapes the page, as the static scan forbids shipped code to.
        document.querySelector('#_r_sv_')?.removeAttribute('role');
        return clicking(plus)(context);
      },
      null,
    );
    expect(
      await exerciseWorkflows([inline], { recipes: recipes('press'), snapshots: [snapshot] }),
    ).toEqual([refusedPlus]);

    // A "Create …" row, as Suno may offer for a name it does not have, is refused too.
    const createRow: Target = {
      role: 'button',
      name: /^Create /,
      within: { around: box, levels: 2, description: 'the Save to picker' },
      description: 'a “Create …” row of the Save to picker',
    };
    const creating = testWorkflow(
      'press',
      [],
      (context) => {
        // Test-only: adds the row Suno may show, as the static scan forbids shipped code to.
        const row = document.createElement('button');
        row.type = 'button';
        row.innerHTML = '<span>Create “Night Drive”</span>';
        document.querySelector('#_r_sv_ .overflow-y-auto')?.append(row);
        return clicking(createRow)(context);
      },
      null,
    );
    expect(
      await exerciseWorkflows([creating], { recipes: recipes('press'), snapshots: [snapshot] }),
    ).toEqual([
      `'press' on ${snapshot}: step 'act' tried a forbidden control (a “Create …” row of the Save to picker: its name starts with Create, Publish, Delete, Trash, or Remove)`,
    ]);

    const bypass = testWorkflow(
      'bypass',
      [],
      () => {
        // Test-only: reaches the page directly, as the static scan forbids shipped code to.
        document
          .querySelector('#_r_sv_ input + button')
          ?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
      },
      null,
    );
    expect(
      await exerciseWorkflows([bypass], { recipes: recipes('bypass'), snapshots: [snapshot] }),
    ).toEqual([`'bypass' on ${snapshot}: click reached button ""`]);
  }, 60_000);

  it('fails a workflow that presses Publish or Move to Trash in a clip menu', async () => {
    for (const item of ['Publish', 'Move to Trash']) {
      const target: Target = { role: 'menuitem', name: item, description: `the ${item} item` };
      const rogue = testWorkflow('menu', ['clip-edit-menu'], clicking(target), target);

      const findings = await exerciseWorkflows([rogue], {
        recipes: recipes('menu'),
        snapshots: ['clip-edit-menu'],
      });

      expect(findings[0], item).toMatch(
        new RegExp(
          `^'menu' on clip-edit-menu: step 'act' tried a forbidden control \\(the ${item} item: `,
        ),
      );
    }
  });

  it('fails a workflow that presses Trash in the library, or removes the source from the form', async () => {
    for (const [snapshot, target] of [
      ['library-list', { role: 'button', name: 'Trash', description: 'the Trash button' }],
      [
        'create-source-simple',
        { role: 'button', name: /^Remove /, description: "the source's Remove button" },
      ],
    ] as const) {
      const rogue = testWorkflow('press', [snapshot], clicking(target), target);

      const findings = await exerciseWorkflows([rogue], {
        recipes: recipes('press'),
        snapshots: [snapshot],
      });

      expect(findings[0], snapshot).toBe(
        `'press' on ${snapshot}: step 'act' tried a forbidden control (${target.description}: its name starts with Create, Publish, Delete, Trash, or Remove)`,
      );
    }
  });

  it('catches an activation that bypasses the primitives, by the spies alone', async () => {
    for (const [snapshot, selector, words] of [
      ['create-songs-simple', '[aria-label="Create song"]', 'button "Create song"'],
      ['library-trash', '[aria-label="Delete permanently"]', 'button "Delete permanently"'],
      ['library-list', '[aria-label="Publish clip"]', 'button "Publish clip"'],
      ['download-dialog', '[role="dialog"] button', 'dialog'],
    ] as const) {
      const rogue = testWorkflow(
        'bypass',
        [],
        () => {
          // Test-only: reaches the page directly, as the static scan forbids shipped code to.
          document
            .querySelector(selector)
            ?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
        },
        null,
      );

      const findings = await exerciseWorkflows([rogue], {
        recipes: recipes('bypass'),
        snapshots: [snapshot],
      });

      expect(findings.length, snapshot).toBeGreaterThan(0);
      expect(
        findings.every((finding) => finding.startsWith(`'bypass' on ${snapshot}: click reached `)),
      ).toBe(true);
      expect(
        findings.some((finding) => finding.includes(words)),
        snapshot,
      ).toBe(true);
    }
  });

  it("catches an Enter key, which can submit Suno's form, anywhere", async () => {
    const rogue = testWorkflow('enter', ['create-songs-simple'], () => {
      document
        .querySelector('textarea')
        ?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    });

    const findings = await exerciseWorkflows([rogue], {
      recipes: recipes('enter'),
      snapshots: ['create-songs-simple'],
    });

    expect(findings).toEqual(['\'enter\' on create-songs-simple: an Enter key on textbox ""']);
  });

  it('lets a workflow answer the Overwrite Lyrics & Styles dialog, and refuses its other controls', async () => {
    const dialog = {
      role: 'dialog',
      name: 'Overwrite Lyrics & Styles?',
      description: 'the dialog',
    } as const;
    for (const answer of ['Overwrite', 'Keep Current']) {
      const target: Target = { role: 'button', name: answer, within: dialog, description: answer };
      const answering = testWorkflow(
        'answer',
        ['overwrite-lyrics-styles-dialog'],
        clicking(target),
        target,
      );
      expect(
        await exerciseWorkflows([answering], {
          recipes: recipes('answer'),
          snapshots: ['overwrite-lyrics-styles-dialog'],
        }),
        answer,
      ).toEqual([]);
    }

    const close: Target = { role: 'button', name: 'Close', within: dialog, description: 'Close' };
    const closing = testWorkflow(
      'close',
      ['overwrite-lyrics-styles-dialog'],
      clicking(close),
      close,
    );
    expect(
      await exerciseWorkflows([closing], {
        recipes: recipes('close'),
        snapshots: ['overwrite-lyrics-styles-dialog'],
      }),
    ).toContain(
      "'close' on overwrite-lyrics-styles-dialog: step 'act' tried a forbidden control (Close: the dialog it is in does not allow it)",
    );
  });

  it('allows creating a workspace only through its primitive, and only for a workflow allowed to', async () => {
    const opener: Target = {
      role: 'button',
      name: 'Create new workspace',
      description: 'the Create new workspace entry',
    };
    const byClick = testWorkflow('workspace', ['workspace-selector'], clicking(opener), opener);
    const byPrimitive = testWorkflow(
      'workspace',
      ['workspace-selector'],
      ({ page }) => {
        const result = page.find(opener);
        if (result.kind === 'found') {
          page.createWorkspaceClick(result.found);
        }
      },
      opener,
    );
    const run = (workflow: Workflow, recipe: RunRecipe) =>
      exerciseWorkflows([workflow], {
        recipes: { workspace: recipe },
        snapshots: ['workspace-selector'],
      });

    expect(await run(byClick, { values: {}, exceptions: ['create-workspace'] })).toContain(
      "'workspace' on workspace-selector: step 'act' tried a forbidden control (the Create new workspace entry: it is the named exception 'create-workspace', pressed only by its own primitive)",
    );
    expect(await run(byPrimitive, { values: {} })).toContain(
      '\'workspace\' on workspace-selector: click reached button "Create new workspace"',
    );
    expect(await run(byPrimitive, { values: {}, exceptions: ['create-workspace'] })).toEqual([]);
  });

  it("lets the Download dialog's format and button be pressed only through their primitive, and never its other controls (#216)", async () => {
    const dialog = { role: 'dialog', name: /^Download\b/, description: 'the dialog' } as const;
    const control = (name: string): Target => ({
      role: 'button',
      name,
      within: dialog,
      description: name,
    });
    const byPrimitive = (name: string) =>
      testWorkflow(
        'download',
        ['download-dialog'],
        ({ page }) => {
          const result = page.find(control(name));
          if (result.kind === 'found') {
            page.downloadDialogClick(result.found);
          }
        },
        control(name),
      );
    const run = (workflow: Workflow, recipe: RunRecipe) =>
      exerciseWorkflows([workflow], {
        recipes: { download: recipe },
        snapshots: ['download-dialog'],
      });

    const unlock = control('Unlock & Download');
    expect(
      await run(testWorkflow('download', ['download-dialog'], clicking(unlock), unlock), {
        values: {},
        exceptions: ['download-clip'],
      }),
    ).toContain(
      "'download' on download-dialog: step 'act' tried a forbidden control (Unlock & Download: it is the named exception 'download-clip', pressed only by its own primitive)",
    );
    expect(await run(byPrimitive('Unlock & Download'), { values: {} })).toContain(
      '\'download\' on download-dialog: click reached button "Unlock & Download"',
    );
    expect(
      await run(byPrimitive('Unlock & Download'), { values: {}, exceptions: ['download-clip'] }),
    ).toEqual([]);
    for (const other of ['MP4 video asset', 'Manage']) {
      expect(
        await run(byPrimitive(other), { values: {}, exceptions: ['download-clip'] }),
        other,
      ).toContain(
        `'download' on download-dialog: step 'act' tried a forbidden control (${other}: it is in a dialog the adapter does not recognise)`,
      );
    }
  });

  it('fails a workflow it has not been told how to run, or one that does not finish on its fixture', async () => {
    const quiet = testWorkflow('quiet', ['library-list'], () => undefined, {
      role: 'link',
      name: 'Library',
      description: 'the Library link',
    });
    expect(await exerciseWorkflows([quiet], { recipes: {}, snapshots: ['library-list'] })).toEqual([
      "'quiet': the guard has not been told how to run it (add a recipe for it)",
    ]);

    const lost = testWorkflow('lost', ['create-songs-simple'], () => undefined, {
      role: 'link',
      name: 'Library',
      description: 'the Library link',
    });
    expect(
      await exerciseWorkflows([lost], {
        recipes: recipes('lost'),
        snapshots: ['create-songs-simple'],
      }),
    ).toEqual([
      "'lost' on create-songs-simple: not every step ran on its own fixture (missed 'act')",
    ]);
  });

  it('fails a workflow module that is not registered, and a registration with no module', () => {
    const [registered] = ADAPTER_WORKFLOWS;
    if (registered === undefined) {
      throw new Error('No workflow is registered.');
    }
    const extra = testWorkflow('extra', ['library-list'], () => undefined);

    expect(
      registryProblems(
        { ...WORKFLOW_MODULES, '../../src/adapter/workflows/extra.ts': { extra } },
        ADAPTER_WORKFLOWS,
        RUN_RECIPES,
      ),
    ).toEqual(["../../src/adapter/workflows/extra.ts: 'extra' is not in ADAPTER_WORKFLOWS"]);
    expect(registryProblems(WORKFLOW_MODULES, [...ADAPTER_WORKFLOWS, extra], RUN_RECIPES)).toEqual([
      "'extra' is registered but no module under adapter/workflows/ has it",
    ]);
    expect(
      registryProblems(WORKFLOW_MODULES, ADAPTER_WORKFLOWS, {
        ...RUN_RECIPES,
        gone: { values: {} },
      }),
    ).toEqual(["the recipe 'gone' is for no registered workflow"]);
    expect(
      registryProblems(
        { ...WORKFLOW_MODULES, '../../src/adapter/workflows/empty.ts': { nothing: 1 } },
        ADAPTER_WORKFLOWS,
        RUN_RECIPES,
      ),
    ).toEqual(['../../src/adapter/workflows/empty.ts exports no workflow']);
    expect(
      registryProblems(
        WORKFLOW_MODULES,
        ADAPTER_WORKFLOWS.map((workflow) =>
          workflow === registered ? { ...registered, fixtures: [] } : workflow,
        ),
        RUN_RECIPES,
      ),
    ).toEqual(expect.arrayContaining([expect.stringMatching(/names no page snapshot/)]));
  });
});
