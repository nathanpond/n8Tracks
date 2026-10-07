import type { Region, Target, TextAnchor } from './primitives.ts';
import { KIND_TABS } from './songsForm.ts';

/**
 * Suno's Create form for Speech, as the TS-003 page snapshots show it
 * (`page.create-speech-simple.html`, `page.create-speech-advanced.html`). Speech shares the Simple
 * and Advanced tabs with Songs (`songsForm.ts`). Its Advanced mode has three sections, Script,
 * Tone, and Advanced, and the Advanced section's buttons are named only "Male", "Female", "Off",
 * and "On", so each is found beside its label inside that section, no further out than the
 * snapshot shows it. The snapshots also hold the Songs and Sounds forms (shown live only on their
 * tabs), which is why the Advanced section is told from Sounds' "Advanced Options".
 */

export const SPEECH_TAB: Target = {
  role: 'tab',
  name: 'Speech',
  within: KIND_TABS,
  description: 'the Speech tab',
};

/** Simple mode's one text box: the Speech description (its label is "Prompt"). */
export const SPEECH_PROMPT: Target = {
  role: 'textbox',
  name: 'Prompt',
  description: 'the Speech description box',
};

/** Advanced mode's collapsible sections, by their headers. */
export const SCRIPT_SECTION: Target = {
  role: 'button',
  name: /^Script\b/,
  description: 'the Script section header',
};

export const TONE_SECTION: Target = {
  role: 'button',
  name: /^Tone\b/,
  description: 'the Tone section header',
};

/** The Advanced section header; Sounds' "Advanced Options" header is another section. */
export const SPEECH_ADVANCED_SECTION: Target = {
  role: 'button',
  name: /^Advanced\b(?! Options\b)/,
  description: 'the Advanced section header of the Speech form',
};

export const SCRIPT_BOX: Target = {
  role: 'textbox',
  name: 'Script',
  description: 'the Script box',
};

export const TONE_BOX: Target = {
  role: 'textbox',
  name: 'Tone',
  description: 'the Tone box',
};

const ADVANCED: Region = {
  around: SPEECH_ADVANCED_SECTION,
  levels: 2,
  description: 'the Advanced section of the Speech form',
};

/** The region around the label `text`; both descriptions are written at the call (#344). */
function beside(text: string, labelDescription: string, description: string): Region {
  const label: TextAnchor = {
    text,
    within: ADVANCED,
    description: labelDescription,
  };
  return { around: label, levels: 2, description };
}

const VOCAL_GENDER = beside(
  'Vocal Gender',
  "the Vocal Gender label in the Speech form's Advanced section",
  "the Speech form's Vocal Gender choice",
);

export const SPEECH_VOCAL_MALE: Target = {
  role: 'button',
  name: 'Male',
  within: VOCAL_GENDER,
  description: "the Speech form's Vocal Gender Male button",
};

export const SPEECH_VOCAL_FEMALE: Target = {
  role: 'button',
  name: 'Female',
  within: VOCAL_GENDER,
  description: "the Speech form's Vocal Gender Female button",
};

const BACKGROUND_MUSIC = beside(
  'Background music',
  "the Background music label in the Speech form's Advanced section",
  "the Speech form's Background music switch",
);

export const BACKGROUND_MUSIC_OFF: Target = {
  role: 'button',
  name: 'Off',
  within: BACKGROUND_MUSIC,
  description: "the Speech form's Background music Off button",
};

export const BACKGROUND_MUSIC_ON: Target = {
  role: 'button',
  name: 'On',
  within: BACKGROUND_MUSIC,
  description: "the Speech form's Background music On button",
};

export const SPEECH_VARIETY_SLIDER: Target = {
  role: 'slider',
  name: 'Variety',
  within: ADVANCED,
  description: "the Variety slider in the Speech form's Advanced section",
};
