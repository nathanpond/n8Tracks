import { sunoPage } from '../addresses.ts';
import { modeTab } from '../fill.ts';
import type { Page, Target } from '../primitives.ts';
import {
  SCRIPT_SECTION,
  SPEECH_ADVANCED_SECTION,
  SPEECH_PROMPT,
  SPEECH_TAB,
  TONE_SECTION,
} from '../speechForm.ts';
import { OK, present, type Check, type Step, type Workflow } from '../workflow.ts';
import {
  expandedSection,
  fillStep,
  pressUnless,
  selected,
  type FillContext,
  type SwitchFormContext,
} from './fillSongs.ts';

/**
 * Filling Suno's Speech form from a generation request (#147, TS-003:
 * `page.create-speech-simple.html`, `page.create-speech-advanced.html`), as the Songs form is
 * filled (`fillSongs.ts`): the form is first made the Speech form in the Version's mode, then,
 * only when it looks as expected, every entry of the mode is filled and read back
 * (`adapter/fill.ts`). Speech has no sources. A tab or mode the form does not offer stops before
 * anything is filled, naming the step. Nothing here presses Create (invariant 4).
 */

/** The Speech form in `mode`: the Speech tab and the mode's tab both selected. */
function speechFormIn(page: Page, mode: string): Check {
  const speech = selected(page, SPEECH_TAB);
  return speech.ok ? selected(page, modeTab(mode)) : speech;
}

/** What a step does to open a section by its header when it is closed (named where it is used). */
function opening(header: Target): Omit<Step<FillContext>, 'name'> {
  return {
    expect: ({ page }) => present(page, header),
    act: ({ page }) => {
      pressUnless(page, header, (current) => expandedSection(current, header));
    },
    verify: ({ page }) => expandedSection(page, header),
  };
}

/** Selects the Speech tab, then the Version's mode tab, as the user would. */
export const switchSpeechForm: Workflow<SwitchFormContext> = {
  id: 'switch-speech-form',
  title: 'Open the Speech form',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [{ step: 'Speech tab', check: (page) => present(page, SPEECH_TAB) }],
  steps: [
    {
      name: 'Speech tab',
      expect: ({ page }) => present(page, SPEECH_TAB),
      act: ({ page }) => {
        pressUnless(page, SPEECH_TAB, (current) => selected(current, SPEECH_TAB));
      },
      verify: ({ page }) => selected(page, SPEECH_TAB),
    },
    {
      name: 'form mode',
      expect: ({ page, mode }) => present(page, modeTab(mode)),
      act: ({ page, mode }) => {
        pressUnless(page, modeTab(mode), (current) => selected(current, modeTab(mode)));
      },
      verify: ({ page, mode }) => selected(page, modeTab(mode)),
    },
  ],
  fixtures: ['create-speech-advanced'],
};

/** Fills Simple mode: its one entry, the Speech description. */
export const fillSpeechSimple: Workflow<FillContext> = {
  id: 'fill-speech-simple',
  title: 'Fill the Speech form (Simple)',
  feature: 'generate',
  startsOn: sunoPage('create'),
  after: 'switch-speech-form',
  needs: [{ step: 'Speech form', check: (page) => speechFormIn(page, 'simple') }],
  steps: [
    {
      name: 'Speech form',
      expect: ({ page }) => speechFormIn(page, 'simple'),
      act: () => undefined,
      verify: () => OK,
    },
    {
      name: 'Speech description',
      expect: ({ page }) => present(page, SPEECH_PROMPT),
      act: () => undefined,
      verify: () => OK,
    },
    fillStep(true),
  ],
  fixtures: ['create-speech-simple'],
};

/**
 * Fills Advanced mode: the Script, Tone, and Advanced sections are opened when closed, then every
 * entry is filled.
 */
export const fillSpeechAdvanced: Workflow<FillContext> = {
  id: 'fill-speech-advanced',
  title: 'Fill the Speech form (Advanced)',
  feature: 'generate',
  startsOn: sunoPage('create'),
  after: 'switch-speech-form',
  needs: [{ step: 'Speech form', check: (page) => speechFormIn(page, 'advanced') }],
  steps: [
    {
      name: 'Speech form',
      expect: ({ page }) => speechFormIn(page, 'advanced'),
      act: () => undefined,
      verify: () => OK,
    },
    { name: 'Script section', ...opening(SCRIPT_SECTION) },
    { name: 'Tone section', ...opening(TONE_SECTION) },
    { name: 'Advanced section', ...opening(SPEECH_ADVANCED_SECTION) },
    fillStep(true),
  ],
  fixtures: ['create-speech-advanced'],
};

/** Check again: every entry read without changing anything, on the Speech form in the mode. */
export const checkSpeechForm: Workflow<FillContext> = {
  id: 'check-speech-form',
  title: 'Check the Speech form',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [{ step: 'Speech form', check: (page) => present(page, SPEECH_TAB) }],
  steps: [
    {
      name: 'Speech form',
      expect: ({ page, job }) => speechFormIn(page, job.mode),
      act: () => undefined,
      verify: () => OK,
    },
    fillStep(false),
  ],
  fixtures: ['create-speech-advanced'],
};
