// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { fakeClock, loadSnapshot } from '../../testing/snapshots.ts';
import { nameOf } from '../primitives.ts';
import { checkWorkflow } from '../registry.ts';
import { expected, OK, runWorkflow, type Check } from '../workflow.ts';
import {
  createWorkspace,
  listOrBreadcrumb,
  moreWorkspaces,
  openWorkspaces,
  PROFILE_MENU,
  selectWorkspace,
} from './workspace.ts';

afterEach(() => {
  document.body.innerHTML = '';
});

/** Records every click that reaches a `role=button`, by its accessible name. */
function clicks(): string[] {
  const pressed: string[] = [];
  document.addEventListener(
    'click',
    (event) => {
      const target = (event.target as Element).closest('[role="button"], button');
      if (target !== null) {
        pressed.push(nameOf(target));
      }
    },
    { capture: true },
  );
  return pressed;
}

function run(
  workflow: Parameters<typeof runWorkflow>[0],
  snapshot: string,
  values: Record<string, unknown>,
) {
  const clock = fakeClock();
  const page = loadSnapshot(snapshot, 'https://suno.com/create', clock);
  const pressed = clicks();
  return { result: runWorkflow(workflow, page, values, { clock }), pressed, page };
}

describe('opening the workspace list (TS-003: page.workspace-selector.html)', () => {
  it('finds the user signed in and the list open, and presses nothing', async () => {
    const { result, pressed, page } = run(openWorkspaces, 'workspace-selector', {});

    expect((await result).ok).toBe(true);
    expect(pressed).toEqual([]);
    expect(checkWorkflow(openWorkspaces, page).state).toBe('ready');
  });

  it('stops at "signed in" when Suno shows no profile menu (the signed-out fallback)', async () => {
    const clock = fakeClock();
    const page = loadSnapshot('workspace-selector', 'https://suno.com/create', clock);
    // No snapshot of a signed-out page exists: the profile menu taken away stands in for one.
    document.querySelector('[data-testid="profile-menu-button"]')?.remove();

    const result = await runWorkflow(openWorkspaces, page, {}, { clock });

    expect(result.ok).toBe(false);
    expect(!result.ok && result.failure).toMatchObject({
      step: 'signed in',
      phase: 'expect',
      expected: PROFILE_MENU.description,
      pageMayBeChanged: false,
    });
  });

  it('is not checked off the Create page, and not working where there is neither list nor breadcrumb', () => {
    expect(
      checkWorkflow(openWorkspaces, loadSnapshot('library-list', 'https://suno.com/me')).state,
    ).toBe('not-checked');
    const create = loadSnapshot('create-songs-simple');
    expect(listOrBreadcrumb(create)).toEqual(
      expected('Suno\'s workspace list, or the "Workspaces" breadcrumb that opens it'),
    );
    expect(checkWorkflow(moreWorkspaces, create).state).toBe('not-working');
  });
});

describe('selecting the Song’s workspace', () => {
  it('presses the row with the name Suno lists for its ID, and waits for the page to show it', async () => {
    let shownAfter = 0;
    let presses = 0;
    const shown = (): Check => (presses > 0 && (shownAfter += 1) > 1 ? OK : expected('shown'));
    const { result, pressed } = run(selectWorkspace, 'workspace-selector', {
      workspaceName: 'My Workspace',
      shown,
    });
    document.addEventListener('click', () => (presses += 1), { capture: true });

    expect((await result).ok).toBe(true);
    expect(pressed).toHaveLength(1);
    expect(pressed[0]).toMatch(/^My Workspace /);
  });

  it('never chooses between two rows of the same name: it stops at the step, pressing nothing', async () => {
    const { result, pressed } = run(selectWorkspace, 'workspace-selector', {
      workspaceName: '<redacted 13 chars>',
      shown: () => OK,
    });

    const ended = await result;
    expect(ended.ok).toBe(false);
    expect(!ended.ok && ended.failure).toMatchObject({
      step: 'select workspace',
      phase: 'expect',
      expected: "the workspace's row in Suno's workspace list (found 2, so none was chosen)",
    });
    expect(pressed).toEqual([]);
  });

  it('stops naming the step when the list lacks the workspace', async () => {
    const { result, pressed } = run(selectWorkspace, 'workspace-selector', {
      workspaceName: 'Not in this list',
      shown: () => OK,
    });

    const ended = await result;
    expect(!ended.ok && ended.failure).toMatchObject({
      step: 'select workspace',
      expected: "the workspace's row in Suno's workspace list",
    });
    expect(pressed).toEqual([]);
  });

  it('stops when the page does not show the workspace after the press', async () => {
    const { result } = run(selectWorkspace, 'workspace-selector', {
      workspaceName: 'My Workspace',
      shown: () => expected("Suno's library pane to show the workspace's songs"),
    });

    const ended = await result;
    expect(!ended.ok && ended.failure).toMatchObject({
      step: 'select workspace',
      phase: 'verify',
      expected: "Suno's library pane to show the workspace's songs",
      pageMayBeChanged: true,
    });
  });
});

describe('creating the Song’s workspace (TS-003: page.create-workspace-dialog.html)', () => {
  it('names the new row after the Song and confirms it, through the exception’s primitive only', async () => {
    const { result, pressed } = run(createWorkspace, 'create-workspace-dialog', {
      workspaceName: 'Night Drive',
      created: () => OK,
    });

    expect((await result).ok).toBe(true);
    expect(
      document.querySelector<HTMLInputElement>('input[aria-label="New workspace name"]')?.value,
    ).toBe('Night Drive');
    expect(pressed).toEqual(['Confirm']);
  });

  it('opens the new row from the list’s "Create new workspace" entry', async () => {
    const { result, pressed } = run(createWorkspace, 'workspace-selector', {
      workspaceName: 'Night Drive',
      created: () => OK,
    });

    // The snapshot is static, so no row opens: the press is all that happens.
    const ended = await result;
    expect(pressed).toEqual(['Create new workspace']);
    expect(!ended.ok && ended.failure).toMatchObject({
      step: 'new workspace row',
      phase: 'verify',
    });
  });

  it('stops after Confirm when Suno does not answer with the new workspace', async () => {
    const { result } = run(createWorkspace, 'create-workspace-dialog', {
      workspaceName: 'Night Drive',
      created: () => expected('Suno to answer with the new workspace and its ID'),
    });

    const ended = await result;
    expect(!ended.ok && ended.failure).toMatchObject({
      step: 'confirm',
      phase: 'verify',
      pageMayBeChanged: true,
    });
  });
});
