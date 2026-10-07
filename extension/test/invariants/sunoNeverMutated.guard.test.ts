// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { ANY_SUNO_PAGE } from '../../src/adapter/addresses.ts';
import type { Page, Target } from '../../src/adapter/primitives.ts';
import { OK, present, type Step, type Workflow } from '../../src/adapter/workflow.ts';
import { ADAPTER_WORKFLOWS } from '../../src/adapter/workflows/index.ts';
import { SNAPSHOT_NAMES, snapshotHtml } from '../../src/testing/snapshots.ts';
import { EXTENSION_ROOT, scanExtension } from './sourceScan.ts';
import {
  exerciseWorkflows,
  forbiddenControls,
  registryProblems,
  type RunRecipe,
} from './workflowGuard.ts';

/**
 * Invariant 4 (CLAUDE.md): Suno is never mutated destructively, and the extension never clicks
 * Create, Publish, or Delete on its own. This guard runs in `npm test` on every pull request:
 *
 * 1. at run time, every registered workflow against every TS-003 snapshot, with spies on the
 *    controls the forbidden-control matcher (`src/adapter/forbidden.ts`) recognises;
 * 2. statically, the extension's shipped source (`sourceScan.ts`): no request to Suno, nothing
 *    reaching Suno's page except through `adapter/primitives.ts`, no workflow it cannot see.
 *
 * Not covered: a forbidden control Suno adds under a new name is caught only when the snapshots
 * and the matcher are updated; the live-site smoke checks in the generate stories supplement it.
 * `sourceScan.ts` lists what the static scan does not see.
 */

/**
 * How the guard runs each registered workflow. A story that adds a workflow adds its recipe
 * here: a workflow without one fails the guard.
 */
const RUN_RECIPES: Readonly<Record<string, RunRecipe>> = {
  'recognise-suno': { values: {} },
  // Scrolls the list on screen; it presses nothing on any snapshot.
  'load-more': { values: {} },
};

/** Every workflow module under `src/adapter/workflows/`, except the list itself. */
const WORKFLOW_MODULES = import.meta.glob<Record<string, unknown>>(
  ['../../src/adapter/workflows/*.ts', '!**/index.ts', '!**/*.test.ts'],
  { eager: true },
);

afterEach(() => {
  document.body.innerHTML = '';
});

describe('invariant 4: the extension never presses a forbidden control on Suno', () => {
  it('runs every registered workflow on every snapshot without activating a forbidden control', async () => {
    expect(ADAPTER_WORKFLOWS.length).toBeGreaterThan(0);
    expect(await exerciseWorkflows(ADAPTER_WORKFLOWS, { recipes: RUN_RECIPES })).toEqual([]);
  });

  it('knows every workflow: each module is registered, and each registered one has a recipe', () => {
    expect(Object.keys(WORKFLOW_MODULES).length).toBeGreaterThan(0);
    expect(registryProblems(WORKFLOW_MODULES, ADAPTER_WORKFLOWS, RUN_RECIPES)).toEqual([]);
  });

  it('spies on the forbidden controls of every snapshot it runs on', () => {
    // Complement: the spies are really there. Every snapshot but the playlist page (which has no
    // Create, Publish, Delete, Trash, or Remove control and no dialog) has at least one.
    const spiedOn = SNAPSHOT_NAMES.filter((name) => {
      document.body.innerHTML = snapshotHtml(name);
      return forbiddenControls(document).length > 0;
    });
    expect(spiedOn).toEqual(SNAPSHOT_NAMES.filter((name) => name !== 'playlist'));
  });

  it('finds no request to Suno and no way to the page around the primitives', () => {
    const report = scanExtension({ root: EXTENSION_ROOT });

    expect(report.findings).toEqual([]);
    // Complement: the scan read the source it guards, and saw the Suno content script's code as
    // page context.
    expect(report.scanned).toEqual(
      expect.arrayContaining([
        'src/adapter/primitives.ts',
        'src/background/apiClient.ts',
        'src/content/suno.ts',
        'src/panel/panel.ts',
        'src/page/observe.ts',
        'src/background/sync.ts',
      ]),
    );
    expect(report.pageContext).toEqual(
      expect.arrayContaining([
        'src/adapter/workflows/recognise.ts',
        'src/adapter/workflows/loadMore.ts',
        'src/adapter/libraryReader.ts',
        'src/content/suno-main.ts',
        'src/content/sunoSync.ts',
        'src/page/observe.ts',
        'src/page/observe-main.ts',
        'src/panel/panel.ts',
        'src/panel/SyncView.ts',
        'src/ui/connectionView.ts',
      ]),
    );
    expect(report.pageContext).not.toContain('src/background/apiClient.ts');
    expect(report.pageContext).not.toContain('src/popup/popup.ts');
  });
});

const CREATE_SONG: Target = {
  role: 'button',
  name: 'Create song',
  description: 'the Create button',
};

/**
 * A test-only workflow of one step that does `act` on the snapshots it lists, once `expectOn` is
 * on the page (at once when null).
 */
function testWorkflow(
  id: string,
  fixtures: readonly string[],
  act: Step['act'],
  expectOn: Target | null = CREATE_SONG,
): Workflow {
  const ready = (page: Page) => (expectOn === null ? OK : present(page, expectOn));
  return {
    id,
    title: `Test only: ${id}`,
    feature: 'generate',
    startsOn: ANY_SUNO_PAGE,
    needs: [{ step: 'act', check: ready }],
    steps: [
      {
        name: 'act',
        expect: ({ page }) => ready(page),
        act,
        verify: () => OK,
      },
    ],
    fixtures,
  };
}

/** Clicks the one element `target` finds, through the click primitive. */
function clicking(target: Target): Step['act'] {
  return ({ page }) => {
    const result = page.find(target);
    if (result.kind === 'found') {
      page.click(result.found);
    }
  };
}

describe('the guard bites', () => {
  const recipes = (...ids: string[]) =>
    Object.fromEntries(ids.map((id) => [id, { values: {} }])) as Record<string, RunRecipe>;

  it('fails a workflow that clicks Create, in each mode', async () => {
    const modes = [
      ['create-songs-simple', 'Create song'],
      ['create-songs-advanced-more-options', 'Create song'],
      ['create-speech-simple', 'Create speech'],
      ['create-speech-advanced', 'Create speech'],
      ['create-sounds-advanced-options', 'Create song'],
    ] as const;
    for (const [snapshot, name] of modes) {
      const target: Target = { role: 'button', name, description: 'the Create button' };
      const rogue = testWorkflow('press-create', [snapshot], clicking(target), target);

      const findings = await exerciseWorkflows([rogue], {
        recipes: recipes('press-create'),
        snapshots: [snapshot],
      });

      expect(findings, snapshot).toEqual([
        `'press-create' on ${snapshot}: step 'act' tried a forbidden control (the Create button: it is the Create button (${name === 'Create song' ? 'Songs and Sounds' : 'Speech'}))`,
        `'press-create' on ${snapshot}: not every step ran on its own fixture (missed 'act')`,
      ]);
    }
  });

  it('fails a workflow that presses Publish or Move to Trash in a clip menu', async () => {
    for (const item of ['Publish', 'Move to Trash']) {
      const target: Target = { role: 'menuitem', name: item, description: `the ${item} item` };
      const rogue = testWorkflow('menu', ['clip-edit-menu'], clicking(target), target);

      const findings = await exerciseWorkflows([rogue], {
        recipes: recipes('menu'),
        snapshots: ['clip-edit-menu'],
      });

      expect(findings[0], item).toMatch(
        new RegExp(
          `^'menu' on clip-edit-menu: step 'act' tried a forbidden control \\(the ${item} item: `,
        ),
      );
    }
  });

  it('fails a workflow that presses Trash in the library, or removes the source from the form', async () => {
    for (const [snapshot, target] of [
      ['library-list', { role: 'button', name: 'Trash', description: 'the Trash button' }],
      [
        'create-source-simple',
        { role: 'button', name: /^Remove /, description: "the source's Remove button" },
      ],
    ] as const) {
      const rogue = testWorkflow('press', [snapshot], clicking(target), target);

      const findings = await exerciseWorkflows([rogue], {
        recipes: recipes('press'),
        snapshots: [snapshot],
      });

      expect(findings[0], snapshot).toBe(
        `'press' on ${snapshot}: step 'act' tried a forbidden control (${target.description}: its name starts with Create, Publish, Delete, Trash, or Remove)`,
      );
    }
  });

  it('catches an activation that bypasses the primitives, by the spies alone', async () => {
    for (const [snapshot, selector, words] of [
      ['create-songs-simple', '[aria-label="Create song"]', 'button "Create song"'],
      ['library-trash', '[aria-label="Delete permanently"]', 'button "Delete permanently"'],
      ['library-list', '[aria-label="Publish clip"]', 'button "Publish clip"'],
      ['download-dialog', '[role="dialog"] button', 'dialog'],
    ] as const) {
      const rogue = testWorkflow(
        'bypass',
        [],
        () => {
          // Test-only: reaches the page directly, as the static scan forbids shipped code to.
          document
            .querySelector(selector)
            ?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
        },
        null,
      );

      const findings = await exerciseWorkflows([rogue], {
        recipes: recipes('bypass'),
        snapshots: [snapshot],
      });

      expect(findings.length, snapshot).toBeGreaterThan(0);
      expect(
        findings.every((finding) => finding.startsWith(`'bypass' on ${snapshot}: click reached `)),
      ).toBe(true);
      expect(
        findings.some((finding) => finding.includes(words)),
        snapshot,
      ).toBe(true);
    }
  });

  it("catches an Enter key, which can submit Suno's form, anywhere", async () => {
    const rogue = testWorkflow('enter', ['create-songs-simple'], () => {
      document
        .querySelector('textarea')
        ?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    });

    const findings = await exerciseWorkflows([rogue], {
      recipes: recipes('enter'),
      snapshots: ['create-songs-simple'],
    });

    expect(findings).toEqual(['\'enter\' on create-songs-simple: an Enter key on textbox ""']);
  });

  it('lets a workflow answer the Overwrite Lyrics & Styles dialog, and refuses its other controls', async () => {
    const dialog = {
      role: 'dialog',
      name: 'Overwrite Lyrics & Styles?',
      description: 'the dialog',
    } as const;
    for (const answer of ['Overwrite', 'Keep Current']) {
      const target: Target = { role: 'button', name: answer, within: dialog, description: answer };
      const answering = testWorkflow(
        'answer',
        ['overwrite-lyrics-styles-dialog'],
        clicking(target),
        target,
      );
      expect(
        await exerciseWorkflows([answering], {
          recipes: recipes('answer'),
          snapshots: ['overwrite-lyrics-styles-dialog'],
        }),
        answer,
      ).toEqual([]);
    }

    const close: Target = { role: 'button', name: 'Close', within: dialog, description: 'Close' };
    const closing = testWorkflow(
      'close',
      ['overwrite-lyrics-styles-dialog'],
      clicking(close),
      close,
    );
    expect(
      await exerciseWorkflows([closing], {
        recipes: recipes('close'),
        snapshots: ['overwrite-lyrics-styles-dialog'],
      }),
    ).toContain(
      "'close' on overwrite-lyrics-styles-dialog: step 'act' tried a forbidden control (Close: the dialog it is in does not allow it)",
    );
  });

  it('allows creating a workspace only through its primitive, and only for a workflow allowed to', async () => {
    const opener: Target = {
      role: 'button',
      name: 'Create new workspace',
      description: 'the Create new workspace entry',
    };
    const byClick = testWorkflow('workspace', ['workspace-selector'], clicking(opener), opener);
    const byPrimitive = testWorkflow(
      'workspace',
      ['workspace-selector'],
      ({ page }) => {
        const result = page.find(opener);
        if (result.kind === 'found') {
          page.createWorkspaceClick(result.found);
        }
      },
      opener,
    );
    const run = (workflow: Workflow, recipe: RunRecipe) =>
      exerciseWorkflows([workflow], {
        recipes: { workspace: recipe },
        snapshots: ['workspace-selector'],
      });

    expect(await run(byClick, { values: {}, exceptions: ['create-workspace'] })).toContain(
      "'workspace' on workspace-selector: step 'act' tried a forbidden control (the Create new workspace entry: it is the named exception 'create-workspace', pressed only by its own primitive)",
    );
    expect(await run(byPrimitive, { values: {} })).toContain(
      '\'workspace\' on workspace-selector: click reached button "Create new workspace"',
    );
    expect(await run(byPrimitive, { values: {}, exceptions: ['create-workspace'] })).toEqual([]);
  });

  it('fails a workflow it has not been told how to run, or one that does not finish on its fixture', async () => {
    const quiet = testWorkflow('quiet', ['library-list'], () => undefined, {
      role: 'link',
      name: 'Library',
      description: 'the Library link',
    });
    expect(await exerciseWorkflows([quiet], { recipes: {}, snapshots: ['library-list'] })).toEqual([
      "'quiet': the guard has not been told how to run it (add a recipe for it)",
    ]);

    const lost = testWorkflow('lost', ['create-songs-simple'], () => undefined, {
      role: 'link',
      name: 'Library',
      description: 'the Library link',
    });
    expect(
      await exerciseWorkflows([lost], {
        recipes: recipes('lost'),
        snapshots: ['create-songs-simple'],
      }),
    ).toEqual([
      "'lost' on create-songs-simple: not every step ran on its own fixture (missed 'act')",
    ]);
  });

  it('fails a workflow module that is not registered, and a registration with no module', () => {
    const [registered] = ADAPTER_WORKFLOWS;
    if (registered === undefined) {
      throw new Error('No workflow is registered.');
    }
    const extra = testWorkflow('extra', ['library-list'], () => undefined);

    expect(
      registryProblems(
        { ...WORKFLOW_MODULES, '../../src/adapter/workflows/extra.ts': { extra } },
        ADAPTER_WORKFLOWS,
        RUN_RECIPES,
      ),
    ).toEqual(["../../src/adapter/workflows/extra.ts: 'extra' is not in ADAPTER_WORKFLOWS"]);
    expect(registryProblems(WORKFLOW_MODULES, [...ADAPTER_WORKFLOWS, extra], RUN_RECIPES)).toEqual([
      "'extra' is registered but no module under adapter/workflows/ has it",
    ]);
    expect(
      registryProblems(WORKFLOW_MODULES, ADAPTER_WORKFLOWS, {
        ...RUN_RECIPES,
        gone: { values: {} },
      }),
    ).toEqual(["the recipe 'gone' is for no registered workflow"]);
    expect(
      registryProblems(
        { ...WORKFLOW_MODULES, '../../src/adapter/workflows/empty.ts': { nothing: 1 } },
        ADAPTER_WORKFLOWS,
        RUN_RECIPES,
      ),
    ).toEqual(['../../src/adapter/workflows/empty.ts exports no workflow']);
    expect(
      registryProblems(
        WORKFLOW_MODULES,
        ADAPTER_WORKFLOWS.map((workflow) =>
          workflow === registered ? { ...registered, fixtures: [] } : workflow,
        ),
        RUN_RECIPES,
      ),
    ).toEqual(expect.arrayContaining([expect.stringMatching(/names no page snapshot/)]));
  });
});
