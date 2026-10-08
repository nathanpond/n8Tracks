import { sunoPage } from '../addresses.ts';
import type { Page, Target } from '../primitives.ts';
import {
  actionItem,
  ADD_VOICE,
  AUDIO_CONDITION,
  chosenVoice,
  EXTEND_FROM,
  extendSeconds,
  extendTime,
  MORE_OPTIONS,
  OVERWRITE_BUTTON,
  SIMPLE_CHIP_THUMBNAIL,
  sourceShown,
  submenu,
  submenuItem,
  VOICE_DIALOG,
  voiceTitle,
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

/** The clip's page with its More options button, or its menu already open (Remix or Edit). */
function clipMenuReachable(page: Page): Check {
  if (
    page.find(submenuItem('Remix')).kind === 'found' ||
    page.find(submenuItem('Edit')).kind === 'found'
  ) {
    return OK;
  }
  return present(page, MORE_OPTIONS);
}

/** Opens the source clip's menu and the submenu that holds the action, and checks it is offered. */
export const openSourceMenu: Workflow<SourceMenuContext> = {
  id: 'open-source-menu',
  title: 'Open the source clip’s menu',
  feature: 'generate',
  startsOn: sunoPage('song'),
  needs: [{ step: 'clip menu', check: clipMenuReachable }],
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
  fixtures: ['clip-page-remix-menu', 'clip-remix-menu'],
};

/** Chooses the action in the open submenu; Suno then opens the Create form with the source. */
export const chooseSourceAction: Workflow<SourceMenuContext> = {
  id: 'choose-source-action',
  title: 'Choose the Suno action',
  feature: 'generate',
  startsOn: sunoPage('song'),
  needs: [{ step: 'action', check: clipMenuReachable }],
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
  fixtures: ['clip-page-remix-menu', 'clip-remix-menu'],
};

/**
 * Suno asks "Overwrite Lyrics & Styles?" when the form already has lyrics and styles, or
 * "Overwrite Styles?" for an instrumental source (TS-005): Overwrite, so that the source loads; the
 * Version's own values are filled over it afterwards (TS-003).
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
  fixtures: ['overwrite-lyrics-styles-dialog', 'overwrite-styles-dialog'],
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

/** Setting an Extend's continue-at time: the seconds the Version continues from. */
export interface ExtendFromContext extends StepContext {
  seconds: number;
}

/** Whether the "Extend from" time shows `seconds` (to the tenth Suno shows). */
function extendsFrom(page: Page, seconds: number): Check {
  const box = page.find(EXTEND_FROM);
  if (box.kind !== 'found') {
    return expected(EXTEND_FROM.description);
  }
  const shown = extendSeconds(page.read(box.found).value ?? '');
  return shown !== null && Math.abs(shown - seconds) < 0.05
    ? OK
    : expected('the “Extend from” time to show where the Version continues from');
}

/**
 * Sets where an Extend continues from (#148, TS-005: `page.create-source-extend.html`): the
 * "Extend from" time is typed as Suno shows it ("00:54.0") and read back within one second.
 */
export const setExtendFrom: Workflow<ExtendFromContext> = {
  id: 'set-extend-from',
  title: 'Set where the Extend continues from',
  feature: 'generate',
  startsOn: sunoPage('create'),
  after: 'verify-source-advanced',
  needs: [{ step: 'extend from', check: (page) => present(page, EXTEND_FROM) }],
  steps: [
    {
      name: 'extend from',
      expect: ({ page }) => present(page, EXTEND_FROM),
      act: ({ page, seconds }) => {
        if (extendsFrom(page, seconds).ok) {
          return;
        }
        const box = page.find(EXTEND_FROM);
        if (box.kind === 'found') {
          page.typeText(box.found, extendTime(seconds));
        }
      },
      verify: ({ page, seconds }) => extendsFrom(page, seconds),
      timeoutMs: 1_000,
    },
  ],
  fixtures: ['create-source-extend'],
};

/** Choosing the Version's voice: its name and its persona's ID. */
export interface ChooseVoiceContext extends StepContext {
  voice: { name: string; personaId: string };
}

const VOICE_CLOSE: Target = {
  role: 'button',
  name: 'Close',
  within: VOICE_DIALOG,
  description: 'the Voice picker’s Close button',
};

function voiceChosen(page: Page, personaId: string): boolean {
  return page.find(chosenVoice(personaId)).kind === 'found';
}

/**
 * Chooses the Version's voice (#148, TS-005): "+ Voice" opens the picker, and the voice is chosen by
 * pressing its title (pressing its image plays a sample), only when exactly one voice in the list has
 * that name; the form then shows it as a link to `/voice/<persona ID>`, which is how it is verified.
 * A voice already chosen on the form is left as it is.
 */
export const chooseVoice: Workflow<ChooseVoiceContext> = {
  id: 'choose-voice',
  title: 'Choose the Voice',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [
    {
      step: 'voice picker',
      check: (page) => (page.find(VOICE_DIALOG).kind === 'found' ? OK : present(page, ADD_VOICE)),
    },
  ],
  steps: [
    {
      name: 'voice picker',
      expect: ({ page, voice }) =>
        voiceChosen(page, voice.personaId) || page.find(VOICE_DIALOG).kind === 'found'
          ? OK
          : present(page, ADD_VOICE),
      act: ({ page, voice }) => {
        if (voiceChosen(page, voice.personaId) || page.find(VOICE_DIALOG).kind === 'found') {
          return;
        }
        const button = page.find(ADD_VOICE);
        if (button.kind === 'found') {
          page.click(button.found);
        }
      },
      verify: ({ page, voice }) =>
        voiceChosen(page, voice.personaId) || page.find(VOICE_DIALOG).kind === 'found'
          ? OK
          : expected(VOICE_DIALOG.description),
    },
    {
      name: 'voice',
      expect: ({ page, voice }) =>
        voiceChosen(page, voice.personaId) ? OK : present(page, voiceTitle(voice.name)),
      act: ({ page, voice }) => {
        if (voiceChosen(page, voice.personaId)) {
          return;
        }
        const title = page.find(voiceTitle(voice.name));
        if (title.kind === 'found') {
          page.click(title.found);
        }
      },
      verify: ({ page, voice }) =>
        voiceChosen(page, voice.personaId)
          ? OK
          : expected('the form to show the Version’s voice as chosen'),
    },
    {
      // Suno closes the picker on a choice (TS-005); should it stay open, its own Close closes it.
      name: 'picker closed',
      expect: () => OK,
      act: ({ page }) => {
        if (page.find(VOICE_DIALOG).kind !== 'found') {
          return;
        }
        const close = page.find(VOICE_CLOSE);
        if (close.kind === 'found') {
          page.click(close.found);
        }
      },
      verify: ({ page }) =>
        page.find(VOICE_DIALOG).kind === 'found' ? expected('the Voice picker, closed') : OK,
    },
  ],
  fixtures: ['create-voice-selected'],
};

/**
 * Closes the Voice picker by its own Close when a voice could not be chosen in it: while it is open,
 * Suno hides the rest of the form (`aria-hidden`, TS-005), so nothing else could be filled.
 */
export const closeVoicePicker: Workflow = {
  id: 'close-voice-picker',
  title: 'Close the Voice picker',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [
    {
      step: 'picker closed',
      check: (page) => (page.find(VOICE_DIALOG).kind === 'found' ? present(page, VOICE_CLOSE) : OK),
    },
  ],
  steps: [
    {
      name: 'picker closed',
      expect: () => OK,
      act: ({ page }) => {
        if (page.find(VOICE_DIALOG).kind !== 'found') {
          return;
        }
        const close = page.find(VOICE_CLOSE);
        if (close.kind === 'found') {
          page.click(close.found);
        }
      },
      verify: ({ page }) =>
        page.find(VOICE_DIALOG).kind === 'found' ? expected('the Voice picker, closed') : OK,
    },
  ],
  fixtures: ['create-voice-selected'],
};
