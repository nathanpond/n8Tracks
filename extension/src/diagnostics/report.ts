import type { StepFailure, Workflow } from '../adapter/workflow.ts';
import { parseVersion, type UpdateSide } from '../compatibility.ts';
import type { ConnectionState } from '../messages.ts';

/**
 * The diagnostic report (invariant 6): a file the user attaches to a bug report when a Suno
 * workflow stops working. It holds what a maintainer needs and nothing private, by construction:
 *
 * - The step log takes only workflow IDs and step names the adapter's registry declares, outcomes
 *   from a fixed list, numbers, and the adapter's own words for what a failed step expected, with
 *   every quoted value and anything shaped like an ID taken out. Anything else is dropped.
 * - The page structure keeps tag names, `role` from the ARIA list, `type` from the input types,
 *   a `data-testid` of plain words, and the names (never the values) of `aria-*` attributes.
 *   No text, no `class`, `id`, `href`, `name`, or any other attribute.
 * - Versions are kept only when they read as versions; the n8Tracks address only as its scheme.
 *
 * The log lives in `chrome.storage.session` (and the adapter's memory), is cleared on Disconnect,
 * and is never sent anywhere: the user saves the report, and nothing in the extension uploads it.
 */

/** The most steps the log keeps; the 201st evicts the oldest. */
export const STEP_LOG_LIMIT = 200;

/** The `chrome.storage.session` key of the log, the reported states, and the last capture. */
export const DIAGNOSTICS_KEY = 'diagnostics';

/** The page-structure capture's limits: depth below the anchor, and nodes in all. */
export const STRUCTURE_MAX_DEPTH = 6;
export const STRUCTURE_MAX_NODES = 300;

/** The most ancestors and siblings of the anchor a capture names (by tag only). */
export const STRUCTURE_MAX_ANCESTORS = 6;
export const STRUCTURE_MAX_SIBLINGS = 50;

/** What the user reads beside the Download control before saving the report. */
export const REPORT_STATEMENT =
  "The report holds the extension, Suno adapter, and n8Tracks versions and whether they work together; your browser's version; each Suno workflow's state; the last 200 workflow steps with their timings and what a failed step expected; and the outline of the Suno page around the last failure, as element names and roles with all text removed. It never holds your token, your n8Tracks address, Suno cookies, lyrics, prompts, styles, titles, IDs, or names. The extension never sends it anywhere: you choose where to attach it.";

export const DOWNLOAD_LABEL = 'Download diagnostic report';

/** `n8tracks-extension-diagnostics-2026-10-06.json`. */
export function reportFileName(generatedAt: Date): string {
  return `n8tracks-extension-diagnostics-${generatedAt.toISOString().slice(0, 10)}.json`;
}

// ---------------------------------------------------------------------------------------------
// Redaction rules
// ---------------------------------------------------------------------------------------------

const UUID = /[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}/i;
const UUID_GLOBAL = new RegExp(UUID.source, 'gi');
/** A run of eight or more hex digits with a digit in it, or six or more digits: an ID or a key. */
const HEX_RUN = /\b(?=[0-9a-f]*\d)[0-9a-f]{8,}\b/gi;
const DIGIT_RUN = /\d{6,}/g;
const ADDRESS = /\b[a-z][a-z0-9+.-]*:\/\/\S*/gi;
const EMAIL_OR_HANDLE = /\S*@\S+/g;
/**
 * A word no description is made of: one with an underscore (`n8t_…`, `cst_…`), one longer than
 * 24 characters, or one of eight or more that mixes letters and digits.
 */
const SECRET_SHAPED =
  /(?<![^\s(])(?:[^\s"]*_[^\s"]*|[^\s"]{25,}|(?=[^\s"]*\d)(?=[^\s"]*[a-z])[^\s"]{8,})/gi;

/** Whether the text holds something shaped like an ID (a UUID, a long hex or digit run). */
export function isIdShaped(text: string): boolean {
  return UUID.test(text) || new RegExp(HEX_RUN.source, 'i').test(text) || /\d{6,}/.test(text);
}

/** The longest an `expected` text in the log may be. */
const EXPECTED_MAX = 300;

/**
 * The adapter's words for what a failed step expected, made safe for the report: a quoted value
 * (an option the run was asked to choose, which may be the user's) becomes `"…"`, and addresses,
 * handles, and ID-shaped runs are taken out. The descriptions themselves are the adapter's own
 * constants ("a text box labelled Styles").
 */
export function redactExpected(text: string): string {
  const redacted = text
    .replace(/"[^"]*"?/g, '"…"')
    .replace(/[“”«»][^“”«»]*[“”«»]?/g, '"…"')
    .replace(ADDRESS, '[address]')
    .replace(EMAIL_OR_HANDLE, '[name]')
    .replace(UUID_GLOBAL, '[id]')
    .replace(HEX_RUN, '[id]')
    .replace(DIGIT_RUN, '[number]')
    .replace(SECRET_SHAPED, '[value]')
    .replace(/[^\p{L}\p{N} ,.;:'()[\]…"-]/gu, ' ')
    .replace(/\s+/g, ' ')
    .trim();
  return redacted.length > EXPECTED_MAX ? `${redacted.slice(0, EXPECTED_MAX - 1)}…` : redacted;
}

/** The ARIA roles a captured element's `role` may report; any other value is dropped. */
const ARIA_ROLES: ReadonlySet<string> = new Set([
  'alert',
  'alertdialog',
  'application',
  'article',
  'banner',
  'button',
  'cell',
  'checkbox',
  'columnheader',
  'combobox',
  'complementary',
  'contentinfo',
  'definition',
  'dialog',
  'directory',
  'document',
  'feed',
  'figure',
  'form',
  'grid',
  'gridcell',
  'group',
  'heading',
  'img',
  'link',
  'list',
  'listbox',
  'listitem',
  'log',
  'main',
  'marquee',
  'math',
  'menu',
  'menubar',
  'menuitem',
  'menuitemcheckbox',
  'menuitemradio',
  'meter',
  'navigation',
  'none',
  'note',
  'option',
  'presentation',
  'progressbar',
  'radio',
  'radiogroup',
  'region',
  'row',
  'rowgroup',
  'rowheader',
  'scrollbar',
  'search',
  'searchbox',
  'separator',
  'slider',
  'spinbutton',
  'status',
  'switch',
  'tab',
  'table',
  'tablist',
  'tabpanel',
  'term',
  'textbox',
  'timer',
  'toolbar',
  'tooltip',
  'tree',
  'treegrid',
  'treeitem',
]);

/** The `type` values a captured element may report: the input and button types. */
const ELEMENT_TYPES: ReadonlySet<string> = new Set([
  'button',
  'checkbox',
  'color',
  'date',
  'datetime-local',
  'email',
  'file',
  'hidden',
  'image',
  'month',
  'number',
  'password',
  'radio',
  'range',
  'reset',
  'search',
  'submit',
  'tel',
  'text',
  'time',
  'url',
  'week',
]);

const TAG = /^[a-z][a-z0-9-]{0,39}$/;
const TEST_ID = /^[a-z0-9_-]{1,40}$/;
const ARIA_NAME = /^aria-[a-z]{2,24}$/;

/** One element of a captured page region: its tag and the attributes kept. Never any text. */
export interface StructureNode {
  tag: string;
  role?: string;
  type?: string;
  testId?: string;
  /** The names of its `aria-*` attributes, never their values. */
  aria?: string[];
  /** The element the capture is anchored on. */
  anchor?: true;
  children?: StructureNode[];
}

/** Where a capture is anchored: the failing element, else its container, else the main region. */
export type StructureAnchor = 'failing-element' | 'container' | 'main-region' | 'page';

/** The page region around a failure, as the report holds it. */
export interface PageStructure {
  anchoredOn: StructureAnchor;
  /** Tag names of the anchor's ancestors, nearest first. */
  ancestors: string[];
  /** Tag names of the anchor's siblings, in page order. */
  siblings: string[];
  /** The anchor and what it holds, breadth-first to six levels. */
  root: StructureNode;
  nodeCount: number;
  /** True when the depth or node limit left part of the region out. */
  truncated: boolean;
}

/** An element's attributes, as a capture may keep them: the rules of {@link StructureNode}. */
export interface ElementFacts {
  tag: string;
  role: string | null;
  type: string | null;
  testId: string | null;
  attributeNames: readonly string[];
}

/** The node a capture keeps for an element: only what the rules allow, so never any text. */
export function structureNodeOf(facts: ElementFacts): StructureNode {
  const tag = facts.tag.toLowerCase();
  const node: StructureNode = { tag: TAG.test(tag) ? tag : 'element' };
  const role = facts.role?.trim().toLowerCase() ?? '';
  if (ARIA_ROLES.has(role)) {
    node.role = role;
  }
  const type = facts.type?.trim().toLowerCase() ?? '';
  if (ELEMENT_TYPES.has(type)) {
    node.type = type;
  }
  const testId = facts.testId ?? '';
  if (TEST_ID.test(testId) && !isIdShaped(testId)) {
    node.testId = testId;
  }
  const aria = [...new Set(facts.attributeNames.map((name) => name.toLowerCase()))]
    .filter((name) => ARIA_NAME.test(name))
    .sort();
  if (aria.length > 0) {
    node.aria = aria;
  }
  return node;
}

// ---------------------------------------------------------------------------------------------
// Validation of what the Suno content script reports (it is page context: nothing is trusted)
// ---------------------------------------------------------------------------------------------

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function safeTag(value: unknown): string | null {
  return typeof value === 'string' && TAG.test(value) ? value : null;
}

/** A reported node, rebuilt through the same rules, within the limits; null when not a node. */
function validNode(value: unknown, depth: number, budget: { left: number }): StructureNode | null {
  if (!isRecord(value) || budget.left <= 0) {
    return null;
  }
  budget.left -= 1;
  const node = structureNodeOf({
    tag: typeof value.tag === 'string' ? value.tag : 'element',
    role: typeof value.role === 'string' ? value.role : null,
    type: typeof value.type === 'string' ? value.type : null,
    testId: typeof value.testId === 'string' ? value.testId : null,
    attributeNames: Array.isArray(value.aria)
      ? value.aria.filter((name): name is string => typeof name === 'string')
      : [],
  });
  if (value.anchor === true) {
    node.anchor = true;
  }
  if (Array.isArray(value.children) && depth < STRUCTURE_MAX_DEPTH) {
    const children = value.children
      .map((child) => validNode(child, depth + 1, budget))
      .filter((child): child is StructureNode => child !== null);
    if (children.length > 0) {
      node.children = children;
    }
  }
  return node;
}

const ANCHORS: readonly StructureAnchor[] = ['failing-element', 'container', 'main-region', 'page'];

/** A reported capture, rebuilt from its parts by the rules; null when it is not one. */
export function validStructure(value: unknown): PageStructure | null {
  if (!isRecord(value)) {
    return null;
  }
  const anchoredOn = ANCHORS.find((anchor) => anchor === value.anchoredOn);
  const budget = { left: STRUCTURE_MAX_NODES };
  const tags = (list: unknown, limit: number) =>
    (Array.isArray(list) ? list : [])
      .slice(0, limit)
      .map(safeTag)
      .filter((tag): tag is string => tag !== null);
  const ancestors = tags(value.ancestors, STRUCTURE_MAX_ANCESTORS);
  const siblings = tags(value.siblings, STRUCTURE_MAX_SIBLINGS);
  budget.left -= siblings.length;
  const root = validNode(value.root, 0, budget);
  if (anchoredOn === undefined || root === null) {
    return null;
  }
  return {
    anchoredOn,
    ancestors,
    siblings,
    root,
    nodeCount: STRUCTURE_MAX_NODES - budget.left,
    truncated: value.truncated === true || budget.left <= 0,
  };
}

/** How one step of a run ended. */
export type StepOutcome = 'ok' | 'failed' | 'timed_out' | 'error' | 'refused';

/** One line of the report's step log. Only declared names, fixed outcomes, and numbers. */
export interface DiagnosticStep {
  workflow: string;
  step: string;
  phase: StepFailure['phase'];
  outcome: StepOutcome;
  /** Milliseconds this check or action took. */
  ms: number;
  /** For the failed step: what was expected, in the adapter's words, redacted. */
  expected?: string;
}

const OUTCOME_OF_KIND: Record<StepFailure['kind'], StepOutcome> = {
  check: 'failed',
  timeout: 'timed_out',
  error: 'error',
  refused: 'refused',
};

const PHASES: readonly StepFailure['phase'][] = ['expect', 'act', 'verify'];

/** The names a workflow declares: its steps and the steps its needs are named after. */
function declaredSteps(workflow: Workflow): ReadonlySet<string> {
  return new Set([
    ...workflow.steps.map((step) => step.name),
    ...workflow.needs.map((need) => need.step),
  ]);
}

function milliseconds(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? Math.round(value) : 0;
}

/**
 * A run the Suno content script reports, as log lines: only a registered workflow, only the
 * steps it declares, and the failure's `expected` redacted. An unknown workflow gives nothing.
 */
export function stepsOfRun(value: unknown, workflows: readonly Workflow[]): DiagnosticStep[] {
  if (!isRecord(value) || !Array.isArray(value.log)) {
    return [];
  }
  const workflow = workflows.find((candidate) => candidate.id === value.workflowId);
  if (workflow === undefined) {
    return [];
  }
  const steps = declaredSteps(workflow);
  const failure = isRecord(value.failure) ? value.failure : null;
  const lines: DiagnosticStep[] = [];
  let previous = 0;
  for (const entry of value.log as unknown[]) {
    if (!isRecord(entry) || typeof entry.step !== 'string' || !steps.has(entry.step)) {
      continue;
    }
    const phase = PHASES.find((candidate) => candidate === entry.phase);
    if (phase === undefined) {
      continue;
    }
    const at = milliseconds(entry.atMs);
    const line: DiagnosticStep = {
      workflow: workflow.id,
      step: entry.step,
      phase,
      outcome: 'ok',
      ms: Math.max(0, at - previous),
    };
    previous = at;
    if (entry.outcome === 'failed') {
      const kind = failure?.kind;
      line.outcome =
        typeof kind === 'string' && kind in OUTCOME_OF_KIND
          ? OUTCOME_OF_KIND[kind as StepFailure['kind']]
          : 'failed';
      if (failure !== null && typeof failure.expected === 'string') {
        line.expected = redactExpected(failure.expected);
      }
    }
    lines.push(line);
  }
  return lines;
}

/** A workflow's state as the report says it. */
export type ReportedState = 'ready' | 'not_working' | 'not_checked' | 'waiting';

export interface ReportedStatus {
  id: string;
  state: ReportedState;
  /** The declared step that failed, when not working. */
  step: string | null;
  /** True when the state comes from a run that stopped, not from the self-check. */
  stopped: boolean;
}

/** The adapter's states (`not-working`), and the report's own when read back from storage. */
const STATE_OF: Record<string, ReportedState> = {
  ready: 'ready',
  'not-working': 'not_working',
  'not-checked': 'not_checked',
  waiting: 'waiting',
  not_working: 'not_working',
  not_checked: 'not_checked',
};

/** The self-check states the content script reports, kept only for registered workflows. */
export function statusesOf(value: unknown, workflows: readonly Workflow[]): ReportedStatus[] {
  if (!Array.isArray(value)) {
    return [];
  }
  const statuses: ReportedStatus[] = [];
  for (const item of value as unknown[]) {
    if (!isRecord(item) || typeof item.state !== 'string') {
      continue;
    }
    const workflow = workflows.find((candidate) => candidate.id === item.id);
    const state = STATE_OF[item.state];
    if (workflow === undefined || state === undefined) {
      continue;
    }
    const step =
      typeof item.step === 'string' && declaredSteps(workflow).has(item.step) ? item.step : null;
    statuses.push({ id: workflow.id, state, step, stopped: item.stopped === true });
  }
  return statuses;
}

const OUTCOMES: readonly StepOutcome[] = ['ok', 'failed', 'timed_out', 'error', 'refused'];

/** A log line read back from storage, through the same rules as when it was written. */
function validStoredStep(value: unknown, workflows: readonly Workflow[]): DiagnosticStep | null {
  if (!isRecord(value) || typeof value.step !== 'string') {
    return null;
  }
  const workflow = workflows.find((candidate) => candidate.id === value.workflow);
  const phase = PHASES.find((candidate) => candidate === value.phase);
  const outcome = OUTCOMES.find((candidate) => candidate === value.outcome);
  if (
    workflow === undefined ||
    phase === undefined ||
    outcome === undefined ||
    !declaredSteps(workflow).has(value.step)
  ) {
    return null;
  }
  const step: DiagnosticStep = {
    workflow: workflow.id,
    step: value.step,
    phase,
    outcome,
    ms: milliseconds(value.ms),
  };
  if (typeof value.expected === 'string') {
    step.expected = redactExpected(value.expected);
  }
  return step;
}

// ---------------------------------------------------------------------------------------------
// The log in extension storage
// ---------------------------------------------------------------------------------------------

/** What `chrome.storage.session` holds under {@link DIAGNOSTICS_KEY}. */
export interface StoredDiagnostics {
  steps: DiagnosticStep[];
  statuses: ReportedStatus[];
  structure: PageStructure | null;
}

/** The part of `chrome.storage` the log uses. */
export interface DiagnosticsStorage {
  get(keys: string[]): Promise<Record<string, unknown>>;
  set(items: Record<string, unknown>): Promise<void>;
  remove(keys: string[]): Promise<void>;
}

/** The versions the report names; the application's comes from the connection. */
export interface ReportVersions {
  extension: string;
  adapter: string;
}

export interface DiagnosticsOptions {
  /** `chrome.storage.session`: kept while the browser runs, never synced, never sent. */
  storage: DiagnosticsStorage;
  /** The adapter's registry: the workflows and steps the log accepts. */
  workflows: readonly Workflow[];
  versions: ReportVersions;
  /** The connection's state, for the application version and compatibility. */
  connectionState: () => Promise<ConnectionState>;
  /** "Chrome 140", or "unknown". */
  browser: () => string;
  now?: () => Date;
}

/** What the Suno content script reports: a run that ended, the self-check states, or both. */
export interface DiagnosticsRecord {
  run?: unknown;
  statuses?: unknown;
}

function emptyLog(): StoredDiagnostics {
  return { steps: [], statuses: [], structure: null };
}

/**
 * The step log and the report, in the service worker. Writes are queued one after another, so
 * two reports at once never lose a line.
 */
export class Diagnostics {
  private readonly options: DiagnosticsOptions;
  private queue: Promise<unknown> = Promise.resolve();

  constructor(options: DiagnosticsOptions) {
    this.options = options;
  }

  /** Adds a run's steps (the oldest go past 200) and its capture, and replaces the states. */
  async record(record: DiagnosticsRecord): Promise<void> {
    await this.update((stored) => {
      const { workflows } = this.options;
      const steps = [...stored.steps, ...stepsOfRun(record.run, workflows)].slice(-STEP_LOG_LIMIT);
      const statuses =
        record.statuses === undefined ? stored.statuses : statusesOf(record.statuses, workflows);
      const failed = isRecord(record.run) && isRecord(record.run.failure);
      const structure =
        failed && isRecord(record.run)
          ? (validStructure(record.run.structure) ?? stored.structure)
          : stored.structure;
      return { steps, statuses, structure };
    });
  }

  /** What is kept now. */
  async read(): Promise<StoredDiagnostics> {
    await this.queue.catch(() => undefined);
    return this.load();
  }

  /** Forgets the log, the states, and the last capture (Disconnect). */
  async clear(): Promise<void> {
    const next = this.queue
      .catch(() => undefined)
      .then(() => this.options.storage.remove([DIAGNOSTICS_KEY]));
    this.queue = next;
    await next;
  }

  /** The report, assembled now. */
  async report(): Promise<DiagnosticReport> {
    const stored = await this.read();
    let connection: ConnectionState = { status: 'not-paired' };
    try {
      connection = await this.options.connectionState();
    } catch {
      // The report is still useful without the connection.
    }
    return assembleReport({
      stored,
      workflows: this.options.workflows,
      versions: this.options.versions,
      connection,
      browser: this.options.browser(),
      generatedAt: (this.options.now ?? (() => new Date()))(),
    });
  }

  private async load(): Promise<StoredDiagnostics> {
    const value = (await this.options.storage.get([DIAGNOSTICS_KEY]))[DIAGNOSTICS_KEY];
    if (!isRecord(value)) {
      return emptyLog();
    }
    // Stored by this module, but read back through the same rules all the same.
    const { workflows } = this.options;
    const steps = (Array.isArray(value.steps) ? (value.steps as unknown[]) : [])
      .map((step) => validStoredStep(step, workflows))
      .filter((step): step is DiagnosticStep => step !== null);
    return {
      steps: steps.slice(-STEP_LOG_LIMIT),
      statuses: statusesOf(value.statuses, workflows),
      structure: validStructure(value.structure),
    };
  }

  private async update(change: (stored: StoredDiagnostics) => StoredDiagnostics): Promise<void> {
    const next = this.queue
      .catch(() => undefined)
      .then(async () => {
        const stored = await this.load();
        await this.options.storage.set({ [DIAGNOSTICS_KEY]: change(stored) });
      });
    this.queue = next;
    await next;
  }
}

// ---------------------------------------------------------------------------------------------
// The report
// ---------------------------------------------------------------------------------------------

export interface DiagnosticReport {
  reportVersion: 1;
  generatedAt: string;
  versions: {
    extension: string | null;
    adapter: string | null;
    /** n8Tracks's version from the last handshake; null when not connected. */
    application: string | null;
    /** null when not connected, so nothing could be compared. */
    compatible: boolean | null;
    /** Which side to update when they do not fit. */
    update: UpdateSide | null;
  };
  connection: {
    status: ConnectionState['status'];
    /** The n8Tracks address's scheme only: `https`, `http`, or null. */
    scheme: 'https' | 'http' | null;
  };
  browser: string;
  workflows: {
    id: string;
    title: string;
    feature: Workflow['feature'];
    state: ReportedState;
    step: string | null;
    stopped: boolean;
  }[];
  steps: DiagnosticStep[];
  /** The page region around the most recent failure; null when none is recorded. */
  pageStructure: PageStructure | null;
}

/** A version as the report may hold it: one that reads as a version, else null. */
function safeVersion(text: string | null | undefined): string | null {
  const trimmed = (text ?? '').trim();
  return parseVersion(trimmed) === null || trimmed.length > 40 ? null : trimmed;
}

function schemeOf(state: ConnectionState): 'https' | 'http' | null {
  if (state.status === 'not-paired') {
    return null;
  }
  try {
    const protocol = new URL(state.address).protocol;
    return protocol === 'https:' ? 'https' : protocol === 'http:' ? 'http' : null;
  } catch {
    return null;
  }
}

export interface ReportInput {
  stored: StoredDiagnostics;
  workflows: readonly Workflow[];
  versions: ReportVersions;
  connection: ConnectionState;
  browser: string;
  generatedAt: Date;
}

/** Puts the report together. Every field is either fixed by the extension or redacted above. */
export function assembleReport(input: ReportInput): DiagnosticReport {
  const { connection, stored } = input;
  const connected = connection.status === 'connected' ? connection : null;
  const reported = new Map(stored.statuses.map((status) => [status.id, status]));
  const adapter = /^\d{1,6}$/.test(input.versions.adapter) ? input.versions.adapter : null;
  return {
    reportVersion: 1,
    generatedAt: input.generatedAt.toISOString(),
    versions: {
      extension: safeVersion(input.versions.extension),
      adapter,
      application: connected === null ? null : safeVersion(connected.applicationVersion),
      compatible: connected === null ? null : connected.compatibility.kind === 'compatible',
      update:
        connected === null || connected.compatibility.kind === 'compatible'
          ? null
          : connected.compatibility.update,
    },
    connection: { status: connection.status, scheme: schemeOf(connection) },
    browser: /^[A-Za-z][A-Za-z ]{0,29} \d{1,4}$/.test(input.browser) ? input.browser : 'unknown',
    workflows: input.workflows.map((workflow) => {
      const status = reported.get(workflow.id);
      return {
        id: workflow.id,
        title: workflow.title,
        feature: workflow.feature,
        state: status?.state ?? 'not_checked',
        step: status?.step ?? null,
        stopped: status?.stopped ?? false,
      };
    }),
    steps: stored.steps,
    pageStructure: stored.structure,
  };
}

/** The browser's user-agent data, where the browser has it. */
export interface UserAgentData {
  brands?: readonly { brand: string; version: string }[];
}

/**
 * The browser and its major version, from `navigator.userAgentData`: "Google Chrome 140", or
 * "unknown". The made-up "Not A Brand" entries are skipped, and a named brand wins over Chromium.
 */
export function browserVersion(data: UserAgentData | undefined): string {
  const brands = (data?.brands ?? []).filter(
    (entry) =>
      /^[A-Za-z][A-Za-z ]{0,29}$/.test(entry.brand) &&
      !/not.*a.*brand/i.test(entry.brand) &&
      /^\d{1,4}/.test(entry.version),
  );
  const chosen = brands.find((entry) => entry.brand !== 'Chromium') ?? brands[0];
  if (chosen === undefined) {
    return 'unknown';
  }
  return `${chosen.brand} ${/^\d{1,4}/.exec(chosen.version)?.[0] ?? ''}`;
}
