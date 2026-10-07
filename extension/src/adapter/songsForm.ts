import type { Region, Target, TextAnchor } from './primitives.ts';

/**
 * Suno's Create form for Songs, as the TS-003 page snapshots show it
 * (`page.create-songs-advanced-more-options.html`, `page.create-source-advanced.html`,
 * `page.create-source-simple.html`). Suno gives most of these controls no test attribute, and the
 * More Options buttons no name of their own beyond "Off", "On", "Male", and "Female", so each is
 * found by role and name inside the part of the form around a label it sits beside, no further
 * out than the snapshot shows it (so a control that has gone is never found elsewhere). The snapshots
 * also hold the Speech and Sounds forms (shown live only on their tabs), which is why every More
 * Options control is looked for inside More Options.
 */

/** The tabs above the form: Songs, Speech, Sounds. */
export const KIND_TABS: Target = {
  role: 'tablist',
  name: 'What to create',
  description: 'the "What to create" tabs (Songs, Speech, Sounds)',
};

export const SONGS_TAB: Target = {
  role: 'tab',
  name: 'Songs',
  within: KIND_TABS,
  description: 'the Songs tab',
};

/** The Simple and Advanced tabs. */
export const MODE_TABS: Target = {
  role: 'tablist',
  name: 'Create form mode',
  description: 'the "Create form mode" tabs (Simple, Advanced)',
};

export const SIMPLE_TAB: Target = {
  role: 'tab',
  name: 'Simple',
  within: MODE_TABS,
  description: 'the Simple tab',
};

export const ADVANCED_TAB: Target = {
  role: 'tab',
  name: 'Advanced',
  within: MODE_TABS,
  description: 'the Advanced tab',
};

/**
 * The model menu button beside the mode tabs; its name is the chosen model's label ("v6-mini").
 * The snapshots show it closed only, so the model entries are blocked on a capture (#339) and
 * nothing presses it; there is no target for the menu it opens until a snapshot shows it.
 */
export const MODEL_BUTTON: Target = {
  role: 'button',
  popup: 'menu',
  within: { around: MODE_TABS, levels: 3, description: 'the top of the Create form' },
  description: 'the model button beside the Simple and Advanced tabs',
};

/** Simple mode's "+" button in the prompt box, which opens the Add menu. */
export const ADD_BUTTON: Target = {
  role: 'button',
  name: 'Add',
  popup: 'menu',
  description: 'the "+" (Add) button in the Song description box',
};

/** Simple mode's Song description: the one text box in the prompt box beside the Add button. */
export const SONG_DESCRIPTION: Target = {
  role: 'textbox',
  within: { around: ADD_BUTTON, levels: 2, description: 'the Song description box' },
  description: 'the Song description box',
};

/** Advanced mode's collapsible sections, by their headers. */
export const LYRICS_SECTION: Target = {
  role: 'button',
  name: /^Lyrics\b/,
  description: 'the Lyrics section header',
};

export const STYLES_SECTION: Target = {
  role: 'button',
  name: /^Styles\b/,
  description: 'the Styles section header',
};

export const MORE_OPTIONS_SECTION: Target = {
  role: 'button',
  name: /^More Options\b/,
  description: 'the More Options section header',
};

/** The Lexical lyrics editor (contenteditable). */
export const LYRICS_EDITOR: Target = {
  role: 'textbox',
  name: 'Lyrics editor',
  description: 'the Lyrics editor',
};

/** The Styles text box; its placeholder (its name) is Suno's changing suggestions. */
export const STYLES_BOX: Target = {
  role: 'textbox',
  within: { around: STYLES_SECTION, levels: 2, description: 'the Styles section' },
  description: 'the Styles text box',
};

const MORE_OPTIONS: Region = {
  around: MORE_OPTIONS_SECTION,
  levels: 2,
  description: 'the More Options section',
};

function label(text: string, description: string): TextAnchor {
  return { text, within: MORE_OPTIONS, description };
}

function beside(anchor: TextAnchor, description: string): Region {
  return { around: anchor, levels: 2, description };
}

export const EXCLUDE_STYLES: Target = {
  role: 'textbox',
  name: 'Exclude styles',
  within: MORE_OPTIONS,
  description: 'the Exclude styles box in More Options',
};

const VOCAL_GENDER = beside(
  label('Vocal Gender', 'the Vocal Gender label in More Options'),
  'the Vocal Gender choice in More Options',
);

export const VOCAL_MALE: Target = {
  role: 'button',
  name: 'Male',
  within: VOCAL_GENDER,
  description: 'the Vocal Gender Male button in More Options',
};

export const VOCAL_FEMALE: Target = {
  role: 'button',
  name: 'Female',
  within: VOCAL_GENDER,
  description: 'the Vocal Gender Female button in More Options',
};

export const DURATION_SLIDER: Target = {
  role: 'slider',
  name: 'Duration',
  within: MORE_OPTIONS,
  description: 'the Duration slider in More Options',
};

const MAX_MODE = beside(
  label('Max Mode', 'the Max Mode label in More Options'),
  'the Max Mode switch in More Options',
);

export const MAX_MODE_OFF: Target = {
  role: 'button',
  name: 'Off',
  within: MAX_MODE,
  description: 'the Max Mode Off button in More Options',
};

export const MAX_MODE_ON: Target = {
  role: 'button',
  name: 'On',
  within: MAX_MODE,
  description: 'the Max Mode On button in More Options',
};

export const WEIRDNESS_SLIDER: Target = {
  role: 'slider',
  name: 'Weirdness',
  within: MORE_OPTIONS,
  description: 'the Weirdness slider in More Options',
};

export const STYLE_INFLUENCE_SLIDER: Target = {
  role: 'slider',
  name: 'Style Influence',
  within: MORE_OPTIONS,
  description: 'the Style Influence slider in More Options',
};

export const VARIETY_SLIDER: Target = {
  role: 'slider',
  name: 'Variety',
  within: MORE_OPTIONS,
  description: 'the Variety slider in More Options',
};

const PERSONALIZE = beside(
  label('Personalize', 'the Personalize label in More Options'),
  'the Personalize switch in More Options',
);

export const PERSONALIZE_OFF: Target = {
  role: 'button',
  name: 'Off',
  within: PERSONALIZE,
  description: 'the Personalize Off button in More Options',
};

export const PERSONALIZE_ON: Target = {
  role: 'button',
  name: 'On',
  within: PERSONALIZE,
  description: 'the Personalize On button in More Options',
};

const SONG_TITLE_NAME = 'Song Title (Optional)';

/** The Advanced form's "Add audio" button, above the source boxes. */
export const ADD_AUDIO: Target = {
  role: 'button',
  name: 'Add audio - Browse, upload, or record audio',
  description: 'the "Add audio" button',
};

/**
 * The two Song Title boxes of the Advanced form: one above the Audio, Voice, and Inspo buttons,
 * one beside the workspace ("Save to..."). TS-003 found the title shared between them, and the
 * snapshot shows both; each is found by its own place, so neither is chosen over the other, and
 * each one shown is set.
 */
export const SONG_TITLE_BOXES: readonly Target[] = [
  {
    role: 'textbox',
    name: SONG_TITLE_NAME,
    within: { around: ADD_AUDIO, levels: 4, description: 'the top of the Advanced form' },
    description: 'the Song Title box above the Audio button',
  },
  {
    role: 'textbox',
    name: SONG_TITLE_NAME,
    within: {
      around: { text: 'Save to...', description: 'the "Save to..." label' },
      levels: 2,
      description: 'the foot of the Advanced form',
    },
    description: 'the Song Title box beside "Save to..."',
  },
];
