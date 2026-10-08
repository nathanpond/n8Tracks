import type { EntryResult, FormJob, FormSource } from './fill.ts';
import type { Page, Region, Target } from './primitives.ts';
import { expected, OK, type Check } from './workflow.ts';

/**
 * Starting a generation from a source (#148, TS-002, TS-003): which of a Version's sources the
 * extension loads into Suno's Create form, the route a person takes to load one (the clip's own
 * page, its "More options" menu, then the action in the Remix or Edit menu), and how the loaded
 * source is verified before anything else is filled: the Audio section (Advanced) names the action,
 * and the source's thumbnail address holds the source clip's Suno ID (the Simple chip shows the
 * thumbnail only).
 *
 * What was captured decides what is built (decision D10). TS-003 captured the clip menus
 * (`page.clip-remix-menu.html`, `page.clip-edit-menu.html`), a loaded Cover
 * (`page.create-source-advanced.html`, `page.create-source-simple.html`), and the Overwrite dialog.
 * TS-005 (2026-10-08) captured the clip's own page (`page.clip-page.html`: its header's "More
 * options", its cover image holding the clip's ID), Suno's 404 for a clip that does not exist, and
 * the Advanced form after Extend, Mashup (one song), Sample this song, and Use as Inspiration (one
 * song), so those routes are taken by the extension in Advanced mode. In Simple mode only Cover's
 * chip is captured; the other actions there are loaded by hand. A Mashup's second song, and
 * Inspiration songs after the first, are added by hand (the form's "Add another song" and the
 * Inspo area were not captured in use), and all are verified before anything else is filled. A
 * playlist used as Inspiration stays by hand: the Inspo picker's dialog has no title, so the
 * forbidden-control matcher keeps refusing it (#133).
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
  /** Whether a snapshot shows the Advanced form after the action, so the extension may plan it. */
  captured: boolean;
  /** Whether a snapshot shows the Simple form after it too (only Cover's chip, TS-003). */
  simple: boolean;
  /**
   * Whether the extension takes the route itself: goes to the clip's page and presses its "More
   * options" menu (TS-005 captured the page). When false, the user loads the source by hand and the
   * extension verifies it on the form (#341).
   */
  automated: boolean;
}

/**
 * The menu route of each Suno action key a source can carry (`sunoAction`; a user type mapped to an
 * action arrives as that action's key, #126). Only Cover's and Reuse Prompt's results are captured.
 */
export const SOURCE_ROUTES: Readonly<Record<string, SourceRoute>> = {
  cover: {
    menu: 'Remix',
    item: 'Cover',
    label: 'Cover',
    captured: true,
    simple: true,
    automated: true,
  },
  reuse_prompt: {
    menu: 'Remix',
    item: 'Reuse Prompt',
    label: null,
    captured: true,
    simple: true,
    automated: true,
  },
  mashup: {
    menu: 'Remix',
    item: 'Mashup',
    label: 'Mashup',
    captured: true,
    simple: false,
    automated: true,
  },
  sample: {
    menu: 'Remix',
    item: 'Sample this song',
    label: 'Sample',
    captured: true,
    simple: false,
    automated: true,
  },
  extend: {
    menu: 'Edit',
    item: 'Extend',
    label: 'Extend',
    captured: true,
    simple: false,
    automated: true,
  },
};

/** A song used as Inspiration, from the Remix menu (TS-005: the Audio section names it Inspo). */
export const INSPIRATION_ROUTE: SourceRoute = {
  menu: 'Remix',
  item: 'Use as Inspiration',
  label: 'Inspo',
  captured: true,
  simple: false,
  automated: true,
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
    'the Inspo picker has no title the extension can recognise it by, and no snapshot shows a chosen playlist',
  'songs.simple.voice': 'no snapshot shows the Simple form’s Voice picker',
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

/**
 * The source the extension loads: the first audio source (or, with none, the first Inspiration
 * song), when its action's result is captured. `others` are the sources of the same entry the
 * user adds by hand on the form (a Mashup's second song, more Inspiration songs); all of them are
 * verified with it before anything else is filled.
 */
export interface LoadedSource {
  source: FormSource;
  sunoId: string;
  route: SourceRoute;
  others: readonly (FormSource & { sunoId: string })[];
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
  const inspiration = inspirationSources(job);
  const [first, ...rest] = audio.length > 0 ? audio : inspiration;
  if (first === undefined) {
    return { stop: null, load: null };
  }
  const route =
    audio.length === 0
      ? INSPIRATION_ROUTE
      : first.sunoAction === null
        ? undefined
        : SOURCE_ROUTES[first.sunoAction];
  // Two audio sources are a Mashup, and only a Mashup; Inspiration takes up to four songs.
  const together =
    audio.length === 0 || (route === SOURCE_ROUTES.mashup && audio.length === 2)
      ? rest
      : rest.length === 0
        ? []
        : null;
  const others = (together ?? []).filter(
    (other): other is FormSource & { sunoId: string } =>
      typeof other.sunoId === 'string' && other.sunoId !== '',
  );
  const sunoId = first.sunoId ?? null;
  if (
    route?.captured !== true ||
    (job.mode === 'simple' && !route.simple) ||
    others.length !== together?.length ||
    sunoId === null ||
    sunoId === ''
  ) {
    return { stop: null, load: null };
  }
  return { stop: null, load: { source: first, sunoId, route, others } };
}

/** Whether `source` is loaded (or verified with what is loaded) by the extension. */
function loadedWith(source: FormSource, load: LoadedSource | null): boolean {
  return load !== null && (load.source === source || load.others.some((other) => other === source));
}

/** Why one source is left to the user, as a step of the panel's note: the source by its title. */
function byHandStep(source: FormSource, load: LoadedSource | null): string | null {
  if (loadedWith(source, load)) {
    return null;
  }
  const name = sourceName(source);
  if (source.group === 'inspiration') {
    return `add ${name} as Inspiration (${INSPIRATION_ROUTE.item}) by hand: the extension loads Inspiration only when the Version has no audio source`;
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
    : `load ${name} with ${route.menu} › ${route.item} by hand: ${route.captured ? 'the extension does not load it in this mode, or with these other sources' : `no snapshot shows Suno’s form after ${route.item} yet`}`;
}

/**
 * The same step as n8Tracks stores it (#340): written text only, the source named generically and
 * an action the extension does not know left unnamed, so it carries none of the Version's text by
 * construction (the source guard in `test/verification-note-source.test.ts`, #379).
 */
function reportedByHandStep(source: FormSource, load: LoadedSource | null): string | null {
  if (loadedWith(source, load)) {
    return null;
  }
  if (source.group === 'inspiration') {
    return `add the source as Inspiration (${INSPIRATION_ROUTE.item}) by hand: the extension loads Inspiration only when the Version has no audio source`;
  }
  if (source.sunoId === null || source.sunoId === undefined || source.sunoId === '') {
    return 'load the source by hand: it is a Song in n8Tracks, not a Suno clip';
  }
  if (source.sunoAction === null) {
    return 'load the source by hand: its relationship type names no Suno action';
  }
  const route = SOURCE_ROUTES[source.sunoAction];
  return route === undefined
    ? 'load the source by hand: the extension does not know its Suno action'
    : `load the source with ${route.menu} › ${route.item} by hand: ${route.captured ? 'the extension does not load it in this mode, or with these other sources' : `no snapshot shows Suno’s form after ${route.item} yet`}`;
}

/** Why the extension cannot set a voice or a playlist entry. */
function blockedWhy(key: string): string {
  return SOURCES_BLOCKED_ON_CAPTURE[key] ?? 'the extension did not choose it';
}

function isStep(step: string | null): step is string {
  return step !== null;
}

/**
 * Everything of a source entry left to the user, as steps of the panel's note: the sources by their
 * titles, a file with its note, the voice and playlist by their names.
 */
function namedSteps(key: string, job: FormJob, load: LoadedSource | null): string[] {
  const value = job.voice?.key === key ? null : job.entries[key];
  const name = quoted(nameOf(value), 'the Version names');
  return [
    ...job.sources
      .filter((source) => source.key === key)
      .map((source) => byHandStep(source, load))
      .filter(isStep),
    ...job.fileInputs
      .filter((file) => file.key === key)
      .map(
        (file) =>
          `attach the audio file by hand${file.description === null ? '' : ` (${file.description})`}`,
      ),
    ...(value === undefined || value === null
      ? []
      : [
          key.endsWith('.voice')
            ? `choose the voice ${name} from + Voice by hand (${blockedWhy(key)})`
            : `add the playlist ${name} from + Inspo by hand (${blockedWhy(key)})`,
        ]),
  ];
}

/**
 * The same steps as n8Tracks stores them (#340): written text only, with the sources, the file note,
 * the voice and the playlist named generically.
 */
function reportedSteps(key: string, job: FormJob, load: LoadedSource | null): string[] {
  const value = job.voice?.key === key ? null : job.entries[key];
  return [
    ...job.sources
      .filter((source) => source.key === key)
      .map((source) => reportedByHandStep(source, load))
      .filter(isStep),
    ...job.fileInputs
      .filter((file) => file.key === key)
      .map((file) =>
        file.description === null
          ? 'attach the audio file by hand'
          : 'attach the audio file by hand (the Version’s file note says which)',
      ),
    ...(value === undefined || value === null
      ? []
      : [
          key.endsWith('.voice')
            ? `choose the voice the Version names from + Voice by hand (${blockedWhy(key)})`
            : `add the playlist the Version names from + Inspo by hand (${blockedWhy(key)})`,
        ]),
  ];
}

/**
 * The summary line of each source entry of the request's mode (`audio`, `voice`, `inspiration`,
 * `simple_add_playlist`): the loaded source's own outcome (`loaded`, from the verification) or the
 * chosen voice's (`job.voice`, #148), and
 * everything else of the entry as to do by hand, named in the panel's `note` (the voice and
 * playlist by their names, a file only the user can attach with its note) and generically in the
 * `reportNote` n8Tracks stores. An entry with nothing is not applicable.
 */
export function sourceEntryResult(
  key: string,
  job: FormJob,
  loaded: EntryResult | null = null,
): EntryResult {
  const plan = planSources(job);
  const steps = namedSteps(key, job, plan.load);
  const reported = reportedSteps(key, job, plan.load);
  const own = [loaded, job.voice ?? null].find((result) => result?.key === key) ?? null;
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
  const sentence = steps.join('; ');
  return `${sentence.charAt(0).toUpperCase()}${sentence.slice(1)}.`;
}

// ---- The clip's page and its menus (TS-003: `page.clip-remix-menu.html`, `page.clip-edit-menu.html`).

/**
 * The clip's cover image in its page's header (TS-005, `page.clip-page.html`): its address holds
 * the clip's Suno ID (`image_large_<id>.jpeg`), which is how the page is known to be the clip's.
 */
export const SONG_COVER: Target = {
  role: 'img',
  name: 'Song Cover Image',
  description: 'the clip’s cover image at the top of its page',
};

/**
 * The clip's "More options" button in its page's header. The page has one per song it lists (eleven
 * more in TS-005), so it is looked for only beside the cover image (#341).
 */
export const MORE_OPTIONS: Target = {
  role: 'button',
  name: 'More options',
  within: { around: SONG_COVER, levels: 3, description: 'the header of the clip’s page' },
  description: 'the clip’s More options button at the top of its page',
};

/** Suno's 404 page, shown at the address of a clip that does not exist (TS-005). */
export const CLIP_NOT_FOUND: Target = {
  role: 'heading',
  name: 'Page not found',
  description: 'Suno’s “Page not found”',
};

/**
 * What the clip's page shows, read only: the clip (its cover image holds `sunoId`), another clip,
 * Suno's 404, or not yet anything the extension knows. A clip in Suno's Trash shows its page as any
 * other (TS-005), so the Trash is known only from n8Tracks' last sync ({@link UNUSABLE}).
 */
export function clipPageShows(
  page: Page,
  sunoId: string,
): 'clip' | 'another clip' | 'not found' | 'nothing yet' {
  if (page.find(CLIP_NOT_FOUND).kind === 'found') {
    return 'not found';
  }
  const cover = page.find(SONG_COVER);
  if (cover.kind !== 'found') {
    return 'nothing yet';
  }
  return holdsClip(page.imageAddress(cover.found), sunoId) ? 'clip' : 'another clip';
}

/** The clip menu's item that opens a submenu: Remix or Edit. */
export function submenuItem(menu: SourceRoute['menu']): Target {
  return {
    role: 'menuitem',
    name: menu,
    popup: 'menu',
    description:
      menu === 'Remix' ? 'the Remix item in the clip’s menu' : 'the Edit item in the clip’s menu',
  };
}

/** The submenu that item opens, named by the item (`aria-labelledby`). */
export function submenu(menu: SourceRoute['menu']): Target {
  return {
    role: 'menu',
    name: menu,
    description: menu === 'Remix' ? 'the clip’s Remix menu' : 'the clip’s Edit menu',
  };
}

/** The action's item in its submenu. */
export function actionItem(route: SourceRoute): Target {
  return {
    role: 'menuitem',
    name: route.item,
    within: submenu(route.menu),
    description: 'the action’s item in the clip’s Remix or Edit menu',
  };
}

// ---- The Create form with a source (TS-003: `page.create-source-advanced.html`, `-simple.html`).

/** The Audio section's condition button, named "Change condition type from <action>". */
export const AUDIO_CONDITION: Target = {
  role: 'button',
  name: /^Change condition type from /,
  description: 'the Audio section’s condition button (Change condition type from …)',
};

const AUDIO_SECTION: Region = {
  around: AUDIO_CONDITION,
  levels: 7,
  description: 'the Audio section around its condition button',
};

/**
 * The loaded source's player in the Audio section, whose image is the source's thumbnail: "Play
 * audio" after Cover or Extend, "Play "<title>"" after Sample, Mashup, or Inspo (TS-005).
 */
export const AUDIO_PLAYER: Target = {
  role: 'button',
  name: /^Play( audio$| ")/,
  within: AUDIO_SECTION,
  description: 'the source’s player in the Audio section',
};

/** A Mashup's songs, in their own region ("Mashup songs. 1 of 2 songs selected.", TS-005). */
export const MASHUP_SONGS: Target = {
  role: 'region',
  name: /^Mashup songs\b/,
  description: 'the Mashup’s songs in the Audio section',
};

/** Each Mashup song's player. */
export const MASHUP_PLAYER: Target = {
  role: 'button',
  name: /^Play "/,
  within: MASHUP_SONGS,
  description: 'a Mashup song’s player',
};

/** The Extend's continue-at time ("Extend from 00:54.0"): the one editable text in the section. */
export const EXTEND_FROM: Target = {
  role: 'textbox',
  within: AUDIO_SECTION,
  description: 'the “Extend from” time in the Audio section',
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

/**
 * The question Suno asks when the form already has lyrics and styles (TS-003), or, loading an
 * instrumental clip, only about the styles ("Overwrite Styles?", TS-005).
 */
export const OVERWRITE_DIALOG: Target = {
  role: 'dialog',
  name: /^Overwrite (Lyrics & Styles|Styles)\?$/,
  description: 'Suno’s "Overwrite Lyrics & Styles?" or "Overwrite Styles?" question',
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
 * holds it (the chip does not name the action). Reads only. What it expected is written here and
 * names no value of the Version (#343): the source's title, the action, and the clip's ID stay out
 * of the step log and the diagnostic report.
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
      : expected('the source chip to show the source clip of the Version');
  }
  const condition = page.find(AUDIO_CONDITION);
  if (condition.kind !== 'found') {
    return expected(`${AUDIO_CONDITION.description}, showing the source`);
  }
  // The button shows the action as its text ("Cover"), as its name says ("… from Cover").
  if (page.read(condition.found).text !== label) {
    return expected('the Audio section to name the source action of the Version');
  }
  if (load.route === SOURCE_ROUTES.mashup || load.route === INSPIRATION_ROUTE) {
    // Every song of the entry, each by its thumbnail, and no other.
    const shown = page
      .readAll(load.route === INSPIRATION_ROUTE ? AUDIO_PLAYER : MASHUP_PLAYER)
      .map((player) => player.image);
    const wanted = [load.sunoId, ...load.others.map((other) => other.sunoId)];
    const missing = wanted.filter((id) => !shown.some((image) => holdsClip(image, id)));
    if (missing.length > 0 || shown.length !== wanted.length) {
      return expected(
        load.route === INSPIRATION_ROUTE
          ? 'the Audio section to show every Inspiration song of the Version, and no other'
          : 'the Mashup to show both source clips of the Version, and no other',
      );
    }
    return OK;
  }
  const player = page.find(AUDIO_PLAYER);
  if (player.kind !== 'found') {
    return expected(AUDIO_PLAYER.description);
  }
  return holdsClip(page.imageAddress(player.found), load.sunoId)
    ? OK
    : expected('the Audio section to show the source clip of the Version');
}

/** A continue-at time as Suno's "Extend from" shows it: minutes, seconds, and tenths ("00:54.0"). */
export function extendTime(seconds: number): string {
  const tenths = Math.round(seconds * 10);
  const minutes = Math.floor(tenths / 600);
  const rest = (tenths % 600) / 10;
  return `${String(minutes).padStart(2, '0')}:${rest.toFixed(1).padStart(4, '0')}`;
}

/** The seconds an "Extend from" time shows, or null when it is not one. */
export function extendSeconds(text: string): number | null {
  const match = /^(\d+):(\d{1,2}(?:\.\d+)?)$/.exec(text.trim());
  return match === null ? null : Number(match[1]) * 60 + Number(match[2]);
}

// ---- The Voice picker (TS-005: `page.voice-picker-with-source.html`, `page.create-voice-selected.html`).

/** The Advanced form's "+ Voice" button, which opens the Voice picker. */
export const ADD_VOICE: Target = {
  role: 'button',
  name: 'Add Voice',
  description: 'the “+ Voice” button',
};

export const VOICE_DIALOG: Target = {
  role: 'dialog',
  name: 'Voice',
  description: 'the Voice picker',
};

/**
 * A voice's title in the picker, which chooses it (pressing its image plays a sample instead). It
 * has no role, so it is found by its whole text, and only when exactly one voice has that name.
 */
export function voiceTitle(name: string): Target {
  return {
    role: 'text',
    name,
    within: VOICE_DIALOG,
    description: 'the voice’s title in the Voice picker',
  };
}

/** The chosen voice on the form: a link to its page, `/voice/<persona ID>`. */
export function chosenVoice(personaId: string): Target {
  return {
    role: 'link',
    address: `/voice/${personaId}`,
    description: 'the chosen voice on the form',
  };
}

/** Any chosen voice on the form: its row's "Remove selected Voice" button. */
export const VOICE_CHOSEN: Target = {
  role: 'button',
  name: 'Remove selected Voice',
  description: 'a chosen voice on the form',
};
