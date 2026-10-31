import { sunoPage } from '../addresses.ts';
import type { Target } from '../primitives.ts';
import { OK, present, type Workflow } from '../workflow.ts';

/**
 * Library's Workspaces tab (#421, TS-007-6): the tab in Library's tab row whose whole text is
 * "Workspaces" and which leads to `/me/workspaces` (`page.library-list.html`,
 * `page.library-songs-page-1.html`: an `<a role="tab">` beside Songs, Playlists, and the rest).
 * Found by its role, its exact name, and the path its own `href` names, so a second element that
 * reads the same, or none, stops the step with nothing pressed.
 */
export const WORKSPACES_TAB: Target = {
  role: 'tab',
  name: 'Workspaces',
  address: '/me/workspaces',
  description: "the Workspaces tab in Library's tab row, leading to /me/workspaces",
};

/**
 * Brings the workspace list onto the page for the sync's first leg (#421). A direct load of
 * `/me/workspaces` makes no `GET /api/project/me` request (the page shows the list from its cache,
 * TS-007, seen again on 2026-10-11), so the observer never sees the list. Pressing Library's
 * Workspaces tab on `/me` does: the page goes to `/me/workspaces` without loading a new document
 * and asks Suno for the list. This is the one place the adapter reaches a page by pressing rather
 * than by address (TS-007-6). The press goes through the click primitive, so the forbidden-control
 * matcher is asked as for any other control; a tab changes only what Library shows. Whether the
 * list came is the library reader's to judge: it waits for Suno's answer, and opens `/me` again
 * when none comes.
 */
export const openWorkspaceList: Workflow = {
  id: 'open-workspace-list',
  title: "Open Library's Workspaces tab",
  feature: 'sync',
  startsOn: sunoPage('library'),
  needs: [{ step: 'workspaces tab', check: (page) => present(page, WORKSPACES_TAB) }],
  steps: [
    {
      name: 'workspaces tab',
      expect: ({ page }) => present(page, WORKSPACES_TAB),
      act: ({ page }) => {
        const tab = page.find(WORKSPACES_TAB);
        if (tab.kind === 'found') {
          page.click(tab.found);
        }
      },
      verify: () => OK,
    },
  ],
  fixtures: ['library-list', 'library-songs-page-1', 'library-songs-groups'],
};
