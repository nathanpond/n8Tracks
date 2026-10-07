import { sunoPage } from '../addresses.ts';
import type { Page } from '../primitives.ts';
import {
  actionItem,
  AUDIO_CONDITION,
  MORE_OPTIONS,
  OVERWRITE_BUTTON,
  SIMPLE_CHIP_THUMBNAIL,
  sourceShown,
  submenu,
  submenuItem,
  type LoadedSource,
  type SourceRoute,
} from '../sources.ts';
import { expected, OK, present, type Check, type StepContext, type Workflow } from '../workflow.ts';

/**
 * Starting from a source (#148, TS-002, TS-003): on the source clip's own page, its More options
 * menu is opened, then the Remix or Edit submenu, and the action is chosen, as a person would; on
 * the Create form, Suno's Overwrite question is answered Overwrite, and the loaded source is
 * verified before anything else is filled (`adapter/sources.ts`). A missing or disabled menu item
 * stops the run as unavailable; no other route is tried. Nothing here presses Create, Publish,
 * Delete, or Move to Trash (invariant 4): the Remix and Edit menus hold Publish and Move to Trash,
 * which the forbidden-control matcher refuses.
 */

/** Opening a source clip's menu and choosing an action: the action's route. */
export interface SourceMenuContext extends StepContext {
  route: SourceRoute;
}

/** Verifying the loaded source on the Create form. */
export interface VerifySourceContext extends StepContext {
  load: LoadedSource;
}

/** The clip's menu is open: its Remix or Edit item is on the page. */
function menuOpen(page: Page, route: SourceRoute): Check {
  return page.find(submenuItem(route.menu)).kind === 'found'
    ? OK
    : expected('the clip’s menu, open');
}

/** The submenu holding the action is open. */
function submenuOpen(page: Page, route: SourceRoute): Check {
  return page.find(submenu(route.menu)).kind === 'found'
    ? OK
    : expected('the clip’s Remix or Edit menu, open');
}

/** Opens the source clip's menu and the submenu that holds the action, and checks it is offered. */
export const openSourceMenu: Workflow<SourceMenuContext> = {
  id: 'open-source-menu',
  title: 'Open the source clip’s menu',
  feature: 'generate',
  startsOn: sunoPage('song'),
  needs: [{ step: 'clip menu', check: (page) => present(page, MORE_OPTIONS) }],
  steps: [
    {
      name: 'clip menu',
      expect: ({ page, route }) => (menuOpen(page, route).ok ? OK : present(page, MORE_OPTIONS)),
      act: ({ page, route }) => {
        if (menuOpen(page, route).ok) {
          return;
        }
        const button = page.find(MORE_OPTIONS);
        if (button.kind === 'found') {
          page.click(button.found);
        }
      },
      verify: ({ page, route }) => menuOpen(page, route),
    },
    {
      name: 'action menu',
      expect: ({ page, route }) => present(page, submenuItem(route.menu)),
      act: ({ page, route }) => {
        if (submenuOpen(page, route).ok) {
          return;
        }
        const item = page.find(submenuItem(route.menu));
        if (item.kind === 'found') {
          page.click(item.found);
        }
      },
      verify: ({ page, route }) => submenuOpen(page, route),
    },
    {
      // A missing or disabled item is unavailable (a plan the user lacks, or a renamed item).
      name: 'action offered',
      expect: ({ page, route }) => present(page, actionItem(route)),
      act: () => undefined,
      verify: () => OK,
    },
  ],
  fixtures: ['clip-remix-menu'],
};

/** Chooses the action in the open submenu; Suno then opens the Create form with the source. */
export const chooseSourceAction: Workflow<SourceMenuContext> = {
  id: 'choose-source-action',
  title: 'Choose the Suno action',
  feature: 'generate',
  startsOn: sunoPage('song'),
  needs: [{ step: 'action', check: (page) => present(page, MORE_OPTIONS) }],
  steps: [
    {
      name: 'action',
      expect: ({ page, route }) => present(page, actionItem(route)),
      act: ({ page, route }) => {
        const item = page.find(actionItem(route));
        if (item.kind === 'found') {
          page.click(item.found);
        }
      },
      verify: () => OK,
    },
  ],
  fixtures: ['clip-remix-menu'],
};

/**
 * Suno asks "Overwrite Lyrics & Styles?" when the form already has lyrics and styles: Overwrite,
 * so that the source loads; the Version's own values are filled over it afterwards (TS-003).
 */
export const answerOverwrite: Workflow = {
  id: 'answer-overwrite',
  title: 'Answer Suno’s Overwrite question',
  feature: 'generate',
  startsOn: sunoPage('create'),
  after: 'choose-source-action',
  needs: [{ step: 'Overwrite', check: (page) => present(page, OVERWRITE_BUTTON) }],
  steps: [
    {
      name: 'Overwrite',
      expect: ({ page }) => present(page, OVERWRITE_BUTTON),
      act: ({ page }) => {
        const button = page.find(OVERWRITE_BUTTON);
        if (button.kind === 'found') {
          page.click(button.found);
        }
      },
      verify: () => OK,
    },
  ],
  fixtures: ['overwrite-lyrics-styles-dialog'],
};

/** Verifies the loaded source in Advanced mode: the Audio section's action and thumbnail. */
export const verifySourceAdvanced: Workflow<VerifySourceContext> = {
  id: 'verify-source-advanced',
  title: 'Verify the source (Advanced)',
  feature: 'generate',
  startsOn: sunoPage('create'),
  after: 'choose-source-action',
  needs: [{ step: 'source shown', check: (page) => present(page, AUDIO_CONDITION) }],
  steps: [
    {
      name: 'source shown',
      expect: ({ page, load }) => sourceShown(page, 'advanced', load),
      act: () => undefined,
      verify: () => OK,
    },
  ],
  fixtures: ['create-source-advanced'],
};

/** Verifies the loaded source in Simple mode: the chip's thumbnail. */
export const verifySourceSimple: Workflow<VerifySourceContext> = {
  id: 'verify-source-simple',
  title: 'Verify the source (Simple)',
  feature: 'generate',
  startsOn: sunoPage('create'),
  after: 'choose-source-action',
  needs: [{ step: 'source shown', check: (page) => present(page, SIMPLE_CHIP_THUMBNAIL) }],
  steps: [
    {
      name: 'source shown',
      expect: ({ page, load }) => sourceShown(page, 'simple', load),
      act: () => undefined,
      verify: () => OK,
    },
  ],
  fixtures: ['create-source-simple'],
};
