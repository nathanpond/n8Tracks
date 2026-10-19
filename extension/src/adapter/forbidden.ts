/**
 * The forbidden-control matcher (invariant 4: Suno is never mutated destructively). Before the
 * click primitive presses anything, it asks this module whether the control may be pressed; there
 * is no parameter to skip the question and no override of a refusal.
 *
 * The matcher reads facts the primitives gather from the page, so it touches no DOM itself:
 *
 * - A button or menu item (`button`, `menuitem`, anything with `role=button`) whose accessible
 *   name, `aria-label`, text, or `title` starts with Create, Publish, Delete, Trash, Move to Trash,
 *   or Remove is forbidden. Links and tabs are not matched: the adapter reaches pages by address,
 *   except Library's Workspaces tab, which a sync presses to make Suno send the workspace list
 *   (#421, TS-007-6).
 * - A control matching one of the selectors known from the TS-003 snapshots is forbidden,
 *   whatever its role or name.
 * - Any control inside a dialog is forbidden unless the dialog is one of the recognised dialogs
 *   below and allows that control by name. An unrecognised dialog fails closed.
 * - In a dialog whose rows are pressed (Suno's Model dialog, TS-007-10), a row whose name or text
 *   starts with one of those words is forbidden: "Create Custom Model", which spends credits.
 * - A control that would submit a form is forbidden.
 * - In Create's Save to picker (#435, {@link SAVE_TO_PICKER}), a dialog or not, only a workspace's
 *   row "<name> (<N> clips)" may be pressed, and none whose name starts with one of those words:
 *   its "+" button and a "Create …" row may create a workspace, and are refused.
 * - In Suno's "Add a song to mashup" dialog (#438, {@link MASHUP_PICKER}), only a song's
 *   "Add <title> to mashup" button (its Select), the "Search clips" box, and Close: its tabs,
 *   filter, sort, liked filter, rows, play, like, and Edit title buttons are refused.
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
 * The Save to picker outside a dialog is known only while its box is an `input` or `textarea` in
 * the light DOM named by `aria-label`, `aria-labelledby`, `placeholder`, or `title` (not by a
 * `<label>`); its markup was not seen live (#435).
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

/** Roles that hold controls rather than being one (a tab list, a group, the Model dialog's grid). */
export const CONTAINER_ROLES: ReadonlySet<string> = new Set([
  'tablist',
  'group',
  'grid',
  'rowgroup',
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
   * Buttons whose whole accessible name matches this may be pressed too (#438: a song's
   * "Add <title> to mashup", one per row, its title Suno's). Only a `button`: a link or an image
   * named the same is still refused, and a button's own forbidden words still apply.
   */
  allowsButtons?: RegExp;
  /**
   * Whether an element with no role may be pressed by its own text (TS-005: a voice is chosen in
   * the Voice picker by pressing its title, which Suno gives no role). Its text must not start with
   * a forbidden word ("Create Voice" is refused), and every element it sits in is still checked.
   */
  pressesText?: boolean;
  /**
   * Whether a `row` may be pressed (TS-007-10: a model is chosen in Suno's Model dialog by pressing
   * its row, which has no name of its own). A row whose name or text starts with a forbidden word
   * is refused: the dialog's "Create Custom Model" row spends credits.
   */
  pressesRows?: boolean;
  /** The TS-003, TS-005, or TS-007 snapshot it was recognised from. */
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
 * Suno's "Add a song to mashup" dialog (#438), opened by the form's "Add another song to Mashup"
 * once a Mashup holds its first song. Its markup is the owner's console capture of 2026-10-19
 * (TS-007-18): a `role=dialog` labelled by its title text, a Close button, the tabs My Library
 * and Everything, a "Search clips" box, a filter "(3)", a sort "New", "Filter to show only liked
 * clips", and a `rowgroup` of rows. Each row is itself a `role=button` ("<title>. Double-tap or
 * press Enter to add to mashup.") holding "Play <title>", a link to `/song/<clip ID>`, "Edit
 * title" (which renames the song in Suno), "Like <title>", and "Add <title> to mashup" (its
 * visible text "Select"). Only the search box (typed into, never pressed), a song's
 * "Add … to mashup", and Close may be pressed: the tabs, filter, sort, liked filter, the rows
 * themselves, play, like, and Edit title are refused, so the list stays the user's library as it
 * is and no song is renamed, liked, or played.
 */
export const MASHUP_PICKER = {
  title: 'Add a song to mashup',
  field: 'Search clips',
  add: /^Add .+ to mashup$/,
  close: 'Close',
  snapshot: 'create-source-mashup-add-song-dialog',
} as const;

/**
 * Dialogs whose controls may be pressed (TS-003, TS-005). Loading a source onto a filled form asks
 * whether to overwrite the lyrics and styles, or only the styles (an instrumental source, TS-005);
 * either answer only changes the form. The Voice picker ("+ Voice") may be closed and its two lists switched, and a voice chosen
 * by its title (#148). Simple mode's Lyrics and Styles dialogs (#146) are only closed: their text
 * is typed, which presses nothing. The Sounds Key popover (#147) is untitled, so it is recognised
 * only while it holds nothing but its notes, Any, Major, Minor, and Apply. The Inspo picker is not
 * here: its dialog has no title and holds the user's playlists, so it cannot be told from any other
 * untitled dialog. The Download dialog is not here either: its controls are the named exception
 * {@link DOWNLOAD_CLIP}, which only the download primitive presses (#216). The Model dialog
 * (#427, TS-007-10) lists the models as rows: a model's row may be pressed, its "Create Custom
 * Model" row never (it spends credits), and Close if it has one. In Suno's "Add a song to mashup"
 * dialog (#438, {@link MASHUP_PICKER}) only a song's "Add … to mashup", the search box, and Close.
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
  {
    title: 'Model',
    allows: ['Close'],
    pressesRows: true,
    snapshot: 'create-songs-advanced-model-dialog',
  },
  {
    title: MASHUP_PICKER.title,
    allows: [MASHUP_PICKER.close, MASHUP_PICKER.field],
    allowsButtons: MASHUP_PICKER.add,
    snapshot: MASHUP_PICKER.snapshot,
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

/**
 * Create's Save to picker (#435), opened by the Save to button at the foot of the form: a text box
 * "Search or create...", a "+" button, and a scrolling list of rows "<name> (<N> clips)" (the
 * owner's screenshot of 2026-10-17; its roles and markup were not seen live, so
 * `page.create-songs-advanced-save-to-picker.html` is constructed). It is known by its text box:
 * in a dialog, by the box among the dialog's controls (it may have no title); outside one, by the
 * box being the nearest text field around the control. In it, only a row may be pressed (an
 * `option` or a `button` whose whole name is a workspace's name and its count of clips), and the
 * box typed into; "+" and anything else, such as a "Create …" row, may create a workspace.
 */
export const SAVE_TO_PICKER: {
  field: string;
  row: RegExp;
  rowRoles: readonly ('option' | 'button')[];
  snapshot: string;
} = {
  field: 'Search or create...',
  row: /^.+ \(\d[\d,]* clips?\)$/,
  rowRoles: ['option', 'button'],
  snapshot: 'create-songs-advanced-save-to-picker',
};

/** Whether the control is in the Save to picker: its dialog holds the box, or the box is beside it. */
function inSaveToPicker(facts: ControlFacts): boolean {
  if (facts.dialog !== null) {
    return isSaveToPicker(facts.dialog);
  }
  // Only a control is asked about the text field beside it, which walks the page around it, and
  // only while the picker's box is on the page.
  return (
    facts.role !== null &&
    !CONTAINER_ROLES.has(facts.role) &&
    facts.fieldOnPage(SAVE_TO_PICKER.field) &&
    facts.inlineField() === SAVE_TO_PICKER.field
  );
}

/** Whether a dialog is the Save to picker, by its text box. */
export function isSaveToPicker(dialog: DialogFacts): boolean {
  return (dialog.controls ?? []).includes(SAVE_TO_PICKER.field);
}

/** The matcher's verdict on a control in the Save to picker, or null when it is pressable there. */
function saveToPickerRefusal(facts: ControlFacts): Verdict | null {
  if ([facts.name, ...facts.otherNames].some((name) => FORBIDDEN_NAME.test(name.trim()))) {
    return forbidden('its name starts with Create, Publish, Delete, Trash, or Remove');
  }
  const field = facts.role === 'textbox' && facts.name === SAVE_TO_PICKER.field;
  const row =
    facts.role !== null &&
    (SAVE_TO_PICKER.rowRoles as readonly string[]).includes(facts.role) &&
    SAVE_TO_PICKER.row.test(facts.name);
  return field || row
    ? null
    : forbidden('the Save to picker allows only a workspace’s row: its “+” may create a workspace');
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
    ((facts.name === 'Create new workspace' &&
      // Never in the Save to picker (#435), whose "+" and "Create …" rows are refused.
      !(facts.fieldOnPage(SAVE_TO_PICKER.field) && facts.inlineField() === SAVE_TO_PICKER.field)) ||
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
 * Whether a dialog has controls the adapter may press: a recognised dialog, the Download dialog,
 * whose controls are the named exception, or the Save to picker, whose rows only may be pressed
 * (#435). Every other dialog fails closed.
 */
export function pressableDialog(dialog: DialogFacts): boolean {
  return (
    recognisedDialog(dialog) !== undefined ||
    DOWNLOAD_DIALOG.title.test(dialog.title) ||
    isSaveToPicker(dialog)
  );
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
  /**
   * Whether a text field of that name is anywhere on the page: a quick look the matcher takes
   * before {@link inlineField}, which walks the page around the control.
   */
  fieldOnPage(name: string): boolean;
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
  if (inSaveToPicker(facts)) {
    const refusal = saveToPickerRefusal(facts);
    if (refusal !== null) {
      return refusal;
    }
  } else if (facts.dialog !== null) {
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
    const row = dialog.pressesRows === true && facts.role === 'row';
    if (row && [facts.name, ...facts.otherNames].some((name) => FORBIDDEN_NAME.test(name.trim()))) {
      return forbidden('its name starts with Create, Publish, Delete, Trash, or Remove');
    }
    const namedButton = facts.role === 'button' && dialog.allowsButtons?.test(facts.name) === true;
    if (!container && !byText && !row && !namedButton && !dialog.allows.includes(facts.name)) {
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
