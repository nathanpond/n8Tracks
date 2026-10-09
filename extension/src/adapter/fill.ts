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
  ADD_BUTTON,
  addMenuItem,
  ADVANCED_TAB,
  CHIP_THUMBNAIL,
  DURATION_AUTO,
  DURATION_CUSTOM,
  DURATION_SLIDER,
  EXCLUDE_STYLES,
  LYRICS_EDITOR,
  MAX_MODE_OFF,
  MAX_MODE_ON,
  MODEL_BUTTON,
  modelItem,
  modelMenu,
  PERSONALIZE_OFF,
  PERSONALIZE_ON,
  SECTION_CHIP_REMOVE,
  sectionDialog,
  sectionDialogClose,
  sectionEditor,
  SIMPLE_TAB,
  type SimpleSection,
  SONG_DESCRIPTION,
  SONG_TITLE_BOXES,
  STYLE_INFLUENCE_SLIDER,
  STYLES_BOX,
  VARIETY_SLIDER,
  VOCAL_FEMALE,
  VOCAL_MALE,
  WEIRDNESS_SLIDER,
  writeNewItem,
} from './songsForm.ts';
import {
  BPM_BOX,
  KEY_APPLY,
  KEY_BUTTON,
  KEY_POPOVER,
  keyChoice,
  scaleTab,
  SOUND_DESCRIPTION,
  SOUNDS_MODEL_BUTTON,
  TYPE_LOOP,
  TYPE_ONE_SHOT,
} from './soundsForm.ts';
import { KEY_NOTES } from './forbidden.ts';
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
  /**
   * Plain words for the user: why, or what to do by hand. Without `reportNote`, n8Tracks stores it
   * too, so it is then text written in the source (#379).
   */
  note?: string;
  /**
   * The note as n8Tracks stores it when `note` quotes the Version's own text (a source's title, a
   * file note, a Voice or playlist name): the same words naming those generically (#340). It is text
   * written in the source, with only the adapter's own constants in it, so it carries none of the
   * Version's text by construction; `test/verification-note-source.test.ts` checks every note that
   * can reach n8Tracks (#379), and n8Tracks checks only that a note is one line.
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
  /** The Voice's outcome (#148), once the extension chose it, or found it chosen, on the form. */
  voice?: EntryResult | null;
}

/** How long a set value may take to show (TS-003 read-back: 300 ms, three times). */
export const READ_BACK_MS = 300;
export const READ_BACK_TRIES = 3;

/** How long one fill may run before it stops (the Discretion's two minutes). */
export const FILL_LIMIT_MS = 120_000;

/** The workspace entry: chosen by the workspace step (#145), listed under Simple for both modes. */
export const WORKSPACE_ENTRY = 'songs.simple.workspace';

/**
 * `fill` entries whose controls no snapshot shows, so the adapter has no filler for them (decision
 * D9). None is left: TS-005 (2026-10-08) captured Simple's Lyrics and Styles sections, Duration's
 * Auto and Custom, and the Sounds Key popover, and TS-006 (2026-10-08) the model menu (#339). An
 * entry listed here again must have no filler (the coverage test checks it).
 */
export const BLOCKED_ON_CAPTURE: ReadonlySet<string> = new Set<string>();

/**
 * The models Suno's model menu offered when TS-006 captured it (2026-10-08), as its items name
 * them. A Version's model outside this list (an older one, such as v4.5) is failed without the
 * menu being opened: the extension never guesses a model.
 */
export const MODEL_LABELS: readonly string[] = ['v6', 'v6-wild', 'v6-mini'];

/** Variety's steps on the slider, 0 to 4 (`docs/suno-import-field-map.json`). */
export const VARIETY_STEPS: readonly string[] = ['off', 'normal', 'high', 'extra', 'max'];

type Wanted =
  | { kind: 'value'; value: FormValue }
  | { kind: 'not_applicable'; note: string }
  | { kind: 'failed'; note: string };

/**
 * What a control shows. A value may carry the adapter's own words on why it is not the Version's
 * (`reportNote`, checked as a note where it is written), which the summary gives when the entry
 * fails (a section on the form the Version does not have).
 */
type Shown =
  | { kind: 'value'; value: FormValue; reportNote?: string }
  | { kind: 'absent' }
  | { kind: 'disabled' };

type Written = { unavailable: string } | undefined;

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
  read(page: Page, wanted: FormValue, job: FormJob): Shown;
  /**
   * Sets the control; `unavailable` when the form does not offer the value. A control reached
   * through a menu or a popover waits for each part to show (TS-005).
   */
  write(page: Page, value: FormValue, job: FormJob): Written | Promise<Written>;
  /**
   * The adapter's own words given with a `set` outcome: what Suno did besides (TS-005). Named as a
   * note n8Tracks may store, so it is checked as written text where it is made.
   */
  reportNote?: string;
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

/** How long a menu, submenu, dialog, or popover may take to show after its control is pressed. */
export const SHOW_WAIT_MS = 2_000;

/** Presses `target` when it is on the page once and enabled; false when it is not. */
function pressIfThere(page: Page, target: Target): boolean {
  const found = locate(page, target);
  if (typeof found === 'string') {
    return false;
  }
  page.click(found);
  return true;
}

/** Waits for `target` to be on the page once; false when it did not show in time. */
async function shows(page: Page, target: Target): Promise<boolean> {
  return page.wait(() => page.find(target).kind === 'found', SHOW_WAIT_MS);
}

/** The Simple form's section entries. */
const SECTION_ENTRY: Readonly<Record<SimpleSection, string>> = {
  Lyrics: 'songs.simple.simple_add_lyrics',
  Styles: 'songs.simple.simple_add_styles',
};

/** A section's text as its chip's Remove button names it: white space collapsed. */
function chipText(text: string): string {
  return text.replace(/\s+/g, ' ').trim();
}

/**
 * The section chips above the Song description (TS-005): each chip's text, from its "Remove …"
 * button, and how many of the chips are not explained by the Version's Lyrics and Styles (a section
 * left on the form from an earlier use). A source chip, with its thumbnail, is not a section.
 */
function sectionChips(page: Page, job: FormJob): { texts: string[]; strays: number } {
  const texts = page
    .readAll(SECTION_CHIP_REMOVE)
    .map((entry) => chipText(entry.name.replace(/^Remove /, '')));
  const sources = page.readAll(CHIP_THUMBNAIL).length;
  const explained = (['Lyrics', 'Styles'] as const).filter((section) => {
    const value = job.entries[SECTION_ENTRY[section]];
    return typeof value === 'string' && value !== '' && texts.includes(chipText(value));
  }).length;
  return { texts, strays: Math.max(0, texts.length - sources - explained) };
}

/** The adapter's words for each section, in its notes (written here, so they are its own). */
const SECTION_TEXT: Readonly<
  Record<
    SimpleSection,
    { stray: string; strayEmpty: string; missing: string; noWriteNew: string; noDialog: string }
  >
> = {
  Lyrics: {
    stray:
      'A Lyrics or Styles section the Version does not have is on Suno’s form: remove it by hand, then add the Version’s lyrics from + › Lyrics › Write new. The extension does not press Remove.',
    strayEmpty:
      'A Lyrics or Styles section the Version does not have is on Suno’s form: remove it by hand. The extension does not press Remove.',
    missing:
      'Suno’s form shows no Lyrics section with the Version’s lyrics: add it by hand from + › Lyrics › Write new.',
    noWriteNew: 'Suno’s Add menu did not offer Lyrics › Write new.',
    noDialog: 'Suno did not open its Lyrics dialog.',
  },
  Styles: {
    stray:
      'A Lyrics or Styles section the Version does not have is on Suno’s form: remove it by hand, then add the Version’s styles from + › Styles › Write new. The extension does not press Remove.',
    strayEmpty:
      'A Lyrics or Styles section the Version does not have is on Suno’s form: remove it by hand. The extension does not press Remove.',
    missing:
      'Suno’s form shows no Styles section with the Version’s styles: add it by hand from + › Styles › Write new.',
    noWriteNew: 'Suno’s Add menu did not offer Styles › Write new.',
    noDialog: 'Suno did not open its Styles dialog.',
  },
};

/** What a section the Version does not have, still on the form, reads as. */
const STRAY_SECTION = 'a Lyrics or Styles section the Version does not have';

/**
 * Simple mode's Lyrics or Styles section (TS-005): + › Lyrics (or Styles) › Write new, the text
 * typed into the dialog that opens, and the dialog closed; the section then shows as a chip, which
 * is how it is read back. A section the Version does not have is never removed (the adapter does
 * not use Remove controls): it is reported failed, to remove by hand, and while one is on the form
 * nothing is added. Closing the Styles dialog makes Suno save the styles to the user's saved style
 * prompts ("Prompt saved."): that is reported with the outcome (decision TS-005-3).
 */
function simpleSection(section: SimpleSection): Filler {
  const entry = SECTION_ENTRY[section];
  return {
    entry,
    control:
      section === 'Lyrics'
        ? 'the Simple form’s Lyrics section'
        : 'the Simple form’s Styles section',
    text: true,
    wanted: wantedText,
    read: (page, wanted, job) => {
      if (locate(page, ADD_BUTTON) === 'absent') {
        return { kind: 'absent' };
      }
      const chips = sectionChips(page, job);
      const text = typeof wanted === 'string' ? wanted : '';
      if (text !== '' && chips.texts.includes(chipText(text))) {
        return { kind: 'value', value: text };
      }
      if (chips.strays > 0) {
        return {
          kind: 'value',
          value: STRAY_SECTION,
          reportNote: text === '' ? SECTION_TEXT[section].strayEmpty : SECTION_TEXT[section].stray,
        };
      }
      return text === ''
        ? { kind: 'value', value: '' }
        : {
            kind: 'value',
            value: '',
            reportNote: SECTION_TEXT[section].missing,
          };
    },
    write: async (page, value, job) => {
      const text = typeof value === 'string' ? value : '';
      if (text === '' || sectionChips(page, job).strays > 0) {
        return undefined;
      }
      // Each part is pressed only when what it opens is not open already.
      const open = (target: Target) => page.find(target).kind === 'found';
      if (!open(sectionEditor(section))) {
        if (!open(writeNewItem(section))) {
          if (
            !open(addMenuItem(section)) &&
            (!pressIfThere(page, ADD_BUTTON) || !(await shows(page, addMenuItem(section))))
          ) {
            return { unavailable: 'Suno’s “+” (Add) menu did not open.' };
          }
          if (
            !pressIfThere(page, addMenuItem(section)) ||
            !(await shows(page, writeNewItem(section)))
          ) {
            return { unavailable: SECTION_TEXT[section].noWriteNew };
          }
        }
        if (
          !pressIfThere(page, writeNewItem(section)) ||
          !(await shows(page, sectionEditor(section)))
        ) {
          return { unavailable: SECTION_TEXT[section].noDialog };
        }
      }
      const editor = locate(page, sectionEditor(section));
      if (typeof editor !== 'string') {
        if (section === 'Lyrics') {
          page.typeText(editor, text);
        } else {
          page.set(editor, text);
        }
      }
      // The dialog is closed by its own Close; what it holds then shows as the section's chip.
      pressIfThere(page, sectionDialogClose(section));
      await page.wait(() => page.find(sectionDialog(section)).kind !== 'found', SHOW_WAIT_MS);
      return undefined;
    },
    ...(section === 'Styles'
      ? {
          reportNote:
            'Suno saved these styles to your saved style prompts when its Styles box was closed (“Prompt saved.”); that is Suno’s own doing.',
        }
      : {}),
  };
}

/**
 * Duration's mode (TS-005): Auto shows Custom and Auto buttons; Custom shows the slider instead.
 * Custom is chosen by its button; back to Auto, only when the buttons are shown, since the way back
 * from the slider is an icon with no name.
 */
const durationMode: Filler = {
  entry: 'songs.advanced.duration_mode',
  control: 'the Duration choice in More Options',
  text: false,
  wanted: (value) =>
    value === 'custom'
      ? { kind: 'value', value: 'custom' }
      : value === 'auto' || value === null || value === undefined
        ? { kind: 'value', value: 'auto' }
        : { kind: 'failed', note: 'The Version’s Duration is not Auto or Custom.' },
  read: (page, wanted) => {
    const slider = locate(page, DURATION_SLIDER);
    if (typeof slider !== 'string') {
      return wanted === 'auto'
        ? {
            kind: 'value',
            value: 'custom',
            reportNote:
              'Suno shows Duration as Custom, and its way back to Auto (the icon beside the label) has no name the extension can press: set Duration to Auto by hand.',
          }
        : { kind: 'value', value: 'custom' };
    }
    const custom = locate(page, DURATION_CUSTOM);
    const auto = locate(page, DURATION_AUTO);
    if (typeof custom === 'string' || typeof auto === 'string') {
      return {
        kind:
          custom === 'disabled' || auto === 'disabled' || slider === 'disabled'
            ? 'disabled'
            : 'absent',
      };
    }
    if (page.read(custom).selected === true) {
      return { kind: 'value', value: 'custom' };
    }
    return { kind: 'value', value: page.read(auto).selected === true ? 'auto' : null };
  },
  write: (page, value) => {
    pressIfThere(page, value === 'custom' ? DURATION_CUSTOM : DURATION_AUTO);
    return undefined;
  },
};

/** The Key button's label: a note and "min" or "maj" ("F# min"), or "Any" (TS-005). */
const KEY_LABEL = /^([A-G]#?)(?:\s*(maj|min)[a-z]*)?$/i;

function keyShown(page: Page): Shown & { scale?: FormValue } {
  const found = locate(page, KEY_BUTTON);
  if (typeof found === 'string') {
    return { kind: found };
  }
  const label = page.read(found).text;
  if (label.toLowerCase() === 'any') {
    return { kind: 'value', value: 'any', scale: null };
  }
  const match = KEY_LABEL.exec(label);
  if (match === null) {
    return { kind: 'value', value: label, scale: label };
  }
  // A key shown without "min" is taken as major: only "F# min" was captured (TS-005).
  const scale = (match[2] ?? 'maj').toLowerCase() === 'min' ? 'minor' : 'major';
  return { kind: 'value', value: (match[1] ?? '').toUpperCase(), scale };
}

function wantedKey(value: unknown): Wanted {
  if (value === null || value === undefined) {
    return { kind: 'value', value: 'any' };
  }
  if (typeof value === 'string' && value.toLowerCase() === 'any') {
    return { kind: 'value', value: 'any' };
  }
  return typeof value === 'string' && KEY_NOTES.includes(value)
    ? { kind: 'value', value }
    : { kind: 'failed', note: 'The Version’s key is not one of Suno’s notes.' };
}

/** The Version's key as the popover chooses it, or undefined when it is not one of Suno's notes. */
function keyOf(job: FormJob): FormValue | undefined {
  const wanted = wantedKey(job.entries['sounds.single.sound_key']);
  return wanted.kind === 'value' ? wanted.value : undefined;
}

function scaleOf(job: FormJob): 'Major' | 'Minor' {
  return job.entries['sounds.single.sound_scale'] === 'minor' ? 'Minor' : 'Major';
}

/**
 * Sets the key and its scale in the Key popover (TS-005): the Key button opens it, the note (or Any)
 * and the Major or Minor tab are chosen, and Apply sets them. Nothing already chosen is pressed again.
 */
async function applyKey(page: Page, note: FormValue, scale: 'Major' | 'Minor'): Promise<Written> {
  if (page.find(KEY_POPOVER).kind !== 'found') {
    if (!pressIfThere(page, KEY_BUTTON) || !(await shows(page, KEY_APPLY))) {
      return { unavailable: 'Suno’s Key popover did not open.' };
    }
  }
  const choice = note === 'any' || note === null ? 'Any' : String(note);
  const chosen = locate(page, keyChoice(choice));
  if (typeof chosen === 'string') {
    return {
      unavailable: `Suno’s Key popover does not offer ${choice === 'Any' ? 'Any' : 'that note'}.`,
    };
  }
  if (page.read(chosen).selected !== true) {
    page.click(chosen);
  }
  if (choice !== 'Any') {
    const tab = locate(page, scaleTab(scale));
    if (typeof tab !== 'string' && page.read(tab).selected !== true) {
      page.click(tab);
    }
  }
  pressIfThere(page, KEY_APPLY);
  return undefined;
}

/** Sounds' Key (TS-005): read from the Key button's label, set in the popover with the scale. */
const soundKey: Filler = {
  entry: 'sounds.single.sound_key',
  control: KEY_BUTTON.description,
  text: false,
  wanted: wantedKey,
  read: (page) => {
    const shown = keyShown(page);
    return shown.kind === 'value' ? { kind: 'value', value: shown.value } : shown;
  },
  write: (page, value, job) => applyKey(page, value, scaleOf(job)),
};

/** Sounds' Key scale: shown with the key ("F# min"); not applicable with Key Any. */
const soundScale: Filler = {
  entry: 'sounds.single.sound_scale',
  control: KEY_BUTTON.description,
  text: false,
  wanted: (value, job) => {
    const key = keyOf(job);
    if (key === 'any') {
      return { kind: 'not_applicable', note: 'Key is Any, so no scale is chosen.' };
    }
    if (key === undefined) {
      return {
        kind: 'failed',
        note: 'The Version’s key is not one of Suno’s notes, so its scale was not chosen.',
      };
    }
    if (value === null || value === undefined) {
      return { kind: 'value', value: 'major' };
    }
    return value === 'major' || value === 'minor'
      ? { kind: 'value', value }
      : { kind: 'failed', note: 'The Version’s scale is not Major or Minor.' };
  },
  read: (page) => {
    const shown = keyShown(page);
    return shown.kind === 'value' ? { kind: 'value', value: shown.scale ?? null } : shown;
  },
  write: (page, value, job) =>
    applyKey(page, keyOf(job) ?? null, value === 'minor' ? 'Minor' : 'Major'),
};

/** The model a Version names, as the menu's items name it; not applicable when it names none. */
function wantedModel(value: unknown): Wanted {
  if (value === null || value === undefined) {
    return { kind: 'not_applicable', note: 'The Version names no model; Suno keeps its own.' };
  }
  const model = typeof value === 'string' ? value.trim().toLowerCase() : '';
  return MODEL_LABELS.includes(model)
    ? { kind: 'value', value: model }
    : {
        kind: 'failed',
        note: 'The Version’s model is not one Suno’s model menu offers; choose a model by hand.',
      };
}

/** The model the button shows ("v6"), as compared: lower case. */
function modelShown(page: Page, button: Target): Shown {
  return shown(page, button, (found) => page.read(found).text.trim().toLowerCase());
}

/**
 * Chooses `model` in the model menu (TS-006): the model button opens it (unless it is open), the
 * model's `menuitemradio` is pressed unless it is already checked, and the menu is closed: Suno
 * closes it on a choice, and the button closes it when it is still open. Only a `menuitemradio` is
 * ever looked for, so "Create Custom Model" (a `menuitem` that spends credits) is never pressed;
 * the forbidden-control matcher refuses it too (its name starts with Create). The read-back is the
 * button's own text, which names the chosen model.
 */
async function chooseModel(page: Page, button: Target, model: string): Promise<Written> {
  const opener = locate(page, button);
  if (typeof opener === 'string') {
    return { unavailable: 'Suno’s model button is not on the form.' };
  }
  const shownAs = page.read(opener).text.trim();
  if (page.find(modelMenu(shownAs)).kind !== 'found') {
    page.click(opener);
    if (!(await shows(page, modelMenu(shownAs)))) {
      return { unavailable: 'Suno’s model menu did not open.' };
    }
  }
  const item = locate(page, modelItem(model, shownAs));
  if (typeof item === 'string') {
    pressIfThere(page, button);
    return {
      unavailable:
        item === 'disabled'
          ? 'Suno’s model menu shows that model disabled (it may need a paid plan).'
          : 'Suno’s model menu does not offer that model.',
    };
  }
  if (page.read(item).checked !== true) {
    page.click(item);
  }
  const closed = await page.wait(() => {
    const now = locate(page, button);
    return typeof now === 'string' || page.read(now).expanded !== true;
  }, SHOW_WAIT_MS);
  if (!closed) {
    pressIfThere(page, button);
  }
  return undefined;
}

/** The model (Songs' Simple and Advanced, and Sounds), chosen in the model menu (TS-006). */
function modelChoice(entry: string, button: Target): Filler {
  return {
    entry,
    control: button.description,
    text: false,
    wanted: wantedModel,
    read: (page) => modelShown(page, button),
    write: (page, value) => chooseModel(page, button, String(value)),
  };
}

/** Speech's and Sounds' fillers (#147), by entry. */
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
  modelChoice('sounds.single.sounds_model', SOUNDS_MODEL_BUTTON),
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
  soundKey,
  soundScale,
];

/** One filler per `fill` entry of the field map, except those blocked on a capture. */
export const FILLERS: readonly Filler[] = [
  modelChoice('songs.simple.model', MODEL_BUTTON),
  modelChoice('songs.advanced.model', MODEL_BUTTON),
  textBox('songs.simple.simple_prompt', SONG_DESCRIPTION),
  simpleSection('Lyrics'),
  simpleSection('Styles'),
  lyrics,
  textBox('songs.advanced.styles', STYLES_BOX),
  textBox('songs.advanced.exclude_styles', EXCLUDE_STYLES),
  vocalGender,
  durationMode,
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

/** What each entry that is not filled comes to; null for an entry with a filler. */
export function unfilledResult(key: string, job: FormJob, workspace: string | null): EntryResult {
  if (key === WORKSPACE_ENTRY) {
    return workspace === null
      ? { key, outcome: 'not_applicable' }
      : { key, outcome: 'set', note: 'Selected by the workspace step.' };
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
    } else if (found.reportNote !== undefined) {
      result.note = found.reportNote;
    }
  }
  if (outcome === 'set' && filler.reportNote !== undefined) {
    result.note = filler.reportNote;
  }
  return result;
}

function unavailable(
  filler: Filler,
  wanted: FormValue,
  shownAs: Shown | { unavailable: string },
): EntryResult {
  const note =
    'unavailable' in shownAs
      ? shownAs.unavailable
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
  const before = filler.read(page, wanted.value, job);
  if (before.kind !== 'value') {
    return unavailable(filler, wanted.value, before);
  }
  if (!same(before.value, wanted.value, filler.text)) {
    try {
      const written = await filler.write(page, wanted.value, job);
      if (written !== undefined) {
        return unavailable(filler, wanted.value, written);
      }
    } catch (error) {
      rethrowStops(error);
      // The read-back below says what the control shows now.
    }
  }
  const settled = await page.wait(() => {
    const now = filler.read(page, wanted.value, job);
    return now.kind === 'value' && same(now.value, wanted.value, filler.text);
  }, READ_BACK_MS * READ_BACK_TRIES);
  return compared(
    filler,
    wanted.value,
    settled ? 'set' : 'failed',
    filler.read(page, wanted.value, job),
  );
}

/** Reads one entry without changing anything (Check again). */
export function checkEntry(page: Page, filler: Filler, job: FormJob): EntryResult {
  const wanted = filler.wanted(job.entries[filler.entry], job);
  if (wanted.kind !== 'value') {
    return { key: filler.entry, outcome: wanted.kind, note: wanted.note };
  }
  const now = filler.read(page, wanted.value, job);
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
