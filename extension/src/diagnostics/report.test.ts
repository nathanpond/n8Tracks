// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { ANY_SUNO_PAGE } from '../adapter/addresses.ts';
import { Page, type Target } from '../adapter/primitives.ts';
import { AdapterSession, WorkflowRegistry, type RecordedRun } from '../adapter/registry.ts';
import { ADAPTER_WORKFLOWS } from '../adapter/workflows/index.ts';
import { expected, OK, present, type StepContext, type Workflow } from '../adapter/workflow.ts';
import { Connection } from '../background/connection.ts';
import { route } from '../background/router.ts';
import type { ConnectedState, ConnectionState } from '../messages.ts';
import { fakeBrowser } from '../testing/fakeBrowser.ts';
import { fakeClock, snapshotHtml } from '../testing/snapshots.ts';
import {
  browserVersion,
  Diagnostics,
  DIAGNOSTICS_KEY,
  isIdShaped,
  redactExpected,
  reportFileName,
  STEP_LOG_LIMIT,
  stepsOfRun,
  structureNodeOf,
  validStructure,
  type DiagnosticReport,
  type StructureNode,
} from './report.ts';

// Sentinels: none of these may appear anywhere in a report (invariant 6).
const TOKEN = 'n8t_SentinelToken0123456789ABCDEFGHIJKLMNOPQRSTUV';
const LYRIC = 'Sentinel lyric line about the silver harbour';
const STYLES = 'sentinelstyle shoegaze dreampop';
const PROMPT = 'Sentinel prompt asking for a lullaby';
const TITLE = 'Sentinel Song Title';
const CLIP_ID = '5e1f0c2a-9b7d-4c3e-8a6f-0d1e2f3a4b5c';
const WORKSPACE_ID = '7a8b9c0d-1e2f-4a3b-9c4d-5e6f7a8b9c0d';
const HANDLE = 'sentinelhandle';
const CREATE_SESSION_TOKEN = 'cst_SentinelCreateSession9f8e7d6c5b4a';
const USER_TIER = 'sentineltier-premier';
const ADDRESS_HOST = 'sentinel-host.example.net';
const CREDENTIAL_NAME = 'Sentinel credential of the user';

const SENTINELS = [
  TOKEN,
  LYRIC,
  STYLES,
  PROMPT,
  TITLE,
  CLIP_ID,
  WORKSPACE_ID,
  HANDLE,
  CREATE_SESSION_TOKEN,
  USER_TIER,
  ADDRESS_HOST,
  CREDENTIAL_NAME,
  // Parts that could survive on their own.
  'silver harbour',
  'shoegaze',
  'lullaby',
  'Sentinel',
  'sentinel',
  '5e1f0c2a',
  '7a8b9c0d',
];

const UUID_SHAPED = /[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}/i;

/** Fails on any sentinel, and on any UUID-shaped value, anywhere in the report. */
function expectNothingPrivate(report: unknown): void {
  const json = JSON.stringify(report);
  for (const sentinel of SENTINELS) {
    expect(json.toLowerCase(), `the report holds "${sentinel}"`).not.toContain(
      sentinel.toLowerCase(),
    );
  }
  expect(json).not.toMatch(UUID_SHAPED);
}

/** A Suno page full of private values: text, attributes, form values, and a raw payload. */
const PRIVATE_PAGE = `
<main>
  <section data-testid="create-form" aria-label="${TITLE}" class="${HANDLE}" id="clip-${CLIP_ID}">
    <h2 title="${TITLE}">${TITLE}</h2>
    <a href="https://suno.com/song/${CLIP_ID}" data-testid="${CLIP_ID}">${TITLE}</a>
    <div data-testid="workspace-${WORKSPACE_ID}" data-workspace="${WORKSPACE_ID}">${WORKSPACE_ID}</div>
    <label for="lyrics">Lyrics</label>
    <textarea id="lyrics" name="lyrics" aria-describedby="${HANDLE}">${LYRIC}</textarea>
    <input type="text" aria-label="Styles" value="${STYLES}" placeholder="${PROMPT}" />
    <span role="${HANDLE}" data-user="${HANDLE}">@${HANDLE}</span>
    <img alt="${TITLE}" src="https://cdn2.suno.ai/image_${CLIP_ID}.jpeg" />
    <script type="application/json">{"token":"${TOKEN}","create_session_token":"${CREATE_SESSION_TOKEN}","user_tier":"${USER_TIER}","prompt":"${PROMPT}"}</script>
    <div role="listbox" aria-label="Workspace">
      <div role="option">${HANDLE}'s workspace</div>
    </div>
  </section>
</main>`;

const CONNECTED: ConnectedState = {
  status: 'connected',
  address: `https://${ADDRESS_HOST}:8443/n8?token=${TOKEN}`,
  credentialName: CREDENTIAL_NAME,
  scopes: ['suno.sync', 'suno.generate'],
  applicationVersion: '0.1.4',
  compatibility: { kind: 'compatible' },
  features: [],
};

interface FillValues {
  lyrics: string;
  styles: string;
  title: string;
  workspace: string;
}

const LYRICS_BOX: Target = { role: 'textbox', name: 'Lyrics', description: 'the Lyrics box' };
const STYLES_BOX: Target = { role: 'textbox', name: 'Styles', description: 'the Styles box' };
const WORKSPACES: Target = {
  role: 'listbox',
  name: 'Workspace',
  within: { testId: 'create-form', description: 'the Create form' },
  description: 'the workspace list',
};
const MISSING_SUBMIT: Target = {
  role: 'button',
  name: 'Not on this page',
  within: { testId: 'create-form', description: 'the Create form' },
  description: 'the button that is not there',
};

/**
 * A test-only workflow that handles every private value: it types the lyrics and styles, then
 * chooses a workspace named by the user, which is not offered, so the run stops with the
 * workspace's name in the primitive's own words.
 */
const fill: Workflow<StepContext & FillValues> = {
  id: 'fill-test',
  title: 'Fill the form (test only)',
  feature: 'generate',
  startsOn: ANY_SUNO_PAGE,
  needs: [{ step: 'lyrics', check: (page) => present(page, LYRICS_BOX) }],
  steps: [
    {
      name: 'lyrics',
      expect: ({ page }) => present(page, LYRICS_BOX),
      act: ({ page, lyrics }) => {
        const found = page.find(LYRICS_BOX);
        if (found.kind === 'found') {
          page.set(found.found, lyrics);
        }
      },
      verify: () => OK,
    },
    {
      name: 'styles',
      expect: ({ page }) => present(page, STYLES_BOX),
      act: ({ page, styles }) => {
        const found = page.find(STYLES_BOX);
        if (found.kind === 'found') {
          page.set(found.found, styles);
        }
      },
      // A careless workflow quoting the user's value in its expectation.
      verify: ({ page, styles }) => {
        const found = page.find(STYLES_BOX);
        return found.kind === 'found' && page.read(found.found).value === styles
          ? OK
          : expected(`the Styles box to hold "${styles}"`);
      },
    },
    {
      name: 'workspace',
      expect: ({ page }) => present(page, WORKSPACES),
      act: ({ page, workspace }) => {
        const found = page.find(WORKSPACES);
        if (found.kind === 'found') {
          page.choose(found.found, workspace);
        }
      },
      verify: () => OK,
    },
  ],
  fixtures: ['create-songs-simple'],
};

const missing: Workflow = {
  id: 'missing-test',
  title: 'Look for a missing button (test only)',
  feature: 'page',
  startsOn: ANY_SUNO_PAGE,
  needs: [{ step: 'button', check: (page) => present(page, MISSING_SUBMIT) }],
  steps: [
    {
      name: 'button',
      expect: ({ page }) => present(page, MISSING_SUBMIT),
      act: () => undefined,
      verify: () => OK,
      timeoutMs: 300,
    },
  ],
  fixtures: ['create-songs-simple'],
};

const NOT_ON_SUNO: Target = {
  role: 'button',
  name: 'Not on Suno',
  description: 'a button Suno does not have',
};

const missingOnSuno: Workflow = {
  id: 'missing-on-suno-test',
  title: 'Look for a button Suno lacks (test only)',
  feature: 'page',
  startsOn: ANY_SUNO_PAGE,
  needs: [{ step: 'button', check: (page) => present(page, NOT_ON_SUNO) }],
  steps: [
    {
      name: 'button',
      expect: ({ page }) => present(page, NOT_ON_SUNO),
      act: () => undefined,
      verify: () => OK,
      timeoutMs: 300,
    },
  ],
  fixtures: ['library-list'],
};

const WORKFLOWS: readonly Workflow[] = [...ADAPTER_WORKFLOWS, fill, missing, missingOnSuno];

function diagnostics(options: { connection?: ConnectionState; browser?: string } = {}) {
  const fake = fakeBrowser();
  const log = new Diagnostics({
    storage: fake.browser.storage,
    workflows: WORKFLOWS,
    versions: { extension: '0.1.0-rc.1', adapter: '1' },
    connectionState: () => Promise.resolve(options.connection ?? CONNECTED),
    browser: () => options.browser ?? 'Google Chrome 140',
    now: () => new Date('2026-10-06T12:30:00Z'),
  });
  return { ...fake, log };
}

/** Runs `workflow` on `html` through an adapter session that reports to the log. */
async function recordRun<C extends StepContext>(
  log: Diagnostics,
  html: string,
  workflow: Workflow<C>,
  values: Omit<C, keyof StepContext>,
): Promise<RecordedRun> {
  document.body.innerHTML = html;
  const recorded: RecordedRun[] = [];
  const session = new AdapterSession(
    new WorkflowRegistry(WORKFLOWS),
    new Page(document, { address: () => 'https://suno.com/create' }),
    (run) => {
      recorded.push(run);
    },
  );
  await session.run(workflow, values, { clock: fakeClock() });
  const [run] = recorded;
  if (run === undefined) {
    throw new Error('The run was not reported.');
  }
  // As the content script sends it: a structured clone through the message channel.
  await log.record({ run: structuredClone(run) });
  return run;
}

function nodes(root: StructureNode): StructureNode[] {
  return [root, ...(root.children ?? []).flatMap(nodes)];
}

afterEach(() => {
  document.body.innerHTML = '';
});

describe('the diagnostic report', () => {
  it('is assembled from a recorded run: versions, browser, states, steps, and the failure', async () => {
    const { log } = diagnostics();
    await recordRun(log, PRIVATE_PAGE, fill, {
      lyrics: LYRIC,
      styles: STYLES,
      title: TITLE,
      workspace: `${HANDLE}'s other workspace ${WORKSPACE_ID}`,
    });
    await log.record({
      statuses: [
        { id: 'recognise-suno', state: 'ready', step: null, stopped: false },
        { id: 'fill-test', state: 'not-working', step: 'workspace', stopped: true },
        // #328: waiting for an earlier step is a state of its own.
        { id: 'fill-songs-simple', state: 'waiting', step: 'Songs form', stopped: false },
      ],
    });

    const report = await log.report();

    expect(report.reportVersion).toBe(1);
    expect(report.generatedAt).toBe('2026-10-06T12:30:00.000Z');
    expect(report.versions).toEqual({
      extension: '0.1.0-rc.1',
      adapter: '1',
      application: '0.1.4',
      compatible: true,
      update: null,
    });
    expect(report.connection).toEqual({ status: 'connected', scheme: 'https' });
    expect(report.browser).toBe('Google Chrome 140');
    expect(report.workflows.map(({ id, state, step }) => ({ id, state, step }))).toEqual([
      { id: 'recognise-suno', state: 'ready', step: null },
      { id: 'load-more', state: 'not_checked', step: null },
      { id: 'open-workspaces', state: 'not_checked', step: null },
      { id: 'more-workspaces', state: 'not_checked', step: null },
      { id: 'select-workspace', state: 'not_checked', step: null },
      { id: 'create-workspace', state: 'not_checked', step: null },
      { id: 'switch-form', state: 'not_checked', step: null },
      { id: 'fill-songs-simple', state: 'waiting', step: 'Songs form' },
      { id: 'fill-songs-advanced', state: 'not_checked', step: null },
      { id: 'check-songs-form', state: 'not_checked', step: null },
      { id: 'switch-speech-form', state: 'not_checked', step: null },
      { id: 'fill-speech-simple', state: 'not_checked', step: null },
      { id: 'fill-speech-advanced', state: 'not_checked', step: null },
      { id: 'check-speech-form', state: 'not_checked', step: null },
      { id: 'switch-sounds-form', state: 'not_checked', step: null },
      { id: 'fill-sounds', state: 'not_checked', step: null },
      { id: 'check-sounds-form', state: 'not_checked', step: null },
      { id: 'open-source-menu', state: 'not_checked', step: null },
      { id: 'choose-source-action', state: 'not_checked', step: null },
      { id: 'answer-overwrite', state: 'not_checked', step: null },
      { id: 'verify-source-advanced', state: 'not_checked', step: null },
      { id: 'verify-source-simple', state: 'not_checked', step: null },
      { id: 'refresh-library', state: 'not_checked', step: null },
      { id: 'fill-test', state: 'not_working', step: 'workspace' },
      { id: 'missing-test', state: 'not_checked', step: null },
      { id: 'missing-on-suno-test', state: 'not_checked', step: null },
    ]);
    expect(report.steps.map(({ step, phase, outcome }) => `${step}/${phase}/${outcome}`)).toEqual([
      'lyrics/expect/ok',
      'lyrics/act/ok',
      'lyrics/verify/ok',
      'styles/expect/ok',
      'styles/act/ok',
      'styles/verify/ok',
      'workspace/expect/ok',
      'workspace/act/error',
    ]);
    const failed = report.steps.at(-1);
    expect(failed).toMatchObject({ workflow: 'fill-test', step: 'workspace', phase: 'act' });
    // The option asked for (the private workspace name) never enters the text (#343, #344).
    expect(failed?.expected).toBe('the workspace list to offer the option asked for');
    for (const step of report.steps) {
      expect(Number.isInteger(step.ms) && step.ms >= 0).toBe(true);
    }
    expect(report.pageStructure?.anchoredOn).toBe('failing-element');
    expect(report.pageStructure?.root).toMatchObject({
      tag: 'div',
      role: 'listbox',
      aria: ['aria-label'],
      anchor: true,
    });
    expectNothingPrivate(report);
  });

  it('holds none of the sentinels, whatever a run, a page, or a careless workflow puts in its way', async () => {
    const { log } = diagnostics();
    // The styles never stick (the page resets them), so the careless verify quotes the value.
    const resetting = PRIVATE_PAGE.replace('<main>', '<main data-reset="1">');
    document.body.innerHTML = resetting;
    const styles = document.querySelector<HTMLInputElement>('input[aria-label="Styles"]');
    styles?.addEventListener('input', () => {
      styles.value = '';
    });
    const session = new AdapterSession(
      new WorkflowRegistry(WORKFLOWS),
      new Page(document, { address: () => `https://suno.com/create?clip=${CLIP_ID}` }),
      (run) => {
        void log.record({ run: structuredClone(run) });
      },
    );
    await session.run(
      fill,
      { lyrics: LYRIC, styles: STYLES, title: TITLE, workspace: WORKSPACE_ID },
      { clock: fakeClock() },
    );
    // And what a page could send pretending to be the content script.
    await log.record({
      run: {
        workflowId: 'fill-test',
        log: [
          { step: TITLE, phase: 'act', outcome: 'ok', atMs: 1 },
          { step: 'lyrics', phase: LYRIC, outcome: 'ok', atMs: 2 },
          { step: 'lyrics', phase: 'act', outcome: 'failed', atMs: 3, note: PROMPT },
        ],
        failure: {
          kind: 'check',
          expected: `${TOKEN} ${CREATE_SESSION_TOKEN} "${USER_TIER}" no: "${PROMPT}" for @${HANDLE} at https://${ADDRESS_HOST}/x clip ${CLIP_ID} ${CLIP_ID.replace(/-/g, '')}`,
          step: TITLE,
        },
        structure: {
          anchoredOn: 'failing-element',
          ancestors: ['main', TITLE, HANDLE.toUpperCase()],
          siblings: [`p ${LYRIC}`],
          root: {
            tag: 'div',
            role: HANDLE,
            type: USER_TIER,
            testId: CLIP_ID,
            aria: [`aria-label=${TITLE}`, 'aria-expanded'],
            text: LYRIC,
            children: [{ tag: 'span', testId: `ws-${WORKSPACE_ID}`, value: STYLES }],
          },
          truncated: false,
        },
      },
      statuses: [
        { id: TITLE, state: 'ready', step: null, stopped: false },
        { id: 'fill-test', state: 'not-working', step: LYRIC, stopped: true, message: PROMPT },
      ],
    });
    await log.record({ run: { workflowId: TITLE, log: [{ step: TITLE, atMs: 1 }] } });

    const report = await log.report();

    expectNothingPrivate(report);
    expect(report.steps.some((step) => step.step === 'styles' && step.outcome === 'failed')).toBe(
      true,
    );
    expect(report.steps.find((step) => step.step === 'styles' && step.expected)?.expected).toBe(
      'the Styles box to hold "…"',
    );
    // The forged run's structure is rebuilt by the rules: the tags and safe names only.
    expect(report.pageStructure).toEqual({
      anchoredOn: 'failing-element',
      ancestors: ['main'],
      siblings: [],
      root: {
        tag: 'div',
        aria: ['aria-expanded'],
        children: [{ tag: 'span' }],
      },
      nodeCount: 2,
      truncated: false,
    });
    expect(report.workflows.find((workflow) => workflow.id === 'fill-test')?.step).toBeNull();
  });

  it('keeps no text of a page region that holds text nodes', () => {
    document.body.innerHTML = `<main><div role="group" data-testid="lyrics-panel">
      <p>${LYRIC}</p><span>${TITLE}<b>${HANDLE}</b></span>${PROMPT}
      <button type="button" aria-label="${TITLE}" aria-pressed="true">${STYLES}</button>
    </div></main>`;
    const page = new Page(document, { address: () => 'https://suno.com/create' });
    page.find({ role: 'button', name: TITLE, description: 'the toggle' });

    const structure = page.structureAround();

    expectNothingPrivate(structure);
    expect(structure.anchoredOn).toBe('failing-element');
    expect(structure.ancestors).toEqual(['div', 'main', 'body', 'html']);
    expect(structure.siblings).toEqual(['p', 'span']);
    expect(structure.root).toEqual({
      tag: 'button',
      type: 'button',
      aria: ['aria-label', 'aria-pressed'],
      anchor: true,
    });
  });

  it('keeps no text of a real Suno snapshot around a failure', async () => {
    const { log } = diagnostics();
    const html = snapshotHtml('library-list');
    await recordRun(log, html, missingOnSuno, {});

    const report = await log.report();
    const structure = JSON.stringify(report.pageStructure);

    // No element and no container: the library page has no main region, so the whole page.
    expect(report.pageStructure?.anchoredOn).toBe('page');
    expect(report.pageStructure?.truncated).toBe(true);
    expect(report.pageStructure?.nodeCount).toBeGreaterThan(20);
    expect(report.pageStructure?.nodeCount).toBeLessThanOrEqual(300);
    expect(report.steps).toEqual([
      {
        workflow: 'missing-on-suno-test',
        step: 'button',
        phase: 'expect',
        outcome: 'failed',
        ms: 300,
        expected: 'a button Suno does not have',
      },
    ]);
    // Every text of the snapshot long enough to be content is absent from the capture.
    document.body.innerHTML = html;
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    let checked = 0;
    for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
      const text = (node.textContent ?? '').trim();
      if (text.length >= 12) {
        checked += 1;
        expect(structure).not.toContain(text);
      }
    }
    expect(checked).toBeGreaterThan(0);
    expect(structure).not.toMatch(UUID_SHAPED);
  });

  it('anchors on the container when the failing element is absent, else on the main region', () => {
    document.body.innerHTML = `<header><nav></nav></header><main><section data-testid="create-form"><p>x</p></section></main>`;
    const page = new Page(document, { address: () => 'https://suno.com/create' });

    page.find(MISSING_SUBMIT);
    expect(page.structureAround()).toMatchObject({
      anchoredOn: 'container',
      root: { tag: 'section', testId: 'create-form', anchor: true },
    });

    page.find({ role: 'dialog', description: 'a dialog' });
    expect(page.structureAround()).toMatchObject({
      anchoredOn: 'main-region',
      root: { tag: 'main', anchor: true },
      siblings: ['header'],
    });
  });

  it('captures breadth-first to six levels and at most 300 nodes, and says it was cut', () => {
    const deep = '<div>'.repeat(10) + '</div>'.repeat(10);
    document.body.innerHTML = `<main>${deep}</main>`;
    const page = new Page(document, { address: () => 'https://suno.com/create' });
    const shallow = page.structureAround();
    expect(shallow.truncated).toBe(true);
    expect(shallow.nodeCount).toBe(7);

    document.body.innerHTML = `<main>${'<p><span></span><span></span></p>'.repeat(200)}</main>`;
    const wide = page.structureAround();
    expect(wide.truncated).toBe(true);
    expect(wide.nodeCount).toBe(300);
    expect(nodes(wide.root)).toHaveLength(300);
    // Breadth-first: every paragraph comes before any span.
    expect((wide.root.children ?? []).length).toBe(200);
  });

  it('keeps only role, type, a plain test ID, and aria names', () => {
    expect(
      structureNodeOf({
        tag: 'BUTTON',
        role: 'button',
        type: 'submit',
        testId: 'create-button',
        attributeNames: ['class', 'id', 'href', 'name', 'aria-label', 'aria-expanded', 'title'],
      }),
    ).toEqual({
      tag: 'button',
      role: 'button',
      type: 'submit',
      testId: 'create-button',
      aria: ['aria-expanded', 'aria-label'],
    });
    for (const testId of [
      CLIP_ID,
      `clip-${CLIP_ID}`,
      'row-1234567',
      'deadbeef42',
      'Has Spaces',
      'x'.repeat(41),
    ]) {
      expect(
        structureNodeOf({ tag: 'div', role: null, type: null, testId, attributeNames: [] }),
      ).toEqual({ tag: 'div' });
    }
    expect(
      structureNodeOf({
        tag: 'div',
        role: 'npond',
        type: 'Sentinel',
        testId: null,
        attributeNames: [],
      }),
    ).toEqual({ tag: 'div' });
  });

  it('redacts quoted values, addresses, handles, and ID-shaped runs from an expectation', () => {
    expect(redactExpected(`the list to offer "${TITLE}" once (found 2)`)).toBe(
      'the list to offer "…" once (found 2)',
    );
    expect(redactExpected(`the clip ${CLIP_ID} at https://suno.com/song/x for @${HANDLE}`)).toBe(
      'the clip [id] at [address] for [name]',
    );
    expect(redactExpected('a run 1234567 and deadbeef42 left "unclosed')).toBe(
      'a run [number] and [id] left "…"',
    );
    expect(redactExpected("Suno's navigation, with its Library link")).toBe(
      "Suno's navigation, with its Library link",
    );
    expect(isIdShaped('navbar-library-tab')).toBe(false);
  });

  it('downloads with no failure recorded: the page structure is then null', async () => {
    const { log } = diagnostics({ connection: { status: 'not-paired' }, browser: 'unknown' });

    const report = await log.report();

    expect(report.pageStructure).toBeNull();
    expect(report.steps).toEqual([]);
    expect(report.versions.application).toBeNull();
    expect(report.versions.compatible).toBeNull();
    expect(report.connection).toEqual({ status: 'not-paired', scheme: null });
    expect(report.browser).toBe('unknown');
    expect(report.workflows.every((workflow) => workflow.state === 'not_checked')).toBe(true);
    expect(reportFileName(new Date(report.generatedAt))).toBe(
      'n8tracks-extension-diagnostics-2026-10-06.json',
    );
  });

  it('says which side to update on a mismatch, and keeps only the scheme of the address', async () => {
    const { log } = diagnostics({
      connection: {
        ...CONNECTED,
        address: `http://${ADDRESS_HOST}`,
        applicationVersion: `0.2.0 ${TITLE}`,
        compatibility: {
          kind: 'mismatch',
          extensionVersion: '0.1.0',
          applicationVersion: `0.2.0 ${TITLE}`,
          update: 'extension',
        },
      },
    });

    const report = await log.report();

    expect(report.versions).toMatchObject({
      application: null,
      compatible: false,
      update: 'extension',
    });
    expect(report.connection.scheme).toBe('http');
    expectNothingPrivate(report);
  });
});

describe('the step log', () => {
  function entries(count: number) {
    return Array.from({ length: count }, (_, index) => ({
      step: 'navigation',
      phase: 'expect',
      outcome: 'ok',
      atMs: index * 10,
    }));
  }

  it('keeps the last 200 steps: the 201st evicts the oldest', async () => {
    const { log } = diagnostics();
    await log.record({ run: { workflowId: 'recognise-suno', log: entries(STEP_LOG_LIMIT) } });
    expect((await log.read()).steps).toHaveLength(200);

    await log.record({
      run: {
        workflowId: 'recognise-suno',
        log: [{ step: 'navigation', phase: 'verify', outcome: 'ok', atMs: 5 }],
      },
    });

    const steps = (await log.read()).steps;
    expect(steps).toHaveLength(200);
    // The first entry (ms 0) is gone; the second (10 ms after it) is now the oldest.
    expect(steps[0]).toMatchObject({ phase: 'expect', ms: 10 });
    expect(steps.at(-1)).toMatchObject({ phase: 'verify', ms: 5 });
  });

  it('is in session storage, and Disconnect clears it with the last capture', async () => {
    const fake = fakeBrowser();
    const log = new Diagnostics({
      storage: fake.browser.storage,
      workflows: WORKFLOWS,
      versions: { extension: '0.1.0', adapter: '1' },
      connectionState: () => Promise.resolve({ status: 'not-paired' }),
      browser: () => 'unknown',
    });
    await recordRun(log, PRIVATE_PAGE, missing, {});
    expect(fake.stored.has(DIAGNOSTICS_KEY)).toBe(true);
    expect((await log.read()).structure).not.toBeNull();

    const connection = new Connection({
      browser: fake.browser,
      versions: { extension: '0.1.0', adapter: '1' },
      fetch: () => Promise.reject(new Error('no network in this test')),
    });
    const id = 'abcdefghijklmnopabcdefghijklmnop';
    await route(
      connection,
      { type: 'disconnect' },
      { id, url: `chrome-extension://${id}/options/options.html` },
      id,
      log,
    );

    expect(fake.stored.has(DIAGNOSTICS_KEY)).toBe(false);
    expect(await log.read()).toEqual({ steps: [], statuses: [], structure: null });
    expect((await log.report()).pageStructure).toBeNull();
  });

  it('takes only a registered workflow and the steps it declares', () => {
    expect(stepsOfRun({ workflowId: 'unknown', log: entries(3) }, WORKFLOWS)).toEqual([]);
    expect(
      stepsOfRun(
        {
          workflowId: 'recognise-suno',
          log: [
            { step: 'navigation', phase: 'expect', outcome: 'ok', atMs: 4 },
            { step: 'not-declared', phase: 'expect', outcome: 'ok', atMs: 5 },
          ],
        },
        WORKFLOWS,
      ),
    ).toEqual([
      { workflow: 'recognise-suno', step: 'navigation', phase: 'expect', outcome: 'ok', ms: 4 },
    ]);
    expect(validStructure({ anchoredOn: 'elsewhere', root: { tag: 'div' } })).toBeNull();
  });
});

describe('the browser version', () => {
  it('is the named brand and its major version, or unknown', () => {
    expect(
      browserVersion({
        brands: [
          { brand: 'Not)A;Brand', version: '99' },
          { brand: 'Chromium', version: '140' },
          { brand: 'Google Chrome', version: '140.0.7339.80' },
        ],
      }),
    ).toBe('Google Chrome 140');
    expect(browserVersion({ brands: [{ brand: 'Chromium', version: '139' }] })).toBe(
      'Chromium 139',
    );
    expect(browserVersion(undefined)).toBe('unknown');
    expect(browserVersion({ brands: [{ brand: `${HANDLE}<script>`, version: '1' }] })).toBe(
      'unknown',
    );
  });
});

describe('a report is a plain object', () => {
  it('survives a JSON round trip unchanged', async () => {
    const { log } = diagnostics();
    await recordRun(log, PRIVATE_PAGE, missing, {});
    const report: DiagnosticReport = await log.report();
    expect(JSON.parse(JSON.stringify(report))).toEqual(report);
  });
});
