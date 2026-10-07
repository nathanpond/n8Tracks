import type { Region, Target, TextAnchor } from './primitives.ts';
import { KIND_TABS } from './songsForm.ts';

/**
 * Suno's Create form for Sounds, as the TS-003 page snapshot shows it
 * (`page.create-sounds-advanced-options.html`; the Speech snapshots show the same form at its
 * defaults: One-Shot, BPM empty, Key Any). Sounds has no Simple or Advanced tabs: the model button
 * sits beside the credits, then come the Sound section and the Advanced Options section, whose
 * Type buttons, BPM box, and Key button are each found beside their label inside that section.
 *
 * The Key picker is not here: no snapshot shows its popover open (its note buttons, Any,
 * Major/Minor, and Apply), so the adapter does not set Key or Key scale (decision D9, `fill.ts`).
 */

export const SOUNDS_TAB: Target = {
  role: 'tab',
  name: 'Sounds',
  within: KIND_TABS,
  description: 'the Sounds tab',
};

/** The credits button at the top of the form; its name changes with the credits left. */
const CREDITS: Target = {
  role: 'button',
  name: /^Credits remaining\b/,
  description: 'the credits button at the top of the Create form',
};

/**
 * The model button beside the credits (Sounds has no mode tabs); its name is the model's label. Not
 * pressed: the model entries are blocked on a capture of the menu it opens (#339).
 */
export const SOUNDS_MODEL_BUTTON: Target = {
  role: 'button',
  popup: 'menu',
  within: { around: CREDITS, levels: 3, description: 'the top of the Sounds form' },
  description: 'the model button at the top of the Sounds form',
};

/** The Sound section and Advanced Options section headers. */
export const SOUND_SECTION: Target = {
  role: 'button',
  name: /^Sound\b/,
  description: 'the Sound section header',
};

export const ADVANCED_OPTIONS_SECTION: Target = {
  role: 'button',
  name: /^Advanced Options\b/,
  description: 'the Advanced Options section header',
};

/** The Sound description box (named by its placeholder). */
export const SOUND_DESCRIPTION: Target = {
  role: 'textbox',
  name: 'Describe the sound you want',
  description: 'the Sound description box',
};

const ADVANCED_OPTIONS: Region = {
  around: ADVANCED_OPTIONS_SECTION,
  levels: 2,
  description: 'the Advanced Options section',
};

function beside(text: string, description: string): Region {
  const label: TextAnchor = {
    text,
    within: ADVANCED_OPTIONS,
    description: `the ${text} label in Advanced Options`,
  };
  return { around: label, levels: 2, description };
}

const TYPE = beside('Type', 'the Type choice in Advanced Options');

export const TYPE_ONE_SHOT: Target = {
  role: 'button',
  name: 'One-Shot',
  within: TYPE,
  description: 'the Type One-Shot button in Advanced Options',
};

export const TYPE_LOOP: Target = {
  role: 'button',
  name: 'Loop',
  within: TYPE,
  description: 'the Type Loop button in Advanced Options',
};

/** The BPM number box; empty shows its placeholder, Auto. */
export const BPM_BOX: Target = {
  role: 'spinbutton',
  within: beside('BPM', 'the BPM box in Advanced Options'),
  description: 'the BPM box in Advanced Options',
};
