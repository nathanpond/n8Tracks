import { isSunoAddress } from './addresses.ts';
import { poll, POLL_MS, realClock, type Clock } from './clock.ts';
import {
  classify,
  forbiddenItself,
  type ControlFacts,
  type DialogFacts,
  type ExceptionName,
  type Verdict,
} from './forbidden.ts';
import {
  STRUCTURE_MAX_ANCESTORS,
  STRUCTURE_MAX_DEPTH,
  STRUCTURE_MAX_NODES,
  STRUCTURE_MAX_SIBLINGS,
  structureNodeOf,
  type PageStructure,
  type StructureAnchor,
  type StructureNode,
} from '../diagnostics/report.ts';

/**
 * The only code in the extension that touches Suno's page. A workflow finds, reads, sets, chooses,
 * clicks, and waits through a {@link Page}; it never holds a DOM element, so it cannot touch the
 * page any other way (an ESLint rule forbids querying, clicking, and dispatching events anywhere
 * else in the Suno content script and the adapter).
 *
 * Elements are found by role and accessible name, optionally inside a test attribute, never by a
 * class name. `find` looks inside open shadow roots, ignores hidden elements and the extension's
 * own panel, and never chooses between two matches.
 */

/** The attribute on the panel's host element: `find` never looks inside it. */
export const PANEL_HOST_ATTRIBUTE = 'data-n8tracks-panel';

/** The roles the adapter finds elements by. */
export type Role =
  | 'button'
  | 'checkbox'
  | 'combobox'
  | 'dialog'
  | 'grid'
  | 'group'
  | 'heading'
  | 'img'
  | 'link'
  | 'list'
  | 'listbox'
  | 'menu'
  | 'menuitem'
  | 'menuitemcheckbox'
  | 'menuitemradio'
  | 'option'
  | 'radio'
  | 'radiogroup'
  | 'region'
  | 'row'
  | 'rowgroup'
  | 'slider'
  | 'spinbutton'
  | 'status'
  | 'switch'
  | 'tab'
  | 'tablist'
  | 'textbox'
  /**
   * Not an ARIA role: an element Suno gives no role, found by its own whole text (`name`, a
   * string), as {@link TextAnchor} finds one (TS-005: a voice's title in the Voice picker), or by
   * an expression its whole text matches (#449: a playlist's "24 songs").
   */
  | 'text';

/** A region of the page known only by its test attribute (`data-testid`). */
export interface Scope {
  testId: string;
  /** Plain words for a report: "the Styles section". */
  description: string;
}

/**
 * A piece of the page's own text that names a control without being tied to it, as Suno's More
 * Options labels ("Max Mode") sit beside their Off and On buttons. It is the innermost visible
 * element whose whole text, white space collapsed, is `text`.
 */
export interface TextAnchor {
  text: string;
  /** Look only inside this element or region, which must itself be found exactly once. */
  within?: Target | Scope | Region;
  /** Plain words for a report: "the label Max Mode". */
  description: string;
}

/**
 * The smallest part of the page around an anchor that holds what is looked for: from the anchor,
 * found exactly once, outwards to the first element that contains a match. Used where Suno gives a
 * control no name of its own but sits it beside one that has (TS-003).
 */
export interface Region {
  around: Target | TextAnchor;
  /**
   * How many elements outwards from the anchor the region may reach: as far as the snapshot shows
   * the control sitting from it, so that a control that has gone is not found in the next part of
   * the page instead.
   */
  levels: number;
  /**
   * What the region may hold at most one of: it never grows past an element holding two, so that
   * it stays within the anchor's own row of a list rather than reaching a part holding the next
   * row's control (#438: one song link per row of Suno's "Add a song to mashup" list).
   */
  single?: Target;
  /** Plain words for a report: "the More Options section". */
  description: string;
}

/** What a workflow looks for. */
export interface Target {
  role: Role;
  /** The accessible name: equal after collapsing white space, or matching the expression. */
  name?: string | RegExp;
  /** A test attribute the element itself carries. */
  testId?: string;
  /** The kind of popup the element opens (`aria-haspopup`): "menu" for a menu button. */
  popup?: string;
  /**
   * The element's lead text: the text of its first `span` that holds text of its own, white space
   * collapsed, compared without regard to case. A row of Suno's Model dialog has no name of its
   * own and starts with the model's name ("v6-wild"), then a badge and a description (TS-007-10).
   */
  leadText?: string;
  /**
   * For a link: the path of the address it leads to, compared exactly (`/song/<clip ID>`, as the
   * Library's song links address a clip, TS-003), or matching the expression (any song's link,
   * TS-007-23). Reads only the link's own `href`.
   */
  address?: string | RegExp;
  /**
   * Something the element holds: at least one visible element inside it matches this. A row of
   * Suno's song lists has no name of its own to tell it from other groups and from the empty
   * placeholders Suno lays out ahead of the rows it has drawn; it holds its song's link
   * (TS-007-23).
   */
  holds?: Target;
  /** Look only inside this element or region, which must itself be found exactly once. */
  within?: Target | Scope | Region;
  /** Plain words for a report, read after "expected": "a text box labelled Styles". */
  description: string;
}

/** An element `find` found. Opaque: only the primitives can reach the element behind it. */
export interface Found {
  readonly target: Target;
}

/** Anything `find` locates on the way to a target. */
type Wanted = Target | Scope | TextAnchor | Region;

export type FindResult =
  | { kind: 'found'; found: Found }
  | { kind: 'not_found'; missing: Wanted }
  | { kind: 'ambiguous'; target: Wanted; count: number };

/** What `read` sees on an element. Values are never logged. */
export interface Reading {
  /** A form control's value, a slider's `aria-valuenow`, or an editable region's text. */
  value: string | null;
  /** The element's text, white space collapsed. */
  text: string;
  /** For checkboxes, switches, and radios. */
  checked: boolean | null;
  /** For tabs, options, and toggle buttons (`aria-selected`, `aria-pressed`, `data-selected`). */
  selected: boolean | null;
  /** For a section header or a menu button (`aria-expanded`). */
  expanded: boolean | null;
  enabled: boolean;
}

/** A primitive could not do what it was asked; `expected` is plain words for the step report. */
export class PrimitiveError extends Error {
  readonly expected: string;

  constructor(expected: string) {
    super(`Expected ${expected}.`);
    this.name = 'PrimitiveError';
    this.expected = expected;
  }
}

/**
 * The forbidden-control matcher refused a press (invariant 4). The page handle that refused it
 * changes nothing more, and the run stops with the step reported as "refused: forbidden control".
 * `control` and `reason` are the adapter's own words, never text from the page.
 */
export class ForbiddenControlError extends PrimitiveError {
  readonly control: string;
  readonly reason: string;

  constructor(control: string, reason: string) {
    super(`${control} not to be a forbidden control (refused: ${reason})`);
    this.name = 'ForbiddenControlError';
    this.control = control;
    this.reason = reason;
  }
}

/** The run this page belongs to has stopped: nothing more may change on the page. */
export class StoppedError extends PrimitiveError {
  constructor() {
    super('the workflow to be still running (it has stopped, so the page is left as it is)');
    this.name = 'StoppedError';
  }
}

const elements = new WeakMap<Found, Element>();

function handle(target: Target, element: Element): Found {
  const found: Found = Object.freeze({ target });
  elements.set(found, element);
  return found;
}

function elementOf(found: Found): Element {
  const element = elements.get(found);
  if (element === undefined) {
    throw new PrimitiveError(`${found.target.description}, found by this adapter`);
  }
  return element;
}

function collapse(text: string): string {
  return text.replace(/\s+/g, ' ').trim();
}

const INPUT_ROLES: Record<string, Role> = {
  button: 'button',
  checkbox: 'checkbox',
  email: 'textbox',
  number: 'spinbutton',
  radio: 'radio',
  range: 'slider',
  reset: 'button',
  search: 'textbox',
  submit: 'button',
  tel: 'textbox',
  text: 'textbox',
  url: 'textbox',
};

/** The element's role: its `role` attribute, else the role its tag implies. */
export function roleOf(element: Element): string | null {
  const explicit = element.getAttribute('role')?.trim().split(/\s+/)[0];
  if (explicit !== undefined && explicit !== '') {
    return explicit;
  }
  const tag = element.localName;
  switch (tag) {
    case 'button':
      return 'button';
    case 'a':
      return element.hasAttribute('href') ? 'link' : null;
    case 'textarea':
      return 'textbox';
    case 'select':
      return element.hasAttribute('multiple') ? 'listbox' : 'combobox';
    case 'option':
      return 'option';
    case 'dialog':
      return 'dialog';
    case 'img':
      return 'img';
    case 'ul':
    case 'ol':
      return 'list';
    case 'input': {
      const type = (element.getAttribute('type') ?? 'text').toLowerCase();
      return INPUT_ROLES[type] ?? (type === 'hidden' ? null : 'textbox');
    }
    default:
      if (/^h[1-6]$/.test(tag)) {
        return 'heading';
      }
      return element.getAttribute('contenteditable') === 'true' ? 'textbox' : null;
  }
}

/** The parent, stepping out of a shadow root to its host. */
function parentOf(element: Element): Element | null {
  if (element.parentElement !== null) {
    return element.parentElement;
  }
  const root = element.getRootNode();
  return root.nodeType === root.DOCUMENT_FRAGMENT_NODE && 'host' in root
    ? (root as ShadowRoot).host
    : null;
}

function hiddenItself(element: Element): boolean {
  if (
    element.hasAttribute('hidden') ||
    element.hasAttribute('inert') ||
    element.getAttribute('aria-hidden') === 'true'
  ) {
    return true;
  }
  const view = element.ownerDocument.defaultView;
  if (view === null) {
    return false;
  }
  const style = view.getComputedStyle(element);
  return (
    style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse'
  );
}

/** Whether the element, or anything it sits in, is hidden from the user. */
function isHidden(element: Element): boolean {
  for (let current: Element | null = element; current !== null; current = parentOf(current)) {
    if (hiddenItself(current)) {
      return true;
    }
  }
  return false;
}

function isEnabled(element: Element): boolean {
  if (element.getAttribute('aria-disabled') === 'true') {
    return false;
  }
  for (let current: Element | null = element; current !== null; current = parentOf(current)) {
    if (
      current.hasAttribute('disabled') &&
      ['button', 'input', 'select', 'textarea', 'fieldset', 'option'].includes(current.localName)
    ) {
      return false;
    }
  }
  return true;
}

/** Text of a subtree for a name: hidden parts left out, a labelled part by its label. */
function textOf(node: Node, root: boolean): string {
  if (node.nodeType === node.TEXT_NODE) {
    return node.textContent ?? '';
  }
  if (node.nodeType !== node.ELEMENT_NODE) {
    return '';
  }
  const element = node as Element;
  if (hiddenItself(element)) {
    return '';
  }
  if (!root) {
    const label = element.getAttribute('aria-label');
    if (label !== null && label.trim() !== '') {
      return ` ${label} `;
    }
    if (element.localName === 'img') {
      return ` ${element.getAttribute('alt') ?? ''} `;
    }
  }
  const children = [...element.childNodes].map((child) => textOf(child, false)).join('');
  // Block content reads as separate words, as a screen reader would say it.
  return ` ${children} `;
}

const NAME_FROM_CONTENT = new Set([
  'button',
  'checkbox',
  'heading',
  'link',
  'menuitem',
  'menuitemcheckbox',
  'menuitemradio',
  'option',
  'radio',
  'switch',
  'tab',
]);

function labelsOf(element: Element): string {
  // `labels` is undefined on elements that cannot be labelled, and null on a hidden input.
  const labels = (element as HTMLInputElement).labels as
    NodeListOf<HTMLLabelElement> | null | undefined;
  return labels === undefined || labels === null
    ? ''
    : [...labels].map((label) => textOf(label, true)).join(' ');
}

/** The element's accessible name, white space collapsed (a working subset of the ARIA rules). */
export function nameOf(element: Element): string {
  const labelledBy = element.getAttribute('aria-labelledby');
  if (labelledBy !== null) {
    const root = element.getRootNode() as Document | ShadowRoot;
    const text = collapse(
      labelledBy
        .split(/\s+/)
        .map((id) => root.getElementById(id))
        .map((label) => (label === null ? '' : textOf(label, true)))
        .join(' '),
    );
    if (text !== '') {
      return text;
    }
  }
  const label = collapse(element.getAttribute('aria-label') ?? '');
  if (label !== '') {
    return label;
  }
  const labelled = collapse(labelsOf(element));
  if (labelled !== '') {
    return labelled;
  }
  if (element.localName === 'img') {
    const alt = collapse(element.getAttribute('alt') ?? '');
    if (alt !== '') {
      return alt;
    }
  }
  const role = roleOf(element);
  if (role !== null && NAME_FROM_CONTENT.has(role)) {
    const content = collapse(textOf(element, true));
    if (content !== '') {
      return content;
    }
  }
  return collapse(
    element.getAttribute('title') ??
      (['input', 'textarea'].includes(element.localName)
        ? (element.getAttribute('placeholder') ?? '')
        : ''),
  );
}

/** Every element under `root`, inside open shadow roots too, leaving out the extension's panel. */
function elementsUnder(root: ParentNode): Element[] {
  const found: Element[] = [];
  const visit = (parent: ParentNode) => {
    for (const child of parent.children) {
      if (child.hasAttribute(PANEL_HOST_ATTRIBUTE)) {
        continue;
      }
      found.push(child);
      if (child.shadowRoot !== null) {
        visit(child.shadowRoot);
      }
      visit(child);
    }
  };
  visit(root);
  return found;
}

function nameMatches(element: Element, name: string | RegExp | undefined): boolean {
  if (name === undefined) {
    return true;
  }
  // A dialog goes by its title: its name, else its first heading's (as the matcher reads it).
  const actual = isDialog(element) ? dialogTitleOf(element) : nameOf(element);
  return typeof name === 'string' ? actual === collapse(name) : name.test(actual);
}

function isRegion(value: Wanted): value is Region {
  return 'around' in value;
}

function isTextAnchor(value: Wanted): value is TextAnchor {
  return 'text' in value;
}

function isTarget(value: Wanted): value is Target {
  return 'role' in value;
}

/**
 * The text of the element's first `span` (itself included) that holds text of its own, white space
 * collapsed; null when it has none. Hidden parts are left out.
 */
export function leadTextOf(element: Element): string | null {
  const spans = [element, ...elementsUnder(element)].filter(
    (inner) =>
      inner.localName === 'span' &&
      [...inner.childNodes].some(
        (child) => child.nodeType === child.TEXT_NODE && (child.textContent ?? '').trim() !== '',
      ) &&
      !isHidden(inner),
  );
  const [lead] = spans;
  return lead === undefined ? null : collapse(lead.textContent);
}

function leadMatches(element: Element, wanted: string | undefined): boolean {
  return (
    wanted === undefined || leadTextOf(element)?.toLowerCase() === collapse(wanted).toLowerCase()
  );
}

/** The path of the address a link leads to, without its query or fragment; null for no `href`. */
function linkPathOf(element: Element): string | null {
  const href = element.getAttribute('href');
  if (href === null) {
    return null;
  }
  try {
    return new URL(href, element.ownerDocument.baseURI).pathname;
  } catch {
    return href.replace(/[?#].*$/, '');
  }
}

function addressMatches(element: Element, wanted: string | RegExp | undefined): boolean {
  if (wanted === undefined) {
    return true;
  }
  const path = linkPathOf(element);
  return path !== null && (typeof wanted === 'string' ? path === wanted : wanted.test(path));
}

/** Whether a visible element inside `element` matches `wanted` (always, when nothing is wanted). */
function holdsMatch(element: Element, wanted: Target | undefined): boolean {
  return (
    wanted === undefined || elementsUnder(element).some((inner) => matchesTarget(inner, wanted))
  );
}

function matchesTarget(element: Element, target: Target): boolean {
  if (target.role === 'text') {
    return (
      roleOf(element) === null &&
      target.name !== undefined &&
      (typeof target.name === 'string'
        ? saysExactly(element, target.name)
        : saysMatching(element, target.name))
    );
  }
  return (
    roleOf(element) === target.role &&
    (target.testId === undefined || element.getAttribute('data-testid') === target.testId) &&
    (target.popup === undefined || element.getAttribute('aria-haspopup') === target.popup) &&
    addressMatches(element, target.address) &&
    leadMatches(element, target.leadText) &&
    nameMatches(element, target.name) &&
    !isHidden(element) &&
    holdsMatch(element, target.holds)
  );
}

/** Whether the element's whole text is `text`, and no element inside it says the same alone. */
function saysExactly(element: Element, text: string): boolean {
  const wanted = collapse(text);
  return (
    collapse(textOf(element, true)) === wanted &&
    !childElementsOf(element).some((child) => collapse(textOf(child, true)) === wanted) &&
    !isHidden(element)
  );
}

/**
 * Whether the element's whole text matches `pattern`, and no element inside it does alone: the
 * innermost element saying, say, "24 songs" (#449: a playlist's count in its header).
 */
function saysMatching(element: Element, pattern: RegExp): boolean {
  return (
    pattern.test(collapse(textOf(element, true))) &&
    !childElementsOf(element).some((child) => pattern.test(collapse(textOf(child, true)))) &&
    !isHidden(element)
  );
}

function matching(root: ParentNode, wanted: Target | Scope | TextAnchor): Element[] {
  const candidates = elementsUnder(root);
  if (isTarget(wanted)) {
    return candidates.filter((element) => matchesTarget(element, wanted));
  }
  if (isTextAnchor(wanted)) {
    return candidates.filter((element) => saysExactly(element, wanted.text));
  }
  return candidates.filter(
    (element) => element.getAttribute('data-testid') === wanted.testId && !isHidden(element),
  );
}

type Located = { kind: 'one'; element: Element } | Exclude<FindResult, { kind: 'found' }>;

/**
 * The container to look for `wanted` in: the region around its anchor, outwards from the anchor
 * to the first element holding a match, never past `root`.
 */
function regionFor(root: ParentNode, region: Region, wanted: Target | TextAnchor): Located {
  const anchor = locate(root, region.around);
  if (anchor.kind !== 'one') {
    return anchor;
  }
  let current = parentOf(anchor.element);
  for (let level = 1; current !== null && level <= region.levels; level += 1) {
    if (region.single !== undefined && matching(current, region.single).length > 1) {
      break;
    }
    if (matching(current, wanted).length > 0) {
      return { kind: 'one', element: current };
    }
    if (current === root) {
      break;
    }
    current = parentOf(current);
  }
  return { kind: 'not_found', missing: wanted };
}

function locate(root: ParentNode, wanted: Wanted): Located {
  if (isRegion(wanted)) {
    // A region is only ever looked inside, through what it is around.
    return locate(root, wanted.around);
  }
  let container: ParentNode = root;
  const within = isTarget(wanted) || isTextAnchor(wanted) ? wanted.within : undefined;
  if (within !== undefined) {
    const outer = isRegion(within)
      ? regionFor(root, within, wanted as Target | TextAnchor)
      : locate(root, within);
    if (outer.kind !== 'one') {
      return outer;
    }
    container = outer.element;
  }
  const [first, ...others] = matching(container, wanted);
  if (first === undefined) {
    return { kind: 'not_found', missing: wanted };
  }
  if (others.length > 0) {
    return { kind: 'ambiguous', target: wanted, count: others.length + 1 };
  }
  return { kind: 'one', element: first };
}

/** Plain words for why `find` did not find exactly one element. */
export function findProblem(result: Exclude<FindResult, { kind: 'found' }>): string {
  return result.kind === 'not_found'
    ? result.missing.description
    : `${result.target.description} (found ${String(result.count)}, so none was chosen)`;
}

const DIALOG_ROLES = new Set(['dialog', 'alertdialog']);

function isTextField(element: Element): boolean {
  return roleOf(element) === 'textbox' && !isHidden(element);
}

/** A form the element would submit when pressed, or null. */
function submitsForm(element: Element): boolean {
  const tag = element.localName;
  const type = (element.getAttribute('type') ?? '').toLowerCase();
  const submitter =
    (tag === 'button' && (type === '' || type === 'submit')) ||
    (tag === 'input' && (type === 'submit' || type === 'image'));
  return submitter && (element as HTMLButtonElement).form !== null;
}

/**
 * The facts of `element` on its own, without the dialog or container it sits in. The names are
 * read only when the matcher asks for them, since reading a name walks the element's subtree.
 */
function ownFacts(element: Element, dialog: ControlFacts['dialog'] = null): ControlFacts {
  let name: string | undefined;
  let otherNames: readonly string[] | undefined;
  return {
    role: roleOf(element),
    get name() {
      name ??= nameOf(element);
      return name;
    },
    get otherNames() {
      otherNames ??= [
        collapse(element.getAttribute('aria-label') ?? ''),
        collapse(textOf(element, true)),
        collapse(element.getAttribute('title') ?? ''),
      ].filter((other) => other !== '');
      return otherNames;
    },
    matches: (selector) => element.matches(selector),
    dialog,
    inlineField: () => (dialog === null ? inlineFieldOf(element) : null),
    fieldOnPage: (field) => fieldOnPage(element.ownerDocument, field),
    submitsForm: submitsForm(element),
  };
}

/** A dialog's title: its accessible name, else its first heading's. */
export function dialogTitleOf(dialog: Element): string {
  const title = nameOf(dialog);
  if (title !== '') {
    return title;
  }
  const heading = elementsUnder(dialog).find((inner) => roleOf(inner) === 'heading');
  return heading === undefined ? '' : nameOf(heading);
}

/** Whether the element is a dialog (`role=dialog` or `alertdialog`, or a `<dialog>`). */
export function isDialog(element: Element): boolean {
  return DIALOG_ROLES.has(roleOf(element) ?? '');
}

/** The roles of the controls a dialog is recognised by when it has no title (`forbidden.ts`). */
const CONTROL_ROLES = new Set([
  'button',
  'checkbox',
  'combobox',
  'link',
  'listbox',
  'menuitem',
  'menuitemcheckbox',
  'menuitemradio',
  'option',
  'radio',
  'searchbox',
  'slider',
  'spinbutton',
  'switch',
  'tab',
  'textbox',
]);

/** What the matcher knows of a dialog: its title, and the names of every visible control in it. */
export function dialogFactsOf(dialog: Element): DialogFacts {
  return {
    title: dialogTitleOf(dialog),
    controls: elementsUnder(dialog)
      .filter((inner) => CONTROL_ROLES.has(roleOf(inner) ?? '') && !isHidden(inner))
      .map((inner) => nameOf(inner)),
  };
}

/** The dialog `element` is in, or null. */
function dialogOf(element: Element): ControlFacts['dialog'] {
  for (let current = parentOf(element); current !== null; current = parentOf(current)) {
    if (isDialog(current)) {
      return dialogFactsOf(current);
    }
  }
  return null;
}

/**
 * Whether an `input` or `textarea` named `field` is in the document (the light DOM only), shown or
 * not, by its `aria-label`, `aria-labelledby`, `placeholder`, or `title`. A quick look the matcher
 * takes for every control, so it reads the document's live lists of those elements, no styles, and
 * no `<label>` (whose lookup walks the document); {@link inlineFieldOf} then reads the visible field
 * beside the control by its accessible name.
 */
function fieldOnPage(document: Document, field: string): boolean {
  const wanted = collapse(field);
  const says = (text: string | null) => text !== null && collapse(text) === wanted;
  const named = (inner: Element) => {
    const labelledBy = inner.getAttribute('aria-labelledby');
    return (
      says(inner.getAttribute('aria-label')) ||
      says(inner.getAttribute('placeholder')) ||
      says(inner.getAttribute('title')) ||
      (labelledBy !== null &&
        says(
          labelledBy
            .split(/\s+/)
            .map((id) => document.getElementById(id)?.textContent ?? '')
            .join(' '),
        ))
    );
  };
  return (
    [...document.getElementsByTagName('input')].some(named) ||
    [...document.getElementsByTagName('textarea')].some(named)
  );
}

/** The label of the text field in the nearest container of `element` that has one. */
function inlineFieldOf(element: Element): string | null {
  for (let current = parentOf(element); current !== null; current = parentOf(current)) {
    const field = elementsUnder(current).find(isTextField);
    if (field !== undefined) {
      return nameOf(field);
    }
  }
  return null;
}

/** What the forbidden-control matcher needs to know about `element`. */
export function controlFacts(element: Element): ControlFacts {
  return ownFacts(element, dialogOf(element));
}

/**
 * The matcher's verdict on pressing `element`: the element itself, then every element it sits
 * in, since a click reaches those too.
 */
export function verdictOf(element: Element): Verdict {
  const verdict = classify(controlFacts(element));
  if (verdict.kind === 'forbidden') {
    return verdict;
  }
  for (let current = parentOf(element); current !== null; current = parentOf(current)) {
    const reason = forbiddenItself(ownFacts(current));
    if (reason !== null) {
      return { kind: 'forbidden', reason: `it is inside a control: ${reason}` };
    }
  }
  return verdict;
}

export interface PageOptions {
  clock?: Clock;
  /** Once aborted, every primitive that changes the page refuses. */
  signal?: AbortSignal;
  /** The page's address; the document's own unless a test stands in for it. */
  address?: () => string;
  /** Loads another address in the tab; the document's own navigation unless a test stands in. */
  navigate?: (address: string) => void;
}

/** Whether the element scrolls its own content (a list pane), rather than the page. */
function scrollsItself(element: Element): boolean {
  if (element.scrollHeight <= element.clientHeight) {
    return false;
  }
  const view = element.ownerDocument.defaultView;
  const overflow = view === null ? '' : view.getComputedStyle(element).overflowY;
  return ['auto', 'scroll', 'overlay'].includes(overflow);
}

/** What a page and its run handles last looked for, so a failure can be located afterwards. */
interface Trail {
  target: Target | null;
}

/** The children of an element, inside an open shadow root too, leaving out the extension's panel. */
function childElementsOf(element: Element): Element[] {
  const children = [...element.children, ...(element.shadowRoot?.children ?? [])];
  return children.filter((child) => !child.hasAttribute(PANEL_HOST_ATTRIBUTE));
}

/** The node the diagnostic report keeps for an element: tag and safe attributes, never text. */
function structureNode(element: Element): StructureNode {
  return structureNodeOf({
    tag: element.localName,
    role: element.getAttribute('role'),
    type: element.getAttribute('type'),
    testId: element.getAttribute('data-testid'),
    attributeNames: element.getAttributeNames(),
  });
}

/**
 * The page region around `anchor` for the diagnostic report (invariant 6): its ancestors and
 * siblings by tag name only, and the anchor with what it holds, breadth-first to six levels and
 * at most 300 nodes. Only tag names and the kept attributes are read; no text node is visited.
 */
function captureStructure(anchor: Element, anchoredOn: StructureAnchor): PageStructure {
  const ancestors: string[] = [];
  for (
    let parent = parentOf(anchor);
    parent !== null && ancestors.length < STRUCTURE_MAX_ANCESTORS;
    parent = parentOf(parent)
  ) {
    ancestors.push(parent.localName);
  }
  const parent = parentOf(anchor);
  const siblings = (parent === null ? [] : childElementsOf(parent))
    .filter((sibling) => sibling !== anchor)
    .map((sibling) => sibling.localName);
  let truncated = siblings.length > STRUCTURE_MAX_SIBLINGS;
  const keptSiblings = siblings.slice(0, STRUCTURE_MAX_SIBLINGS);
  let count = keptSiblings.length + 1;
  const root: StructureNode = { ...structureNode(anchor), anchor: true };
  const queue: { element: Element; node: StructureNode; depth: number }[] = [
    { element: anchor, node: root, depth: 0 },
  ];
  for (let next = queue.shift(); next !== undefined; next = queue.shift()) {
    const children = childElementsOf(next.element);
    if (children.length > 0 && next.depth >= STRUCTURE_MAX_DEPTH) {
      truncated = true;
      continue;
    }
    for (const child of children) {
      if (count >= STRUCTURE_MAX_NODES) {
        truncated = true;
        break;
      }
      const node = structureNode(child);
      (next.node.children ??= []).push(node);
      count += 1;
      queue.push({ element: child, node, depth: next.depth + 1 });
    }
  }
  return { anchoredOn, ancestors, siblings: keptSiblings, root, nodeCount: count, truncated };
}

function viewOf(element: Element): Window & typeof globalThis {
  const view = element.ownerDocument.defaultView;
  if (view === null) {
    throw new PrimitiveError('the page to be open');
  }
  return view;
}

function selectedOf(element: Element): boolean | null {
  for (const attribute of ['aria-selected', 'aria-pressed', 'data-selected']) {
    const value = element.getAttribute(attribute);
    if (value === 'true' || value === 'false') {
      return value === 'true';
    }
  }
  return null;
}

function checkedOf(element: Element): boolean | null {
  const aria = element.getAttribute('aria-checked');
  if (aria === 'true' || aria === 'false') {
    return aria === 'true';
  }
  if (element.localName === 'input') {
    const type = (element.getAttribute('type') ?? '').toLowerCase();
    if (type === 'checkbox' || type === 'radio') {
      return (element as HTMLInputElement).checked;
    }
  }
  return null;
}

const BLOCK_ELEMENTS = new Set([
  'p',
  'div',
  'li',
  'pre',
  'blockquote',
  'h1',
  'h2',
  'h3',
  'h4',
  'h5',
  'h6',
]);

/**
 * The text of an editable region as its lines: each paragraph (Suno's Lexical editor writes one
 * `<p>` per line) and each line break ends a line; an empty paragraph holding only its `<br>` is
 * one empty line.
 */
function editableText(root: Element): string {
  const lines: string[] = [];
  let line = '';
  let endedByBreak = false;
  const visit = (node: Node) => {
    if (node.nodeType === node.TEXT_NODE) {
      line += node.textContent ?? '';
      endedByBreak = false;
      return;
    }
    if (node.nodeType !== node.ELEMENT_NODE) {
      return;
    }
    const element = node as Element;
    if (element.localName === 'br') {
      lines.push(line);
      line = '';
      endedByBreak = true;
      return;
    }
    const block = element !== root && BLOCK_ELEMENTS.has(element.localName);
    if (block && line !== '') {
      lines.push(line);
      line = '';
    }
    for (const child of element.childNodes) {
      visit(child);
    }
    if (block) {
      if (!(endedByBreak && line === '')) {
        lines.push(line);
      }
      line = '';
      endedByBreak = false;
    }
  };
  visit(root);
  if (line !== '') {
    lines.push(line);
  }
  return lines.join('\n');
}

/** How long typing waits for an editor to take its selection, and then the text (TS-007-8). */
export const TYPE_SETTLE_MS = 50;

/**
 * Pastes `text` into `element` as plain text; whether the page took it (an editor that handles
 * paste cancels the event). A browser without clipboard events (jsdom) has nothing to take it.
 */
function pasted(element: Element, text: string): boolean {
  const view = viewOf(element);
  if (typeof view.ClipboardEvent !== 'function' || typeof view.DataTransfer !== 'function') {
    return false;
  }
  const data = new view.DataTransfer();
  data.setData('text/plain', text);
  return !element.dispatchEvent(
    new view.ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }),
  );
}

function valueOf(element: Element): string | null {
  if (['input', 'textarea', 'select'].includes(element.localName)) {
    return (element as HTMLInputElement).value;
  }
  if (roleOf(element) === 'slider' || roleOf(element) === 'spinbutton') {
    return element.getAttribute('aria-valuenow');
  }
  if (element.getAttribute('contenteditable') === 'true') {
    return editableText(element);
  }
  return null;
}

function expandedOf(element: Element): boolean | null {
  const value = element.getAttribute('aria-expanded');
  return value === 'true' || value === 'false' ? value === 'true' : null;
}

/** The native value setter, so that React's own tracking sees the change as the user's. */
function setNativeValue(element: HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement) {
  const view = viewOf(element);
  const prototype =
    element.localName === 'textarea'
      ? view.HTMLTextAreaElement.prototype
      : element.localName === 'select'
        ? view.HTMLSelectElement.prototype
        : view.HTMLInputElement.prototype;
  return (value: string) => {
    // The prototype's setter with the element as receiver, not the element's own property.
    if (!Reflect.set(prototype, 'value', value, element)) {
      throw new PrimitiveError('a form control that takes a value');
    }
  };
}

function announce(element: Element, ...types: string[]): void {
  const view = viewOf(element);
  for (const type of types) {
    element.dispatchEvent(new view.Event(type, { bubbles: true, composed: true }));
  }
}

/** A range input to set a slider through: the slider itself, or one inside it. */
function rangeInputOf(element: Element): HTMLInputElement | null {
  if (element.localName === 'input') {
    return element as HTMLInputElement;
  }
  return (
    (elementsUnder(element).find(
      (inner) =>
        inner.localName === 'input' && (inner.getAttribute('type') ?? '').toLowerCase() === 'range',
    ) as HTMLInputElement | undefined) ?? null
  );
}

/** The most key presses a slider is given to reach a value before the read-back decides. */
const MAX_KEY_STEPS = 1000;

/**
 * How long a slider is given after each arrow key before it is read again (TS-007-9): Suno's
 * sliders apply a key a task later, so a key sent before the last one took is lost.
 */
export const SLIDER_KEY_SETTLE_MS = 10;

/**
 * How long a slider may take to show a key at the most: a key that changes nothing in this time
 * ends the stepping (a slider at its end, or one that ignores keys). TS-007-9 saw a key show about
 * 300 ms after a burst of keys.
 */
export const SLIDER_KEY_WAIT_MS = 400;

/** A slider's `aria-valuenow` as a number; NaN when it has none. */
function sliderValueOf(element: Element): number {
  const value = element.getAttribute('aria-valuenow');
  return value === null || value.trim() === '' ? Number.NaN : Number(value);
}

/** Sends one arrow key (down and up) to a slider. */
function sendArrow(element: Element, key: 'ArrowLeft' | 'ArrowRight'): void {
  const view = viewOf(element);
  element.dispatchEvent(
    new view.KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }),
  );
  element.dispatchEvent(new view.KeyboardEvent('keyup', { key, bubbles: true, cancelable: true }));
}

function readingOf(element: Element): Reading {
  return {
    value: valueOf(element),
    text: collapse(element.textContent),
    checked: checkedOf(element),
    selected: selectedOf(element),
    expanded: expandedOf(element),
    enabled: isEnabled(element),
  };
}

/** The element's own image's address, or that of the one visible image inside it; else null. */
function imageAddressOf(element: Element): string | null {
  const images =
    element.localName === 'img'
      ? [element]
      : elementsUnder(element).filter((inner) => inner.localName === 'img' && !isHidden(inner));
  const [image, ...others] = images;
  if (image === undefined || others.length > 0) {
    return null;
  }
  const address = image.getAttribute('src');
  return address === null || address.trim() === '' ? null : address.trim();
}

/** One element {@link Page.readAll} read: never the element itself. */
/** The next-page button of Suno's list pager (TS-007, TS-007-23): it is found by structure. */
export const PAGER_NEXT: Target = {
  role: 'button',
  description: "the list pager's next-page button (›), beside the page number",
};

/**
 * Suno's list pager (TS-007, Library › Songs; TS-007-23, a workspace's songs): no pager on the page
 * (`none`), its next-page button (`found`), or a pager that does not look as captured (`mismatch`,
 * with what was expected).
 */
export type PagerResult =
  { kind: 'none' } | { kind: 'found'; found: Found } | { kind: 'mismatch'; expected: string };

/** Whether every one of `buttons` is a plain `type=button` that shows only an icon. */
function showsOnlyIcons(buttons: readonly Element[]): boolean {
  return buttons.every(
    (button) =>
      button.localName === 'button' &&
      button.getAttribute('type') === 'button' &&
      collapse(button.textContent) === '' &&
      button.querySelector('svg') !== null,
  );
}

/**
 * Library's pager, as TS-007 captured it below Library › Songs (and TS-007-23 saw again): one
 * visible `span` whose whole text is "Page", its parent holding exactly, in order, that span, a
 * button, one `input` with a placeholder (the page number, `#`), and a button, then nothing but
 * buttons. The button after the input is the next page. Every button there must have no name and
 * show only an icon.
 */
function libraryPagerOf(root: ParentNode): PagerResult {
  const labels = elementsUnder(root).filter(
    (element) =>
      element.localName === 'span' &&
      element.children.length === 0 &&
      collapse(element.textContent) === 'Page' &&
      !isHidden(element),
  );
  if (labels.length === 0) {
    return { kind: 'none' };
  }
  const mismatch = (what: string): PagerResult => ({
    kind: 'mismatch',
    expected: `one list pager (Page, ‹, the page number, ›) ${what}`,
  });
  const [label] = labels;
  if (labels.length > 1 || label === undefined) {
    return mismatch('on the page, not several');
  }
  const container = label.parentElement;
  if (container === null) {
    return mismatch('with its controls beside the Page label');
  }
  const [first, previous, number, next, ...rest] = [...container.children];
  if (
    first !== label ||
    previous?.localName !== 'button' ||
    number?.localName !== 'input' ||
    !number.hasAttribute('placeholder') ||
    next?.localName !== 'button' ||
    rest.some((element) => element.localName !== 'button')
  ) {
    return mismatch('in that order, with nothing else but buttons after');
  }
  const buttons = [previous, next, ...rest];
  if (buttons.some((button) => nameOf(button) !== '') || !showsOnlyIcons(buttons)) {
    return mismatch('whose buttons have no name and show only an icon');
  }
  return { kind: 'found', found: handle(PAGER_NEXT, next) };
}

/** The name of the page-number box in the pager above a workspace's songs (TS-007-23). */
export const PAGE_NUMBER_NAME = 'Current page number';

/**
 * The pager in the header of a workspace's songs on `/create?wid=` (TS-007-23, seen live on
 * 2026-10-31): one visible `input` named "Current page number", its parent holding exactly, in
 * order, a button named "Previous page", that input, and a button named "Next page", each button
 * showing only an icon. The last is the next page.
 */
function namedPagerOf(root: ParentNode): PagerResult {
  const numbers = elementsUnder(root).filter(
    (element) =>
      element.localName === 'input' && nameOf(element) === PAGE_NUMBER_NAME && !isHidden(element),
  );
  if (numbers.length === 0) {
    return { kind: 'none' };
  }
  const mismatch = (what: string): PagerResult => ({
    kind: 'mismatch',
    expected: `one list pager (Previous page, ${PAGE_NUMBER_NAME}, Next page) ${what}`,
  });
  const [number] = numbers;
  if (numbers.length > 1 || number === undefined) {
    return mismatch('on the page, not several');
  }
  const [previous, middle, next, ...rest] =
    number.parentElement === null ? [] : [...number.parentElement.children];
  if (
    previous === undefined ||
    next === undefined ||
    middle !== number ||
    rest.length > 0 ||
    nameOf(previous) !== 'Previous page' ||
    nameOf(next) !== 'Next page'
  ) {
    return mismatch('in that order, with nothing else beside them');
  }
  if (!showsOnlyIcons([previous, next])) {
    return mismatch('whose buttons show only an icon');
  }
  return { kind: 'found', found: handle(PAGER_NEXT, next) };
}

/**
 * Finds the next-page button of the list pager by its structure: Library's (`‹ [#] ›` after a
 * "Page" label, TS-007) or the one above a workspace's songs (`‹ [n] ›`, its parts named,
 * TS-007-23). Anything that looks like a pager but not as captured is a mismatch, and so are two
 * pagers on one page, so a button that could be something else is never pressed.
 */
function pagerNextOf(root: ParentNode): PagerResult {
  const library = libraryPagerOf(root);
  const named = namedPagerOf(root);
  if (library.kind === 'mismatch') {
    return library;
  }
  if (named.kind === 'mismatch') {
    return named;
  }
  if (library.kind === 'found' && named.kind === 'found') {
    return { kind: 'mismatch', expected: 'one list pager on the page, not two' };
  }
  return library.kind === 'found' ? library : named;
}

export interface ReadAllEntry {
  name: string;
  reading: Reading;
  image: string | null;
  /** Its lead text (as {@link Target.leadText} compares it), or null. */
  lead: string | null;
}

/**
 * Suno's page, through the primitives a workflow may use: find, read, set a value, choose an
 * option, click, and wait. Reading never changes anything; set, choose, and click refuse once the
 * run's signal is aborted, and refuse a disabled element or one no longer on the page.
 */
export class Page {
  private readonly document: Document;
  private readonly clock: Clock;
  private readonly signal: AbortSignal | undefined;
  private readonly location: () => string;
  private readonly navigation: (address: string) => void;
  /** The press the matcher refused on this handle, after which it changes nothing more. */
  private refused: ForbiddenControlError | null = null;
  /** Shared with the run handles made from this page: what was last looked for. */
  private readonly trail: Trail;

  constructor(document: Document, options: PageOptions = {}, trail: Trail = { target: null }) {
    this.document = document;
    this.clock = options.clock ?? realClock;
    this.signal = options.signal;
    this.location = options.address ?? (() => document.location.href);
    this.navigation =
      options.navigate ??
      ((address) => {
        document.location.assign(address);
      });
    this.trail = trail;
  }

  /** The same page for one run: once `signal` is aborted, nothing more changes on the page. */
  withSignal(signal: AbortSignal): Page {
    return new Page(
      this.document,
      { clock: this.clock, signal, address: this.location, navigate: this.navigation },
      this.trail,
    );
  }

  /**
   * The structure of the page region around the most recent failure, for the diagnostic report:
   * anchored on the element last looked for when it is on the page once, else on the container
   * it was looked for in, else on the page's main region. Reads only; never any text.
   */
  structureAround(): PageStructure {
    const target = this.trail.target;
    if (target !== null) {
      const failing = locate(this.document, target);
      if (failing.kind === 'one') {
        return captureStructure(failing.element, 'failing-element');
      }
      if (target.within !== undefined) {
        const container = locate(this.document, target.within);
        if (container.kind === 'one') {
          return captureStructure(container.element, 'container');
        }
      }
    }
    const main = elementsUnder(this.document).find(
      (element) => element.localName === 'main' || element.getAttribute('role') === 'main',
    );
    if (main !== undefined) {
      return captureStructure(main, 'main-region');
    }
    return captureStructure(this.document.body, 'page');
  }

  /** The page's address. */
  address(): URL {
    return new URL(this.location());
  }

  /** Finds exactly one visible element; two matches are `ambiguous`, never the first of them. */
  find(target: Target): FindResult {
    this.trail.target = target;
    const located = locate(this.document, target);
    return located.kind === 'one'
      ? { kind: 'found', found: handle(target, located.element) }
      : located;
  }

  /** What the element shows. */
  read(found: Found): Reading {
    return readingOf(elementOf(found));
  }

  /**
   * The address of the image the element shows: its own `src` when it is an image, else the `src`
   * of the one visible image inside it; null when there is none or more than one. Reads only. A
   * source's thumbnail address holds the source clip's Suno ID (TS-002), which is how a loaded
   * source is verified (#148).
   */
  imageAddress(found: Found): string | null {
    return imageAddressOf(elementOf(found));
  }

  /**
   * Reads every visible element `target` matches, where `find` would not choose between them: what
   * each shows, its accessible name, and its image's address (as {@link imageAddress}). Read only:
   * it hands out no element, so nothing can be pressed through it (TS-005: the Simple form's Lyrics
   * and Styles chips, a Mashup's two songs). An empty list when the target, or what it is looked for
   * in, is not on the page.
   */
  readAll(target: Target): ReadAllEntry[] {
    return this.readAllElements(target).map((element) => ({
      name: nameOf(element),
      reading: readingOf(element),
      image: imageAddressOf(element),
      lead: leadTextOf(element),
    }));
  }

  /**
   * For every visible element `target` matches, in page order, the paths that the visible links
   * inside it which match `link` lead to (their own `href`, without query or fragment), each once,
   * in order. Read only, as {@link readAll}: it hands out no element. A playlist page's rows are read
   * so for their songs' IDs (TS-007-24), which Suno puts nowhere else but its embedded page data.
   */
  readLinks(target: Target, link: Target): string[][] {
    return this.readAllElements(target).map((element) => {
      const paths: string[] = [];
      for (const inner of elementsUnder(element)) {
        const path = matchesTarget(inner, link) ? linkPathOf(inner) : null;
        if (path !== null && !paths.includes(path)) {
          paths.push(path);
        }
      }
      return paths;
    });
  }

  /** The visible elements `target` matches, as {@link readAll} finds them. Never handed out. */
  private readAllElements(target: Target): Element[] {
    this.trail.target = target;
    let container: ParentNode = this.document;
    if (target.within !== undefined) {
      const outer = isRegion(target.within)
        ? regionFor(this.document, target.within, target)
        : locate(this.document, target.within);
      if (outer.kind !== 'one') {
        return [];
      }
      container = outer.element;
    }
    return matching(container, target);
  }

  /**
   * Sets a text box or a number box to `value`, as typing would: the native setter and an `input`
   * event. A slider is moved with {@link slide}. Never sends Enter.
   */
  set(found: Found, value: string | number): void {
    const element = this.changeable(found);
    if (roleOf(element) === 'slider') {
      throw new PrimitiveError(
        `${found.target.description} to take a typed value (it is a slider)`,
      );
    }
    if (element.localName === 'input' || element.localName === 'textarea') {
      setNativeValue(element as HTMLInputElement)(String(value));
      announce(element, 'input', 'change');
      return;
    }
    throw new PrimitiveError(`${found.target.description} to take a typed value`);
  }

  /**
   * Moves a slider to `value`: through its range input when it has one (the native setter and an
   * `input` event), else by arrow keys, one at a time. Suno's sliders apply a key a task later and
   * drop a key sent before the last one took (TS-007-9: five keys sent at once moved 50 to 51), so
   * after each key the slider is read again, {@link SLIDER_KEY_SETTLE_MS} later and then until it
   * changes or {@link SLIDER_KEY_WAIT_MS} has passed. The keys stop at the value, when a key
   * changes nothing in that time, or after a key passes the value (a slider that moves in steps,
   * Duration by 5, ends on the nearer step); at most {@link MAX_KEY_STEPS} keys. Synthetic keys
   * were unreliable in TS-002, so the step's read-back decides whether this worked. Never sends
   * Enter.
   */
  async slide(found: Found, value: number): Promise<void> {
    const element = this.changeable(found);
    if (roleOf(element) !== 'slider') {
      throw new PrimitiveError(`${found.target.description} to be a slider`);
    }
    const input = rangeInputOf(element);
    if (input !== null) {
      setNativeValue(input)(String(value));
      announce(input, 'input', 'change');
      return;
    }
    const current = () => sliderValueOf(this.changeable(found));
    const press = (key: 'ArrowLeft' | 'ArrowRight') => {
      sendArrow(this.changeable(found), key);
    };
    for (let step = 0; step < MAX_KEY_STEPS; step += 1) {
      const before = current();
      if (!Number.isFinite(before) || before === value) {
        return;
      }
      const key = before < value ? 'ArrowRight' : 'ArrowLeft';
      press(key);
      const after = await this.afterKey(current, before);
      if (after === before) {
        return;
      }
      if (before < value ? after > value : after < value) {
        // Passed it: a slider that moves in steps (Duration, by 5) ends on the nearer step.
        if (Math.abs(after - value) > Math.abs(before - value)) {
          press(key === 'ArrowRight' ? 'ArrowLeft' : 'ArrowRight');
          await this.afterKey(current, after);
        }
        return;
      }
    }
  }

  /**
   * Sends Escape to an open dialog or menu, as a user closing it does (TS-007-10: Escape closes
   * Suno's Model dialog). It presses nothing and never sends Enter; whether the dialog closed is
   * for the caller to read.
   */
  dismiss(found: Found): void {
    const element = this.reachable(found);
    const view = viewOf(element);
    for (const type of ['keydown', 'keyup']) {
      element.dispatchEvent(
        new view.KeyboardEvent(type, {
          key: 'Escape',
          code: 'Escape',
          keyCode: 27,
          bubbles: true,
          cancelable: true,
        }),
      );
    }
  }

  /**
   * Replaces the text of an editable region (Suno's Lexical lyrics editor, which `set` refuses) as
   * a user would: the region is focused, everything in it is selected with the select-all shortcut
   * (Lexical keeps its own selection, which a DOM selection alone does not move), and the whole
   * text is pasted, so each line becomes a paragraph with blank lines and leading spaces kept
   * (TS-007-8: Suno's editor ignores `insertParagraph`, `insertLineBreak`, and a typed newline, and
   * joined every line into one). Empty text is deleted with Backspace. Where nothing takes the
   * paste or the key, the text goes in through the browser's editing commands, one line at a time.
   * The editor learns its selection from the browser's `selectionchange`, a task after focus, and
   * drops a paste that comes before it (TS-007-8), so the text waits {@link TYPE_SETTLE_MS} after
   * focus, and as long again after the paste for the editor to apply it.
   * Nothing is pressed and no Enter key is sent. The step's read-back decides whether it worked.
   */
  async typeText(found: Found, text: string): Promise<void> {
    let element = this.changeable(found);
    if (element.getAttribute('contenteditable') !== 'true') {
      throw new PrimitiveError(`${found.target.description} to be an editable text region`);
    }
    const document = element.ownerDocument;
    const view = viewOf(element);
    (element as HTMLElement).focus();
    view.getSelection()?.selectAllChildren(element);
    await this.clock.sleep(TYPE_SETTLE_MS);
    element = this.changeable(found);
    const apple = /Mac|iPhone|iPad/.test(view.navigator.platform);
    const key = (init: KeyboardEventInit) =>
      element.dispatchEvent(
        new view.KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...init }),
      );
    key({ key: 'a', code: 'KeyA', keyCode: 65, metaKey: apple, ctrlKey: !apple });
    const lines = text.replace(/\r\n?/g, '\n').split('\n');
    if (text === '') {
      if (!key({ key: 'Backspace', code: 'Backspace', keyCode: 8 })) {
        await this.clock.sleep(TYPE_SETTLE_MS);
        return;
      }
    } else if (pasted(element, lines.join('\n'))) {
      await this.clock.sleep(TYPE_SETTLE_MS);
      return;
    }
    // execCommand is deprecated, but it is the one way to give an editor the browser's own trusted
    // input events (a synthetic `beforeinput` is not inserted by the browser). It is read by name
    // so that a browser without it (jsdom) is told apart, not called.
    const execCommand: unknown = Reflect.get(document, 'execCommand');
    if (typeof execCommand !== 'function') {
      throw new PrimitiveError(`a browser that can type into ${found.target.description}`);
    }
    const edit = (command: string, value?: string) => {
      Reflect.apply(execCommand, document, [command, false, value]);
    };
    if (text === '') {
      edit('delete');
      return;
    }
    lines.forEach((line, index) => {
      if (index > 0) {
        edit('insertParagraph');
      }
      if (line !== '') {
        edit('insertText', line);
      }
    });
  }

  /**
   * Chooses the option named `option` in a select, or the one option, radio, tab, menu item, or
   * button of that name inside the found element, by clicking it.
   */
  choose(found: Found, option: string): void {
    // The matcher first (#332): a forbidden control is refused even while it is disabled.
    const element = this.reachable(found);
    this.judge(element, found.target, null);
    if (!isEnabled(element)) {
      throw new PrimitiveError(`${found.target.description} to be enabled`);
    }
    if (element.localName === 'select') {
      const select = element as HTMLSelectElement;
      const [match, ...others] = [...select.options].filter(
        (item) => collapse(item.text) === collapse(option),
      );
      if (match === undefined || others.length > 0) {
        throw new PrimitiveError(`${found.target.description} to offer the option asked for`);
      }
      setNativeValue(select)(match.value);
      announce(select, 'input', 'change');
      return;
    }
    const choices = elementsUnder(element).filter(
      (inner) =>
        [
          'option',
          'radio',
          'tab',
          'menuitem',
          'menuitemradio',
          'menuitemcheckbox',
          'button',
        ].includes(roleOf(inner) ?? '') &&
        nameOf(inner) === collapse(option) &&
        !isHidden(inner),
    );
    const [choice, ...others] = choices;
    if (choice === undefined || others.length > 0) {
      throw new PrimitiveError(
        choice === undefined
          ? `${found.target.description} to offer the option asked for`
          : `${found.target.description} to offer the option asked for once (found ${String(choices.length)})`,
      );
    }
    this.press(choice, found.target, null);
  }

  /**
   * Clicks the element, with the pointer events a user's click sends first. The forbidden-control
   * matcher is asked first, on every call: a forbidden control or a named exception is refused.
   */
  click(found: Found): void {
    this.press(this.reachable(found), found.target, null);
  }

  /**
   * Clicks one of the controls of invariant 4's one permitted change, creating a workspace, and
   * nothing else. Only the workspace workflow (`adapter/workflows/workspace.ts`) may call it; the
   * invariant 4 guard's static scan fails on any other caller.
   */
  createWorkspaceClick(found: Found): void {
    const element = this.reachable(found);
    // A forbidden control, or a different exception once there is one, is refused here, enabled or
    // not (#332); anything else allowed is not this primitive's to press.
    if (this.judge(element, found.target, 'create-workspace').kind !== 'exception') {
      throw new PrimitiveError(`${found.target.description} to be Suno's create-workspace control`);
    }
    this.press(element, found.target, 'create-workspace');
  }

  /**
   * Clicks one of the Download dialog's controls the download exception names (a format, "Unlock &
   * Download", Close), and nothing else. Only the download workflows
   * (`adapter/workflows/download.ts`) may call it; the invariant 4 guard's static scan fails on any
   * other caller.
   */
  downloadDialogClick(found: Found): void {
    const element = this.reachable(found);
    if (this.judge(element, found.target, 'download-clip').kind !== 'exception') {
      throw new PrimitiveError(`${found.target.description} to be in Suno's Download dialog`);
    }
    this.press(element, found.target, 'download-clip');
  }

  /**
   * The list pager's next-page button, found by structure ({@link pagerNextOf}); reads only. Press
   * it with {@link click}, which asks the forbidden-control matcher as for any other control.
   */
  pagerNext(): PagerResult {
    this.trail.target = PAGER_NEXT;
    return pagerNextOf(this.document);
  }

  /**
   * Scrolls the page, and every region of it that scrolls its own content, to the end, as a user
   * scrolling a list to its bottom does, so that Suno asks for the list's next page (TS-003: the
   * Library, Trash, and workspace lists load more on scroll). It presses nothing and sends no
   * event: the browser reports the scroll to the page itself. The extension's panel is left alone.
   */
  scrollToEnd(): void {
    this.ensureRunning();
    // Some documents (jsdom among them) have no scrolling element.
    const root = this.document.scrollingElement as Element | null | undefined;
    const scrolling = [
      ...(root === null || root === undefined ? [] : [root]),
      ...elementsUnder(this.document).filter(
        (element) => scrollsItself(element) && !isHidden(element),
      ),
    ];
    for (const element of new Set(scrolling)) {
      element.scrollTop = element.scrollHeight;
    }
  }

  /**
   * Scrolls one list to its end, and nothing else on the page: the nearest element that scrolls
   * its own content, from the list outwards (the list itself included), else the page itself when
   * it scrolls. Where a list sits beside other panes that scroll, such as a workspace's songs on
   * `/create?wid=` beside Suno's Create form (#422), only the list's own pane moves. Like
   * {@link scrollToEnd} it presses nothing and sends no event. A list in nothing that scrolls (all
   * of it on screen, or jsdom, which lays nothing out) is left as it is; the reader, which waits
   * for Suno's next page after each scroll, says when none came.
   */
  scrollListToEnd(found: Found): void {
    const list = this.reachable(found);
    for (let current: Element | null = list; current !== null; current = parentOf(current)) {
      if (scrollsItself(current) && !isHidden(current)) {
        current.scrollTop = current.scrollHeight;
        return;
      }
    }
    const root = this.document.scrollingElement as Element | null | undefined;
    if (root !== null && root !== undefined && root.scrollHeight > root.clientHeight) {
      root.scrollTop = root.scrollHeight;
    }
  }

  /**
   * Loads a Suno page by its address, as typing it would: the adapter reaches pages by address,
   * never by pressing a link or a button (the forbidden-control matcher's own rule), with one
   * exception: the sync's workspace list, which a direct load does not ask Suno for, is reached by
   * pressing Library's Workspaces tab through {@link click} (#421, TS-007-6). Only a suno.com
   * address is accepted. The content script is replaced with the page, so nothing of a run
   * survives it except what the service worker holds.
   */
  go(address: URL): void {
    this.ensureRunning();
    if (!isSunoAddress(address)) {
      throw new PrimitiveError('an address on suno.com');
    }
    this.navigation(address.href);
  }

  /** The refusal that stopped this handle, if the matcher refused a press on it. */
  refusal(): ForbiddenControlError | null {
    return this.refused;
  }

  /** Waits until `condition` holds, reading it every 100 ms; false when `timeoutMs` passed first. */
  async wait(condition: () => boolean, timeoutMs: number): Promise<boolean> {
    const result = await poll(() => ({ ok: condition() }), timeoutMs, this.clock, POLL_MS);
    return result.ok;
  }

  /**
   * A slider's value after a key: read {@link SLIDER_KEY_SETTLE_MS} later, and again until it is
   * not `before` or {@link SLIDER_KEY_WAIT_MS} has passed; `before` when nothing changed.
   */
  private async afterKey(read: () => number, before: number): Promise<number> {
    const deadline = this.clock.now() + SLIDER_KEY_WAIT_MS;
    for (;;) {
      await this.clock.sleep(SLIDER_KEY_SETTLE_MS);
      const now = read();
      if (now !== before || this.clock.now() >= deadline) {
        return now;
      }
    }
  }

  /** Throws when this handle has stopped: refused a press, or its run is over. */
  private ensureRunning(): void {
    if (this.refused !== null) {
      throw this.refused;
    }
    if (this.signal?.aborted === true) {
      throw new StoppedError();
    }
  }

  /** The found element, still on the page, enabled or not: a press asks the matcher next. */
  private reachable(found: Found): Element {
    this.ensureRunning();
    const element = elementOf(found);
    if (!element.isConnected || isHidden(element)) {
      throw new PrimitiveError(`${found.target.description} to be still on the page`);
    }
    return element;
  }

  private changeable(found: Found): Element {
    const element = this.reachable(found);
    if (!isEnabled(element)) {
      throw new PrimitiveError(`${found.target.description} to be enabled`);
    }
    return element;
  }

  /**
   * The one place a press reaches the page. The matcher is asked before any event, with no way to
   * skip it: only an allowed control, or the named exception `allow`, is pressed.
   */
  private press(element: Element, target: Target, allow: ExceptionName | null): void {
    this.judge(element, target, allow);
    if (!isEnabled(element)) {
      throw new PrimitiveError(`${target.description} to be enabled`);
    }
    const view = viewOf(element);
    const init = { bubbles: true, cancelable: true, composed: true, button: 0 };
    element.dispatchEvent(new view.PointerEvent('pointerdown', init));
    element.dispatchEvent(new view.MouseEvent('mousedown', init));
    element.dispatchEvent(new view.PointerEvent('pointerup', init));
    element.dispatchEvent(new view.MouseEvent('mouseup', init));
    element.dispatchEvent(new view.MouseEvent('click', init));
  }

  /**
   * The matcher's verdict on pressing `element`, asked before anything else about it (whether it
   * is enabled included, #332): a forbidden control, or an exception other than `allow`, is refused
   * and poisons the handle.
   */
  private judge(element: Element, target: Target, allow: ExceptionName | null): Verdict {
    if (this.refused !== null) {
      throw this.refused;
    }
    if (this.signal?.aborted === true) {
      throw new StoppedError();
    }
    const verdict = verdictOf(element);
    if (
      verdict.kind === 'forbidden' ||
      (verdict.kind === 'exception' && verdict.exception !== allow)
    ) {
      this.refused = new ForbiddenControlError(
        target.description,
        verdict.kind === 'forbidden'
          ? verdict.reason
          : `it is the named exception '${verdict.exception}', pressed only by its own primitive`,
      );
      throw this.refused;
    }
    return verdict;
  }
}
