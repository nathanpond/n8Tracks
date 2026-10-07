import {
  songOfAddress,
  sunoCreateAddress,
  sunoPageOf,
  sunoSongAddress,
} from '../adapter/addresses.ts';
import { realClock, type Clock } from '../adapter/clock.ts';
import { verificationReport, type EntryResult, type FormJob } from '../adapter/fill.ts';
import { isFinished, watchedClipsOf } from '../adapter/finished.ts';
import { PAGE_RETRIES, PAGE_WAIT_MS, type Observations } from '../adapter/libraryReader.ts';
import type { ObservedMessage } from '../adapter/observed.ts';
import type { Page } from '../adapter/primitives.ts';
import type { AdapterSession } from '../adapter/registry.ts';
import {
  OVERWRITE_DIALOG,
  planSources,
  sourceName,
  sourceShown,
  type LoadedSource,
} from '../adapter/sources.ts';
import { ADAPTER_VERSION } from '../adapter/version.ts';
import { expected, failureText, OK, type Check, type RunResult } from '../adapter/workflow.ts';
import type { FillContext } from '../adapter/workflows/fillSongs.ts';
import { formWorkflowsFor } from '../adapter/workflows/index.ts';
import {
  answerOverwrite,
  chooseSourceAction,
  openSourceMenu,
  verifySourceAdvanced,
  verifySourceSimple,
} from '../adapter/workflows/sources.ts';
import { refreshLibrary } from '../adapter/workflows/watchCompletion.ts';
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
  SourcePhase,
  GenerateReply,
  GenerateState,
  ObservedSummary,
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
 *
 * With the workspace selected, it fills the Create form (#146 Songs, #147 Speech and Sounds), with
 * the workflows for the request's kind and mode (`FORM_WORKFLOWS`): the kind's tab and the
 * Version's mode, always first (a Song's source applies only in the mode active when its action was
 * chosen), then the source when the kind loads one (#148), then every entry of the mode, each read
 * back. A kind the form does not offer
 * (its tab missing) stops before anything is filled, at the step that opens the form. The panel shows the verification summary, which goes
 * to n8Tracks, and the request waits for the user to review the form and click Create. The
 * extension never clicks Create (invariant 4). Check again in the panel reads every entry again
 * without changing anything; when the form has gone, the request stops.
 *
 * While the request waits, the tab watches for the user's own Create click (#149): the page observer
 * passes on Suno's answer and the values the page sent, which go to n8Tracks to be recorded as
 * Generations; the panel says what each came to. Every further Create on the form is recorded the
 * same way. An answer not as expected sends nothing, and the panel and the request say the clips were
 * not recorded and that a sync will bring them in. Leaving the Create page, or 30 minutes after the
 * last Create (an hour before the first), ends the watch; once a Create was recorded, leaving the page
 * ends the request (done), and a load of the tab never fills the form again.
 *
 * A Version with a source the extension can load (#148, `adapter/sources.ts`): after the mode is
 * chosen, the tab goes to the source clip's page, opens its menu and chooses the action, and Suno
 * opens the Create form with the source (in the same page, or in a new load that carries on from
 * the service worker's record of the phase). Suno's Overwrite question is answered Overwrite; the
 * source is verified before anything else is filled. When it is not the Version's, the panel asks
 * the user to load it by hand and press Continue, which verifies it again, as often as needed. A
 * source n8Tracks knows is unusable stops the request before the form changes, naming it.
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
  form: 'open Songs form',
  chooseForm: 'choose form',
  fill: 'fill form',
  review: 'review and create',
  check: 'check form',
  record: 'record Create',
  left: 'left the Create page',
  source: 'open source',
  action: 'choose source action',
  verifySource: 'verify source',
  byHand: 'load source by hand',
} as const;

export type GenerateStep = (typeof GENERATE_STEPS)[keyof typeof GENERATE_STEPS];

export const NOT_SIGNED_IN =
  'You are not signed in to Suno. Sign in, then start Generate on Suno again from n8Tracks.';
export const NO_CREATE_PAGE =
  'Suno did not show its Create page. If you are not signed in to Suno, sign in, then start Generate on Suno again from n8Tracks.';
export const LIST_NOT_READ =
  "Suno's workspace list did not load completely, so nothing was chosen or marked. Try again.";
export const NO_FORM =
  'The extension cannot fill the Create form for this kind of Version. Fill it by hand in Suno, then click Create.';
export const FORM_GONE =
  'The form is no longer on this page, so it could not be checked. Start Generate on Suno again from n8Tracks.';
export const NOT_EXPECTED =
  'Suno’s answer to Create was not as expected, so its clips were not recorded in n8Tracks; a sync will bring them in.';
export const LEFT_CREATE =
  'You left Suno’s Create page, so the extension stopped watching for Creates.';
export const SAME_NAME =
  "Suno has more than one workspace with the name of the Song's workspace, so the extension cannot tell which row is the Song's. Rename one of them in Suno, then try again.";

/** The source's page in Suno did not show the clip's menu (#148). */
export function couldNotOpen(name: string, problem: string): string {
  return `Suno did not open the source ${name}: ${problem}. It may be in Suno’s Trash, deleted, or not yours. Nothing on the form was changed beyond its mode; load the source by hand, or fix it in n8Tracks and start again.`;
}

/** The action is missing from the clip's menu, or disabled there (#148). */
export function actionUnavailable(name: string, action: string): string {
  return `Suno does not offer ${action} for the source ${name} (the menu item is missing or disabled; it may need a plan you lack). Nothing on the form was changed beyond its mode.`;
}

/** After the action, Suno did not show the Create form (#148). */
export function noFormAfter(name: string, action: string): string {
  return `Suno did not open the Create form after ${action} on the source ${name}. Start Generate on Suno again from n8Tracks.`;
}

/** How long Suno may take to ask "Overwrite Lyrics & Styles?" after an action with no source to see. */
export const OVERWRITE_WAIT_MS = 3_000;

/** How long Suno may take to show the source, or to ask first, after the action. */
export const SOURCE_WAIT_MS = 10_000;

/** How many loads of the tab may go to reaching the Create page. */
export const MAXIMUM_LOADS = 2;

/** How long a new workspace may take to show as selected by itself before its row is pressed. */
export const SELECTED_BY_ITSELF_MS = 5_000;

/** How often the watch for the user's Create looks at the page's address again (#149). */
export const CREATE_POLL_MS = 1_000;

/** How long the watch goes on after the last recorded Create (n8Tracks ends the request then too). */
export const AFTER_LAST_CREATE_MS = 30 * 60_000;

/** How long the watch goes on before the first Create (n8Tracks lets a request idle an hour). */
export const BEFORE_FIRST_CREATE_MS = 60 * 60_000;

/**
 * How long the completion watch waits for a feed answer naming an unfinished clip before it prompts
 * the page to read its library pane again (#154).
 */
export const COMPLETION_PROMPT_MS = 15_000;

/**
 * The longest the tab watches for clips to finish (#154): the service worker ends each Create's watch
 * after ten minutes (an alarm); the tab stops by itself a minute later should it not hear so.
 */
export const COMPLETION_LIMIT_MS = 11 * 60_000;

/** How the panel names each step while it runs. */
const STEP_TEXT: Readonly<Record<GenerateStep, string>> = {
  'open Suno': 'Opening Suno’s Create page',
  'check sign-in': 'Checking that you are signed in to Suno',
  'read workspace list': 'Reading Suno’s workspace list',
  'select workspace': 'Selecting the Song’s workspace',
  'choose workspace': 'Waiting for your choice',
  'create workspace': 'Creating the Song’s workspace',
  'workspace selected': 'The Song’s workspace is selected',
  'open Songs form': 'Opening the Songs form',
  'choose form': 'Opening the form for the Version',
  'fill form': 'Filling the form from the Version',
  'review and create': 'Review the form, then click Create',
  'check form': 'Checking the form again',
  'record Create': 'Recording your Create in n8Tracks',
  'left the Create page': 'You left the Create page',
  'open source': 'Opening the source clip in Suno',
  'choose source action': 'Choosing the Suno action for the source',
  'verify source': 'Checking the source on the Create form',
  'load source by hand': 'Waiting for you to load the source',
};

export interface SunoGenerateOptions {
  page: Page;
  session: AdapterSession;
  observations: Observations;
  send: (request: Request) => Promise<unknown>;
  /** Shows a state in the panel's Generate on Suno, opening the panel. */
  show: (state: GenerateViewState) => void;
  clock?: Clock;
  /** The time a summary is checked at; the real time unless a test stands in. */
  now?: () => Date;
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
  /** The request this tab works on, once the service worker has given it. */
  private job: GenerateJob | null = null;
  /** What the form was filled from, for Check again; null until a fill has run. */
  private filled: { form: FormJob; workspace: string | null } | null = null;
  /** Whether the watch for the user's Create is running (#149). */
  private watching = false;
  /** How many Creates n8Tracks has recorded for the request. */
  private created = 0;
  /** Waiting for the user to load the source by hand and press Continue (#148). */
  private waitingSource: (() => void) | null = null;
  /** Whether the completion watch is running (#154), and the Suno IDs it watches. */
  private completing = false;
  private watchedClips = new Set<string>();

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

  /** Whether the completion watch is running (#154). */
  get watchingCompletion(): boolean {
    return this.completing;
  }

  /** Whether the panel is waiting for the user to load the source by hand. */
  get waitingForSource(): boolean {
    return this.waitingSource !== null;
  }

  /** "Continue" in the panel: the source the user loaded by hand is checked again. */
  continueSource(): void {
    const waiting = this.waitingSource;
    this.waitingSource = null;
    waiting?.();
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
    if (job !== null) {
      this.job = job;
    }
    // Clips of the user's Creates this tab still watches for completion (#154), with or without a
    // request: the watch outlives the request when the user leaves the Create page.
    const watching =
      isRecord(answer) && Array.isArray(answer.watching)
        ? answer.watching.filter((id): id is string => typeof id === 'string')
        : [];
    void this.watchCompletion(watching);
    if (job === null) {
      return;
    }
    const { page } = this.options;
    this.created = job.created ?? 0;
    if (this.created > 0) {
      // A Create was recorded: the form is never filled again. On it, further Creates are watched for.
      if (sunoPageOf(page.address()) === 'create') {
        this.options.show({ kind: 'recorded', recorded: true, message: this.createdText() });
        void this.watchCreates();
      } else {
        await this.leave();
      }
      return;
    }
    if (job.source !== undefined && job.source !== null && job.form !== null) {
      await this.resumeSource(job.form, job.source);
      return;
    }
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
      await this.fill(workspace.name);
    }
  }

  /**
   * Fills the Create form from the request (#146) and reports the summary; the request then waits
   * for the user. A request with no form to fill (its snapshot unreadable) ends here. A source the
   * extension can load is loaded first, in the Version's mode (#148).
   */
  private async fill(workspace: string): Promise<void> {
    const form = this.job?.form ?? null;
    if (form === null) {
      return;
    }
    const workflows = formWorkflowsFor(form.kind, form.mode);
    if (workflows === null) {
      if (await this.begin(GENERATE_STEPS.chooseForm, 'filling')) {
        await this.stop(NO_FORM);
      }
      return;
    }
    const opening = form.kind === 'song' ? GENERATE_STEPS.form : GENERATE_STEPS.chooseForm;
    if (!(await this.begin(opening, 'filling'))) {
      return;
    }
    // A source n8Tracks knows is unusable stops the request before anything on the form changes.
    const plan = planSources(form);
    if (plan.stop !== null) {
      await this.stop(plan.stop);
      return;
    }
    // The kind's form and the Version's mode always come first: a source applies only in the mode
    // active when its action was chosen (TS-002, #148), so a Song with sources is switched too.
    const switched = await this.options.session.run(
      workflows.open,
      { mode: form.mode },
      this.runOptions(),
    );
    if (!switched.ok) {
      await this.stop(failureText(switched.failure));
      return;
    }
    if (workflows.loadsSources && plan.load !== null) {
      await this.openSource(plan.load);
      return;
    }
    await this.fillRest(form, workspace, null);
  }

  /** Fills every entry of the mode, with the loaded source's outcome, and reports the summary. */
  private async fillRest(
    form: FormJob,
    workspace: string | null,
    loaded: EntryResult | null,
  ): Promise<void> {
    if (!(await this.begin(GENERATE_STEPS.fill, 'filling'))) {
      return;
    }
    const workflows = formWorkflowsFor(form.kind, form.mode);
    if (workflows === null) {
      await this.stop(NO_FORM);
      return;
    }
    const job: FormJob = loaded === null ? form : { ...form, loaded };
    const context: Omit<FillContext, 'page' | 'signal'> = { job, workspace, results: [] };
    const filling = await this.options.session.run(workflows.fill, context, this.runOptions());
    if (!filling.ok) {
      await this.stop(failureText(filling.failure));
      return;
    }
    this.filled = { form: job, workspace };
    await this.review(GENERATE_STEPS.review, job, context.results);
  }

  /** Records the phase for the next page load, then goes to the source clip's page (#148). */
  private async openSource(load: LoadedSource): Promise<void> {
    if (!(await this.begin(GENERATE_STEPS.source, 'filling'))) {
      return;
    }
    if (!(await this.keepSource({ phase: 'opening', sunoId: load.sunoId }))) {
      return;
    }
    this.options.page.go(sunoSongAddress(load.sunoId));
  }

  /** A page load while a source is being loaded: carries on where the last load left off. */
  private async resumeSource(form: FormJob, phase: SourcePhase): Promise<void> {
    const { page } = this.options;
    const load = planSources(form).load;
    const name = load === null ? '“?”' : sourceName(load.source);
    if (load?.sunoId !== phase.sunoId) {
      this.step = GENERATE_STEPS.source;
      await this.stop(
        'The Version’s sources changed while the extension was loading one. Start again from n8Tracks.',
      );
      return;
    }
    if (phase.phase === 'opening') {
      if (songOfAddress(page.address()) === phase.sunoId) {
        await this.chooseAction(form, load);
        return;
      }
      this.step = GENERATE_STEPS.source;
      await this.stop(couldNotOpen(name, 'Suno showed another page instead of the clip’s'));
      return;
    }
    if (sunoPageOf(page.address()) === 'create') {
      await this.afterAction(form, load);
      return;
    }
    this.step = GENERATE_STEPS.action;
    await this.stop(noFormAfter(name, load.route.item));
  }

  /** On the source clip's page: opens its menu and chooses the action (#148). */
  private async chooseAction(form: FormJob, load: LoadedSource): Promise<void> {
    if (!(await this.begin(GENERATE_STEPS.action, 'filling'))) {
      return;
    }
    const name = sourceName(load.source);
    const context = { route: load.route };
    const opened = await this.options.session.run(openSourceMenu, context, this.runOptions());
    if (!opened.ok) {
      await this.stop(
        opened.failure.step === 'action offered'
          ? actionUnavailable(name, load.route.item)
          : couldNotOpen(name, failureText(opened.failure)),
      );
      return;
    }
    // Choosing the action may load the Create page anew: the next load must know it was chosen.
    if (!(await this.keepSource({ phase: 'chosen', sunoId: load.sunoId }))) {
      return;
    }
    const chosen = await this.options.session.run(chooseSourceAction, context, this.runOptions());
    if (!chosen.ok) {
      await this.stop(failureText(chosen.failure));
      return;
    }
    const { page } = this.options;
    if (!(await page.wait(() => sunoPageOf(page.address()) === 'create', PAGE_WAIT_MS))) {
      await this.stop(noFormAfter(name, load.route.item));
      return;
    }
    await this.afterAction(form, load);
  }

  /**
   * On the Create form after the action: Overwrite when Suno asks, then the source verified before
   * anything else is filled; when it is not the Version's, the user loads it by hand and presses
   * Continue, and it is verified again.
   */
  private async afterAction(form: FormJob, load: LoadedSource): Promise<void> {
    if (!(await this.begin(GENERATE_STEPS.verifySource, 'filling'))) {
      return;
    }
    const { page, session } = this.options;
    const seen = () => load.route.label !== null && sourceShown(page, form.mode, load).ok;
    const asked = () => page.find(OVERWRITE_DIALOG).kind === 'found';
    await page.wait(
      () => asked() || seen(),
      load.route.label === null ? OVERWRITE_WAIT_MS : SOURCE_WAIT_MS,
    );
    if (asked()) {
      const answered = await session.run(answerOverwrite, {}, this.runOptions());
      if (!answered.ok) {
        await this.stop(failureText(answered.failure));
        return;
      }
    }
    const name = sourceName(load.source);
    let loaded: EntryResult;
    if (load.route.label === null) {
      // Reuse Prompt copies the source's inputs and leaves no source to see (Discretion).
      loaded = {
        key: load.source.key,
        outcome: 'set',
        note: `${load.route.item} copied the inputs of ${name} into the form; the Version’s own values were filled over them.`,
        reportNote: `${load.route.item} copied the inputs of the source into the form; the Version’s own values were filled over them.`,
      };
    } else {
      const verify = form.mode === 'simple' ? verifySourceSimple : verifySourceAdvanced;
      for (;;) {
        const verified = await session.run(verify, { load }, this.runOptions());
        if (verified.ok) {
          break;
        }
        if (!(await this.byHand(load, failureText(verified.failure)))) {
          return;
        }
      }
      loaded = {
        key: load.source.key,
        outcome: 'verified',
        note:
          form.mode === 'simple'
            ? `${name} is on the form: the source chip’s thumbnail is the source clip’s (Suno’s Simple chip does not name the action).`
            : `${name} is on the form: the Audio section names ${load.route.label} and its thumbnail is the source clip’s.`,
        reportNote:
          form.mode === 'simple'
            ? 'The source is on the form: the source chip’s thumbnail is the source clip’s (Suno’s Simple chip does not name the action).'
            : `The source is on the form: the Audio section names ${load.route.label} and its thumbnail is the source clip’s.`,
      };
    }
    if (!(await this.keepSource(null))) {
      return;
    }
    await this.fillRest(form, this.job?.workspace?.name ?? null, loaded);
  }

  /**
   * The source on the form is not the Version's: the panel asks the user to load it by hand, and
   * the request waits (reported, so n8Tracks shows why). True once the user pressed Continue.
   */
  private async byHand(load: LoadedSource, why: string): Promise<boolean> {
    const message = `The source on Suno’s form is not the Version’s (${why}). Load ${sourceName(load.source)} with ${load.route.menu} › ${load.route.item}, then press Continue.`;
    this.step = GENERATE_STEPS.byHand;
    const answer = reply(
      await this.options
        .send({ type: 'generate-progress', state: 'filling', step: GENERATE_STEPS.byHand, message })
        .catch(() => undefined),
    );
    if (!answer.ok) {
      this.options.show({ kind: 'stopped', message: answer.message });
      return false;
    }
    await new Promise<void>((resolve) => {
      this.waitingSource = resolve;
      this.options.show({ kind: 'source', message });
    });
    return this.begin(GENERATE_STEPS.verifySource, 'filling');
  }

  /** Keeps the source phase for the next page load; false (and stopped) when it could not be. */
  private async keepSource(source: SourcePhase | null): Promise<boolean> {
    const kept = reply(
      await this.options.send({ type: 'generate-source', source }).catch(() => undefined),
    );
    if (!kept.ok) {
      await this.stop(kept.message, kept.ended);
      return false;
    }
    return true;
  }

  /**
   * Check again (the summary's button): every entry read without changing anything; the new
   * summary replaces the reported one. When the form has gone, the request stops.
   */
  async checkAgain(): Promise<void> {
    const filled = this.filled;
    if (filled === null) {
      return;
    }
    this.options.show({ kind: 'working', step: STEP_TEXT[GENERATE_STEPS.check] });
    const context: Omit<FillContext, 'page' | 'signal'> = {
      job: filled.form,
      workspace: filled.workspace,
      results: [],
    };
    const workflows = formWorkflowsFor(filled.form.kind, filled.form.mode);
    const checked =
      workflows === null
        ? null
        : await this.options.session.run(workflows.check, context, this.runOptions());
    if (checked?.ok !== true) {
      this.filled = null;
      await this.stop(FORM_GONE);
      return;
    }
    await this.review(GENERATE_STEPS.check, filled.form, context.results);
  }

  /** Shows the summary and reports it; the request waits for the user's Create. */
  private async review(
    step: GenerateStep,
    form: FormJob,
    results: readonly EntryResult[],
  ): Promise<void> {
    const checkedAt = this.options.now?.() ?? new Date();
    this.options.show({
      kind: 'verification',
      form: form.kind,
      mode: form.mode,
      results,
      checkedAt,
    });
    const verification = await verificationReport(results, form.mode, ADAPTER_VERSION, checkedAt);
    this.step = step;
    const answer = reply(
      await this.options
        .send({ type: 'generate-progress', state: 'waiting', step, verification })
        .catch(() => undefined),
    );
    if (!answer.ok) {
      this.filled = null;
      this.options.show({ kind: 'stopped', message: answer.message });
      return;
    }
    void this.watchCreates();
  }

  /**
   * Watches for the user's own Create click while the tab stays on the Create page (#149): each
   * answer the page observer passes on is recorded in n8Tracks. Never clicks anything. Ends when the
   * page leaves Create, when the request is over, or after the time a request waits.
   */
  private async watchCreates(): Promise<void> {
    if (this.watching) {
      return;
    }
    this.watching = true;
    const clock = this.options.clock ?? realClock;
    const signal = new AbortController().signal;
    let since = clock.now();
    try {
      for (;;) {
        const limit = this.created > 0 ? AFTER_LAST_CREATE_MS : BEFORE_FIRST_CREATE_MS;
        if (clock.now() - since >= limit) {
          return;
        }
        if (sunoPageOf(this.options.page.address()) !== 'create') {
          await this.leave();
          return;
        }
        const message = await this.options.observations.next(
          'create',
          () => true,
          CREATE_POLL_MS,
          signal,
        );
        if (message === null) {
          continue;
        }
        if (!(await this.record(message))) {
          return;
        }
        since = clock.now();
      }
    } finally {
      this.watching = false;
    }
  }

  /**
   * Records one observed Create: Suno's answer must name its request and clips, or nothing is sent and
   * the request says so at its step. False when the request is over.
   */
  private async record(message: ObservedMessage): Promise<boolean> {
    this.step = GENERATE_STEPS.record;
    const response = message.body;
    const clips = isRecord(response) && Array.isArray(response.clips) ? response.clips : [];
    const expectedShape =
      isRecord(response) &&
      typeof response.id === 'string' &&
      response.id !== '' &&
      clips.length > 0 &&
      clips.every((clip) => isRecord(clip) && typeof clip.id === 'string' && clip.id !== '');
    if (!expectedShape) {
      this.options.show({ kind: 'recorded', recorded: false, message: NOT_EXPECTED });
      const answer = reply(
        await this.options
          .send({
            type: 'generate-progress',
            state: 'waiting',
            step: GENERATE_STEPS.record,
            message: NOT_EXPECTED,
          })
          .catch(() => undefined),
      );
      return answer.ok || !answer.ended;
    }
    this.options.show({ kind: 'working', step: STEP_TEXT[GENERATE_STEPS.record] });
    const answer = reply<{ recorded: ObservedSummary }>(
      await this.options
        .send({
          type: 'generate-observed',
          response,
          submitted: message.submitted ?? null,
        })
        .catch(() => undefined),
    );
    if (!answer.ok) {
      this.options.show(
        answer.ended
          ? { kind: 'stopped', message: answer.message }
          : { kind: 'recorded', recorded: false, message: answer.message },
      );
      return !answer.ended;
    }
    this.created += 1;
    this.options.show({ kind: 'recorded', recorded: true, message: answer.recorded.message });
    void this.watchCompletion(
      clips.flatMap((clip) => (isRecord(clip) && typeof clip.id === 'string' ? [clip.id] : [])),
    );
    return true;
  }

  /**
   * Watches for the clips `sunoIds` of the user's Creates to finish (#154), alongside any it already
   * watches: every feed answer the page gets is read for them, and the finished ones go to the service
   * worker, which sends them to n8Tracks; it answers which clips are still watched. When no feed answer
   * has named one for 15 seconds, the Create page is prompted to read its library pane again
   * (`refresh-library`: the Song's workspace row; it changes nothing in Suno). Ends when no clip is
   * watched any more (all finished, or their ten minutes up), or when n8Tracks cannot be told.
   */
  private async watchCompletion(sunoIds: readonly string[]): Promise<void> {
    for (const sunoId of sunoIds) {
      this.watchedClips.add(sunoId);
    }
    if (this.completing || this.watchedClips.size === 0) {
      return;
    }
    this.completing = true;
    const clock = this.options.clock ?? realClock;
    const signal = new AbortController().signal;
    const started = clock.now();
    try {
      while (this.watchedClips.size > 0 && clock.now() - started < COMPLETION_LIMIT_MS) {
        const message = await this.options.observations.next(
          'library-feed',
          (seen) => watchedClipsOf(seen.body, this.watchedClips).length > 0,
          COMPLETION_PROMPT_MS,
          signal,
        );
        if (message === null) {
          await this.promptRefresh();
        }
        const clips =
          message === null
            ? []
            : watchedClipsOf(message.body, this.watchedClips).filter((clip) => isFinished(clip));
        const answer = reply<{ watching: string[] }>(
          await this.options.send({ type: 'generate-completion', clips }).catch(() => undefined),
        );
        if (!answer.ok) {
          return;
        }
        this.watchedClips = new Set(answer.watching);
      }
    } finally {
      this.completing = false;
    }
  }

  /** Prompts the Create page to read its library pane again; on any other page, nothing (#154). */
  private async promptRefresh(): Promise<void> {
    const workspaceName = this.job?.workspace?.name ?? this.filled?.workspace ?? null;
    if (workspaceName === null || sunoPageOf(this.options.page.address()) !== 'create') {
      return;
    }
    await this.options.session.run(refreshLibrary, { workspaceName }, this.runOptions());
  }

  /** The tab left the Create page after a recorded Create: the request is done. */
  private async leave(): Promise<void> {
    if (this.created === 0) {
      // Nothing recorded: the request goes on waiting (a load of the Create page fills the form again).
      return;
    }
    this.step = GENERATE_STEPS.left;
    this.options.show({
      kind: 'recorded',
      recorded: true,
      message: `${this.createdText()} ${LEFT_CREATE}`,
    });
    await this.options
      .send({ type: 'generate-progress', state: 'done', step: GENERATE_STEPS.left })
      .catch(() => undefined);
  }

  private createdText(): string {
    return this.created === 1
      ? 'Your Create was recorded in n8Tracks.'
      : `Your ${String(this.created)} Creates were recorded in n8Tracks.`;
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
