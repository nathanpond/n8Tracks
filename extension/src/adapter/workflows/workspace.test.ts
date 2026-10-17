// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { fakeClock, loadSnapshot } from '../../testing/snapshots.ts';
import { nameOf } from '../primitives.ts';
import { checkWorkflow } from '../registry.ts';
import { expected, OK, runWorkflow } from '../workflow.ts';
import {
  createWorkspace,
  listOrBreadcrumb,
  openWorkspaces,
  PROFILE_MENU,
  searchWorkspaces,
  signedIn,
  WORKSPACE_SEARCH,
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
    expect(checkWorkflow(searchWorkspaces, create).state).toBe('not-working');
  });
});

describe('checking sign-in, on Create and on a workspace’s songs (TS-007-15)', () => {
  it('reads the profile menu and presses nothing, on /create and on /create?wid=', async () => {
    for (const [snapshot, address] of [
      ['workspace-selector', 'https://suno.com/create'],
      ['create-workspace-songs', 'https://suno.com/create?wid=w-1'],
    ] as const) {
      const clock = fakeClock();
      const page = loadSnapshot(snapshot, address, clock);
      const pressed = clicks();
      expect((await runWorkflow(signedIn, page, {}, { clock })).ok).toBe(true);
      expect(pressed).toEqual([]);
      document.body.innerHTML = '';
    }
  });

  it('stops at "signed in" without the profile menu', async () => {
    const clock = fakeClock();
    const page = loadSnapshot('workspace-selector', 'https://suno.com/create', clock);
    document.querySelector('[data-testid="profile-menu-button"]')?.remove();

    const result = await runWorkflow(signedIn, page, {}, { clock });

    expect(!result.ok && result.failure.step).toBe('signed in');
  });
});

describe('searching the workspace list (TS-007-15: its search box, in page.workspace-selector.html)', () => {
  it('types the name into the search box as typing would, and presses nothing', async () => {
    const { result, pressed } = run(searchWorkspaces, 'workspace-selector', {
      query: 'Night Drive',
    });

    expect((await result).ok).toBe(true);
    expect(
      document.querySelector<HTMLInputElement>('input[aria-label="Search workspaces"]')?.value,
    ).toBe('Night Drive');
    expect(pressed).toEqual([]);
    // A blank query clears it again.
    const clock = fakeClock();
    const page = loadSnapshot('workspace-selector', 'https://suno.com/create', clock);
    const box = document.querySelector<HTMLInputElement>('input[aria-label="Search workspaces"]');
    if (box !== null) {
      box.value = 'Night Drive';
    }
    expect((await runWorkflow(searchWorkspaces, page, { query: '' }, { clock })).ok).toBe(true);
    expect(box?.value).toBe('');
  });

  it('is not working where the list is closed, and stops at the search box', async () => {
    const clock = fakeClock();
    const page = loadSnapshot('create-songs-simple', 'https://suno.com/create', clock);

    const result = await runWorkflow(searchWorkspaces, page, { query: 'x' }, { clock });

    expect(!result.ok && result.failure).toMatchObject({
      step: 'search box',
      expected: WORKSPACE_SEARCH.description,
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
