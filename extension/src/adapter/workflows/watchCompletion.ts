import { sunoPage } from '../addresses.ts';
import { workspaceRow } from '../workspaces.ts';
import { expected, OK, present, type StepContext, type Workflow } from '../workflow.ts';
import { listOrBreadcrumb, WORKSPACE_LIST, WORKSPACES_BREADCRUMB } from './workspace.ts';

/**
 * The completion watch's refresh prompt (#154). After the user's Create, Suno's progress does not
 * reach the page by its feed alone (TS-001), so while clips are unfinished and no feed answer has
 * named them for 15 seconds, the watch prompts the Create page to read its library pane again: it
 * presses the Song's workspace row in the workspace list (opening the list by its breadcrumb when it
 * is closed), which makes the pane ask Suno for that workspace's songs (#145,
 * `page.workspace-selector.html`). Both presses are read-only: they choose what the pane shows and
 * change nothing in Suno. No library filter is toggled, so there is no filter state to restore
 * (TS-003). The answer it prompts is read by the page observer; this workflow waits for nothing.
 */

/** The workspace whose row is pressed: its name as Suno lists it. */
export interface RefreshLibraryContext extends StepContext {
  workspaceName: string;
}

export const refreshLibrary: Workflow<RefreshLibraryContext> = {
  id: 'refresh-library',
  title: 'Read the library pane again',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [{ step: 'workspace list', check: listOrBreadcrumb }],
  steps: [
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
    {
      name: 'workspace row',
      expect: ({ page, workspaceName }) => {
        const row = workspaceRow(workspaceName);
        return row === null
          ? expected('a workspace with a name to find its row by')
          : present(page, row);
      },
      act: ({ page, workspaceName }) => {
        const row = workspaceRow(workspaceName);
        const result = row === null ? null : page.find(row);
        if (result?.kind === 'found') {
          page.click(result.found);
        }
      },
      verify: () => OK,
    },
  ],
  fixtures: ['workspace-selector'],
};
