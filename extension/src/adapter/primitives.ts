import { poll, POLL_MS, realClock, type Clock } from './clock.ts';

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
  const labels = (element as HTMLInputElement).labels as NodeListOf<HTMLLabelElement> | undefined;
  return labels === undefined ? '' : [...labels].map((label) => textOf(label, true)).join(' ');
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

export interface PageOptions {
  clock?: Clock;
  /** Once aborted, every primitive that changes the page refuses. */
  signal?: AbortSignal;
  /** The page's address; the document's own unless a test stands in for it. */
  address?: () => string;
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

  constructor(document: Document, options: PageOptions = {}) {
    this.document = document;
    this.clock = options.clock ?? realClock;
    this.signal = options.signal;
    this.location = options.address ?? (() => document.location.href);
  }

  /** The same page for one run: once `signal` is aborted, nothing more changes on the page. */
  withSignal(signal: AbortSignal): Page {
    return new Page(this.document, { clock: this.clock, signal, address: this.location });
  }

  /** The page's address. */
  address(): URL {
    return new URL(this.location());
  }

  /** Finds exactly one visible element; two matches are `ambiguous`, never the first of them. */
  find(target: Target): FindResult {
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
    this.press(choice, found.target.description);
  }

  /** Clicks the element, with the pointer events a user's click sends first. */
  click(found: Found): void {
    this.press(this.changeable(found), found.target.description);
  }

  /** Waits until `condition` holds, reading it every 100 ms; false when `timeoutMs` passed first. */
  async wait(condition: () => boolean, timeoutMs: number): Promise<boolean> {
    const result = await poll(() => ({ ok: condition() }), timeoutMs, this.clock, POLL_MS);
    return result.ok;
  }

  private changeable(found: Found): Element {
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

  private press(element: Element, description: string): void {
    if (this.signal?.aborted === true) {
      throw new StoppedError();
    }
    if (!isEnabled(element)) {
      throw new PrimitiveError(`${description} to be enabled`);
    }
    // The invariant 4 story (#133) checks the forbidden-control matcher here, before any event.
    const view = viewOf(element);
    const init = { bubbles: true, cancelable: true, composed: true, button: 0 };
    element.dispatchEvent(new view.PointerEvent('pointerdown', init));
    element.dispatchEvent(new view.MouseEvent('mousedown', init));
    element.dispatchEvent(new view.PointerEvent('pointerup', init));
    element.dispatchEvent(new view.MouseEvent('mouseup', init));
    element.dispatchEvent(new view.MouseEvent('click', init));
  }
}
