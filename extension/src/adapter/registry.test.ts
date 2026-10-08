// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { fakeClock, loadSnapshot, SNAPSHOT_NAMES } from '../testing/snapshots.ts';
import { ANY_SUNO_PAGE, sunoPage } from './addresses.ts';
import type { Target } from './primitives.ts';
import { AdapterSession, WorkflowRegistry, type WorkflowStatus } from './registry.ts';
import { expected, OK, present, type Step, type Workflow } from './workflow.ts';
import { ADAPTER_WORKFLOWS } from './workflows/index.ts';
import { recogniseSuno } from './workflows/recognise.ts';

const ADVANCED = 'create-songs-advanced-more-options';

const STYLES: Target = {
  role: 'textbox',
  within: { testId: 'create-form-styles-wrapper', description: 'the Styles section' },
  description: 'a text box labelled Styles',
};
const CREATE_TABS: Target = {
  role: 'tablist',
  name: 'What to create',
  description: 'the Songs, Speech, and Sounds tabs',
};

function step(name: string, target: Target): Step {
  return {
    name,
    expect: ({ page }) => present(page, target),
    act: () => undefined,
    verify: () => OK,
  };
}

/**
 * Test-only stand-ins for the fill and sync workflows their own stories build: each declares what
 * it needs from the Create page, as a real one does.
 */
const fillSongsForm: Workflow = {
  id: 'test-fill-songs',
  title: 'Fill Songs form',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [
    { step: 'mode', check: (page) => present(page, CREATE_TABS) },
    { step: 'styles', check: (page) => present(page, STYLES) },
  ],
  steps: [step('mode', CREATE_TABS), step('styles', STYLES)],
  fixtures: [ADVANCED],
};
const librarySync: Workflow = {
  id: 'test-library-sync',
  title: 'Library sync',
  feature: 'sync',
  startsOn: sunoPage('create'),
  needs: [{ step: 'library', check: (page) => present(page, CREATE_TABS) }],
  steps: [step('library', CREATE_TABS)],
  fixtures: [ADVANCED],
};

function byId(statuses: WorkflowStatus[]): Record<string, WorkflowStatus> {
  return Object.fromEntries(statuses.map((status) => [status.id, status]));
}

afterEach(() => {
  document.body.innerHTML = '';
});

describe('the self-check', () => {
  it('Demo: with the Styles box removed, Fill Songs form names the step and sync stays ready', () => {
    const page = loadSnapshot(ADVANCED, 'https://suno.com/create');
    document.querySelector('[data-testid="create-form-styles-wrapper"] textarea')?.remove();
    const registry = new WorkflowRegistry([fillSongsForm, librarySync]);

    const statuses = byId(registry.check(page));

    expect(statuses['test-fill-songs']).toMatchObject({
      state: 'not-working',
      step: 'styles',
      message: "Fill Songs form: step 'styles' expected a text box labelled Styles",
    });
    expect(statuses['test-library-sync']).toMatchObject({ state: 'ready', message: null });
  });

  it('marks both ready on the unchanged snapshot', () => {
    const page = loadSnapshot(ADVANCED, 'https://suno.com/create');

    const states = new WorkflowRegistry([fillSongsForm, librarySync])
      .check(page)
      .map((status) => status.state);

    expect(states).toEqual(['ready', 'ready']);
  });

  it('leaves the others ready when one workflow fails, and when one throws', () => {
    const page = loadSnapshot(ADVANCED, 'https://suno.com/create');
    const throwing: Workflow = {
      ...librarySync,
      id: 'test-throws',
      title: 'Throws',
      needs: [
        {
          step: 'reads',
          check: () => {
            throw new Error('broken probe');
          },
        },
      ],
    };
    const failing: Workflow = {
      ...librarySync,
      id: 'test-fails',
      title: 'Fails',
      needs: [{ step: 'missing', check: () => expected('something Suno removed') }],
    };

    const statuses = byId(
      new WorkflowRegistry([throwing, failing, fillSongsForm, librarySync]).check(page),
    );

    expect(statuses['test-throws']).toMatchObject({
      state: 'not-working',
      message: "Throws: step 'reads' expected the page to be readable",
    });
    expect(statuses['test-fails']?.state).toBe('not-working');
    expect(statuses['test-fill-songs']?.state).toBe('ready');
    expect(statuses['test-library-sync']?.state).toBe('ready');
  });

  it('does not check a workflow on a page it does not start on', () => {
    const page = loadSnapshot(ADVANCED, 'https://suno.com/me');

    const [status] = new WorkflowRegistry([fillSongsForm]).check(page);

    expect(status).toMatchObject({ state: 'not-checked', startsOn: 'the Create page' });
  });

  it('clicks and changes nothing while checking', () => {
    const page = loadSnapshot(ADVANCED, 'https://suno.com/create');
    const events: string[] = [];
    for (const type of ['click', 'input', 'change', 'keydown', 'pointerdown', 'mousedown']) {
      document.addEventListener(type, () => events.push(type), { capture: true });
    }
    const observer = new MutationObserver(() => undefined);
    observer.observe(document.body, { subtree: true, childList: true, attributes: true });

    new WorkflowRegistry([...ADAPTER_WORKFLOWS, fillSongsForm, librarySync]).check(page);

    expect(events).toEqual([]);
    expect(observer.takeRecords()).toEqual([]);
  });
});

// #328: a workflow whose needs an earlier one sets up (a mode tab, Suno's question) waits for it.
describe('the self-check of chained workflows', () => {
  const WAITING = {
    'fill-songs-simple': 'Open the Songs form',
    'fill-speech-simple': 'Open the Speech form',
    'fill-speech-advanced': 'Open the Speech form',
    'fill-sounds': 'Open the Sounds form',
    'answer-overwrite': 'Choose the Suno action',
    'verify-source-advanced': 'Choose the Suno action',
    'verify-source-simple': 'Choose the Suno action',
    'set-extend-from': 'Verify the source (Advanced)',
  };

  it('Demo step 1: on the intact Create page, every workflow is ready, waiting for an earlier step, or not for this page', () => {
    const page = loadSnapshot('workspace-selector', 'https://suno.com/create');

    const statuses = new WorkflowRegistry(ADAPTER_WORKFLOWS).check(page);

    expect(statuses.filter((status) => status.state === 'not-working')).toEqual([]);
    expect(
      Object.fromEntries(
        statuses
          .filter((status) => status.state === 'waiting')
          .map((status) => [status.id, status.message]),
      ),
    ).toEqual(WAITING);
    // Complement: what the Create page itself offers is checked, and ready.
    expect(byId(statuses)['switch-form']?.state).toBe('ready');
    expect(byId(statuses)['fill-songs-advanced']?.state).toBe('ready');
  });

  it('shows a chained workflow ready once its needs hold, and one that waits for none as not working', () => {
    // A workflow that waits for none is not working where its needs fail: this snapshot has no tabs.
    const simple = loadSnapshot('create-songs-simple', 'https://suno.com/create');
    expect(byId(new WorkflowRegistry(ADAPTER_WORKFLOWS).check(simple))['switch-form']?.state).toBe(
      'not-working',
    );
    document.body.innerHTML = '';

    const advanced = loadSnapshot(ADVANCED, 'https://suno.com/create');
    const statuses = byId(new WorkflowRegistry(ADAPTER_WORKFLOWS).check(advanced));
    expect(statuses['fill-songs-advanced']?.state).toBe('ready');
    expect(statuses['fill-songs-simple']).toMatchObject({
      state: 'waiting',
      step: 'Songs form',
      message: 'Open the Songs form',
    });
  });

  it('refuses a workflow that waits for one not registered before it', () => {
    const waits: Workflow = { ...fillSongsForm, id: 'test-waits', after: 'test-library-sync' };

    expect(() => new WorkflowRegistry([waits, librarySync])).toThrow(
      "waits for 'test-library-sync', which is not registered before it",
    );
    expect(new WorkflowRegistry([librarySync, waits]).all()).toHaveLength(2);
  });
});

describe('the Recognise the Suno page workflow', () => {
  it.each(recogniseSuno.fixtures)('is ready on the %s snapshot', (name) => {
    const page = loadSnapshot(name, 'https://suno.com/me');

    expect(new WorkflowRegistry([recogniseSuno]).check(page)).toEqual([
      {
        id: 'recognise-suno',
        title: 'Recognise the Suno page',
        feature: 'page',
        state: 'ready',
        startsOn: 'any suno.com page',
        step: null,
        message: null,
        stopped: false,
      },
    ]);
  });

  it("is not working on a page without Suno's navigation, naming what it expected", () => {
    const page = loadSnapshot('download-dialog', 'https://suno.com/create');

    const [status] = new WorkflowRegistry(ADAPTER_WORKFLOWS).check(page);

    expect(status?.message).toBe(
      "Recognise the Suno page: step 'navigation' expected Suno's navigation, with its Library link",
    );
  });

  it('is not checked off suno.com', () => {
    const page = loadSnapshot('library-list', 'https://example.com/me');

    expect(new WorkflowRegistry(ADAPTER_WORKFLOWS).check(page)[0]?.state).toBe('not-checked');
  });

  it('names only snapshots that exist', () => {
    for (const workflow of ADAPTER_WORKFLOWS) {
      expect(SNAPSHOT_NAMES).toEqual(expect.arrayContaining([...workflow.fixtures]));
    }
  });
});

describe('the registry', () => {
  it('refuses a second workflow of the same ID, one without snapshots, steps, or needs, and repeated step names', () => {
    const registry = new WorkflowRegistry([librarySync]);

    expect(() => {
      registry.register(librarySync);
    }).toThrow('already registered');
    expect(() => {
      registry.register({ ...librarySync, id: 'a', fixtures: [] });
    }).toThrow('names no page snapshot');
    expect(() => {
      registry.register({ ...librarySync, id: 'b', steps: [] });
    }).toThrow('no steps');
    expect(() => {
      registry.register({ ...librarySync, id: 'c', needs: [] });
    }).toThrow('declares no needs');
    expect(() => {
      registry.register({
        ...librarySync,
        id: 'd',
        steps: [step('same', CREATE_TABS), step('same', CREATE_TABS)],
      });
    }).toThrow('two steps of the same name');
    expect(registry.all().map((workflow) => workflow.id)).toEqual(['test-library-sync']);
  });

  it("registers the recognition check, the library reader's load-more, the workspace steps, the Songs, Speech, and Sounds form fills, the source steps, the completion watch's refresh prompt, and the download steps in this version", () => {
    expect(ADAPTER_WORKFLOWS.map((workflow) => workflow.id)).toEqual([
      'recognise-suno',
      'load-more',
      'open-workspaces',
      'more-workspaces',
      'select-workspace',
      'create-workspace',
      'switch-form',
      'fill-songs-simple',
      'fill-songs-advanced',
      'check-songs-form',
      'switch-speech-form',
      'fill-speech-simple',
      'fill-speech-advanced',
      'check-speech-form',
      'switch-sounds-form',
      'fill-sounds',
      'check-sounds-form',
      'open-source-menu',
      'choose-source-action',
      'answer-overwrite',
      'verify-source-advanced',
      'verify-source-simple',
      'set-extend-from',
      'choose-voice',
      'close-voice-picker',
      'refresh-library',
      'open-clip-menu',
      'choose-download',
      'choose-download-format',
      'close-download-dialog',
    ]);
    expect(recogniseSuno.startsOn).toBe(ANY_SUNO_PAGE);
  });
});

describe('a session', () => {
  it('shows the last stop of a workflow until it runs again, and the self-check for the rest', async () => {
    const page = loadSnapshot(ADVANCED, 'https://suno.com/create');
    const session = new AdapterSession(new WorkflowRegistry([fillSongsForm, librarySync]), page);
    const textarea = document.querySelector('[data-testid="create-form-styles-wrapper"] textarea');
    const parent = textarea?.parentElement;
    textarea?.remove();

    const failed = await session.run(
      { ...fillSongsForm, steps: fillSongsForm.steps.map((s) => ({ ...s, timeoutMs: 200 })) },
      {},
      { clock: fakeClock() },
    );
    if (textarea) {
      parent?.append(textarea);
    }

    expect(failed.ok).toBe(false);
    expect(byId(session.statuses())['test-fill-songs']).toMatchObject({
      state: 'not-working',
      stopped: true,
      // The mode step had acted, so the page may be partly changed.
      message:
        "Fill Songs form: step 'styles' expected a text box labelled Styles. The page may be partly changed.",
    });
    expect(byId(session.statuses())['test-library-sync']?.state).toBe('ready');
    expect(session.stepLog('test-fill-songs').map((entry) => entry.step)).toEqual([
      'mode',
      'mode',
      'mode',
      'styles',
    ]);

    await session.run(fillSongsForm, {}, { clock: fakeClock() });

    expect(byId(session.statuses())['test-fill-songs']).toMatchObject({
      state: 'ready',
      stopped: false,
    });
  });

  it('forgets a stop on Try again, keeping the step log', async () => {
    const page = loadSnapshot(ADVANCED, 'https://suno.com/create');
    const session = new AdapterSession(new WorkflowRegistry([fillSongsForm]), page);
    const textarea = document.querySelector('[data-testid="create-form-styles-wrapper"] textarea');
    textarea?.remove();
    await session.run(
      { ...fillSongsForm, steps: fillSongsForm.steps.map((s) => ({ ...s, timeoutMs: 200 })) },
      {},
      { clock: fakeClock() },
    );
    expect(byId(session.statuses())['test-fill-songs']?.stopped).toBe(true);

    session.forget('test-fill-songs');
    session.forget('never-ran');

    // The self-check again: the Styles box is still gone, so not working, but not a stop.
    expect(byId(session.statuses())['test-fill-songs']).toMatchObject({
      state: 'not-working',
      stopped: false,
    });
    expect(session.stepLog('test-fill-songs')).not.toEqual([]);
  });
});
