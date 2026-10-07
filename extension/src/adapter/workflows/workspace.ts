import { sunoPage } from '../addresses.ts';
import type { Page, Target } from '../primitives.ts';
import { expected, OK, present, type Check, type StepContext, type Workflow } from '../workflow.ts';
import { workspaceRow } from '../workspaces.ts';

/**
 * The Song's workspace on Suno's Create page (#145, TS-003: `page.workspace-selector.html` and
 * `page.create-workspace-dialog.html`): open the workspace list, scroll it for more, select a
 * workspace by its row, and create one named after the Song. Creating a workspace is invariant 4's
 * one permitted change to Suno: its two controls are pressed only here, and only through
 * `Page.createWorkspaceClick`. The content script (`content/sunoGenerate.ts`) decides which of
 * these run, from the request and the user's choice in the panel; nothing is created unless the
 * user chose to create.
 */

/**
 * Suno's profile menu button, which the TS-003 snapshots of signed-in pages carry. There is no
 * snapshot of a signed-out page, so being signed in is told by this button being there.
 */
export const PROFILE_MENU: Target = {
  role: 'button',
  testId: 'profile-menu-button',
  description: "Suno's profile menu, shown only to a signed-in user",
};

/**
 * The "Workspaces" breadcrumb above the library pane on /create, which opens the workspace list.
 * The snapshot shows the list open, not this breadcrumb, so it is found by its name alone; the
 * workspace-name breadcrumb beside it renames the workspace and is never pressed.
 */
export const WORKSPACES_BREADCRUMB: Target = {
  role: 'button',
  name: 'Workspaces',
  description: 'the "Workspaces" breadcrumb above the library pane',
};

/** The open workspace list, known by its search box. */
export const WORKSPACE_LIST: Target = {
  role: 'textbox',
  name: 'Search workspaces',
  description: "Suno's workspace list, with its search box",
};

/** The list's first row, which opens an inline row for a new workspace (the named exception). */
export const CREATE_ENTRY: Target = {
  role: 'button',
  name: 'Create new workspace',
  description: 'the "Create new workspace" entry of the workspace list',
};

/** The inline row's name field. */
export const NEW_WORKSPACE_NAME: Target = {
  role: 'textbox',
  name: 'New workspace name',
  description: 'the "New workspace name" field of the new workspace row',
};

/** The inline row's Confirm button (the named exception, by the field beside it). */
export const CONFIRM_NEW_WORKSPACE: Target = {
  role: 'button',
  name: 'Confirm',
  description: "the new workspace row's Confirm button",
};

/** The workspace list is open, or the breadcrumb that opens it is there. */
export function listOrBreadcrumb(page: Page): Check {
  if (page.find(WORKSPACE_LIST).kind === 'found') {
    return present(page, WORKSPACE_LIST);
  }
  return page.find(WORKSPACES_BREADCRUMB).kind === 'found'
    ? present(page, WORKSPACES_BREADCRUMB)
    : expected('Suno\'s workspace list, or the "Workspaces" breadcrumb that opens it');
}

/** Selecting a workspace: its name as Suno lists it, and how the page shows it was selected. */
export interface SelectWorkspaceContext extends StepContext {
  workspaceName: string;
  /** Whether the page now shows the workspace: its library pane asked Suno for its songs. */
  shown: () => Check;
}

/** Creating a workspace: the name it is given, and whether Suno answered with its ID. */
export interface CreateWorkspaceContext extends StepContext {
  workspaceName: string;
  /** Whether Suno answered the creation with the new workspace (`POST /api/project`). */
  created: () => Check;
}

/** Signed in, and the workspace list open. Reads, and presses only the breadcrumb. */
export const openWorkspaces: Workflow = {
  id: 'open-workspaces',
  title: 'Open the workspace list',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [
    { step: 'signed in', check: (page) => present(page, PROFILE_MENU) },
    { step: 'workspace list', check: listOrBreadcrumb },
  ],
  steps: [
    {
      name: 'signed in',
      expect: ({ page }) => present(page, PROFILE_MENU),
      act: () => undefined,
      verify: () => OK,
    },
    {
      name: 'workspace list',
      expect: ({ page }) => listOrBreadcrumb(page),
      act: ({ page }) => {
        if (page.find(WORKSPACE_LIST).kind === 'found') {
          return;
        }
        const breadcrumb = page.find(WORKSPACES_BREADCRUMB);
        if (breadcrumb.kind === 'found') {
          page.click(breadcrumb.found);
        }
      },
      verify: ({ page }) => present(page, WORKSPACE_LIST),
    },
  ],
  fixtures: ['workspace-selector'],
};

/** Scrolls the open workspace list to its end, so that Suno asks for its next page. */
export const moreWorkspaces: Workflow = {
  id: 'more-workspaces',
  title: 'Load more of the workspace list',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [{ step: 'workspace list', check: listOrBreadcrumb }],
  steps: [
    {
      name: 'workspace list',
      expect: ({ page }) => present(page, WORKSPACE_LIST),
      act: ({ page }) => {
        page.scrollToEnd();
      },
      verify: () => OK,
    },
  ],
  fixtures: ['workspace-selector'],
};

/**
 * Selects the workspace by its row in the open list, found by the name Suno lists for its ID. Two
 * rows that match (two workspaces of that name) are never chosen between: the step stops.
 */
export const selectWorkspace: Workflow<SelectWorkspaceContext> = {
  id: 'select-workspace',
  title: 'Select the Song’s workspace',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [{ step: 'select workspace', check: listOrBreadcrumb }],
  steps: [
    {
      name: 'select workspace',
      expect: ({ page, workspaceName }) => {
        const row = workspaceRow(workspaceName);
        return row === null
          ? expected('a workspace with a name to select it by')
          : present(page, row);
      },
      act: ({ page, workspaceName }) => {
        const row = workspaceRow(workspaceName);
        const result = row === null ? null : page.find(row);
        if (result?.kind === 'found') {
          page.click(result.found);
        }
      },
      verify: ({ shown }) => shown(),
      timeoutMs: 15_000,
    },
  ],
  fixtures: ['workspace-selector'],
};

/**
 * Creates a workspace through Suno's own inline row: "Create new workspace", the name, Confirm.
 * Run only when the user chose to create one in the panel. Each press is the create-workspace
 * exception's primitive; a row already open from an earlier try is used as it is.
 */
export const createWorkspace: Workflow<CreateWorkspaceContext> = {
  id: 'create-workspace',
  title: 'Create the Song’s workspace',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [{ step: 'new workspace row', check: listOrBreadcrumb }],
  steps: [
    {
      name: 'new workspace row',
      expect: ({ page }) =>
        page.find(NEW_WORKSPACE_NAME).kind === 'found'
          ? present(page, NEW_WORKSPACE_NAME)
          : present(page, CREATE_ENTRY),
      act: ({ page }) => {
        if (page.find(NEW_WORKSPACE_NAME).kind === 'found') {
          return;
        }
        const entry = page.find(CREATE_ENTRY);
        if (entry.kind === 'found') {
          page.createWorkspaceClick(entry.found);
        }
      },
      verify: ({ page }) => present(page, NEW_WORKSPACE_NAME),
    },
    {
      name: 'workspace name',
      expect: ({ page }) => present(page, NEW_WORKSPACE_NAME),
      act: ({ page, workspaceName }) => {
        const field = page.find(NEW_WORKSPACE_NAME);
        if (field.kind === 'found') {
          page.set(field.found, workspaceName);
        }
      },
      verify: ({ page, workspaceName }) => {
        const field = page.find(NEW_WORKSPACE_NAME);
        return field.kind === 'found' && page.read(field.found).value === workspaceName
          ? OK
          : expected(`${NEW_WORKSPACE_NAME.description} to hold the Song's title`);
      },
    },
    {
      name: 'confirm',
      expect: ({ page }) => present(page, CONFIRM_NEW_WORKSPACE),
      act: ({ page }) => {
        const confirm = page.find(CONFIRM_NEW_WORKSPACE);
        if (confirm.kind === 'found') {
          page.createWorkspaceClick(confirm.found);
        }
      },
      verify: ({ created }) => created(),
      timeoutMs: 20_000,
    },
  ],
  fixtures: ['create-workspace-dialog'],
};
