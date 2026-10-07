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
 * Exactly one change to Suno is permitted in M4, creating a workspace ({@link CREATE_WORKSPACE}).
 * Its controls are neither allowed nor forbidden: they are the named exception, which only the
 * dedicated primitive `Page.createWorkspaceClick` may press.
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

/** A dialog the adapter recognises, by its title, and the controls in it that may be pressed. */
export interface RecognisedDialog {
  title: string;
  /** Accessible names of the controls that may be pressed; every other control is forbidden. */
  allows: readonly string[];
  /** The TS-003 snapshot it was recognised from. */
  snapshot: string;
}

/**
 * Dialogs whose controls may be pressed (TS-003). Loading a source onto a filled form asks
 * whether to overwrite the lyrics and styles; either answer only changes the form. The Voice
 * picker ("+ Voice", #146) may be closed and its two lists switched; choosing a voice in it is the
 * source story's (#148), which adds what it presses. The Inspo picker is not here: its dialog has
 * no title, and recognising an untitled dialog would recognise every untitled one. The Download
 * dialog is not here either: only the download story's own primitive may recognise it (#216).
 */
export const RECOGNISED_DIALOGS: readonly RecognisedDialog[] = [
  {
    title: 'Overwrite Lyrics & Styles?',
    allows: ['Overwrite', 'Keep Current'],
    snapshot: 'overwrite-lyrics-styles-dialog',
  },
  {
    title: 'Voice',
    allows: ['Close', 'My Voices', 'Favorites'],
    snapshot: 'voice-picker',
  },
];

/** The names of the permitted changes to Suno. M4 has exactly one. */
export type ExceptionName = 'create-workspace';

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

/** Every permitted change. A new one is a change to invariant 4, not to this list alone. */
export const EXCEPTIONS: readonly Exception[] = [CREATE_WORKSPACE];

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
  dialog: { title: string } | null;
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
    const title = facts.dialog.title;
    const dialog = RECOGNISED_DIALOGS.find((candidate) => candidate.title === title);
    if (dialog === undefined) {
      return forbidden('it is in a dialog the adapter does not recognise');
    }
    if (!dialog.allows.includes(facts.name)) {
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
