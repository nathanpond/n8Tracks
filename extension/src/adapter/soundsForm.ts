import type { Region, Target, TextAnchor } from './primitives.ts';
import { KIND_TABS } from './songsForm.ts';

/**
 * Suno's Create form for Sounds, as the TS-003 page snapshot shows it
 * (`page.create-sounds-advanced-options.html`; the Speech snapshots show the same form at its
 * defaults: One-Shot, BPM empty, Key Any). Sounds has no Simple or Advanced tabs: the model button
 * sits beside the credits, then come the Sound section and the Advanced Options section, whose
 * Type buttons, BPM box, and Key button are each found beside their label inside that section.
 *
 * The Key picker (TS-005, `page.create-sounds-key-*.html`): the Key button, named by what it shows
 * ("Any", "F# min"), opens an untitled popover of note buttons (C to B with sharps), Any, Major and
 * Minor tabs, and Apply; the key is set when Apply is pressed, and the button then shows it.
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
 * The model button beside the credits (Sounds has no mode tabs); its name is the model's label. It
 * is the same button, and opens the same menu, as on the Songs tab (TS-006, `modelMenu`).
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

/** The region around the label `text`; both descriptions are written at the call (#344). */
function beside(text: string, labelDescription: string, description: string): Region {
  const label: TextAnchor = {
    text,
    within: ADVANCED_OPTIONS,
    description: labelDescription,
  };
  return { around: label, levels: 2, description };
}

const TYPE = beside(
  'Type',
  'the Type label in Advanced Options',
  'the Type choice in Advanced Options',
);

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
  within: beside('BPM', 'the BPM label in Advanced Options', 'the BPM box in Advanced Options'),
  description: 'the BPM box in Advanced Options',
};

/** The Key button, beside its label; it opens the Key popover (`aria-haspopup="dialog"`). */
export const KEY_BUTTON: Target = {
  role: 'button',
  popup: 'dialog',
  within: beside('Key', 'the Key label in Advanced Options', 'the Key choice in Advanced Options'),
  description: 'the Key button in Advanced Options',
};

/** The Key popover: an untitled dialog (the forbidden-control matcher knows it by its controls). */
export const KEY_POPOVER: Target = {
  role: 'dialog',
  name: '',
  description: 'the Key popover',
};

/** A note button (C, C#, … B) or Any, in the Key popover. */
export function keyChoice(note: string): Target {
  return {
    role: 'button',
    name: note,
    within: KEY_POPOVER,
    description: note === 'Any' ? 'Any in the Key popover' : 'a note button in the Key popover',
  };
}

/** The Major or Minor tab in the Key popover. */
export function scaleTab(scale: 'Major' | 'Minor'): Target {
  return {
    role: 'tab',
    name: scale,
    within: KEY_POPOVER,
    description:
      scale === 'Major' ? 'the Major tab in the Key popover' : 'the Minor tab in the Key popover',
  };
}

export const KEY_APPLY: Target = {
  role: 'button',
  name: 'Apply',
  within: KEY_POPOVER,
  description: 'the Key popover’s Apply button',
};
