import type { Page } from './primitives.ts';
import {
  expected,
  failureText,
  runWorkflow,
  type Check,
  type Feature,
  type RunOptions,
  type RunResult,
  type StepContext,
  type StepFailure,
  type StepLogEntry,
  type Workflow,
} from './workflow.ts';

/**
 * The workflows the adapter knows, and what the panel shows for each. Workflows are independent:
 * each declares what it needs from the page, the self-check reads those needs one workflow at a
 * time, and one that fails, or throws, leaves the others' states alone.
 */

/** `ready`; `not-working`, with the failing step; or `not-checked` on a page it does not start on. */
export type WorkflowState = 'ready' | 'not-working' | 'not-checked';

export interface WorkflowStatus {
  id: string;
  title: string;
  feature: Feature;
  state: WorkflowState;
  /** "any suno.com page": where it starts, for a `not-checked` workflow. */
  startsOn: string;
  /** The failing step and the report line, when `not-working`. */
  step: string | null;
  message: string | null;
  /** True when the state comes from a run that stopped, not from the self-check. */
  stopped: boolean;
}

function readNeed(check: () => Check): Check {
  try {
    return check();
  } catch {
    return expected('the page to be readable');
  }
}

/** Reads one workflow's needs on `page`, once each, without clicking anything. */
export function checkWorkflow(workflow: Workflow, page: Page): WorkflowStatus {
  const base = {
    id: workflow.id,
    title: workflow.title,
    feature: workflow.feature,
    startsOn: workflow.startsOn.description,
    stopped: false,
  };
  let address: URL;
  try {
    address = page.address();
  } catch {
    return { ...base, state: 'not-checked', step: null, message: null };
  }
  if (!workflow.startsOn.matches(address)) {
    return { ...base, state: 'not-checked', step: null, message: null };
  }
  for (const need of workflow.needs) {
    const result = readNeed(() => need.check(page));
    if (!result.ok) {
      return {
        ...base,
        state: 'not-working',
        step: need.step,
        message: failureText({
          workflowId: workflow.id,
          workflow: workflow.title,
          step: need.step,
          phase: 'expect',
          kind: 'check',
          expected: result.expected,
          pageMayBeChanged: false,
        }),
      };
    }
  }
  return { ...base, state: 'ready', step: null, message: null };
}

export class WorkflowRegistry {
  private readonly workflows = new Map<string, Workflow>();

  constructor(workflows: readonly Workflow[] = []) {
    for (const workflow of workflows) {
      this.register(workflow);
    }
  }

  /** Adds a workflow. Its ID must be new, and it must have steps, needs, and its snapshots. */
  register(workflow: Workflow): void {
    if (this.workflows.has(workflow.id)) {
      throw new Error(`A workflow '${workflow.id}' is already registered.`);
    }
    if (workflow.steps.length === 0 || workflow.needs.length === 0) {
      throw new Error(`The workflow '${workflow.id}' has no steps or declares no needs.`);
    }
    if (workflow.fixtures.length === 0) {
      throw new Error(`The workflow '${workflow.id}' names no page snapshot it was built on.`);
    }
    const names = workflow.steps.map((step) => step.name);
    if (new Set(names).size !== names.length) {
      throw new Error(`The workflow '${workflow.id}' has two steps of the same name.`);
    }
    this.workflows.set(workflow.id, workflow);
  }

  all(): readonly Workflow[] {
    return [...this.workflows.values()];
  }

  /** The self-check: each workflow's state on `page`, one at a time. */
  check(page: Page): WorkflowStatus[] {
    return this.all().map((workflow) => checkWorkflow(workflow, page));
  }
}

/** What the adapter remembers of the last run of a workflow, in memory only, until a reload. */
interface LastRun {
  failure: StepFailure | null;
  log: StepLogEntry[];
}

/**
 * The adapter on one Suno tab: the registered workflows, the page, and the last run of each.
 * The panel shows `statuses()`: a workflow whose last run stopped shows that stop until it is
 * run again or the page reloads; the others show the self-check.
 */
export class AdapterSession {
  private readonly registry: WorkflowRegistry;
  private readonly page: Page;
  private readonly lastRuns = new Map<string, LastRun>();

  constructor(registry: WorkflowRegistry, page: Page) {
    this.registry = registry;
    this.page = page;
  }

  statuses(): WorkflowStatus[] {
    return this.registry.check(this.page).map((status) => {
      const failure = this.lastRuns.get(status.id)?.failure ?? null;
      return failure === null
        ? status
        : {
            ...status,
            state: 'not-working',
            step: failure.step,
            message: failureText(failure),
            stopped: true,
          };
    });
  }

  /** Runs a workflow and remembers how it ended. */
  async run<C extends StepContext>(
    workflow: Workflow<C>,
    values: Omit<C, keyof StepContext>,
    options: RunOptions = {},
  ): Promise<RunResult> {
    const result = await runWorkflow(workflow, this.page, values, options);
    this.lastRuns.set(workflow.id, {
      failure: result.ok ? null : result.failure,
      log: result.log,
    });
    return result;
  }

  /** The step log of the last run of a workflow, for the diagnostic report. */
  stepLog(workflowId: string): readonly StepLogEntry[] {
    return this.lastRuns.get(workflowId)?.log ?? [];
  }
}
