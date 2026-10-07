import { FIELD_MAP } from './fieldMap.ts';
import { sourceEntryResult } from './sources.ts';
import {
  ForbiddenControlError,
  StoppedError,
  type Found,
  type Page,
  type Target,
} from './primitives.ts';
import {
  ADVANCED_TAB,
  DURATION_SLIDER,
  EXCLUDE_STYLES,
  LYRICS_EDITOR,
  MAX_MODE_OFF,
  MAX_MODE_ON,
  MODEL_BUTTON,
  MODEL_MENU,
  PERSONALIZE_OFF,
  PERSONALIZE_ON,
  SIMPLE_TAB,
  SONG_DESCRIPTION,
  SONG_TITLE_BOXES,
  STYLE_INFLUENCE_SLIDER,
  STYLES_BOX,
  VARIETY_SLIDER,
  VOCAL_FEMALE,
  VOCAL_MALE,
  WEIRDNESS_SLIDER,
} from './songsForm.ts';
import {
  BPM_BOX,
  SOUND_DESCRIPTION,
  SOUNDS_MODEL_BUTTON,
  TYPE_LOOP,
  TYPE_ONE_SHOT,
} from './soundsForm.ts';
import {
  BACKGROUND_MUSIC_OFF,
  BACKGROUND_MUSIC_ON,
  SCRIPT_BOX,
  SPEECH_PROMPT,
  SPEECH_VARIETY_SLIDER,
  SPEECH_VOCAL_FEMALE,
  SPEECH_VOCAL_MALE,
  TONE_BOX,
} from './speechForm.ts';

/**
 * Filling Suno's Create form from a generation request (#146 Songs, #147 Speech and Sounds), and
 * the verification summary: what each entry of the adapter's field map (`fieldMap.ts`) for the
 * Version's kind and mode came to. A filler is registered here for each `fill` entry: it reads the
 * control, sets it as the user would, and reads it back. A failed entry is recorded and the next is still filled; the run never presses
 * Create (invariant 4), and the user is told to review the form and click Create.
 */

/** What became of one entry; `verified` is a source loaded and seen on the form (#148). */
export type EntryOutcome =
  'set' | 'verified' | 'failed' | 'unavailable' | 'manual' | 'not_applicable' | 'unsupported';

/** A value as the form shows it: text, a slider's number, a toggle, a choice, or nothing. */
export type FormValue = string | number | boolean | null;

/** One line of the verification summary. */
export interface EntryResult {
  /** The field-map entry (`songs.advanced.weirdness`), or an unsupported value's key. */
  key: string;
  outcome: EntryOutcome;
  /** What the Version asks the control to show, when one was compared. */
  expected?: FormValue;
  /** What the control showed, when one was compared and differed. */
  found?: FormValue;
  /** Text values are compared after normalising, and sent to n8Tracks as length and hash only. */
  text?: boolean;
  /** Plain words for the user: why, or what to do by hand. */
  note?: string;
  /**
   * The note as n8Tracks stores it when `note` quotes the Version's own text (a source's title, a
   * file note, a Voice or playlist name): the same words naming those generically. n8Tracks keeps
   * only the adapter's words and refuses a note that carries the Version's text (#340).
   */
  reportNote?: string;
}

/** A source of the request, as the summary names it and the source story (#148) loads it. */
export interface FormSource {
  key: string;
  title: string | null;
  sunoAction: string | null;
  /** `audio` or `inspiration`; audio when not given. */
  group?: string;
  /** Its place in its group, from 1. */
  position?: number;
  /** The source clip's Suno ID; null for a Song-level source, which is loaded by hand. */
  sunoId?: string | null;
  /** n8Tracks' availability of it: `ok`, `not_imported`, `deleted`, `trashed`, or `missing`. */
  availability?: string | null;
  /** Where an Extend continues from, in seconds. */
  continueAtSeconds?: number | null;
}

/** A file input of the Version, which only the user can attach. */
export interface FormFileInput {
  key: string;
  description: string | null;
}

/** What the extension fills from: the request's snapshot, read by the service worker (#144). */
export interface FormJob {
  /** `song`, `speech`, or `sound`. */
  kind: string;
  /** `simple` or `advanced` for a Song. */
  mode: string;
  entries: Readonly<Record<string, unknown>>;
  sources: readonly FormSource[];
  fileInputs: readonly FormFileInput[];
  /** Keys of the Version's values that have no field-map entry. */
  unsupported: readonly string[];
  /** The loaded source's outcome (#148), once it was loaded and verified on the form. */
  loaded?: EntryResult | null;
}

/** How long a set value may take to show (TS-003 read-back: 300 ms, three times). */
export const READ_BACK_MS = 300;
export const READ_BACK_TRIES = 3;

/** How long one fill may run before it stops (the Discretion's two minutes). */
export const FILL_LIMIT_MS = 120_000;

/** The workspace entry: chosen by the workspace step (#145), listed under Simple for both modes. */
export const WORKSPACE_ENTRY = 'songs.simple.workspace';

/**
 * `fill` entries whose controls no TS-003 snapshot shows, so the adapter does not set them
 * (decision D9): Simple's Add Lyrics and Add Styles sections (TS-003 did not exercise them),
 * Duration's Auto or Custom mode (the snapshot shows the slider, not how Auto is shown), and the
 * Sounds Key and Key scale (no snapshot shows the Key picker's popover, with its notes, Any,
 * Major/Minor, and Apply). The summary tells the user to do them by hand until the owner captures
 * those page states.
 */
export const BLOCKED_ON_CAPTURE: ReadonlySet<string> = new Set([
  'songs.simple.simple_add_lyrics',
  'songs.simple.simple_add_styles',
  'songs.advanced.duration_mode',
  'sounds.single.sound_key',
  'sounds.single.sound_scale',
]);

/** Variety's steps on the slider, 0 to 4 (`docs/suno-import-field-map.json`). */
export const VARIETY_STEPS: readonly string[] = ['off', 'normal', 'high', 'extra', 'max'];

type Wanted =
  | { kind: 'value'; value: FormValue }
  | { kind: 'not_applicable'; note: string }
  | { kind: 'failed'; note: string };

type Shown = { kind: 'value'; value: FormValue } | { kind: 'absent' } | { kind: 'disabled' };

/** A filler: one `fill` entry, set and read back through the primitives. */
export interface Filler {
  entry: string;
  /** Plain words for the control, in the summary's notes. */
  control: string;
  /** Whether the value is text, compared normalised and reported as length and hash. */
  text: boolean;
  /** The value the Version asks for, from the request's value of this entry. */
  wanted(value: unknown, job: FormJob): Wanted;
  /** What the form shows; given what is wanted, a value that differs from it is preferred. */
  read(page: Page, wanted: FormValue): Shown;
  /** Sets the control; `unavailable` when the form does not offer the value. */
  write(page: Page, value: FormValue): { unavailable: string } | undefined;
}

/** How many characters `text` has, a character outside the Basic Multilingual Plane counting once. */
export function characterCount(text: string): number {
  return text.replace(/[\uD800-\uDBFF][\uDC00-\uDFFF]/g, '_').length;
}

/** Text as compared: Unix line endings, no white space at line ends, no trailing blank lines. */
export function normalisedText(text: string): string {
  return text
    .replace(/\r\n?/g, '\n')
    .split('\n')
    .map((line) => line.trimEnd())
    .join('\n')
    .trimEnd();
}

function same(a: FormValue, b: FormValue, text: boolean): boolean {
  if (text) {
    return normalisedText(String(a ?? '')) === normalisedText(String(b ?? ''));
  }
  return a === b;
}

function locate(page: Page, target: Target): Found | 'absent' | 'disabled' {
  const result = page.find(target);
  if (result.kind !== 'found') {
    return 'absent';
  }
  return page.read(result.found).enabled ? result.found : 'disabled';
}

function shown(page: Page, target: Target, value: (found: Found) => FormValue): Shown {
  const found = locate(page, target);
  return typeof found === 'string' ? { kind: found } : { kind: 'value', value: value(found) };
}

function wantedText(value: unknown): Wanted {
  if (value === null || value === undefined) {
    return { kind: 'value', value: '' };
  }
  return typeof value === 'string'
    ? { kind: 'value', value }
    : { kind: 'failed', note: 'The Version’s value is not text.' };
}

function wantedNumber(minimum: number, maximum: number): (value: unknown) => Wanted {
  return (value) =>
    typeof value === 'number' && Number.isInteger(value) && value >= minimum && value <= maximum
      ? { kind: 'value', value }
      : { kind: 'failed', note: 'The Version’s value is outside what the slider takes.' };
}

function wantedSwitch(value: unknown): Wanted {
  return typeof value === 'boolean'
    ? { kind: 'value', value }
    : { kind: 'failed', note: 'The Version’s value is not on or off.' };
}

/** A text box or text area: the native setter, as typing would. */
function textBox(entry: string, target: Target): Filler {
  return {
    entry,
    control: target.description,
    text: true,
    wanted: wantedText,
    read: (page) => shown(page, target, (found) => page.read(found).value ?? ''),
    write: (page, value) => {
      const found = locate(page, target);
      if (typeof found !== 'string') {
        page.set(found, String(value ?? ''));
      }
    },
  };
}

/** A slider moved with arrow keys (TS-003); its `aria-valuenow` is the value. */
function slider(entry: string, target: Target, wanted: Filler['wanted']): Filler {
  return {
    entry,
    control: target.description,
    text: false,
    wanted,
    read: (page) =>
      shown(page, target, (found) => {
        const value = Number(page.read(found).value);
        return Number.isFinite(value) ? value : null;
      }),
    write: (page, value) => {
      const found = locate(page, target);
      if (typeof found !== 'string') {
        page.set(found, Number(value));
      }
    },
  };
}

/** Two buttons, Off and On, of which the selected one is the value (Max Mode, Personalize). */
function onOff(entry: string, off: Target, on: Target, control: string): Filler {
  return {
    entry,
    control,
    text: false,
    wanted: wantedSwitch,
    read: (page) => {
      const offFound = locate(page, off);
      const onFound = locate(page, on);
      if (typeof offFound === 'string' || typeof onFound === 'string') {
        return { kind: offFound === 'absent' || onFound === 'absent' ? 'absent' : 'disabled' };
      }
      if (page.read(onFound).selected === true) {
        return { kind: 'value', value: true };
      }
      return { kind: 'value', value: page.read(offFound).selected === true ? false : null };
    },
    write: (page, value) => {
      const found = locate(page, value === true ? on : off);
      if (typeof found !== 'string' && page.read(found).selected !== true) {
        page.click(found);
      }
    },
  };
}

const VOCAL_BUTTONS: readonly [string, Target][] = [
  ['male', VOCAL_MALE],
  ['female', VOCAL_FEMALE],
];

/** Vocal Gender: Male, Female, or neither selected (None deselects the selected one). */
const vocalGender: Filler = {
  entry: 'songs.advanced.vocal_gender',
  control: 'the Vocal Gender choice in More Options',
  text: false,
  wanted: (value) =>
    value === null || value === undefined
      ? { kind: 'value', value: null }
      : typeof value === 'string' && VOCAL_BUTTONS.some(([name]) => name === value)
        ? { kind: 'value', value }
        : { kind: 'failed', note: 'The Version’s value is not Male, Female, or None.' },
  read: (page) => {
    let selected: FormValue = null;
    for (const [name, target] of VOCAL_BUTTONS) {
      const found = locate(page, target);
      if (typeof found === 'string') {
        return { kind: found };
      }
      if (page.read(found).selected === true) {
        selected = name;
      }
    }
    return { kind: 'value', value: selected };
  },
  write: (page, value) => {
    for (const [name, target] of VOCAL_BUTTONS) {
      const found = locate(page, target);
      if (typeof found === 'string') {
        continue;
      }
      const selected = page.read(found).selected === true;
      // The wanted one is pressed when not selected; the other is pressed (deselected) when it is.
      if ((name === value) !== selected) {
        page.click(found);
      }
    }
  },
};

/**
 * The model, by the model list entry's Suno label (#114): the model button shows the chosen
 * model's label. Another model is chosen from the menu the button opens; a model the menu does
 * not offer is unavailable. No snapshot shows the menu open, so it is known by its role only.
 */
function model(entry: string, button: Target = MODEL_BUTTON): Filler {
  return {
    entry,
    control: button.description,
    text: false,
    wanted: (value) =>
      value === null || value === undefined
        ? { kind: 'not_applicable', note: 'The Version names no model; Suno keeps its own.' }
        : typeof value === 'string'
          ? { kind: 'value', value }
          : { kind: 'failed', note: 'The Version’s model is not a name.' },
    read: (page) => shown(page, button, (found) => page.read(found).text),
    write: (page, value) => {
      const found = locate(page, button);
      if (typeof found === 'string') {
        return;
      }
      if (page.read(found).expanded !== true) {
        page.click(found);
      }
      const menu = page.find(MODEL_MENU);
      if (menu.kind !== 'found') {
        return { unavailable: 'Suno’s model menu did not open, so choose the model by hand.' };
      }
      try {
        page.choose(menu.found, String(value));
      } catch (error) {
        if (error instanceof ForbiddenControlError || error instanceof StoppedError) {
          throw error;
        }
        return { unavailable: 'Suno’s model menu does not offer this model.' };
      }
      return undefined;
    },
  };
}

/** The Lyrics editor (Lexical): typed in through the browser's editing commands. */
const lyrics: Filler = {
  entry: 'songs.advanced.lyrics',
  control: LYRICS_EDITOR.description,
  text: true,
  wanted: wantedText,
  read: (page) => shown(page, LYRICS_EDITOR, (found) => page.read(found).value ?? ''),
  write: (page, value) => {
    const found = locate(page, LYRICS_EDITOR);
    if (typeof found !== 'string') {
      page.typeText(found, String(value ?? ''));
    }
  },
};

/** Song Title: each of the form's two shared title boxes that is shown is set. */
const title: Filler = {
  entry: 'songs.advanced.title',
  control: 'the Song Title box',
  text: true,
  wanted: wantedText,
  read: (page, wanted) => {
    const values: string[] = [];
    let disabled = false;
    for (const box of SONG_TITLE_BOXES) {
      const found = locate(page, box);
      if (found === 'disabled') {
        disabled = true;
      } else if (found !== 'absent') {
        values.push(page.read(found).value ?? '');
      }
    }
    const [first] = values;
    if (first === undefined) {
      return { kind: disabled ? 'disabled' : 'absent' };
    }
    const differing = values.find((value) => !same(value, wanted, true));
    return { kind: 'value', value: differing ?? first };
  },
  write: (page, value) => {
    for (const box of SONG_TITLE_BOXES) {
      const found = locate(page, box);
      if (typeof found !== 'string') {
        page.set(found, String(value ?? ''));
      }
    }
  },
};

/** Variety, by its step name, as the slider's step (Songs and Speech alike). */
function wantedVariety(value: unknown): Wanted {
  const step = typeof value === 'string' ? VARIETY_STEPS.indexOf(value) : -1;
  return step < 0
    ? { kind: 'failed', note: 'The Version’s Variety is not one of Suno’s steps.' }
    : { kind: 'value', value: step };
}

/**
 * A choice of buttons of which the selected one is the value (Speech's Vocal Gender, Sounds'
 * Type). With `none`, nothing selected is a value too: the selected button is pressed again to
 * deselect it, as Songs' Vocal Gender does; the read-back says whether Suno let it.
 */
function choice(
  entry: string,
  control: string,
  buttons: readonly (readonly [string, Target])[],
  none: boolean,
): Filler {
  return {
    entry,
    control,
    text: false,
    wanted: (value) =>
      none && (value === null || value === undefined)
        ? { kind: 'value', value: null }
        : typeof value === 'string' && buttons.some(([name]) => name === value)
          ? { kind: 'value', value }
          : { kind: 'failed', note: 'The Version’s value is not one Suno’s form offers here.' },
    read: (page) => {
      let selected: FormValue = null;
      for (const [name, target] of buttons) {
        const found = locate(page, target);
        if (typeof found === 'string') {
          return { kind: found };
        }
        if (page.read(found).selected === true) {
          selected = name;
        }
      }
      return { kind: 'value', value: selected };
    },
    write: (page, value) => {
      for (const [name, target] of buttons) {
        const found = locate(page, target);
        if (typeof found === 'string') {
          continue;
        }
        const selected = page.read(found).selected === true;
        // The wanted one is pressed when not selected; with `none`, a selected other is pressed
        // to deselect it. Without it, pressing the wanted one moves the selection.
        if (name === value ? !selected : none && selected) {
          page.click(found);
        }
      }
    },
  };
}

/**
 * Sounds' BPM: a number box whose empty value shows Auto. Empty (null) is Auto; any whole number
 * is typed in, out of Suno's range too, and the read-back reports what Suno kept.
 */
const soundBpm: Filler = {
  entry: 'sounds.single.sound_bpm',
  control: BPM_BOX.description,
  text: false,
  wanted: (value) =>
    value === null || value === undefined
      ? { kind: 'value', value: null }
      : typeof value === 'number' && Number.isInteger(value)
        ? { kind: 'value', value }
        : { kind: 'failed', note: 'The Version’s BPM is not a whole number.' },
  read: (page) =>
    shown(page, BPM_BOX, (found) => {
      const text = (page.read(found).value ?? '').trim();
      const value = Number(text);
      return text === '' ? null : Number.isFinite(value) ? value : text;
    }),
  write: (page, value) => {
    const found = locate(page, BPM_BOX);
    if (typeof found !== 'string') {
      page.set(found, value === null ? '' : String(value));
    }
  },
};

/** Speech's and Sounds' fillers (#147), by entry; Key and Key scale are blocked on a capture. */
const SPEECH_AND_SOUNDS_FILLERS: readonly Filler[] = [
  textBox('speech.simple.speech_prompt', SPEECH_PROMPT),
  textBox('speech.advanced.speech_script', SCRIPT_BOX),
  textBox('speech.advanced.speech_tone', TONE_BOX),
  choice(
    'speech.advanced.speech_vocal_gender',
    'the Vocal Gender choice in the Speech form',
    [
      ['male', SPEECH_VOCAL_MALE],
      ['female', SPEECH_VOCAL_FEMALE],
    ],
    true,
  ),
  onOff(
    'speech.advanced.speech_background_music',
    BACKGROUND_MUSIC_OFF,
    BACKGROUND_MUSIC_ON,
    'the Background music switch',
  ),
  slider('speech.advanced.speech_variety', SPEECH_VARIETY_SLIDER, wantedVariety),
  model('sounds.single.sounds_model', SOUNDS_MODEL_BUTTON),
  textBox('sounds.single.sound_description', SOUND_DESCRIPTION),
  choice(
    'sounds.single.sound_type',
    'the Type choice in Advanced Options',
    [
      ['one_shot', TYPE_ONE_SHOT],
      ['loop', TYPE_LOOP],
    ],
    false,
  ),
  soundBpm,
];

/** One filler per `fill` entry of the field map, except those blocked on a capture. */
export const FILLERS: readonly Filler[] = [
  model('songs.simple.model'),
  textBox('songs.simple.simple_prompt', SONG_DESCRIPTION),
  model('songs.advanced.model'),
  lyrics,
  textBox('songs.advanced.styles', STYLES_BOX),
  textBox('songs.advanced.exclude_styles', EXCLUDE_STYLES),
  vocalGender,
  slider('songs.advanced.duration_seconds', DURATION_SLIDER, (value, job) =>
    job.entries['songs.advanced.duration_mode'] === 'custom'
      ? wantedNumber(10, 360)(value)
      : { kind: 'not_applicable', note: 'Duration is Auto, so no length is set.' },
  ),
  onOff('songs.advanced.max_mode', MAX_MODE_OFF, MAX_MODE_ON, 'the Max Mode switch'),
  slider('songs.advanced.weirdness', WEIRDNESS_SLIDER, wantedNumber(0, 100)),
  slider('songs.advanced.style_influence', STYLE_INFLUENCE_SLIDER, wantedNumber(0, 100)),
  slider('songs.advanced.variety', VARIETY_SLIDER, wantedVariety),
  onOff('songs.advanced.personalize', PERSONALIZE_OFF, PERSONALIZE_ON, 'the Personalize switch'),
  title,
  ...SPEECH_AND_SOUNDS_FILLERS,
];

/** The mode tab of a Song's mode. */
export function modeTab(mode: string): Target {
  return mode === 'simple' ? SIMPLE_TAB : ADVANCED_TAB;
}

/** The field map's tab of each kind of Version. */
const TABS: Readonly<Record<string, string>> = { song: 'songs', speech: 'speech', sound: 'sounds' };

/**
 * The field-map entries the summary lists for a Version of `kind` in `mode`, in the map's order:
 * the entries of the kind's tab and mode, and for a Song the workspace (a Songs entry; a Speech or
 * a Sound lists only its own, so a Simple Speech lists its one entry).
 */
export function summaryEntries(mode: string, kind = 'song'): string[] {
  const prefix = `${TABS[kind] ?? kind}.${mode}.`;
  return FIELD_MAP.filter(
    (entry) =>
      entry.entry.startsWith(prefix) || (kind === 'song' && entry.entry === WORKSPACE_ENTRY),
  ).map((entry) => entry.entry);
}

/** A `source` entry: the loaded source's outcome, and the rest named as to do by hand (#148). */
function sourceResult(key: string, job: FormJob): EntryResult {
  return sourceEntryResult(key, job, job.loaded ?? null);
}

/** A `manual` entry: a file only the user can attach, with the Version's note. */
function manualResult(key: string, job: FormJob): EntryResult {
  const files = job.fileInputs.filter((file) => file.key === key);
  if (files.length === 0) {
    return { key, outcome: 'not_applicable' };
  }
  const notes = files.map((file) => file.description).filter((note) => note !== null);
  return notes.length === 0
    ? { key, outcome: 'manual', note: 'Attach the file by hand.' }
    : {
        key,
        outcome: 'manual',
        note: `Attach the file by hand: ${notes.join('; ')}.`,
        reportNote: 'Attach the file by hand: the Version’s file note says which.',
      };
}

/** An entry blocked on a capture (D9): never set by the adapter, so told to the user. */
function blockedResult(key: string, job: FormJob): EntryResult {
  const value = job.entries[key];
  switch (key) {
    case 'sounds.single.sound_key':
      return {
        key,
        outcome: 'manual',
        expected: typeof value === 'string' ? value : 'any',
        note: 'Choose the key in Suno’s Key picker, then press Apply: the extension cannot use the Key picker yet.',
      };
    case 'sounds.single.sound_scale': {
      const soundKey = job.entries['sounds.single.sound_key'];
      if (typeof soundKey !== 'string' || soundKey.toLowerCase() === 'any') {
        return { key, outcome: 'not_applicable', note: 'Key is Any, so no scale is chosen.' };
      }
      return {
        key,
        outcome: 'manual',
        expected: typeof value === 'string' ? value : null,
        note: 'Choose the scale with the key in Suno’s Key picker, then press Apply.',
      };
    }
    case 'songs.advanced.duration_mode':
      return {
        key,
        outcome: 'manual',
        expected: typeof value === 'string' ? value : null,
        note:
          value === 'custom'
            ? 'Check that Duration is set to the length below, not Auto: the extension cannot read Suno’s Duration mode yet.'
            : 'Set Duration to Auto by hand: the extension cannot read Suno’s Duration mode yet.',
      };
    default: {
      const section = key.endsWith('lyrics') ? 'Lyrics' : 'Styles';
      if (typeof value !== 'string') {
        return {
          key,
          outcome: 'not_applicable',
          note: `The Version adds no ${section} section. The extension cannot see Simple’s added sections yet, so check that none is added.`,
        };
      }
      return {
        key,
        outcome: 'manual',
        expected: value,
        text: true,
        note: `The extension cannot add Simple’s ${section} section yet: add it from the + menu and enter the Version’s ${section.toLowerCase()}.`,
      };
    }
  }
}

/** What each entry that is not filled comes to; null for an entry with a filler. */
export function unfilledResult(key: string, job: FormJob, workspace: string | null): EntryResult {
  if (key === WORKSPACE_ENTRY) {
    return workspace === null
      ? { key, outcome: 'not_applicable' }
      : { key, outcome: 'set', note: 'Selected by the workspace step.' };
  }
  if (BLOCKED_ON_CAPTURE.has(key)) {
    return blockedResult(key, job);
  }
  const how = FIELD_MAP.find((entry) => entry.entry === key)?.how;
  if (how === 'source') {
    return sourceResult(key, job);
  }
  if (how === 'manual') {
    return manualResult(key, job);
  }
  return {
    key,
    outcome: 'unavailable',
    note: 'The extension has no filler for this entry.',
  };
}

/** The Version's values with no field-map entry. */
export function unsupportedResults(job: FormJob): EntryResult[] {
  return job.unsupported.map((key) => ({
    key,
    outcome: 'unsupported',
    note: 'Suno’s form has no place the extension knows for this value.',
  }));
}

function rethrowStops(error: unknown): void {
  if (error instanceof ForbiddenControlError || error instanceof StoppedError) {
    throw error;
  }
}

function compared(
  filler: Filler,
  wanted: FormValue,
  outcome: EntryOutcome,
  found: Shown,
): EntryResult {
  const result: EntryResult = { key: filler.entry, outcome, expected: wanted };
  if (filler.text) {
    result.text = true;
  }
  if (outcome === 'failed') {
    result.found = found.kind === 'value' ? found.value : null;
    if (found.kind !== 'value') {
      result.note = `${filler.control} went from the page while it was set.`;
    }
  }
  return result;
}

function unavailable(filler: Filler, wanted: FormValue, shownAs: Shown | string): EntryResult {
  const note =
    typeof shownAs === 'string'
      ? shownAs
      : shownAs.kind === 'disabled'
        ? `Suno shows ${filler.control} disabled (it may need a paid plan).`
        : `Suno’s form does not show ${filler.control}.`;
  return {
    key: filler.entry,
    outcome: 'unavailable',
    expected: wanted,
    ...(filler.text ? { text: true } : {}),
    note,
  };
}

/**
 * Sets one entry and reads it back: the control is set only when it differs (so a toggle is never
 * pressed off by mistake), and the read-back waits up to three times 300 ms for the value to show.
 */
export async function fillEntry(page: Page, filler: Filler, job: FormJob): Promise<EntryResult> {
  const wanted = filler.wanted(job.entries[filler.entry], job);
  if (wanted.kind !== 'value') {
    return { key: filler.entry, outcome: wanted.kind, note: wanted.note };
  }
  const before = filler.read(page, wanted.value);
  if (before.kind !== 'value') {
    return unavailable(filler, wanted.value, before);
  }
  if (!same(before.value, wanted.value, filler.text)) {
    try {
      const written = filler.write(page, wanted.value);
      if (written !== undefined) {
        return unavailable(filler, wanted.value, written.unavailable);
      }
    } catch (error) {
      rethrowStops(error);
      // The read-back below says what the control shows now.
    }
  }
  const settled = await page.wait(() => {
    const now = filler.read(page, wanted.value);
    return now.kind === 'value' && same(now.value, wanted.value, filler.text);
  }, READ_BACK_MS * READ_BACK_TRIES);
  return compared(
    filler,
    wanted.value,
    settled ? 'set' : 'failed',
    filler.read(page, wanted.value),
  );
}

/** Reads one entry without changing anything (Check again). */
export function checkEntry(page: Page, filler: Filler, job: FormJob): EntryResult {
  const wanted = filler.wanted(job.entries[filler.entry], job);
  if (wanted.kind !== 'value') {
    return { key: filler.entry, outcome: wanted.kind, note: wanted.note };
  }
  const now = filler.read(page, wanted.value);
  if (now.kind !== 'value') {
    return unavailable(filler, wanted.value, now);
  }
  return compared(
    filler,
    wanted.value,
    same(now.value, wanted.value, filler.text) ? 'set' : 'failed',
    now,
  );
}

/**
 * Every entry of the Version's kind and mode, in the map's order, then the unsupported values: each filler's
 * entry set (or, with `change` false, only read), the others as {@link unfilledResult} says.
 */
export async function verifyForm(
  page: Page,
  job: FormJob,
  workspace: string | null,
  change: boolean,
  fillers: readonly Filler[] = FILLERS,
): Promise<EntryResult[]> {
  const results: EntryResult[] = [];
  for (const key of summaryEntries(job.mode, job.kind)) {
    const filler = fillers.find((candidate) => candidate.entry === key);
    if (filler === undefined) {
      results.push(unfilledResult(key, job, workspace));
      continue;
    }
    try {
      results.push(change ? await fillEntry(page, filler, job) : checkEntry(page, filler, job));
    } catch (error) {
      rethrowStops(error);
      results.push({
        key,
        outcome: 'failed',
        note: `${filler.control} could not be read.`,
      });
    }
  }
  return [...results, ...unsupportedResults(job)];
}

/** A value as n8Tracks stores it: text as its length and SHA-256, anything else as it is. */
export type ReportedValue = FormValue | { length: number; sha256: string };

export interface ReportedEntry {
  key: string;
  outcome: EntryOutcome;
  expected?: ReportedValue;
  found?: ReportedValue;
  note?: string;
}

/** The summary as the PATCH carries it (`verification`). */
export interface VerificationReport {
  adapterVersion: number;
  mode: string;
  checkedAt: string;
  entries: ReportedEntry[];
}

async function sha256(text: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, '0')).join('');
}

async function reported(value: FormValue, text: boolean): Promise<ReportedValue> {
  if (!text || value === null) {
    return value;
  }
  const normalised = normalisedText(String(value));
  return { length: characterCount(normalised), sha256: await sha256(normalised) };
}

/**
 * The summary for n8Tracks: text values (lyrics, styles, prompts, titles) only as length and hash
 * (invariant 6), everything else as it is; notes are the adapter's own words, never the Version's
 * text (`reportNote` where the panel's note quotes it, #340).
 */
export async function verificationReport(
  results: readonly EntryResult[],
  mode: string,
  adapterVersion: number,
  checkedAt: Date,
): Promise<VerificationReport> {
  const entries: ReportedEntry[] = [];
  for (const result of results) {
    const entry: ReportedEntry = { key: result.key, outcome: result.outcome };
    if (result.expected !== undefined) {
      entry.expected = await reported(result.expected, result.text === true);
    }
    if (result.found !== undefined) {
      entry.found = await reported(result.found, result.text === true);
    }
    const note = result.reportNote ?? result.note;
    if (note !== undefined) {
      entry.note = note;
    }
    entries.push(entry);
  }
  return { adapterVersion, mode, checkedAt: checkedAt.toISOString(), entries };
}
