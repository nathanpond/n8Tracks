import type { EntryResult, FormJob, FormSource } from './fill.ts';
import type { Page, Target } from './primitives.ts';
import { expected, OK, type Check } from './workflow.ts';

/**
 * Starting a generation from a source (#148, TS-002, TS-003): which of a Version's sources the
 * extension loads into Suno's Create form, the route a person takes to load one (the clip's own
 * page, its "More options" menu, then the action in the Remix or Edit menu), and how the loaded
 * source is verified before anything else is filled: the Audio section (Advanced) names the action,
 * and the source's thumbnail address holds the source clip's Suno ID (the Simple chip shows the
 * thumbnail only).
 *
 * What TS-003 captured decides what is built (decision D10). The menus (`page.clip-remix-menu.html`,
 * `page.clip-edit-menu.html`), a loaded Cover (`page.create-source-advanced.html`,
 * `page.create-source-simple.html`), and the Overwrite dialog are captured, so Cover and Reuse
 * Prompt are loaded. No snapshot shows the form after Extend, Mashup, Sample this song, or a
 * single song used as Inspiration, nor a chosen voice or playlist: those are listed in the summary
 * as to do by hand, never guessed at. The Inspo picker's dialog has no title, so the
 * forbidden-control matcher keeps refusing it (#133), and a playlist is added by hand.
 */

/** A Suno action the extension can take from a clip's menu. */
export interface SourceRoute {
  /** The clip menu's submenu that holds the action. */
  menu: 'Remix' | 'Edit';
  /** The action's menu item, by its accessible name (TS-003). */
  item: string;
  /**
   * The action as the Audio section's condition button names it ("Change condition type from
   * Cover"); null when the action leaves no source on the form (Reuse Prompt only copies inputs).
   */
  label: string | null;
  /** Whether a TS-003 snapshot shows the form after the action, so the extension may take it. */
  captured: boolean;
}

/**
 * The menu route of each Suno action key a source can carry (`sunoAction`; a user type mapped to an
 * action arrives as that action's key, #126). Only Cover's and Reuse Prompt's results are captured.
 */
export const SOURCE_ROUTES: Readonly<Record<string, SourceRoute>> = {
  cover: { menu: 'Remix', item: 'Cover', label: 'Cover', captured: true },
  reuse_prompt: { menu: 'Remix', item: 'Reuse Prompt', label: null, captured: true },
  mashup: { menu: 'Remix', item: 'Mashup', label: 'Mashup', captured: false },
  sample: { menu: 'Remix', item: 'Sample this song', label: 'Sample', captured: false },
  extend: { menu: 'Edit', item: 'Extend', label: 'Extend', captured: false },
};

/** A single song used as Inspiration, from the Remix menu (not captured: done by hand). */
export const INSPIRATION_ROUTE: SourceRoute = {
  menu: 'Remix',
  item: 'Use as Inspiration',
  label: 'Inspo',
  captured: false,
};

/** Suno takes at most four songs as Inspiration (TS-002). */
export const MAXIMUM_INSPIRATION_SONGS = 4;

/**
 * The source entries of the field map that no TS-003 snapshot lets the extension set, and why:
 * the summary tells the user to do them by hand until the owner captures those page states.
 */
export const SOURCES_BLOCKED_ON_CAPTURE: Readonly<Record<string, string>> = {
  'songs.simple.simple_add_playlist':
    'the Inspo picker has no title the extension can recognise it by, and no snapshot shows a chosen playlist',
  'songs.advanced.inspiration':
    'no snapshot shows the form with a song or a playlist added as Inspiration',
  'songs.simple.voice': 'no snapshot shows the form with a voice chosen',
  'songs.advanced.voice': 'no snapshot shows the form with a voice chosen',
};

/** The availabilities n8Tracks knows that mean a source cannot be used, in plain words. */
export const UNUSABLE: Readonly<Record<string, string>> = {
  trashed: 'is in Suno’s Trash',
  missing: 'is no longer in Suno’s library',
  deleted: 'was deleted in n8Tracks',
};

/** How the summary names a Suno action key: the menu item's own name, else the key itself. */
export function actionName(action: string | null | undefined): string {
  if (action === null || action === undefined) {
    return 'a source';
  }
  return SOURCE_ROUTES[action]?.item ?? action;
}

function quoted(text: string | null | undefined, otherwise: string): string {
  return text === null || text === undefined || text.trim() === '' ? otherwise : `“${text}”`;
}

/** What a source is called in the summary and the panel. */
export function sourceName(source: FormSource): string {
  return quoted(source.title, 'the source clip');
}

/** The source the extension loads: the first audio source, when its action's result is captured. */
export interface LoadedSource {
  source: FormSource;
  sunoId: string;
  route: SourceRoute;
}

/** What to do with a request's sources before anything else is filled. */
export interface SourcePlan {
  /** Why the extension stops before changing the form (a source n8Tracks knows is unusable). */
  stop: string | null;
  /** The source to load from its clip's menu, if any. */
  load: LoadedSource | null;
}

function nameOf(value: unknown): string | null {
  if (typeof value === 'object' && value !== null && 'name' in value) {
    const name = value.name;
    return typeof name === 'string' && name.trim() !== '' ? name : null;
  }
  return null;
}

function audioSources(job: FormJob): FormSource[] {
  return job.sources
    .filter((source) => (source.group ?? 'audio') === 'audio')
    .toSorted((a, b) => (a.position ?? 0) - (b.position ?? 0));
}

function inspirationSources(job: FormJob): FormSource[] {
  return job.sources.filter((source) => source.group === 'inspiration');
}

/**
 * The plan for `job`: a stop when any source is one n8Tracks knows cannot be used (each named with
 * its problem, before anything on the form changes) or when the request holds more Inspiration
 * songs than Suno takes; else the source to load, if one can be. Order (Discretion): the audio
 * source first, then Inspiration, then Voice; only the audio source is ever loaded.
 */
export function planSources(job: FormJob): SourcePlan {
  const unusable = job.sources
    .filter((source) => source.availability !== undefined && source.availability !== null)
    .filter((source) => UNUSABLE[source.availability ?? ''] !== undefined)
    .map((source) => `${sourceName(source)} ${UNUSABLE[source.availability ?? ''] ?? ''}`);
  if (unusable.length > 0) {
    return {
      stop: `The Version’s source ${unusable.join('; ')}, so nothing on Suno’s form was changed. Restore it in Suno and sync, or change the Version’s sources, then start again.`,
      load: null,
    };
  }
  if (inspirationSources(job).length > MAXIMUM_INSPIRATION_SONGS) {
    return {
      stop: `The Version has more than ${String(MAXIMUM_INSPIRATION_SONGS)} songs as Inspiration, which Suno does not take, so nothing on Suno’s form was changed.`,
      load: null,
    };
  }
  const audio = audioSources(job);
  const [first] = audio;
  // A Mashup needs both its sources loaded; its result is not captured, so neither is loaded.
  if (first === undefined || audio.length > 1) {
    return { stop: null, load: null };
  }
  const route = first.sunoAction === null ? undefined : SOURCE_ROUTES[first.sunoAction];
  const sunoId = first.sunoId ?? null;
  if (route?.captured !== true || sunoId === null || sunoId === '') {
    return { stop: null, load: null };
  }
  return { stop: null, load: { source: first, sunoId, route } };
}

/**
 * Why one source is left to the user, as a step of the summary's note: naming the source by its
 * title, or with `named` false generically, as n8Tracks stores the note (#340).
 */
function byHandStep(source: FormSource, load: LoadedSource | null, named: boolean): string | null {
  if (load?.source === source) {
    return null;
  }
  const name = named ? sourceName(source) : 'the source';
  if (source.group === 'inspiration') {
    return `add ${name} as Inspiration (${INSPIRATION_ROUTE.item}) by hand: no snapshot shows that form yet`;
  }
  if (source.sunoId === null || source.sunoId === undefined || source.sunoId === '') {
    return `load ${name} by hand: it is a Song in n8Tracks, not a Suno clip`;
  }
  if (source.sunoAction === null) {
    return `load ${name} by hand: its relationship type names no Suno action`;
  }
  const route = SOURCE_ROUTES[source.sunoAction];
  return route === undefined
    ? `load ${name} as ${source.sunoAction} by hand: the extension does not know that action`
    : `load ${name} with ${route.menu} › ${route.item} by hand: no snapshot shows Suno’s form after ${route.item} yet`;
}

/**
 * The summary line of each source entry of the request's mode (`audio`, `voice`, `inspiration`,
 * `simple_add_playlist`): the loaded source's own outcome (`loaded`, from the verification), and
 * everything else of the entry as to do by hand, named (the voice and playlist by their names, a
 * file only the user can attach with its note). An entry with nothing is not applicable.
 */
export function sourceEntryResult(
  key: string,
  job: FormJob,
  loaded: EntryResult | null = null,
): EntryResult {
  const plan = planSources(job);
  const stepsOf = (named: boolean): string[] => {
    const steps = [
      ...job.sources
        .filter((source) => source.key === key)
        .map((source) => byHandStep(source, plan.load, named))
        .filter((step): step is string => step !== null),
      ...job.fileInputs
        .filter((file) => file.key === key)
        .map(
          (file) =>
            `attach the audio file by hand${
              file.description === null
                ? ''
                : named
                  ? ` (${file.description})`
                  : ' (the Version’s file note says which)'
            }`,
        ),
    ];
    const value = job.entries[key];
    if (value !== undefined && value !== null) {
      const why = SOURCES_BLOCKED_ON_CAPTURE[key] ?? 'the extension cannot set it';
      const name = named ? quoted(nameOf(value), 'the Version names') : 'the Version names';
      steps.push(
        key.endsWith('.voice')
          ? `choose the voice ${name} from + Voice by hand (${why})`
          : `add the playlist ${name} from + Inspo by hand (${why})`,
      );
    }
    return steps;
  };
  const steps = stepsOf(true);
  const reported = stepsOf(false);
  const own = loaded !== null && loaded.key === key ? loaded : null;
  if (own !== null) {
    return steps.length === 0
      ? own
      : {
          ...own,
          note: [own.note, `Also ${steps.join('; ')}.`].filter(Boolean).join(' '),
          reportNote: [own.reportNote ?? own.note, `Also ${reported.join('; ')}.`]
            .filter(Boolean)
            .join(' '),
        };
  }
  if (steps.length === 0) {
    return { key, outcome: 'not_applicable' };
  }
  return { key, outcome: 'manual', note: sentenceOf(steps), reportNote: sentenceOf(reported) };
}

/** Steps as one note: the first capitalised, joined by semicolons, ending with a full stop. */
function sentenceOf(steps: readonly string[]): string {
  const [first, ...rest] = steps;
  const sentence = [first === undefined ? '' : first.charAt(0).toUpperCase() + first.slice(1)];
  return `${[...sentence, ...rest].join('; ')}.`;
}

// ---- The clip's page and its menus (TS-003: `page.clip-remix-menu.html`, `page.clip-edit-menu.html`).

/**
 * A clip's "More options" button: the name every captured Suno list gives a clip's menu button
 * (Library, Trash, playlist). The clip's own page was not captured, so it is found only when the
 * page has exactly one; anything else stops the run, naming the step.
 */
export const MORE_OPTIONS: Target = {
  role: 'button',
  name: 'More options',
  description: 'the clip’s More options button (once on its page)',
};

/** The clip menu's item that opens a submenu: Remix or Edit. */
export function submenuItem(menu: SourceRoute['menu']): Target {
  return {
    role: 'menuitem',
    name: menu,
    popup: 'menu',
    description: `the ${menu} item in the clip’s menu`,
  };
}

/** The submenu that item opens, named by the item (`aria-labelledby`). */
export function submenu(menu: SourceRoute['menu']): Target {
  return { role: 'menu', name: menu, description: `the clip’s ${menu} menu` };
}

/** The action's item in its submenu. */
export function actionItem(route: SourceRoute): Target {
  return {
    role: 'menuitem',
    name: route.item,
    within: submenu(route.menu),
    description: `the ${route.item} item in the ${route.menu} menu`,
  };
}

// ---- The Create form with a source (TS-003: `page.create-source-advanced.html`, `-simple.html`).

/** The Audio section's condition button, named "Change condition type from <action>". */
export const AUDIO_CONDITION: Target = {
  role: 'button',
  name: /^Change condition type from /,
  description: 'the Audio section’s condition button (Change condition type from …)',
};

/** The loaded source's player in the Audio section, whose image is the source's thumbnail. */
export const AUDIO_PLAYER: Target = {
  role: 'button',
  name: 'Play audio',
  within: {
    around: AUDIO_CONDITION,
    levels: 7,
    description: 'the Audio section around its condition button',
  },
  description: 'the source’s player in the Audio section',
};

/** The Simple form's source chip: its thumbnail sits beside the chip's Remove button. */
export const SIMPLE_CHIP_THUMBNAIL: Target = {
  role: 'img',
  within: {
    around: { role: 'button', name: /^Remove /, description: 'the source chip’s Remove button' },
    levels: 1,
    description: 'the source chip above the Song description',
  },
  description: 'the source chip’s thumbnail',
};

/** The question Suno asks when the form already has lyrics and styles (TS-003). */
export const OVERWRITE_DIALOG: Target = {
  role: 'dialog',
  name: 'Overwrite Lyrics & Styles?',
  description: 'Suno’s "Overwrite Lyrics & Styles?" question',
};

export const OVERWRITE_BUTTON: Target = {
  role: 'button',
  name: 'Overwrite',
  within: OVERWRITE_DIALOG,
  description: 'the Overwrite button',
};

/** Whether a thumbnail address holds the clip's Suno ID (`…/image_<clip id>.jpeg`, TS-002). */
export function holdsClip(address: string | null, sunoId: string): boolean {
  if (address === null || sunoId === '') {
    return false;
  }
  try {
    return new URL(address).pathname.includes(sunoId);
  } catch {
    return false;
  }
}

/**
 * The verification rule (TS-002): in Advanced mode the Audio section's condition names the action
 * and its player's thumbnail holds the source clip's Suno ID; in Simple mode the chip's thumbnail
 * holds it (the chip does not name the action). Reads only.
 */
export function sourceShown(page: Page, mode: string, load: LoadedSource): Check {
  const label = load.route.label ?? load.route.item;
  if (mode === 'simple') {
    const chip = page.find(SIMPLE_CHIP_THUMBNAIL);
    if (chip.kind !== 'found') {
      return expected(`${SIMPLE_CHIP_THUMBNAIL.description}, showing the source`);
    }
    return holdsClip(page.imageAddress(chip.found), load.sunoId)
      ? OK
      : expected(`the source chip to show ${sourceName(load.source)}`);
  }
  const condition = page.find(AUDIO_CONDITION);
  if (condition.kind !== 'found') {
    return expected(`${AUDIO_CONDITION.description}, showing the source`);
  }
  // The button shows the action as its text ("Cover"), as its name says ("… from Cover").
  if (page.read(condition.found).text !== label) {
    return expected(`the Audio section to name the action ${label}`);
  }
  const player = page.find(AUDIO_PLAYER);
  if (player.kind !== 'found') {
    return expected(AUDIO_PLAYER.description);
  }
  return holdsClip(page.imageAddress(player.found), load.sunoId)
    ? OK
    : expected(`the Audio section to show ${sourceName(load.source)}`);
}
