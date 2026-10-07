import { sunoCreateAddress, sunoPageOf } from '../adapter/addresses.ts';
import type { Clock } from '../adapter/clock.ts';
import { PAGE_RETRIES, PAGE_WAIT_MS, type Observations } from '../adapter/libraryReader.ts';
import type { ObservedMessage } from '../adapter/observed.ts';
import type { Page } from '../adapter/primitives.ts';
import type { AdapterSession } from '../adapter/registry.ts';
import { expected, failureText, OK, type Check, type RunResult } from '../adapter/workflow.ts';
import {
  createWorkspace,
  moreWorkspaces,
  openWorkspaces,
  selectWorkspace,
} from '../adapter/workflows/workspace.ts';
import {
  createdWorkspaceOf,
  MAXIMUM_WORKSPACE_PAGES,
  namedLike,
  workspaceNameFor,
  workspaceOfFeed,
  workspaceOptions,
  WorkspaceList,
  type ListedWorkspace,
  type WorkspaceOption,
} from '../adapter/workspaces.ts';
import type {
  ChosenWorkspace,
  GenerateJob,
  GenerateReply,
  GenerateState,
  Request,
} from '../messages.ts';
import type { GenerateViewState } from '../panel/GenerateView.ts';

/**
 * The Suno tab's half of Generate on Suno (#145): on each page load it asks the service worker
 * whether this tab is working on a request, and if so gets the Song's workspace selected on Create.
 * Before each step the service worker reads the request and reports the step; a request that is no
 * longer active stops the tab where it is. It never starts anything by itself.
 *
 * - The Song has a workspace that Suno's complete list holds: it is selected by its row, found by
 *   the name Suno lists for its ID, and the page must then load that workspace's songs.
 * - The Song has none, or Suno's complete list does not hold it (which, reported, makes n8Tracks
 *   mark it Unavailable): the panel offers to create a workspace named after the Song or to use one
 *   Suno has. Nothing is created until the user chooses; waiting counts towards the request's hour.
 * - Not signed in to Suno (no profile menu, or no Create page after loading it twice): it stops
 *   and says so.
 */

/** The steps reported to n8Tracks, a fixed list (the Version page shows them). */
export const GENERATE_STEPS = {
  open: 'open Suno',
  signIn: 'check sign-in',
  list: 'read workspace list',
  select: 'select workspace',
  choose: 'choose workspace',
  create: 'create workspace',
  selected: 'workspace selected',
} as const;

export type GenerateStep = (typeof GENERATE_STEPS)[keyof typeof GENERATE_STEPS];

export const NOT_SIGNED_IN =
  'You are not signed in to Suno. Sign in, then start Generate on Suno again from n8Tracks.';
export const NO_CREATE_PAGE =
  'Suno did not show its Create page. If you are not signed in to Suno, sign in, then start Generate on Suno again from n8Tracks.';
export const LIST_NOT_READ =
  "Suno's workspace list did not load completely, so nothing was chosen or marked. Try again.";
export const SAME_NAME =
  "Suno has more than one workspace with the name of the Song's workspace, so the extension cannot tell which row is the Song's. Rename one of them in Suno, then try again.";

/** How many loads of the tab may go to reaching the Create page. */
export const MAXIMUM_LOADS = 2;

/** How long a new workspace may take to show as selected by itself before its row is pressed. */
export const SELECTED_BY_ITSELF_MS = 5_000;

/** How the panel names each step while it runs. */
const STEP_TEXT: Readonly<Record<GenerateStep, string>> = {
  'open Suno': 'Opening Suno’s Create page',
  'check sign-in': 'Checking that you are signed in to Suno',
  'read workspace list': 'Reading Suno’s workspace list',
  'select workspace': 'Selecting the Song’s workspace',
  'choose workspace': 'Waiting for your choice',
  'create workspace': 'Creating the Song’s workspace',
  'workspace selected': 'The Song’s workspace is selected',
};

export interface SunoGenerateOptions {
  page: Page;
  session: AdapterSession;
  observations: Observations;
  send: (request: Request) => Promise<unknown>;
  /** Shows a state in the panel's Generate on Suno, opening the panel. */
  show: (state: GenerateViewState) => void;
  clock?: Clock;
}

type Choice = { kind: 'create' } | { kind: 'pick'; option: WorkspaceOption };

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** A generate answer, or a failure in plain words when the service worker gave none. */
function reply<T extends object>(answer: unknown): GenerateReply<T> {
  if (isRecord(answer) && typeof answer.ok === 'boolean') {
    return answer as GenerateReply<T>;
  }
  return {
    ok: false,
    ended: false,
    message: 'The extension did not answer. Reload the Suno page.',
  };
}

export class SunoGenerate {
  private readonly options: SunoGenerateOptions;
  private step: GenerateStep = GENERATE_STEPS.open;
  private waiting: ((choice: Choice) => void) | null = null;

  constructor(options: SunoGenerateOptions) {
    this.options = options;
  }

  /** Whether the panel is offering the choice of workspace. */
  get choosing(): boolean {
    return this.waiting !== null;
  }

  /** "Create a workspace named …" in the panel. */
  create(): void {
    this.answer({ kind: 'create' });
  }

  /** "Use …" in the panel. */
  pick(option: WorkspaceOption): void {
    this.answer({ kind: 'pick', option });
  }

  private answer(choice: Choice): void {
    const waiting = this.waiting;
    this.waiting = null;
    waiting?.(choice);
  }

  /** On every page load: does this tab's part, if the tab is working on a request. */
  async resume(): Promise<void> {
    let answer: unknown;
    try {
      answer = await this.options.send({ type: 'generate-resume' });
    } catch {
      return;
    }
    const job =
      isRecord(answer) && isRecord(answer.job) ? (answer.job as unknown as GenerateJob) : null;
    if (job === null) {
      return;
    }
    const { page } = this.options;
    if (sunoPageOf(page.address()) !== 'create') {
      if (job.loads <= MAXIMUM_LOADS) {
        this.options.show({ kind: 'working', step: STEP_TEXT[GENERATE_STEPS.open] });
        page.go(sunoCreateAddress());
        return;
      }
      await this.stop(NO_CREATE_PAGE);
      return;
    }
    await this.run(job);
  }

  private async run(job: GenerateJob): Promise<void> {
    if (!(await this.begin(GENERATE_STEPS.signIn))) {
      return;
    }
    const opened = await this.options.session.run(openWorkspaces, {}, this.runOptions());
    if (!opened.ok) {
      await this.stop(
        opened.failure.step === 'signed in' ? NOT_SIGNED_IN : failureText(opened.failure),
      );
      return;
    }

    if (!(await this.begin(GENERATE_STEPS.list))) {
      return;
    }
    const list = await this.readList();
    if (list === null) {
      await this.stop(LIST_NOT_READ);
      return;
    }
    const listed = list.workspaces();
    // Suno's complete list goes to n8Tracks: renames follow, and a workspace it no longer lists
    // (the Song's among them) becomes Unavailable there.
    const reported = reply<{ songCounts: Record<string, number> }>(
      await this.options
        .send({ type: 'generate-workspaces', workspaces: listed.map((item) => item.raw) })
        .catch(() => undefined),
    );
    if (!reported.ok) {
      await this.stop(reported.message);
      return;
    }

    const own = job.workspace;
    const found = own === null ? undefined : listed.find((item) => item.id === own.sunoId);
    if (found !== undefined) {
      await this.select(found, listed);
      return;
    }
    const counts = new Map(Object.entries(reported.songCounts));
    await this.offer(
      job,
      listed,
      counts,
      own === null ? 'none' : 'unavailable',
      own?.sunoId ?? null,
    );
  }

  /** The panel offers the choice, and the request waits for it. */
  private async offer(
    job: GenerateJob,
    listed: readonly ListedWorkspace[],
    counts: ReadonlyMap<string, number>,
    reason: 'none' | 'unavailable',
    exclude: string | null,
  ): Promise<void> {
    if (!(await this.begin(GENERATE_STEPS.choose))) {
      return;
    }
    const choice = await new Promise<Choice>((resolve) => {
      this.waiting = resolve;
      this.options.show({
        kind: 'choose',
        title: job.songTitle,
        reason,
        options: workspaceOptions(listed, job.songTitle, counts, exclude),
      });
    });
    if (choice.kind === 'pick') {
      const picked = listed.find((item) => item.id === choice.option.id);
      if (picked === undefined) {
        return;
      }
      if (!(await this.save({ sunoId: picked.id, name: picked.name, how: 'picked' }))) {
        return;
      }
      await this.select(picked, listed);
      return;
    }
    await this.createFor(job, listed);
  }

  /** Creates a workspace named after the Song through Suno's own row, then records and selects it. */
  private async createFor(job: GenerateJob, listed: readonly ListedWorkspace[]): Promise<void> {
    if (!(await this.begin(GENERATE_STEPS.create))) {
      return;
    }
    const name = workspaceNameFor(job.songTitle);
    const watching = this.watch('workspace-created', () => true, 30_000);
    const result = await this.options.session.run(
      createWorkspace,
      {
        workspaceName: name,
        created: () =>
          watching.seen !== null && createdWorkspaceOf(watching.seen) !== null
            ? OK
            : expected('Suno to answer with the new workspace and its ID'),
      },
      this.runOptions(),
    );
    watching.stop();
    const created = watching.seen === null ? null : createdWorkspaceOf(watching.seen);
    if (!result.ok || created === null) {
      await this.stop(
        result.ok ? 'Suno did not answer with the new workspace.' : failureText(result.failure),
      );
      return;
    }
    const workspace: ListedWorkspace = {
      ...created,
      name: created.name === '' ? name : created.name,
    };
    // Created once; only the report is sent again if it fails on the way.
    if (!(await this.save({ sunoId: workspace.id, name: workspace.name, how: 'created' }))) {
      return;
    }
    // Suno may select the new workspace by itself; if not, its row is pressed.
    const shown = this.watch(
      'library-feed',
      (message) => workspaceOfFeed(message) === workspace.id,
      SELECTED_BY_ITSELF_MS,
    );
    await shown.done;
    if (shown.seen !== null) {
      await this.selected(workspace);
      return;
    }
    await this.select(workspace, [...listed, workspace]);
  }

  /** Selects `workspace` by its row in the open list; the page must then show its songs. */
  private async select(
    workspace: ListedWorkspace,
    listed: readonly ListedWorkspace[],
  ): Promise<void> {
    if (!(await this.begin(GENERATE_STEPS.select))) {
      return;
    }
    // A workspace of the same name and another ID is never selected in its place.
    if (namedLike(listed, workspace.name).length > 1) {
      await this.stop(SAME_NAME);
      return;
    }
    // The library pane already shows it (the page loaded with it selected): nothing to press.
    if ((await this.shownWorkspace()) === workspace.id) {
      await this.selected(workspace);
      return;
    }
    const shown = this.watch(
      'library-feed',
      (message) => workspaceOfFeed(message) === workspace.id,
      20_000,
    );
    const result: RunResult = await this.options.session.run(
      selectWorkspace,
      {
        workspaceName: workspace.name,
        shown: (): Check =>
          shown.seen !== null ? OK : expected("Suno's library pane to show the workspace's songs"),
      },
      this.runOptions(),
    );
    shown.stop();
    if (!result.ok) {
      await this.stop(failureText(result.failure));
      return;
    }
    await this.selected(workspace);
  }

  private async selected(workspace: ListedWorkspace): Promise<void> {
    if (await this.begin(GENERATE_STEPS.selected)) {
      this.options.show({ kind: 'selected', name: workspace.name });
    }
  }

  /**
   * The workspace whose songs the library pane asked for last, from the requests seen so far (they
   * are taken), or null when it asked for none.
   */
  private async shownWorkspace(): Promise<string | null> {
    const signal = new AbortController().signal;
    let shown: string | null = null;
    for (;;) {
      const message = await this.options.observations.next('library-feed', () => true, 0, signal);
      if (message === null) {
        return shown;
      }
      shown = workspaceOfFeed(message);
    }
  }

  /**
   * Reads Suno's whole workspace list as the page loads it: pages already seen are taken, then the
   * list is scrolled for each next page. Null when a page does not come.
   */
  private async readList(): Promise<WorkspaceList | null> {
    const { observations } = this.options;
    const list = new WorkspaceList();
    const signal = new AbortController().signal;
    const take = async (timeoutMs: number) => {
      const message = await observations.next('workspaces', () => true, timeoutMs, signal);
      return message !== null && list.add(message);
    };
    while (await take(0)) {
      // Every page the page already asked for.
    }
    for (let pages = 0; !list.complete; pages += 1) {
      if (pages > MAXIMUM_WORKSPACE_PAGES) {
        return null;
      }
      let arrived = list.pagesInOrder === 0 ? await take(PAGE_WAIT_MS) : false;
      for (let attempt = 0; !arrived && attempt <= PAGE_RETRIES; attempt += 1) {
        const scrolled = await this.options.session.run(moreWorkspaces, {}, this.runOptions());
        if (!scrolled.ok) {
          return null;
        }
        arrived = await take(PAGE_WAIT_MS);
      }
      if (!arrived) {
        return null;
      }
      while (await take(0)) {
        // Any further page that came with it.
      }
    }
    return list;
  }

  /**
   * Watches for a response of `kind` that `accept` takes, from now on, for `timeoutMs`; `seen` is
   * it once it came.
   */
  private watch(
    kind: ObservedMessage['kind'],
    accept: (message: ObservedMessage) => boolean,
    timeoutMs: number,
  ): { seen: ObservedMessage | null; done: Promise<void>; stop(): void } {
    const controller = new AbortController();
    const watching = {
      seen: null as ObservedMessage | null,
      done: Promise.resolve(),
      stop: () => {
        controller.abort();
      },
    };
    watching.done = this.options.observations
      .next(kind, accept, timeoutMs, controller.signal)
      .then((message) => {
        watching.seen = message;
      })
      .catch(() => undefined);
    return watching;
  }

  /** Records the user's choice as the Song's workspace; false (and stopped) when it was not. */
  private async save(workspace: ChosenWorkspace): Promise<boolean> {
    this.options.show({ kind: 'working', step: 'Saving the workspace for the Song in n8Tracks' });
    const saved = reply(
      await this.options.send({ type: 'generate-resolve', workspace }).catch(() => undefined),
    );
    if (!saved.ok) {
      await this.stop(
        workspace.how === 'created'
          ? `The workspace was created in Suno, but n8Tracks did not record it for the Song (${saved.message}). Choose it as an existing workspace next time.`
          : saved.message,
        saved.ended,
      );
      return false;
    }
    return true;
  }

  /**
   * A step begins: the service worker reads the request and reports the step. False, with the
   * panel saying why, when the request is over or n8Tracks cannot be reached.
   */
  private async begin(step: GenerateStep, state: GenerateState = 'workspace'): Promise<boolean> {
    this.step = step;
    if (step !== GENERATE_STEPS.choose) {
      this.options.show({ kind: 'working', step: STEP_TEXT[step] });
    }
    const answer = reply(
      await this.options.send({ type: 'generate-progress', state, step }).catch(() => undefined),
    );
    if (!answer.ok) {
      this.options.show({ kind: 'stopped', message: answer.message });
      return false;
    }
    return true;
  }

  /** Stops the request at the current step, saying why, in n8Tracks and in the panel. */
  private async stop(message: string, ended = false): Promise<void> {
    this.options.show({ kind: 'stopped', message });
    if (ended) {
      return;
    }
    await this.options
      .send({ type: 'generate-progress', state: 'stopped', step: this.step, message })
      .catch(() => undefined);
  }

  private runOptions() {
    return this.options.clock === undefined ? {} : { clock: this.options.clock };
  }
}
