/**
 * The forbidden-control matcher (invariant 4: Suno is never mutated destructively). Before the
 * click primitive presses anything, it asks this module whether the control may be pressed; there
 * is no parameter to skip the question and no override of a refusal.
 *
 * The matcher reads facts the primitives gather from the page, so it touches no DOM itself:
 *
 * - A button or menu item (`button`, `menuitem`, anything with `role=button`) whose accessible
 *   name, `aria-label`, text, or `title` starts with Create, Publish, Delete, Trash, Move to Trash,
 *   or Remove is forbidden. Links are not matched: the adapter reaches pages by address.
 * - A control matching one of the selectors known from the TS-003 snapshots is forbidden,
 *   whatever its role or name.
 * - Any control inside a dialog is forbidden unless the dialog is one of the recognised dialogs
 *   below and allows that control by name. An unrecognised dialog fails closed.
 * - A control that would submit a form is forbidden.
 *
 * False positives are accepted: a refusal stops the workflow, it never presses something else.
 *
 * Two changes to Suno are permitted: creating a workspace ({@link CREATE_WORKSPACE}, M4) and having
 * a clip's file prepared in its Download dialog ({@link DOWNLOAD_CLIP}, #216), which may unlock the
 * clip for download. Their controls are neither allowed nor forbidden: each is a named exception,
 * which only its own primitive (`Page.createWorkspaceClick`, `Page.downloadDialogClick`) may press.
 *
 * Not covered: a forbidden control Suno adds under a new name is caught only when the snapshots
 * and this matcher are updated. The live-site smoke checks in the generate stories supplement it.
 */

/** The words a forbidden control's name starts with, compared case-insensitively. */
export const FORBIDDEN_NAME = /^(create|publish|delete|trash|move to trash|remove)\b/i;

/** The roles the name rule applies to: buttons and menu items, not links or tabs. */
export const PRESSABLE_ROLES: ReadonlySet<string> = new Set([
  'button',
  'menuitem',
  'menuitemcheckbox',
  'menuitemradio',
]);

/** Selectors of forbidden controls in the TS-003 snapshots; a match refuses whatever the name. */
export const FORBIDDEN_SELECTORS: readonly { selector: string; what: string }[] = [
  { selector: '[aria-label="Create song"]', what: 'the Create button (Songs and Sounds)' },
  { selector: '[aria-label="Create speech"]', what: 'the Create button (Speech)' },
  { selector: '[aria-label="Publish clip"]', what: "a library row's Publish button" },
  { selector: '[role="menuitem"][aria-label="Publish"]', what: "a clip menu's Publish item" },
  {
    selector: '[role="menuitem"][aria-label="Move to Trash"]',
    what: "a clip menu's Move to Trash item",
  },
  {
    selector: '[aria-label="Delete permanently"]',
    what: "a Trash row's Delete permanently button",
  },
];

/** Roles that hold controls rather than being one (a tab list, a group). */
export const CONTAINER_ROLES: ReadonlySet<string> = new Set([
  'tablist',
  'group',
  'presentation',
  'none',
]);

/** A dialog the adapter recognises, by its title, and the controls in it that may be pressed. */
export interface RecognisedDialog {
  /**
   * Its title. An untitled dialog (`''`) is recognised only by what it holds: every control in it
   * must be one of `allows`, so any other untitled dialog, or this one with a control added,
   * fails closed (TS-005: the Sounds Key popover has no title).
   */
  title: string;
  /** Accessible names of the controls that may be pressed; every other control is forbidden. */
  allows: readonly string[];
  /**
   * Whether an element with no role may be pressed by its own text (TS-005: a voice is chosen in
   * the Voice picker by pressing its title, which Suno gives no role). Its text must not start with
   * a forbidden word ("Create Voice" is refused), and every element it sits in is still checked.
   */
  pressesText?: boolean;
  /** The TS-003 or TS-005 snapshot it was recognised from. */
  snapshot: string;
}

/** The Sounds Key popover's notes (TS-005), as its buttons name them. */
export const KEY_NOTES: readonly string[] = [
  'C',
  'C#',
  'D',
  'D#',
  'E',
  'F',
  'F#',
  'G',
  'G#',
  'A',
  'A#',
  'B',
];

/**
 * Dialogs whose controls may be pressed (TS-003, TS-005). Loading a source onto a filled form asks
 * whether to overwrite the lyrics and styles, or only the styles (an instrumental source, TS-005);
 * either answer only changes the form. The Voice picker ("+ Voice") may be closed and its two lists switched, and a voice chosen
 * by its title (#148). Simple mode's Lyrics and Styles dialogs (#146) are only closed: their text
 * is typed, which presses nothing. The Sounds Key popover (#147) is untitled, so it is recognised
 * only while it holds nothing but its notes, Any, Major, Minor, and Apply. The Inspo picker is not
 * here: its dialog has no title and holds the user's playlists, so it cannot be told from any other
 * untitled dialog. The Download dialog is not here either: its controls are the named exception
 * {@link DOWNLOAD_CLIP}, which only the download primitive presses (#216).
 */
export const RECOGNISED_DIALOGS: readonly RecognisedDialog[] = [
  {
    title: 'Overwrite Lyrics & Styles?',
    allows: ['Overwrite', 'Keep Current'],
    snapshot: 'overwrite-lyrics-styles-dialog',
  },
  {
    title: 'Overwrite Styles?',
    allows: ['Overwrite', 'Keep Current'],
    snapshot: 'overwrite-styles-dialog',
  },
  {
    title: 'Voice',
    allows: ['Close', 'My Voices', 'Favorites'],
    pressesText: true,
    snapshot: 'voice-picker',
  },
  { title: 'Lyrics', allows: ['Close'], snapshot: 'create-songs-simple-lyrics-dialog' },
  { title: 'Styles', allows: ['Close'], snapshot: 'create-songs-simple-styles-dialog' },
  {
    title: '',
    allows: [...KEY_NOTES, 'Any', 'Major', 'Minor', 'Apply'],
    snapshot: 'create-sounds-key-popover-fsharp-minor',
  },
];

/** The recognised dialog `dialog` is, or undefined: by title, an untitled one by all it holds. */
export function recognisedDialog(dialog: DialogFacts): RecognisedDialog | undefined {
  return RECOGNISED_DIALOGS.find(
    (candidate) =>
      candidate.title === dialog.title &&
      (candidate.title !== '' ||
        (dialog.controls !== undefined &&
          dialog.controls.length > 0 &&
          dialog.controls.every((name) => candidate.allows.includes(name)))),
  );
}

/** The names of the permitted changes to Suno. */
export type ExceptionName = 'create-workspace' | 'download-clip';

/** A permitted change to Suno: the controls that make it, recognised by the dialog they sit in. */
export interface Exception {
  name: ExceptionName;
  /** Plain words for a report. */
  description: string;
  /** The TS-003 snapshots it was recognised from. */
  snapshots: readonly string[];
  matches(facts: ControlFacts): boolean;
}

/**
 * Creating a workspace (the workspace story, #145). Suno's create-workspace "dialog" is an inline
 * row in the workspace list (`page.create-workspace-dialog.html`) whose title is its "New
 * workspace name" field; its Confirm button is recognised by that field, not by its own text. The
 * row is opened by the list's "Create new workspace" entry (`page.workspace-selector.html`), which
 * is part of the same change.
 */
export const CREATE_WORKSPACE: Exception = {
  name: 'create-workspace',
  description: 'creating a Suno workspace',
  snapshots: ['workspace-selector', 'create-workspace-dialog'],
  matches: (facts) =>
    facts.dialog === null &&
    facts.role === 'button' &&
    (facts.name === 'Create new workspace' ||
      (facts.name === 'Confirm' && facts.inlineField() === 'New workspace name')),
};

/**
 * A clip's Download dialog (TS-004, `page.download-dialog.html`): titled "Download" and the clip's
 * title, it lists M4A, MP3, WAV, "MP4 video asset", and "Stems & MIDI" (with Manage), and one
 * button, "Unlock & Download".
 */
export const DOWNLOAD_DIALOG = {
  /** The dialog's title: "Download" followed by the clip's title in quotes. */
  title: /^Download\b/,
  /**
   * The controls the download primitive may press: a format, the dialog's one button, and Close.
   * "MP4 video asset" and Stems & MIDI's Manage are not among them, so they stay forbidden.
   */
  allows: ['WAV', 'MP3', 'M4A', 'Unlock & Download', 'Close'],
  snapshot: 'download-dialog',
} as const;

/**
 * Having a clip's file prepared in its Download dialog (the download story, #216): choosing WAV,
 * MP3, or M4A and pressing "Unlock & Download". For a clip not yet unlocked this spends one of the
 * user's plan downloads, which a run does only within the count the user confirmed before it
 * started (TS-004, the maintainer's decision of 2026-10-05). Nothing is created, published,
 * deleted, or trashed. Only `Page.downloadDialogClick`, called from the download workflows, presses
 * these controls.
 */
export const DOWNLOAD_CLIP: Exception = {
  name: 'download-clip',
  description: "having a clip's file prepared in its Download dialog",
  snapshots: [DOWNLOAD_DIALOG.snapshot],
  matches: (facts) =>
    facts.dialog !== null &&
    DOWNLOAD_DIALOG.title.test(facts.dialog.title) &&
    facts.role === 'button' &&
    (DOWNLOAD_DIALOG.allows as readonly string[]).includes(facts.name),
};

/** Every permitted change. A new one is a change to invariant 4, not to this list alone. */
export const EXCEPTIONS: readonly Exception[] = [CREATE_WORKSPACE, DOWNLOAD_CLIP];

/**
 * Whether a dialog has controls the adapter may press: a recognised dialog, or the Download
 * dialog, whose controls are the named exception. Every other dialog fails closed.
 */
export function pressableDialog(dialog: DialogFacts): boolean {
  return recognisedDialog(dialog) !== undefined || DOWNLOAD_DIALOG.title.test(dialog.title);
}

/** A dialog as the matcher knows it. */
export interface DialogFacts {
  /** Its title: its accessible name, else its first heading's. */
  title: string;
  /**
   * The accessible names of every control in it (buttons, menu items, tabs, links, form controls),
   * which is how an untitled dialog is recognised; unknown when not given.
   */
  controls?: readonly string[];
}

/** What the primitives read from a control before pressing it. */
export interface ControlFacts {
  role: string | null;
  /** The accessible name, white space collapsed. */
  name: string;
  /** Every other name the control goes by: `aria-label`, text, and `title`. */
  otherNames: readonly string[];
  /** Whether the element matches a CSS selector (the known selectors). */
  matches(selector: string): boolean;
  /** The nearest dialog the control is in, by title (its accessible name), or null. */
  dialog: DialogFacts | null;
  /** Outside a dialog: the label of the text field in the nearest container that has one. */
  inlineField(): string | null;
  /** Pressing it would submit a form. */
  submitsForm: boolean;
}

export type Verdict =
  | { kind: 'allowed' }
  | { kind: 'forbidden'; reason: string }
  | { kind: 'exception'; exception: ExceptionName };

const ALLOWED: Verdict = { kind: 'allowed' };

function forbidden(reason: string): Verdict {
  return { kind: 'forbidden', reason };
}

/**
 * Why the control is forbidden by its own name or a known selector, or null. This part also
 * applies to every control the pressed element sits inside, since a click reaches them too.
 */
export function forbiddenItself(facts: ControlFacts): string | null {
  const known = FORBIDDEN_SELECTORS.find((entry) => facts.matches(entry.selector));
  if (known !== undefined) {
    return `it is ${known.what}`;
  }
  if (
    facts.role !== null &&
    PRESSABLE_ROLES.has(facts.role) &&
    [facts.name, ...facts.otherNames].some((name) => FORBIDDEN_NAME.test(name.trim()))
  ) {
    return 'its name starts with Create, Publish, Delete, Trash, or Remove';
  }
  return null;
}

/** Whether the control may be pressed, is forbidden (and why), or is a named exception. */
export function classify(facts: ControlFacts): Verdict {
  const exception = EXCEPTIONS.find((candidate) => candidate.matches(facts));
  if (exception !== undefined) {
    return { kind: 'exception', exception: exception.name };
  }
  if (facts.dialog !== null) {
    const dialog = recognisedDialog(facts.dialog);
    if (dialog === undefined) {
      return forbidden('it is in a dialog the adapter does not recognise');
    }
    // A container a press on one of its controls reaches (the Key popover's Major/Minor tab list)
    // is not a control of the dialog's own: its controls are what the dialog allows or refuses.
    const container = facts.role !== null && CONTAINER_ROLES.has(facts.role);
    const byText =
      dialog.pressesText === true &&
      facts.role === null &&
      facts.otherNames.length > 0 &&
      !facts.otherNames.some((name) => FORBIDDEN_NAME.test(name.trim()));
    if (!container && !byText && !dialog.allows.includes(facts.name)) {
      return forbidden('the dialog it is in does not allow it');
    }
  }
  const reason = forbiddenItself(facts);
  if (reason !== null) {
    return forbidden(reason);
  }
  if (facts.submitsForm) {
    return forbidden('pressing it would submit a form');
  }
  return ALLOWED;
}
