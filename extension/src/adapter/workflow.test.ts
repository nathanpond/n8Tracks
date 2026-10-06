// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeClock, loadSnapshot } from '../testing/snapshots.ts';
import type { Page, Target } from './primitives.ts';
import {
  DEFAULT_STEP_TIMEOUT_MS,
  expected,
  failureText,
  OK,
  present,
  runWorkflow,
  type Step,
  type Workflow,
} from './workflow.ts';
import { ANY_SUNO_PAGE } from './addresses.ts';

const ADVANCED = 'create-songs-advanced-more-options';
const CLEAR_STYLES: Target = { role: 'button', name: 'Clear styles', description: 'Clear styles' };
const MISSING: Target = { role: 'button', name: 'Nope', description: 'a button named Nope' };

function workflow(steps: Step[]): Workflow {
  return {
    id: 'test',
    title: 'Test workflow',
    feature: 'generate',
    startsOn: ANY_SUNO_PAGE,
    needs: [{ step: 'first', check: () => OK }],
    steps,
    fixtures: [ADVANCED],
  };
}

/** A step that clicks `target`, counting its acts. */
function clickStep(name: string, target: Target, acts: string[]): Step {
  return {
    name,
    expect: ({ page }) => present(page, target),
    act: ({ page }) => {
      acts.push(name);
      const result = page.find(target);
      if (result.kind === 'found') {
        page.click(result.found);
      }
    },
    verify: () => OK,
  };
}

/** Records every DOM change under the body from now on. */
function watchMutations() {
  const observer = new MutationObserver(() => undefined);
  observer.observe(document.body, {
    subtree: true,
    childList: true,
    attributes: true,
    characterData: true,
  });
  return observer;
}

/** Makes a click on Clear styles change the page, as Suno would. */
function clearStylesChangesThePage(): void {
  document.querySelector('[aria-label="Clear styles"]')?.addEventListener('click', (event) => {
    (event.currentTarget as Element).setAttribute('data-cleared', 'true');
  });
}

afterEach(() => {
  document.body.innerHTML = '';
  vi.useRealTimers();
});

describe('runWorkflow', () => {
  it('runs each step in order: expect, act, verify', async () => {
    const page = loadSnapshot(ADVANCED, undefined, fakeClock());
    const acts: string[] = [];

    const result = await runWorkflow(
      workflow([clickStep('first', CLEAR_STYLES, acts), clickStep('second', CLEAR_STYLES, acts)]),
      page,
      {},
      { clock: fakeClock() },
    );

    expect(result.ok).toBe(true);
    expect(acts).toEqual(['first', 'second']);
    expect(result.log.map((entry) => `${entry.step} ${entry.phase} ${entry.outcome}`)).toEqual([
      'first expect ok',
      'first act ok',
      'first verify ok',
      'second expect ok',
      'second act ok',
      'second verify ok',
    ]);
  });

  it('never runs act when expect fails, and changes nothing on the page', async () => {
    const page = loadSnapshot(ADVANCED);
    clearStylesChangesThePage();
    const acts: string[] = [];
    const clock = fakeClock();
    const observer = watchMutations();

    const result = await runWorkflow(
      workflow([clickStep('first', MISSING, acts), clickStep('second', CLEAR_STYLES, acts)]),
      page,
      {},
      { clock },
    );

    expect(acts).toEqual([]);
    expect(result).toMatchObject({
      ok: false,
      failure: {
        workflow: 'Test workflow',
        step: 'first',
        phase: 'expect',
        kind: 'check',
        expected: 'a button named Nope',
        pageMayBeChanged: false,
      },
    });
    expect(result.ok ? '' : failureText(result.failure)).toBe(
      "Test workflow: step 'first' expected a button named Nope",
    );
    // Complement: nothing at all changed on the page after the failed step.
    expect(observer.takeRecords()).toEqual([]);
    // It polled every 100 ms for the default 10 seconds before giving up.
    expect(clock.slept).toHaveLength(DEFAULT_STEP_TIMEOUT_MS / 100);
  });

  it('acts once expect holds, reading it again every 100 ms', async () => {
    const page = loadSnapshot(ADVANCED);
    const clock = fakeClock();
    let reads = 0;
    const acts: string[] = [];

    const result = await runWorkflow(
      workflow([
        {
          name: 'late',
          expect: () => {
            reads += 1;
            return reads < 4 ? expected('the form to load') : OK;
          },
          act: () => {
            acts.push('late');
          },
          verify: () => OK,
        },
      ]),
      page,
      {},
      { clock },
    );

    expect(result.ok).toBe(true);
    expect(acts).toEqual(['late']);
    expect(clock.slept).toEqual([100, 100, 100]);
  });

  it('stops when verify fails: later steps never run, and the report says the page may be changed', async () => {
    const page = loadSnapshot(ADVANCED);
    clearStylesChangesThePage();
    const acts: string[] = [];
    const first: Step = {
      ...clickStep('clear', CLEAR_STYLES, acts),
      verify: () => expected('the Styles box to be empty'),
      timeoutMs: 300,
    };

    const result = await runWorkflow(
      workflow([first, clickStep('after', CLEAR_STYLES, acts)]),
      page,
      {},
      { clock: fakeClock() },
    );
    const observer = watchMutations();

    expect(acts).toEqual(['clear']);
    expect(result).toMatchObject({
      ok: false,
      failure: { step: 'clear', phase: 'verify', kind: 'check', pageMayBeChanged: true },
    });
    expect(result.ok ? '' : failureText(result.failure)).toBe(
      "Test workflow: step 'clear' expected the Styles box to be empty. The page may be partly changed.",
    );
    // Nothing is rolled back and nothing more happens.
    expect(
      document.querySelector('[aria-label="Clear styles"]')?.getAttribute('data-cleared'),
    ).toBe('true');
    await Promise.resolve();
    expect(observer.takeRecords()).toEqual([]);
  });

  it('reports a timeout as the step, and the late act cannot change the page afterwards', async () => {
    const page = loadSnapshot(ADVANCED);
    clearStylesChangesThePage();
    let release: () => void = () => undefined;
    let lateError: unknown = null;
    const slow: Step = {
      name: 'slow',
      expect: ({ page: current }) => present(current, CLEAR_STYLES),
      act: async ({ page: current }: { page: Page }) => {
        await new Promise<void>((resolve) => {
          release = resolve;
        });
        const result = current.find(CLEAR_STYLES);
        try {
          if (result.kind === 'found') {
            current.click(result.found);
          }
        } catch (error) {
          lateError = error;
        }
      },
      verify: () => OK,
      timeoutMs: 20,
    };
    const observer = watchMutations();

    const result = await runWorkflow(workflow([slow]), page, {});
    release();
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(result).toMatchObject({
      ok: false,
      failure: {
        step: 'slow',
        phase: 'act',
        kind: 'timeout',
        expected: 'the step to finish within 0.02 seconds',
      },
    });
    expect((lateError as Error | null)?.name).toBe('StoppedError');
    expect(observer.takeRecords()).toEqual([]);
  });

  it('reports an exception in act as a failure of that step', async () => {
    const page = loadSnapshot(ADVANCED, undefined, fakeClock());
    const failing: Step = {
      name: 'broken',
      expect: () => OK,
      act: () => {
        throw new Error('boom: secret lyric text');
      },
      verify: () => OK,
    };

    const result = await runWorkflow(workflow([failing]), page, {}, { clock: fakeClock() });

    expect(result).toMatchObject({
      ok: false,
      failure: {
        step: 'broken',
        phase: 'act',
        kind: 'error',
        expected: 'the step to finish without an error',
      },
    });
    // The exception's message, which could hold page values, is never carried into the report.
    expect(JSON.stringify(result)).not.toContain('secret');
  });

  it('reports a primitive refusal in act in its own words', async () => {
    const page = loadSnapshot(ADVANCED, undefined, fakeClock());
    const typing: Step = {
      name: 'type',
      expect: () => OK,
      act: ({ page: current }) => {
        const result = current.find({
          role: 'tab',
          name: 'Speech',
          description: 'the Speech tab',
        });
        if (result.kind === 'found') {
          current.set(result.found, 'x');
        }
      },
      verify: () => OK,
    };

    const result = await runWorkflow(workflow([typing]), page, {}, { clock: fakeClock() });

    expect(result.ok ? '' : failureText(result.failure)).toBe(
      "Test workflow: step 'type' expected the Speech tab to take a typed value. The page may be partly changed.",
    );
  });

  it('treats an expect that throws as a failed check, not a crash', async () => {
    const page = loadSnapshot(ADVANCED, undefined, fakeClock());

    const result = await runWorkflow(
      workflow([
        {
          name: 'reader',
          expect: () => {
            throw new TypeError('cannot read');
          },
          act: () => undefined,
          verify: () => OK,
          timeoutMs: 100,
        },
      ]),
      page,
      {},
      { clock: fakeClock() },
    );

    expect(result).toMatchObject({
      ok: false,
      failure: { step: 'reader', phase: 'expect', expected: 'the page to be readable' },
    });
  });

  it('passes a workflow its own values next to the page', async () => {
    const page = loadSnapshot(ADVANCED, undefined, fakeClock());
    const seen: string[] = [];
    interface FillContext {
      page: Page;
      signal: AbortSignal;
      styles: string;
    }
    const fill: Workflow<FillContext> = {
      ...workflow([]),
      steps: [
        {
          name: 'styles',
          expect: () => OK,
          act: (context) => {
            seen.push(context.styles);
          },
          verify: () => OK,
        },
      ],
    };

    await runWorkflow(fill, page, { styles: 'disco' }, { clock: fakeClock() });

    expect(seen).toEqual(['disco']);
  });
});
