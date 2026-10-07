import { sunoPage } from '../addresses.ts';
import { ADVANCED_OPTIONS_SECTION, SOUND_SECTION, SOUNDS_TAB } from '../soundsForm.ts';
import { OK, present, type Workflow } from '../workflow.ts';
import {
  expandedSection,
  fillStep,
  pressUnless,
  selected,
  type FillContext,
  type SwitchFormContext,
} from './fillSongs.ts';

/**
 * Filling Suno's Sounds form from a generation request (#147, TS-003:
 * `page.create-sounds-advanced-options.html`), as the Songs form is filled (`fillSongs.ts`). Sounds
 * has one form and no mode tabs: the Sounds tab is selected, the Sound and Advanced Options
 * sections are opened, and every entry is filled and read back (`adapter/fill.ts`). Key and Key
 * scale are left to the user (D9). A missing tab stops before anything is filled, naming the step.
 * Nothing here presses Create (invariant 4).
 */

/** Selects the Sounds tab, as the user would; Sounds has no mode, so the mode is not used. */
export const switchSoundsForm: Workflow<SwitchFormContext> = {
  id: 'switch-sounds-form',
  title: 'Open the Sounds form',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [{ step: 'Sounds tab', check: (page) => present(page, SOUNDS_TAB) }],
  steps: [
    {
      name: 'Sounds tab',
      expect: ({ page }) => present(page, SOUNDS_TAB),
      act: ({ page }) => {
        pressUnless(page, SOUNDS_TAB, (current) => selected(current, SOUNDS_TAB));
      },
      verify: ({ page }) => selected(page, SOUNDS_TAB),
    },
  ],
  fixtures: ['create-sounds-advanced-options'],
};

/** Fills the Sounds form: its two sections opened when closed, then every entry. */
export const fillSounds: Workflow<FillContext> = {
  id: 'fill-sounds',
  title: 'Fill the Sounds form',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [{ step: 'Sounds form', check: (page) => selected(page, SOUNDS_TAB) }],
  steps: [
    {
      name: 'Sounds form',
      expect: ({ page }) => selected(page, SOUNDS_TAB),
      act: () => undefined,
      verify: () => OK,
    },
    {
      name: 'Sound section',
      expect: ({ page }) => present(page, SOUND_SECTION),
      act: ({ page }) => {
        pressUnless(page, SOUND_SECTION, (current) => expandedSection(current, SOUND_SECTION));
      },
      verify: ({ page }) => expandedSection(page, SOUND_SECTION),
    },
    {
      name: 'Advanced Options',
      expect: ({ page }) => present(page, ADVANCED_OPTIONS_SECTION),
      act: ({ page }) => {
        pressUnless(page, ADVANCED_OPTIONS_SECTION, (current) =>
          expandedSection(current, ADVANCED_OPTIONS_SECTION),
        );
      },
      verify: ({ page }) => expandedSection(page, ADVANCED_OPTIONS_SECTION),
    },
    fillStep(true),
  ],
  fixtures: ['create-sounds-advanced-options'],
};

/** Check again: every entry read without changing anything, on the Sounds form. */
export const checkSoundsForm: Workflow<FillContext> = {
  id: 'check-sounds-form',
  title: 'Check the Sounds form',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [{ step: 'Sounds form', check: (page) => present(page, SOUNDS_TAB) }],
  steps: [
    {
      name: 'Sounds form',
      expect: ({ page }) => selected(page, SOUNDS_TAB),
      act: () => undefined,
      verify: () => OK,
    },
    fillStep(false),
  ],
  fixtures: ['create-sounds-advanced-options'],
};
