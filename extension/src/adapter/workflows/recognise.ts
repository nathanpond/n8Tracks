import { ANY_SUNO_PAGE } from '../addresses.ts';
import type { Target } from '../primitives.ts';
import { OK, present, type Workflow } from '../workflow.ts';

/**
 * The Library link in Suno's own navigation (TS-003: `page.library-list.html`,
 * `page.library-trash.html`, and `page.workspace-selector.html`, which are whole pages). Found by
 * role and its test attribute: one per page, unlike the logo, which appears twice.
 */
export const SUNO_NAVIGATION: Target = {
  role: 'link',
  testId: 'navbar-library-tab',
  description: "Suno's navigation, with its Library link",
};

/**
 * The page is recognisably Suno: the one workflow of the adapter story. It reads only, and every
 * other workflow is registered by its own story.
 */
export const recogniseSuno: Workflow = {
  id: 'recognise-suno',
  title: 'Recognise the Suno page',
  feature: 'page',
  startsOn: ANY_SUNO_PAGE,
  needs: [{ step: 'navigation', check: (page) => present(page, SUNO_NAVIGATION) }],
  steps: [
    {
      name: 'navigation',
      expect: ({ page }) => present(page, SUNO_NAVIGATION),
      act: () => undefined,
      verify: () => OK,
    },
  ],
  fixtures: ['library-list', 'library-trash', 'workspace-selector'],
};
