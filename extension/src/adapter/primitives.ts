import { poll, POLL_MS, realClock, type Clock } from './clock.ts';
import {
  classify,
  forbiddenItself,
  type ControlFacts,
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
  | 'heading'
  | 'img'
  | 'link'
  | 'listbox'
  | 'menu'
  | 'menuitem'
  | 'menuitemcheckbox'
  | 'menuitemradio'
  | 'option'
  | 'radio'
  | 'radiogroup'
  | 'slider'
  | 'spinbutton'
  | 'status'
  | 'switch'
  | 'tab'
  | 'tablist'
  | 'textbox';

/** A region of the page known only by its test attribute (`data-testid`). */
export interface Scope {
  testId: string;
  /** Plain words for a report: "the Styles section". */
  description: string;
}

/** What a workflow looks for. */
export interface Target {
  role: Role;
  /** The accessible name: equal after collapsing white space, or matching the expression. */
  name?: string | RegExp;
  /** A test attribute the element itself carries. */
  testId?: string;
  /** Look only inside this element or region, which must itself be found exactly once. */
  within?: Target | Scope;
  /** Plain words for a report, read after "expected": "a text box labelled Styles". */
  description: string;
}

/** An element `find` found. Opaque: only the primitives can reach the element behind it. */
export interface Found {
  readonly target: Target;
}

export type FindResult =
  | { kind: 'found'; found: Found }
  | { kind: 'not_found'; missing: Target | Scope }
  | { kind: 'ambiguous'; target: Target | Scope; count: number };

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
  const actual = nameOf(element);
  return typeof name === 'string' ? actual === collapse(name) : name.test(actual);
}

function isScope(value: Target | Scope): value is Scope {
  return !('role' in value);
}

function matchesTarget(element: Element, target: Target): boolean {
  return (
    roleOf(element) === target.role &&
    (target.testId === undefined || element.getAttribute('data-testid') === target.testId) &&
    nameMatches(element, target.name) &&
    !isHidden(element)
  );
}

function matching(root: ParentNode, wanted: Target | Scope): Element[] {
  const candidates = elementsUnder(root);
  return isScope(wanted)
    ? candidates.filter(
        (element) => element.getAttribute('data-testid') === wanted.testId && !isHidden(element),
      )
    : candidates.filter((element) => matchesTarget(element, wanted));
}

type Located = { kind: 'one'; element: Element } | Exclude<FindResult, { kind: 'found' }>;

function locate(root: ParentNode, wanted: Target | Scope): Located {
  let container: ParentNode = root;
  if (!isScope(wanted) && wanted.within !== undefined) {
    const outer = locate(root, wanted.within);
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

/** The dialog `element` is in, by title, or null. */
function dialogOf(element: Element): ControlFacts['dialog'] {
  for (let current = parentOf(element); current !== null; current = parentOf(current)) {
    if (isDialog(current)) {
      return { title: dialogTitleOf(current) };
    }
  }
  return null;
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

function valueOf(element: Element): string | null {
  if (['input', 'textarea', 'select'].includes(element.localName)) {
    return (element as HTMLInputElement).value;
  }
  if (roleOf(element) === 'slider' || roleOf(element) === 'spinbutton') {
    return element.getAttribute('aria-valuenow');
  }
  if (element.getAttribute('contenteditable') === 'true') {
    return element.textContent;
  }
  return null;
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
 * Moves a slider that has no input by arrow keys until it shows `wanted`, stopping when a key
 * changes nothing or would pass the value. Synthetic keys were unreliable in TS-002, so the
 * step's read-back decides whether this worked. Never sends Enter.
 */
function stepSlider(element: Element, wanted: number): void {
  const view = viewOf(element);
  const current = () => Number(element.getAttribute('aria-valuenow'));
  for (let step = 0; step < MAX_KEY_STEPS; step += 1) {
    const before = current();
    if (!Number.isFinite(before) || before === wanted) {
      return;
    }
    const key = before < wanted ? 'ArrowRight' : 'ArrowLeft';
    element.dispatchEvent(
      new view.KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }),
    );
    element.dispatchEvent(
      new view.KeyboardEvent('keyup', { key, bubbles: true, cancelable: true }),
    );
    const after = current();
    if (after === before || (before < wanted ? after > wanted : after < wanted)) {
      return;
    }
  }
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
  /** The press the matcher refused on this handle, after which it changes nothing more. */
  private refused: ForbiddenControlError | null = null;
  /** Shared with the run handles made from this page: what was last looked for. */
  private readonly trail: Trail;

  constructor(document: Document, options: PageOptions = {}, trail: Trail = { target: null }) {
    this.document = document;
    this.clock = options.clock ?? realClock;
    this.signal = options.signal;
    this.location = options.address ?? (() => document.location.href);
    this.trail = trail;
  }

  /** The same page for one run: once `signal` is aborted, nothing more changes on the page. */
  withSignal(signal: AbortSignal): Page {
    return new Page(
      this.document,
      { clock: this.clock, signal, address: this.location },
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
    const element = elementOf(found);
    return {
      value: valueOf(element),
      text: collapse(element.textContent),
      checked: checkedOf(element),
      selected: selectedOf(element),
      enabled: isEnabled(element),
    };
  }

  /**
   * Sets a text box, a number box, or a slider to `value`, as typing would: the native setter and
   * an `input` event. A slider without an input is moved by arrow keys. Never sends Enter.
   */
  set(found: Found, value: string | number): void {
    const element = this.changeable(found);
    const role = roleOf(element);
    if (role === 'slider') {
      const input = rangeInputOf(element);
      if (input === null) {
        stepSlider(element, Number(value));
        return;
      }
      setNativeValue(input)(String(value));
      announce(input, 'input', 'change');
      return;
    }
    if (element.localName === 'input' || element.localName === 'textarea') {
      setNativeValue(element as HTMLInputElement)(String(value));
      announce(element, 'input', 'change');
      return;
    }
    throw new PrimitiveError(`${found.target.description} to take a typed value`);
  }

  /**
   * Chooses the option named `option` in a select, or the one option, radio, tab, menu item, or
   * button of that name inside the found element, by clicking it.
   */
  choose(found: Found, option: string): void {
    const element = this.changeable(found);
    if (element.localName === 'select') {
      const select = element as HTMLSelectElement;
      const [match, ...others] = [...select.options].filter(
        (item) => collapse(item.text) === collapse(option),
      );
      if (match === undefined || others.length > 0) {
        throw new PrimitiveError(`${found.target.description} to offer "${option}"`);
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
          ? `${found.target.description} to offer "${option}"`
          : `${found.target.description} to offer "${option}" once (found ${String(choices.length)})`,
      );
    }
    this.press(choice, found.target.description, null);
  }

  /**
   * Clicks the element, with the pointer events a user's click sends first. The forbidden-control
   * matcher is asked first, on every call: a forbidden control or a named exception is refused.
   */
  click(found: Found): void {
    this.press(this.changeable(found), found.target.description, null);
  }

  /**
   * Clicks one of the controls of invariant 4's one permitted change, creating a workspace, and
   * nothing else. Only the workspace workflow (`adapter/workflows/workspace.ts`) may call it; the
   * invariant 4 guard's static scan fails on any other caller.
   */
  createWorkspaceClick(found: Found): void {
    const element = this.changeable(found);
    // A different exception, once there is one, is refused by `press` as not the one allowed.
    if (verdictOf(element).kind !== 'exception') {
      throw new PrimitiveError(`${found.target.description} to be Suno's create-workspace control`);
    }
    this.press(element, found.target.description, 'create-workspace');
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

  private changeable(found: Found): Element {
    if (this.refused !== null) {
      throw this.refused;
    }
    if (this.signal?.aborted === true) {
      throw new StoppedError();
    }
    const element = elementOf(found);
    if (!element.isConnected || isHidden(element)) {
      throw new PrimitiveError(`${found.target.description} to be still on the page`);
    }
    if (!isEnabled(element)) {
      throw new PrimitiveError(`${found.target.description} to be enabled`);
    }
    return element;
  }

  /**
   * The one place a press reaches the page. The matcher is asked before any event, with no way to
   * skip it: only an allowed control, or the named exception `allow`, is pressed.
   */
  private press(element: Element, description: string, allow: ExceptionName | null): void {
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
        description,
        verdict.kind === 'forbidden'
          ? verdict.reason
          : `it is the named exception '${verdict.exception}', pressed only by its own primitive`,
      );
      throw this.refused;
    }
    if (!isEnabled(element)) {
      throw new PrimitiveError(`${description} to be enabled`);
    }
    const view = viewOf(element);
    const init = { bubbles: true, cancelable: true, composed: true, button: 0 };
    element.dispatchEvent(new view.PointerEvent('pointerdown', init));
    element.dispatchEvent(new view.MouseEvent('mousedown', init));
    element.dispatchEvent(new view.PointerEvent('pointerup', init));
    element.dispatchEvent(new view.MouseEvent('mouseup', init));
    element.dispatchEvent(new view.MouseEvent('click', init));
  }
}
