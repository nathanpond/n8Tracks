import type { PagePattern } from './addresses.ts';
import { poll, POLL_MS, realClock, type Clock } from './clock.ts';
import {
  findProblem,
  ForbiddenControlError,
  PrimitiveError,
  type Page,
  type Target,
} from './primitives.ts';

/**
 * Workflows and the step runner. A workflow is a list of named steps, and each step is data:
 * what the page must look like before it acts (`expect`), what it does (`act`), and what must be
 * true afterwards (`verify`). The runner is the only caller of `act`. When a check fails or times
 * out, the run stops at once, the page is left as it is, and the report names the workflow, the
 * step, and what was expected. Nothing is retried on another element.
 */

/** The answer of a check: either fine, or what the page should have shown, in plain words. */
export type Check = { ok: true } | { ok: false; expected: string };

export const OK: Check = { ok: true };

/** A failed check: `expected` reads after "expected", as in "a text box labelled Styles". */
export function expected(text: string): Check {
  return { ok: false, expected: text };
}

/** What every step is given. A workflow may add its own values in a context that extends this. */
export interface StepContext {
  /** Suno's page, through the primitives only; it stops working once the run has stopped. */
  page: Page;
  signal: AbortSignal;
}

/** One named step. `expect` and `verify` only read the page. */
export interface Step<C extends StepContext = StepContext> {
  name: string;
  expect(context: C): Check;
  act(context: C): void | Promise<void>;
  verify(context: C): Check;
  /** How long `expect`, `act`, and `verify` may each take; 10 seconds unless set. */
  timeoutMs?: number;
}

/** A read-only check of the page a workflow starts on, named after the step that needs it. */
export interface Probe {
  step: string;
  check(page: Page): Check;
}

/** The part of the extension a workflow belongs to, which the panel groups by. */
export type Feature = 'page' | 'sync' | 'generate';

export interface Workflow<C extends StepContext = StepContext> {
  /** Stable, for the registry and the guard tests: `recognise-suno`. */
  id: string;
  /** How reports name it: "Fill Songs form". */
  title: string;
  feature: Feature;
  /** The pages it can start on; on any other page its state is "not checked". */
  startsOn: PagePattern;
  /** What it needs from the page it starts on: the self-check reads these, and clicks nothing. */
  needs: readonly Probe[];
  /**
   * The ID of the workflow that sets up the page state its needs describe (#328): the mode tab it
   * switched to, the question its press raised. The self-check shows it as waiting for that step,
   * not as not working, when its needs do not hold yet; it must be registered before this one.
   */
  after?: string;
  steps: readonly Step<C>[];
  /** The TS-003 page snapshots (`page.<name>.html`) it is built on and tested against. */
  fixtures: readonly string[];
}

/** The step timeout when a step declares none (the adapter story: 10 seconds). */
export const DEFAULT_STEP_TIMEOUT_MS = 10_000;

/** Where a run stopped. Holds no value from the page or from n8Tracks (invariant 6). */
export interface StepFailure {
  workflowId: string;
  workflow: string;
  step: string;
  phase: 'expect' | 'act' | 'verify';
  /**
   * `check`: the page did not look as expected in time; `timeout`: `act` did not finish; `error`:
   * `act` failed; `refused`: the forbidden-control matcher refused a press (invariant 4).
   */
  kind: 'check' | 'timeout' | 'error' | 'refused';
  /** What was expected; for `refused`, the control and why it is forbidden, in the adapter's words. */
  expected: string;
  /** An earlier or this step acted, so the page may be partly changed (nothing is rolled back). */
  pageMayBeChanged: boolean;
}

/** One line of a run's step log, for the panel and the diagnostic report. Never holds values. */
export interface StepLogEntry {
  step: string;
  phase: StepFailure['phase'];
  outcome: 'ok' | 'failed';
  /** Milliseconds since the run started. */
  atMs: number;
}

export type RunResult =
  { ok: true; log: StepLogEntry[] } | { ok: false; failure: StepFailure; log: StepLogEntry[] };

/** The report of a failure, as the panel shows it. */
export function failureText(failure: StepFailure): string {
  const text =
    failure.kind === 'refused'
      ? `${failure.workflow}: step '${failure.step}' refused: forbidden control (${failure.expected})`
      : `${failure.workflow}: step '${failure.step}' expected ${failure.expected}`;
  return failure.pageMayBeChanged ? `${text}. The page may be partly changed.` : text;
}

/** A check for a step's `expect`: the target is on the page once, visible, and enabled. */
export function present(page: Page, target: Target): Check {
  const result = page.find(target);
  if (result.kind !== 'found') {
    return expected(findProblem(result));
  }
  return page.read(result.found).enabled ? OK : expected(`${target.description}, enabled`);
}

/** Runs a check, turning an exception into a failed check rather than a crash of the run. */
function safely(check: () => Check, readableDescription: string): Check {
  try {
    return check();
  } catch (error) {
    return expected(error instanceof PrimitiveError ? error.expected : readableDescription);
  }
}

export interface RunOptions {
  clock?: Clock;
  pollMs?: number;
}

const TIMED_OUT = Symbol('timed out');

function refusedText(refusal: ForbiddenControlError): string {
  return `${refusal.control}: ${refusal.reason}`;
}

/**
 * Runs `workflow` on `page`, step by step: `expect` is read every 100 ms until it holds or the
 * step's timeout passes, and only then does `act` run; `verify` is read the same way afterwards.
 * The first failure stops the run, and the page handle given to the steps is cut off, so a
 * step still running cannot change the page afterwards.
 */
export async function runWorkflow<C extends StepContext>(
  workflow: Workflow<C>,
  page: Page,
  values: Omit<C, keyof StepContext>,
  options: RunOptions = {},
): Promise<RunResult> {
  const clock = options.clock ?? realClock;
  const pollMs = options.pollMs ?? POLL_MS;
  const controller = new AbortController();
  const context = {
    ...values,
    page: page.withSignal(controller.signal),
    signal: controller.signal,
  } as C;
  const started = clock.now();
  const log: StepLogEntry[] = [];
  let acted = false;

  const record = (step: string, phase: StepFailure['phase'], outcome: 'ok' | 'failed') => {
    log.push({ step, phase, outcome, atMs: Math.round(clock.now() - started) });
  };
  const stop = (
    step: Step<C>,
    phase: StepFailure['phase'],
    kind: StepFailure['kind'],
    expectedText: string,
  ): RunResult => {
    controller.abort();
    record(step.name, phase, 'failed');
    return {
      ok: false,
      log,
      failure: {
        workflowId: workflow.id,
        workflow: workflow.title,
        step: step.name,
        phase,
        kind,
        expected: expectedText,
        pageMayBeChanged: acted,
      },
    };
  };

  try {
    for (const step of workflow.steps) {
      const timeoutMs = step.timeoutMs ?? DEFAULT_STEP_TIMEOUT_MS;

      const before = await poll(
        () => safely(() => step.expect(context), 'the page to be readable'),
        timeoutMs,
        clock,
        pollMs,
      );
      if (!before.ok) {
        return stop(step, 'expect', 'check', before.expected);
      }
      record(step.name, 'expect', 'ok');

      acted = true;
      let timer: ReturnType<typeof setTimeout> | undefined;
      try {
        const acting = Promise.resolve().then(() => step.act(context));
        // An act still running after its timeout may fail later; that is already reported.
        acting.catch(() => undefined);
        const outcome = await Promise.race([
          acting,
          new Promise<typeof TIMED_OUT>((resolve) => {
            timer = setTimeout(() => {
              resolve(TIMED_OUT);
            }, timeoutMs);
          }),
        ]);
        const refusedMeanwhile = context.page.refusal();
        if (outcome === TIMED_OUT && refusedMeanwhile !== null) {
          return stop(step, 'act', 'refused', refusedText(refusedMeanwhile));
        }
        if (outcome === TIMED_OUT) {
          return stop(
            step,
            'act',
            'timeout',
            `the step to finish within ${String(timeoutMs / 1000)} seconds`,
          );
        }
      } catch (error) {
        const refusal = context.page.refusal();
        if (refusal !== null || error instanceof ForbiddenControlError) {
          return stop(
            step,
            'act',
            'refused',
            refusedText(refusal ?? (error as ForbiddenControlError)),
          );
        }
        return stop(
          step,
          'act',
          'error',
          error instanceof PrimitiveError ? error.expected : 'the step to finish without an error',
        );
      } finally {
        clearTimeout(timer);
      }
      // A refusal the step caught itself still stops the run: there is no override.
      const refusal = context.page.refusal();
      if (refusal !== null) {
        return stop(step, 'act', 'refused', refusedText(refusal));
      }
      record(step.name, 'act', 'ok');

      const after = await poll(
        () => safely(() => step.verify(context), 'the result on the page to be readable'),
        timeoutMs,
        clock,
        pollMs,
      );
      if (!after.ok) {
        return stop(step, 'verify', 'check', after.expected);
      }
      record(step.name, 'verify', 'ok');
    }
    return { ok: true, log };
  } finally {
    // The run is over either way: its page handle changes nothing more.
    controller.abort();
  }
}
