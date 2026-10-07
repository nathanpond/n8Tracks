import { RECOGNISED_DIALOGS, type ExceptionName } from '../../src/adapter/forbidden.ts';
import {
  dialogTitleOf,
  isDialog,
  nameOf,
  roleOf,
  verdictOf,
} from '../../src/adapter/primitives.ts';
import { WorkflowRegistry } from '../../src/adapter/registry.ts';
import { runWorkflow, type Workflow } from '../../src/adapter/workflow.ts';
import { fakeClock, loadSnapshot, SNAPSHOT_NAMES } from '../../src/testing/snapshots.ts';

/**
 * The runtime half of the invariant 4 guard. It runs every workflow against every TS-003 page
 * snapshot (live DOM in jsdom) with capture-phase spies on the controls the forbidden-control
 * matcher recognises, and reports:
 *
 * - any click, pointer press, Enter or Space key, or form submission that reached one of them
 *   (the create-workspace exception only for a workflow allowed to make it);
 * - any Enter key dispatched anywhere, and any form submitted anywhere;
 * - any press the matcher refused (a workflow that tried is a bug even though nothing happened);
 * - a workflow that did not run every step on each snapshot it lists as a fixture;
 * - a workflow the guard has not been told how to run.
 *
 * Not covered: a forbidden control Suno adds under a new name is caught only when the snapshots
 * and the matcher are updated; the live-site smoke checks in the generate stories supplement it.
 */

/** How the guard runs one workflow: the values its steps need, and where each snapshot is. */
export interface RunRecipe {
  values: Record<string, unknown>;
  /** The page's address for a snapshot; {@link snapshotAddress} unless set. */
  address?: (snapshot: string) => string;
  /** The permitted changes this workflow makes (invariant 4's exceptions), by name. */
  exceptions?: readonly ExceptionName[];
}

const PLAYLIST_ADDRESS = 'https://suno.com/playlist/00000000-0000-4000-8000-000000000101';

/** Where each snapshot was taken (fixtures README); the Create page for the rest. */
export function snapshotAddress(snapshot: string): string {
  switch (snapshot) {
    case 'library-list':
      return 'https://suno.com/me';
    case 'library-trash':
      return 'https://suno.com/me/trash';
    case 'playlist':
      return PLAYLIST_ADDRESS;
    default:
      return 'https://suno.com/create';
  }
}

/** One spied control: plain words for the report, and what the matcher said of it. */
interface Spied {
  element: Element;
  words: string;
  exception: ExceptionName | null;
}

const ACTIVATING_KEYS = new Set(['Enter', ' ', 'Spacebar']);
const SPIED_EVENTS = ['click', 'pointerdown', 'mousedown', 'keydown', 'submit'] as const;

function describe(element: Element): string {
  return `${roleOf(element) ?? element.localName} "${nameOf(element)}"`;
}

/**
 * The controls on the loaded page a workflow must never activate: every element with a role the
 * matcher refuses or treats as an exception, and every dialog it does not recognise (a click
 * anywhere inside one is an activation).
 */
export function forbiddenControls(document: Document): Spied[] {
  const spied: Spied[] = [];
  for (const element of document.body.querySelectorAll('*')) {
    const role = roleOf(element);
    if (role === null) {
      continue;
    }
    if (isDialog(element)) {
      // The dialog's own verdict is on what it sits in; a click anywhere inside an unrecognised
      // one is an activation, and a recognised one's controls are spied one by one.
      const title = dialogTitleOf(element);
      if (!RECOGNISED_DIALOGS.some((dialog) => dialog.title === title)) {
        spied.push({ element, words: `${describe(element)} (anything in it)`, exception: null });
      }
      continue;
    }
    const verdict = verdictOf(element);
    if (verdict.kind !== 'allowed') {
      spied.push({
        element,
        words: describe(element),
        exception: verdict.kind === 'exception' ? verdict.exception : null,
      });
    }
  }
  return spied;
}

/** Records activations of the spied controls, and Enter and submissions anywhere, until stopped. */
function watch(document: Document, spied: readonly Spied[]) {
  const activations: { words: string; event: string; exception: ExceptionName | null }[] = [];
  const anywhere: string[] = [];
  const removers: (() => void)[] = [];
  const listen = (target: EventTarget, type: string, listener: (event: Event) => void) => {
    target.addEventListener(type, listener, { capture: true });
    removers.push(() => {
      target.removeEventListener(type, listener, { capture: true });
    });
  };

  for (const control of spied) {
    for (const type of SPIED_EVENTS) {
      listen(control.element, type, (event) => {
        const key = (event as KeyboardEvent).key;
        if (type === 'keydown' && !ACTIVATING_KEYS.has(key)) {
          return;
        }
        activations.push({
          words: control.words,
          event: type === 'keydown' ? `keydown ${key === ' ' ? 'Space' : key}` : type,
          exception: control.exception,
        });
      });
    }
  }
  listen(document, 'keydown', (event) => {
    if ((event as KeyboardEvent).key === 'Enter') {
      anywhere.push(`an Enter key on ${describe(event.target as Element)}`);
    }
  });
  listen(document, 'submit', (event) => {
    anywhere.push(`a form submitted (${describe(event.target as Element)})`);
  });

  return {
    activations,
    anywhere,
    stop: () => {
      for (const remove of removers) {
        remove();
      }
    },
  };
}

export interface ExerciseOptions {
  recipes: Readonly<Record<string, RunRecipe>>;
  /** The snapshots to run on; every one unless set. */
  snapshots?: readonly string[];
}

/** Runs each workflow on each snapshot under the spies; no findings means the guard passes. */
export async function exerciseWorkflows(
  workflows: readonly Workflow[],
  options: ExerciseOptions,
): Promise<string[]> {
  const findings: string[] = [];
  const snapshots = options.snapshots ?? SNAPSHOT_NAMES;

  for (const workflow of workflows) {
    const recipe = options.recipes[workflow.id];
    if (recipe === undefined) {
      findings.push(
        `'${workflow.id}': the guard has not been told how to run it (add a recipe for it)`,
      );
      continue;
    }
    for (const fixture of workflow.fixtures) {
      if (!SNAPSHOT_NAMES.includes(fixture)) {
        findings.push(`'${workflow.id}': lists the fixture '${fixture}', which is no snapshot`);
      }
    }

    for (const snapshot of snapshots) {
      const clock = fakeClock();
      const page = loadSnapshot(snapshot, (recipe.address ?? snapshotAddress)(snapshot), clock);
      const watching = watch(document, forbiddenControls(document));
      let result;
      try {
        result = await runWorkflow(workflow, page, recipe.values, { clock, pollMs: 1000 });
      } finally {
        watching.stop();
      }
      const where = `'${workflow.id}' on ${snapshot}`;

      for (const activation of watching.activations) {
        const permitted =
          activation.exception !== null && (recipe.exceptions ?? []).includes(activation.exception);
        if (!permitted) {
          findings.push(`${where}: ${activation.event} reached ${activation.words}`);
        }
      }
      for (const event of watching.anywhere) {
        findings.push(`${where}: ${event}`);
      }
      if (!result.ok && result.failure.kind === 'refused') {
        findings.push(
          `${where}: step '${result.failure.step}' tried a forbidden control (${result.failure.expected})`,
        );
      }
      if (workflow.fixtures.includes(snapshot)) {
        const verified = new Set(
          result.log
            .filter((entry) => entry.phase === 'verify' && entry.outcome === 'ok')
            .map((entry) => entry.step),
        );
        const missed = workflow.steps.filter((step) => !verified.has(step.name));
        if (!result.ok || missed.length > 0) {
          findings.push(
            `${where}: not every step ran on its own fixture (missed ${missed
              .map((step) => `'${step.name}'`)
              .join(', ')})`,
          );
        }
      }
    }
  }
  return findings;
}

function isWorkflow(value: unknown): value is Workflow {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as Workflow).id === 'string' &&
    Array.isArray((value as Workflow).steps) &&
    Array.isArray((value as Workflow).fixtures)
  );
}

/**
 * The registry's completeness: every module under `adapter/workflows/` exports a workflow, every
 * exported workflow is registered, every registered one comes from a module, the registry
 * accepts them all (steps, needs, and fixtures present), and every recipe is for one of them.
 */
export function registryProblems(
  modules: Readonly<Record<string, Readonly<Record<string, unknown>>>>,
  registered: readonly Workflow[],
  recipes: Readonly<Record<string, RunRecipe>>,
): string[] {
  const problems: string[] = [];
  const exported = new Set<Workflow>();
  for (const [path, members] of Object.entries(modules)) {
    const workflows = Object.values(members).filter(isWorkflow);
    if (workflows.length === 0) {
      problems.push(`${path} exports no workflow`);
    }
    for (const workflow of workflows) {
      exported.add(workflow);
      if (!registered.includes(workflow)) {
        problems.push(`${path}: '${workflow.id}' is not in ADAPTER_WORKFLOWS`);
      }
    }
  }
  for (const workflow of registered) {
    if (!exported.has(workflow)) {
      problems.push(`'${workflow.id}' is registered but no module under adapter/workflows/ has it`);
    }
  }
  try {
    new WorkflowRegistry(registered);
  } catch (error) {
    problems.push(error instanceof Error ? error.message : String(error));
  }
  const ids = new Set(registered.map((workflow) => workflow.id));
  for (const id of Object.keys(recipes)) {
    if (!ids.has(id)) {
      problems.push(`the recipe '${id}' is for no registered workflow`);
    }
  }
  return problems;
}
