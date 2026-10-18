import {
  songOfAddress,
  sunoCreateAddress,
  sunoPageOf,
  sunoSongAddress,
  sunoWorkspaceAddress,
  workspaceOfAddress,
} from '../adapter/addresses.ts';
import { realClock, type Clock } from '../adapter/clock.ts';
import {
  verificationReport,
  type EntryResult,
  type FormJob,
  type SaveToProblem,
} from '../adapter/fill.ts';
import { isFinished, watchedClipsOf } from '../adapter/finished.ts';
import { PAGE_WAIT_MS, type Observations } from '../adapter/libraryReader.ts';
import type { ObservedMessage } from '../adapter/observed.ts';
import type { Page } from '../adapter/primitives.ts';
import type { AdapterSession } from '../adapter/registry.ts';
import {
  clipPageShows,
  EXTEND_FROM,
  extendSeconds,
  extendTime,
  INSPIRATION_ROUTE,
  MASHUP_DIALOG,
  mashupChoice,
  mashupClipsOf,
  mashupShowsFirstOnly,
  OVERWRITE_DIALOG,
  planSources,
  SOURCE_ROUTES,
  sourceName,
  sourceShown,
  type LoadedSource,
  type MashupClip,
} from '../adapter/sources.ts';
import { saveToRows, saveToShown, saveToShows } from '../adapter/songsForm.ts';
import { ADAPTER_VERSION } from '../adapter/version.ts';
import { expected, failureText, OK } from '../adapter/workflow.ts';
import type { FillContext } from '../adapter/workflows/fillSongs.ts';
import { formWorkflowsFor } from '../adapter/workflows/index.ts';
import { chooseSaveTo, closeSaveTo } from '../adapter/workflows/saveTo.ts';
import {
  answerOverwrite,
  chooseMashupSong,
  chooseSourceAction,
  chooseVoice,
  closeMashupDialog,
  closeVoicePicker,
  openSourceMenu,
  searchMashupSong,
  setExtendFrom,
  verifySourceAdvanced,
  verifySourceSimple,
} from '../adapter/workflows/sources.ts';
import {
  createWorkspace,
  openWorkspaces,
  searchWorkspaces,
  signedIn,
} from '../adapter/workflows/workspace.ts';
import {
  createdWorkspaceOf,
  KnownWorkspaces,
  nameHolds,
  namedLike,
  workspaceNameFor,
  workspaceOfFeed,
  workspaceOptions,
  type WorkspaceOption,
} from '../adapter/workspaces.ts';
import type {
  ChosenWorkspace,
  GenerateJob,
  RequestWorkspace,
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
 * - The Song has a workspace n8Tracks holds Available: the tab opens it by its address
 *   (`/create?wid=<id>`, a page load the request resumes on) and takes it as selected once the page
 *   shows it, by that address or by the library pane asking for its songs (TS-007-12). Suno's list
 *   is not read (TS-007-15: Suno loads more of it only on the user's own scroll).
 * - The Song has none, n8Tracks holds it Unavailable, or Suno did not show it by its address within
 *   the request's loads: the list is opened and the panel offers to create a workspace named after
 *   the Song or to use one Suno has, from the pages Suno loaded and what its search box finds by
 *   name (the user can search from the panel; the Song's title is searched first, so a workspace
 *   with its name is offered first, #335). Nothing is created until the user chooses; waiting counts
 *   towards the request's hour. The choice is then opened by its address too, unless Suno shows it
 *   already, so a workspace is never told from another of the same name by its row.
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
 *
 * A Mashup's second song (#438): once the Mashup shows its first song, "Add another song to
 * Mashup" opens Suno's "Add a song to mashup" dialog, the second song's title is typed into its
 * search box (never Enter, never a tab), and its row is chosen by its Suno ID when Suno's answer to
 * the search matches the rows one for one, else as the one row of exactly its title; its Select is
 * pressed and nothing else. With no such row, or several, the dialog is closed and the user adds
 * the song by hand and presses Continue. Both songs are verified as before.
 *
 * Loading a source may set the form's Save to to the source's workspace (#435, TS-007-16). Before
 * the rest of a Song's form is filled, Save to is read; when it shows another workspace, its picker
 * is opened, the Song's workspace typed (never Enter, never "+"), and the one row of exactly that
 * name chosen. With none, or several, the summary's Workspace entry leaves it to the user.
 */

/** The steps reported to n8Tracks, a fixed list (the Version page shows them). */
export const GENERATE_STEPS = {
  open: 'open Suno',
  signIn: 'check sign-in',
  list: 'read workspace list',
  select: 'select workspace',
  address: 'open workspace by address',
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
export const NO_FORM =
  'The extension cannot fill the Create form for this kind of Version. Fill it by hand in Suno, then click Create.';
export const FORM_GONE =
  'The form is no longer on this page, so it could not be checked. Start Generate on Suno again from n8Tracks.';
export const NOT_EXPECTED =
  'Suno’s answer to Create was not as expected, so its clips were not recorded in n8Tracks; a sync will bring them in.';
export const LEFT_CREATE =
  'You left Suno’s Create page, so the extension stopped watching for Creates.';
/** What the panel adds once the user pressed Done (#430). */
export const FINISHED_CREATES = 'You pressed Done, so the extension stopped watching for Creates.';

/**
 * How long the tab waits for a first page of the workspace list once it is open, when the page has
 * none yet. Suno may show a list it holds without asking again (TS-007-13), so none may come: the
 * choice then goes on with what search finds.
 */
export const LIST_WAIT_MS = 5_000;

/** How long the tab waits for Suno's answers after typing into the list's search box. */
export const SEARCH_WAIT_MS = 3_000;

/** The source's page in Suno did not show the clip's menu (#148). */
export function couldNotOpen(name: string, problem: string): string {
  return `Suno did not open the source ${name}: ${problem}. It may be in Suno’s Trash, deleted, or not yours. Nothing on the form was changed beyond its mode; load the source by hand, or fix it in n8Tracks and start again.`;
}

/** The action is missing from the clip's menu, or disabled there (#148). */
export function actionUnavailable(name: string, action: string): string {
  return `Suno does not offer ${action} for the source ${name} (the menu item is missing or disabled; it may need a plan you lack). Nothing on the form was changed beyond its mode.`;
}

/**
 * The clip's page is not captured (#341), so the user loads the source: what the panel and n8Tracks
 * say. The extension checks the source on the form afterwards, before filling anything else.
 */
export function loadByHandFirst(name: string, menu: string, item: string): string {
  return `The extension cannot open the source’s page in Suno yet, so load ${name} by hand: open it in Suno, then More options › ${menu} › ${item}. Back on the Create form, press Continue: the extension checks the source before filling anything else.`;
}

/** What the clip's page showed instead of the clip (#148, TS-005), in plain words. */
export const CLIP_PAGE_PROBLEMS = {
  'not found': 'Suno says “Page not found” at its address',
  'another clip': 'Suno’s page shows another clip',
  'nothing yet': 'Suno did not show the clip’s page in time',
} as const;

/** After the action, Suno did not show the Create form (#148). */
export function noFormAfter(name: string, action: string): string {
  return `Suno did not open the Create form after ${action} on the source ${name}. Start Generate on Suno again from n8Tracks.`;
}

/** How long Suno may take to ask "Overwrite Lyrics & Styles?" after an action with no source to see. */
export const OVERWRITE_WAIT_MS = 3_000;

/** How long Suno may take to ask "Overwrite Styles?" after a voice is chosen (TS-005). */
export const VOICE_QUESTION_WAIT_MS = 1_000;

/** How long Suno may take to show the source, or to ask first, after the action. */
export const SOURCE_WAIT_MS = 10_000;

/**
 * How long the tab waits for Suno's answer to the Mashup dialog's search (#438) before choosing
 * the song by its title alone: Suno may filter the rows it holds and send nothing.
 */
export const MASHUP_SEARCH_WAIT_MS = 3_000;

/** How long Suno may take to close the Mashup dialog once a song's Select is pressed. */
export const MASHUP_CLOSE_WAIT_MS = 3_000;

/** Why the extension could not add a Mashup's second song itself (#438), as the panel says it. */
export const MASHUP_NOT_ADDED = {
  none: 'no song in Suno’s “Add a song to mashup” list is named exactly as the second song',
  several:
    'more than one song in Suno’s “Add a song to mashup” list is named as the second song, so the extension chose none',
} as const;

/**
 * How many loads of the tab may go to reaching the Create page, or the Song's workspace on it by
 * its address; past them the workspace counts as not shown, and the choice is offered.
 */
export const MAXIMUM_LOADS = 2;

/** How long a new workspace may take to show as selected by itself before it is opened by address. */
export const SELECTED_BY_ITSELF_MS = 5_000;

/** How often the watch for the user's Create looks at the page's address again (#149). */
export const CREATE_POLL_MS = 1_000;

/** How long the watch goes on after the last recorded Create (n8Tracks ends the request then too). */
export const AFTER_LAST_CREATE_MS = 30 * 60_000;

/** How long the watch goes on before the first Create (n8Tracks lets a request idle an hour). */
export const BEFORE_FIRST_CREATE_MS = 60 * 60_000;

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
  'open workspace by address': 'Opening the Song’s workspace in Suno',
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

type Choice =
  | { kind: 'create' }
  | { kind: 'pick'; option: WorkspaceOption }
  | { kind: 'search'; query: string };

/** Why the panel offers the choice: no workspace, one n8Tracks holds Unavailable, or one not shown. */
type ChoiceReason = 'none' | 'unavailable' | 'unopened';

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

/**
 * Whether the extension adds the Mashup's second song itself (#438): an Advanced Mashup of two
 * Suno clips, the second with a title to search for.
 */
function addsMashupSong(form: FormJob, load: LoadedSource): boolean {
  const [second, ...more] = load.others;
  return (
    form.mode === 'advanced' &&
    load.route === SOURCE_ROUTES.mashup &&
    second !== undefined &&
    more.length === 0 &&
    (second.title ?? '').trim() !== ''
  );
}

/** The Song's workspace when n8Tracks holds it Available: the one to open by its address. */
function usableWorkspace(job: GenerateJob): RequestWorkspace | null {
  return job.workspace !== null && job.workspace.state === 'available' ? job.workspace : null;
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
  /** The user pressed Done and n8Tracks finished the request (#430): no more Creates are watched for. */
  private finished = false;
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

  /** "Search" in the panel: `query` is typed into Suno's own search box, and the choice offered again. */
  search(query: string): void {
    this.answer({ kind: 'search', query });
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

  /**
   * "Done" in the panel (#430): n8Tracks ends the request as finished, and the tab stops watching for
   * Creates. When n8Tracks cannot be reached, Done stays on offer; when the request has ended already,
   * the panel says what n8Tracks says about it.
   */
  async finish(): Promise<void> {
    const answer = reply(
      await this.options.send({ type: 'generate-finish' }).catch(() => undefined),
    );
    if (answer.ok || answer.ended) {
      this.finished = true;
    }
    this.options.show(
      answer.ok
        ? {
            kind: 'recorded',
            recorded: true,
            ended: true,
            message: `${this.createdText()} ${FINISHED_CREATES}`,
          }
        : answer.ended
          ? { kind: 'recorded', recorded: true, ended: true, message: answer.message }
          : {
              kind: 'recorded',
              recorded: true,
              message: `${this.createdText()} ${answer.message}`,
            },
    );
  }

  /** Whether Done ended the request (#430); read afresh after each wait. */
  private hasFinished(): boolean {
    return this.finished;
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
        // Straight to the Song's workspace when it has one (TS-007-15); Create otherwise.
        const usable = usableWorkspace(job);
        this.options.show({ kind: 'working', step: STEP_TEXT[GENERATE_STEPS.open] });
        page.go(usable === null ? sunoCreateAddress() : sunoWorkspaceAddress(usable.sunoId));
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
    const signed = await this.options.session.run(signedIn, {}, this.runOptions());
    if (!signed.ok) {
      await this.stop(NOT_SIGNED_IN);
      return;
    }

    const own = job.workspace;
    const usable = usableWorkspace(job);
    if (usable !== null) {
      if (!(await this.begin(GENERATE_STEPS.select))) {
        return;
      }
      // Selected already: the address names it, or the library pane asked for its songs.
      if (await this.shows(usable.sunoId)) {
        await this.selected(usable.name);
        return;
      }
      // Opened by its address, a page load the request resumes on (TS-007-15); the loads bound it.
      if (job.loads <= MAXIMUM_LOADS) {
        await this.openByAddress(usable.sunoId);
        return;
      }
    }
    await this.choose(
      job,
      own === null ? 'none' : own.state === 'unavailable' ? 'unavailable' : 'unopened',
    );
  }

  /**
   * No usable workspace: the list is opened, what Suno loaded is read (no more is asked for: Suno
   * pages it only on the user's own scroll, TS-007-15), the Song's title is searched for, what was
   * found goes to n8Tracks (never as a complete list, so nothing becomes Unavailable), and the
   * panel offers the choice.
   */
  private async choose(job: GenerateJob, reason: ChoiceReason): Promise<void> {
    if (!(await this.begin(GENERATE_STEPS.list))) {
      return;
    }
    const opened = await this.options.session.run(openWorkspaces, {}, this.runOptions());
    if (!opened.ok) {
      await this.stop(
        opened.failure.step === 'signed in' ? NOT_SIGNED_IN : failureText(opened.failure),
      );
      return;
    }
    const known = new KnownWorkspaces();
    if (!(await this.take(known, 0))) {
      await this.take(known, LIST_WAIT_MS);
    }
    // A workspace with the Song's name, wherever it is in the list, is offered first (#335).
    await this.lookUp(workspaceNameFor(job.songTitle), known);
    await this.lookUp('', known);
    const counts = await this.report(known);
    if (counts === null) {
      return;
    }
    await this.offer(job, known, counts, reason);
  }

  /**
   * The panel offers the choice, and the request waits for it. A search from the panel is typed
   * into Suno's search box; what it finds is added, and the choice offered again.
   */
  private async offer(
    job: GenerateJob,
    known: KnownWorkspaces,
    reported: ReadonlyMap<string, number>,
    reason: ChoiceReason,
  ): Promise<void> {
    if (!(await this.begin(GENERATE_STEPS.choose))) {
      return;
    }
    const own = job.workspace?.sunoId ?? null;
    // The Song's own workspace is replaced by the choice when n8Tracks still holds it Available.
    const replaces = reason === 'unopened' && own !== null ? { replaces: own } : {};
    let counts = reported;
    let query = '';
    for (;;) {
      const choice = await new Promise<Choice>((resolve) => {
        this.waiting = resolve;
        const listed = known.workspaces().filter((item) => nameHolds(item.name, query));
        this.options.show({
          kind: 'choose',
          title: job.songTitle,
          reason,
          query,
          sameName: namedLike(known.workspaces(), workspaceNameFor(job.songTitle)).length > 0,
          options: workspaceOptions(listed, job.songTitle, counts, own),
        });
      });
      if (choice.kind === 'search') {
        query = choice.query;
        this.options.show({ kind: 'working', step: 'Searching Suno’s workspaces' });
        await this.lookUp(query, known);
        counts = (await this.report(known)) ?? counts;
        continue;
      }
      if (choice.kind === 'pick') {
        const picked = known.get(choice.option.id);
        if (picked === undefined) {
          return;
        }
        if (
          !(await this.save({ sunoId: picked.id, name: picked.name, how: 'picked', ...replaces }))
        ) {
          return;
        }
        await this.open(picked.id, picked.name);
        return;
      }
      await this.createFor(job, replaces);
      return;
    }
  }

  /** Creates a workspace named after the Song through Suno's own row, then records and opens it. */
  private async createFor(job: GenerateJob, replaces: { replaces?: string }): Promise<void> {
    if (!(await this.begin(GENERATE_STEPS.create))) {
      return;
    }
    // The list as Suno shows it unsearched, with its "Create new workspace" row.
    await this.options.session.run(searchWorkspaces, { query: '' }, this.runOptions());
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
    const workspaceName = created.name === '' ? name : created.name;
    // Created once; only the report is sent again if it fails on the way.
    if (
      !(await this.save({ sunoId: created.id, name: workspaceName, how: 'created', ...replaces }))
    ) {
      return;
    }
    // Suno may select the new workspace by itself; if not, it is opened by its address.
    const shown = this.watch(
      'library-feed',
      (message) => workspaceOfFeed(message) === created.id,
      SELECTED_BY_ITSELF_MS,
    );
    await shown.done;
    if (shown.seen !== null || workspaceOfAddress(this.options.page.address()) === created.id) {
      await this.selected(workspaceName);
      return;
    }
    await this.open(created.id, workspaceName);
  }

  /** The chosen workspace: selected when the page shows it already, else opened by its address. */
  private async open(sunoId: string, name: string): Promise<void> {
    if (!(await this.begin(GENERATE_STEPS.select))) {
      return;
    }
    if (await this.shows(sunoId)) {
      await this.selected(name);
      return;
    }
    await this.openByAddress(sunoId);
  }

  /**
   * Loads the Create page opened for the workspace (`/create?wid=<id>`, TS-007-15): the request
   * resumes on that load, which takes the workspace as selected when the page shows it.
   */
  private async openByAddress(sunoId: string): Promise<void> {
    if (!(await this.begin(GENERATE_STEPS.address))) {
      return;
    }
    this.options.page.go(sunoWorkspaceAddress(sunoId));
  }

  /**
   * Whether the page shows the workspace as selected: the address names it (TS-007-12: Suno shows
   * the songs it holds without asking again), or the library pane asked for its songs last.
   */
  private async shows(sunoId: string): Promise<boolean> {
    return (
      workspaceOfAddress(this.options.page.address()) === sunoId ||
      (await this.shownWorkspace()) === sunoId
    );
  }

  private async selected(name: string): Promise<void> {
    if (await this.begin(GENERATE_STEPS.selected)) {
      this.options.show({ kind: 'selected', name });
      await this.fill(name);
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
      // The clip's page is not captured (#341): unless the route is, the user loads the source.
      if (plan.load.route.automated) {
        await this.openSource(plan.load);
      } else {
        await this.loadByHand(form, plan.load);
      }
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
    // Save to first (#435): loading a source may have set it to the source's workspace.
    const saveTo =
      form.kind === 'song' && workspace !== null ? await this.setSaveTo(workspace) : null;
    const voice = await this.chooseVoice(form);
    const job: FormJob = {
      ...form,
      ...(loaded === null ? {} : { loaded }),
      ...(voice === null ? {} : { voice }),
      ...(saveTo === null ? {} : { saveTo }),
    };
    const context: Omit<FillContext, 'page' | 'signal'> = { job, workspace, results: [] };
    const filling = await this.options.session.run(workflows.fill, context, this.runOptions());
    if (!filling.ok) {
      await this.stop(failureText(filling.failure));
      return;
    }
    this.filled = { form: job, workspace };
    await this.review(GENERATE_STEPS.review, job, context.results);
  }

  /**
   * Sets Create's Save to back to the Song's workspace (#435), before the rest is filled: loading a
   * source may set it to the source's workspace, and the clips would go there. When the form shows
   * no Save to, or it shows the workspace already, nothing is pressed. Otherwise the picker is
   * opened, the name typed (never Enter, never "+"), and the one row of exactly that name chosen;
   * the summary's Workspace entry reads Save to back (`workspaceResult`). Null when Save to shows
   * the workspace or there is none; else why it was left to the user (the picker is closed).
   */
  private async setSaveTo(workspace: string): Promise<SaveToProblem | null> {
    const { page, session } = this.options;
    if (saveToShown(page) === null || saveToShows(page, workspace)) {
      return null;
    }
    const chosen = await session.run(chooseSaveTo, { workspaceName: workspace }, this.runOptions());
    if (chosen.ok) {
      return null;
    }
    const rows = saveToRows(page, workspace).count;
    await session.run(closeSaveTo, {}, this.runOptions());
    if (chosen.failure.step !== 'workspace row') {
      return 'not chosen';
    }
    return rows === 0 ? 'no row' : rows > 1 ? 'several rows' : 'not chosen';
  }

  /**
   * Chooses the Version's voice in the Advanced form (#148, TS-005), before the rest is filled: by its
   * title in the Voice picker when exactly one voice has that name, verified by the link the form
   * then shows (`/voice/<persona ID>`). Null when the Version has no voice, or the mode's picker was
   * not captured (Simple); otherwise the voice's outcome, `manual` with the voice named when it could
   * not be chosen.
   */
  private async chooseVoice(form: FormJob): Promise<EntryResult | null> {
    const key = `songs.${form.mode}.voice`;
    const value = form.entries[key];
    if (form.kind !== 'song' || form.mode !== 'advanced' || !isRecord(value)) {
      return null;
    }
    const name = typeof value.name === 'string' ? value.name : '';
    const personaId = typeof value.personaId === 'string' ? value.personaId : '';
    if (name.trim() === '' || personaId === '') {
      return null;
    }
    const { page, session } = this.options;
    const chosen = await session.run(
      chooseVoice,
      { voice: { name, personaId } },
      this.runOptions(),
    );
    if (chosen.ok) {
      // Suno may ask about the styles a voice carries; the Version's own are filled afterwards.
      await page.wait(() => page.find(OVERWRITE_DIALOG).kind === 'found', VOICE_QUESTION_WAIT_MS);
      if (page.find(OVERWRITE_DIALOG).kind === 'found') {
        await session.run(answerOverwrite, {}, this.runOptions());
      }
      return {
        key,
        outcome: 'verified',
        note: `The voice “${name}” is chosen on the form: its link is the Version’s voice.`,
        reportNote: 'The voice is chosen on the form: its link is the Version’s voice.',
      };
    }
    // While the picker is open, Suno hides the rest of the form (TS-005): it is closed first.
    await session.run(closeVoicePicker, {}, this.runOptions());
    return {
      key,
      outcome: 'manual',
      note: `Choose the voice “${name}” from + Voice by hand: ${failureText(chosen.failure)}.`,
      reportNote:
        'Choose the voice the Version names from + Voice by hand: the extension could not choose it (no single voice of that name in the list).',
    };
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

  /**
   * The user loads the source by hand (#341): the phase is kept for the page loads the action takes,
   * the panel asks for it, and after Continue the source is checked on the form.
   */
  private async loadByHand(form: FormJob, load: LoadedSource): Promise<void> {
    if (!(await this.keepSource({ phase: 'byHand', sunoId: load.sunoId }))) {
      return;
    }
    const message = loadByHandFirst(sourceName(load.source), load.route.menu, load.route.item);
    if (!(await this.askByHand(message))) {
      return;
    }
    await this.afterAction(form, load);
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
    if (phase.phase === 'byHand') {
      // Back on the Create form after the user's action: the source is checked. Anywhere else the
      // tab waits for the user to finish (the clip's own page is where they choose the action).
      if (sunoPageOf(page.address()) === 'create') {
        await this.afterAction(form, load);
      } else {
        this.options.show({ kind: 'working', step: STEP_TEXT[GENERATE_STEPS.byHand] });
      }
      return;
    }
    if (phase.phase === 'opening') {
      // Only an automated route goes on from the clip's page (#341): it is not captured otherwise.
      if (!load.route.automated) {
        this.step = GENERATE_STEPS.source;
        await this.stop(
          'The extension cannot use the source’s page in Suno yet. Start again from n8Tracks and load the source by hand when asked.',
        );
        return;
      }
      if (songOfAddress(page.address()) === phase.sunoId) {
        // The page must show the clip itself (its cover holds the ID), not Suno's 404 (TS-005).
        await page.wait(() => clipPageShows(page, phase.sunoId) !== 'nothing yet', PAGE_WAIT_MS);
        const shows = clipPageShows(page, phase.sunoId);
        if (shows !== 'clip') {
          this.step = GENERATE_STEPS.source;
          await this.stop(couldNotOpen(name, CLIP_PAGE_PROBLEMS[shows]));
          return;
        }
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
    // A Mashup the extension completes itself (#438) shows its first song only, until then.
    const adds = addsMashupSong(form, load);
    const firstOnly = () => adds && mashupShowsFirstOnly(page, load);
    await page.wait(
      () => asked() || seen() || firstOnly(),
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
      // Reuse Prompt copies the source's inputs and leaves no source on the form to see, so which
      // clip's inputs were copied is not checked (#342): it is reported as to check by hand.
      loaded = {
        key: load.source.key,
        outcome: 'manual',
        note: `Not verified: ${load.route.item} leaves no source on the form to check, so the extension cannot tell whose inputs were copied. Check that it was used on ${name}; the Version’s own values were filled over its inputs.`,
        reportNote: `Not verified: ${load.route.item} leaves no source on the form to check, so the extension cannot tell whose inputs were copied. Check that it was used on the source; the Version’s own values were filled over its inputs.`,
      };
    } else {
      const verify = form.mode === 'simple' ? verifySourceSimple : verifySourceAdvanced;
      // The Mashup's second song (#438): added through Suno's dialog when it can be found there.
      if (adds && !seen()) {
        await page.wait(() => seen() || firstOnly(), SOURCE_WAIT_MS);
      }
      let why = firstOnly() ? await this.addMashupSong(load) : null;
      for (;;) {
        const verified = await session.run(verify, { load }, this.runOptions());
        if (verified.ok) {
          break;
        }
        if (!(await this.byHand(load, why ?? failureText(verified.failure)))) {
          return;
        }
        why = null;
      }
      const several = load.others.length > 0;
      loaded = {
        key: load.source.key,
        outcome: 'verified',
        note:
          form.mode === 'simple'
            ? `${name} is on the form: the source chip’s thumbnail is the source clip’s (Suno’s Simple chip does not name the action).`
            : several
              ? `${[name, ...load.others.map(sourceName)].join(' and ')} are on the form: the Audio section names ${load.route.label} and shows each one’s thumbnail.`
              : `${name} is on the form: the Audio section names ${load.route.label} and its thumbnail is the source clip’s.`,
        reportNote:
          form.mode === 'simple'
            ? 'The source is on the form: the source chip’s thumbnail is the source clip’s (Suno’s Simple chip does not name the action).'
            : several
              ? `The sources are on the form: the Audio section names ${load.route.label} and shows each one’s thumbnail.`
              : `The source is on the form: the Audio section names ${load.route.label} and its thumbnail is the source clip’s.`,
      };
      const continueAt = load.source.continueAtSeconds;
      if (load.route === SOURCE_ROUTES.extend && typeof continueAt === 'number') {
        loaded = await this.extendFrom(loaded, continueAt);
      }
    }
    if (!(await this.keepSource(null))) {
      return;
    }
    await this.fillRest(form, this.job?.workspace?.name ?? null, loaded);
  }

  /**
   * Adds a Mashup's second song through Suno's "Add a song to mashup" dialog (#438): the dialog is
   * opened and the song's title typed into its search box; Suno's answer to the search, when the
   * page gets one within {@link MASHUP_SEARCH_WAIT_MS}, lets the song's row be told by its Suno ID,
   * else it is the one row of exactly its title; that row's Select is pressed, and the dialog is
   * waited on to close. Null once a Select was pressed and the dialog closed: the form is verified
   * next, as for every source. Otherwise the dialog is closed (its ×, else Escape) and why the song
   * was not added is answered, for the by-hand message; nothing else is pressed.
   */
  private async addMashupSong(load: LoadedSource): Promise<string | null> {
    const [second] = load.others;
    const title = second?.title ?? '';
    if (second === undefined || title.trim() === '') {
      return null;
    }
    const { page, session } = this.options;
    const song = { title, sunoId: second.sunoId };
    const lists = (message: ObservedMessage) => mashupClipsOf(message.body, song.sunoId) !== null;
    // A list the page got before the search is not its answer.
    await this.latest('library-feed', lists, 0);
    const searched = await session.run(searchMashupSong, { song }, this.runOptions());
    let failure = searched.ok ? null : failureText(searched.failure);
    if (searched.ok) {
      const answer = await this.latest('library-feed', lists, MASHUP_SEARCH_WAIT_MS);
      const clips: MashupClip[] | null =
        answer === null ? null : mashupClipsOf(answer.body, song.sunoId);
      const chosen: { by: 'id' | 'title' | null } = { by: null };
      const choosing = await session.run(
        chooseMashupSong,
        { song, clips, chosen },
        this.runOptions(),
      );
      if (choosing.ok && chosen.by !== null) {
        await page.wait(() => page.find(MASHUP_DIALOG).kind !== 'found', MASHUP_CLOSE_WAIT_MS);
      }
      const choice = mashupChoice(page, song, clips).kind;
      failure = !choosing.ok
        ? choosing.failure.step === 'song row' && choice !== 'row'
          ? MASHUP_NOT_ADDED[choice]
          : failureText(choosing.failure)
        : null;
    }
    if (page.find(MASHUP_DIALOG).kind === 'found') {
      // While it is open the form may be hidden behind it, so nothing else could be read.
      await session.run(closeMashupDialog, {}, this.runOptions());
    }
    return failure;
  }

  /**
   * Takes every response of `kind` that `accept` takes, waiting up to `timeoutMs` for the first
   * when there is none yet; the last of them, or null.
   */
  private async latest(
    kind: ObservedMessage['kind'],
    accept: (message: ObservedMessage) => boolean,
    timeoutMs: number,
  ): Promise<ObservedMessage | null> {
    const signal = new AbortController().signal;
    let last: ObservedMessage | null = null;
    for (let wait = timeoutMs; ; wait = 0) {
      const message = await this.options.observations.next(kind, accept, wait, signal);
      if (message === null) {
        return last;
      }
      last = message;
    }
  }

  /**
   * Sets where an Extend continues from (TS-005) and reads it back: the verified source's outcome,
   * or `failed` with what the form shows.
   */
  private async extendFrom(loaded: EntryResult, seconds: number): Promise<EntryResult> {
    const { page, session } = this.options;
    const set = await session.run(setExtendFrom, { seconds }, this.runOptions());
    const time = extendTime(seconds);
    if (set.ok) {
      return {
        ...loaded,
        note: `${loaded.note ?? ''} It continues from ${time}.`.trim(),
        reportNote:
          `${loaded.reportNote ?? ''} The Extend continues from the Version’s time.`.trim(),
      };
    }
    const box = page.find(EXTEND_FROM);
    const shown = box.kind === 'found' ? extendSeconds(page.read(box.found).value ?? '') : null;
    return {
      key: loaded.key,
      outcome: 'failed',
      expected: seconds,
      found: shown,
      note: `The source is on the form, but its “Extend from” time is not ${time}: set it by hand.`,
      reportNote:
        'The source is on the form, but its “Extend from” time is not the Version’s: set it by hand.',
    };
  }

  /**
   * The source on the form is not the Version's: the panel asks the user to load it by hand, and
   * the request waits (reported, so n8Tracks shows why). True once the user pressed Continue.
   * Further Inspiration songs are always added this way (TS-005 did not capture adding them), and a
   * Mashup's second song when the extension could not add it through Suno's dialog (#438).
   */
  private async byHand(load: LoadedSource, why: string): Promise<boolean> {
    const name = sourceName(load.source);
    const others = load.others.map(sourceName).join(' and ');
    const message =
      load.others.length === 0
        ? `The source on Suno’s form is not the Version’s (${why}). Load ${name} with ${load.route.menu} › ${load.route.item}, then press Continue.`
        : load.route === INSPIRATION_ROUTE
          ? `Suno’s form does not show every Inspiration song of the Version yet (${why}). Add ${others} as Inspiration by hand (on each song’s page, More options › Remix › Use as Inspiration), with ${name}, then press Continue.`
          : `Suno’s Mashup does not show both of the Version’s songs yet (${why}). Add ${others} with “Add another song to Mashup”, with ${name}, then press Continue.`;
    return (await this.askByHand(message)) && this.begin(GENERATE_STEPS.verifySource, 'filling');
  }

  /**
   * Asks the user to load the source by hand (reported, so n8Tracks shows why) and waits for the
   * panel's Continue; false when the request has ended.
   */
  private async askByHand(message: string): Promise<boolean> {
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
    return true;
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
        if (this.hasFinished() || clock.now() - since >= limit) {
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
        if (message === null || this.hasFinished()) {
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
   * watches. It only listens (#429): every feed answer the page gets on its own, from the user's
   * browsing or Suno's own reading, is read for them, and the finished ones go to the service worker,
   * which sends them to n8Tracks and answers which clips are still watched. It never presses,
   * scrolls, or loads anything to make Suno answer. Ends when no clip is watched any more (all
   * finished, or their ten minutes up), when n8Tracks cannot be told, or quietly at its own limit
   * when no feed answer named a watched clip; a completion it misses is brought in by the next sync
   * (#314).
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
      while (this.watchedClips.size > 0) {
        const left = COMPLETION_LIMIT_MS - (clock.now() - started);
        const message =
          left <= 0
            ? null
            : await this.options.observations.next(
                'library-feed',
                (seen) => watchedClipsOf(seen.body, this.watchedClips).length > 0,
                left,
                signal,
              );
        if (message === null) {
          // Nothing named a watched clip before the limit: the watch ends, telling no one.
          return;
        }
        const clips = watchedClipsOf(message.body, this.watchedClips).filter((clip) =>
          isFinished(clip),
        );
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

  /** The tab left the Create page after a recorded Create: the request is done. */
  private async leave(): Promise<void> {
    if (this.created === 0 || this.finished) {
      // Nothing recorded: the request goes on waiting (a load of the Create page fills the form again).
      // Done ended it already (#430).
      return;
    }
    this.step = GENERATE_STEPS.left;
    this.options.show({
      kind: 'recorded',
      recorded: true,
      ended: true,
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
   * Takes every workspace-list answer the page got (each is given once), waiting up to `timeoutMs`
   * for the first when there is none yet. True when one came.
   */
  private async take(known: KnownWorkspaces, timeoutMs: number): Promise<boolean> {
    const signal = new AbortController().signal;
    let took = false;
    for (let wait = timeoutMs; ; wait = 0) {
      const message = await this.options.observations.next('workspaces', () => true, wait, signal);
      if (message === null) {
        return took;
      }
      took = known.add(message) || took;
    }
  }

  /**
   * Types `query` into the list's search box (opening the list first if it closed) and takes what
   * Suno answers within {@link SEARCH_WAIT_MS}. Presses nothing but the breadcrumb that opens the
   * list. When the box cannot be used, what is known stays as it is.
   */
  private async lookUp(query: string, known: KnownWorkspaces): Promise<void> {
    const { session } = this.options;
    const opened = await session.run(openWorkspaces, {}, this.runOptions());
    if (!opened.ok) {
      return;
    }
    await this.take(known, 0);
    const typed = await session.run(searchWorkspaces, { query }, this.runOptions());
    if (typed.ok) {
      await this.take(known, SEARCH_WAIT_MS);
    }
  }

  /**
   * Reports the workspaces seen to n8Tracks, never as Suno's complete list (TS-007-15), and answers
   * the n8Tracks Songs in each; null (and stopped) when n8Tracks did not take it.
   */
  private async report(known: KnownWorkspaces): Promise<Map<string, number> | null> {
    const reported = reply<{ songCounts: Record<string, number> }>(
      await this.options
        .send({
          type: 'generate-workspaces',
          workspaces: known.workspaces().map((item) => item.raw),
        })
        .catch(() => undefined),
    );
    if (!reported.ok) {
      await this.stop(reported.message, reported.ended);
      return null;
    }
    return new Map(Object.entries(reported.songCounts));
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
