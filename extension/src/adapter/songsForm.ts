import { SAVE_TO_PICKER } from './forbidden.ts';
import type { Page, Region, Target, TextAnchor } from './primitives.ts';

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
 * The model button beside the mode tabs; its text is the chosen model's label ("v6"). Since
 * 2026-10-11 it opens a dialog (TS-007-10, #427: `aria-haspopup="dialog"`, four parent levels above
 * the Advanced tab, `page.create-songs-advanced-model-dialog.html`). It is looked for up to four
 * levels out from the mode tabs; the nearest level that holds such a button is the one used.
 */
export const MODEL_BUTTON: Target = {
  role: 'button',
  popup: 'dialog',
  within: { around: MODE_TABS, levels: 4, description: 'the top of the Create form' },
  description: 'the model button beside the Simple and Advanced tabs',
};

/**
 * The model button as TS-006 captured it (2026-10-08): a menu button three levels out from the mode
 * tabs (`page.create-songs-model-menu.html`). Kept as the second shape the model filler looks for,
 * in case Suno shows the menu again (TS-007-10).
 */
export const MODEL_MENU_BUTTON: Target = {
  role: 'button',
  popup: 'menu',
  within: { around: MODE_TABS, levels: 3, description: 'the top of the Create form' },
  description: 'the model button beside the Simple and Advanced tabs',
};

/**
 * The dialog the model button opens (TS-007-10): `role="dialog"` labelled by its heading, "Model".
 * It holds a grid of rows: "Create Custom Model" (Beta, spends credits, never pressed), an "All
 * Models" header, and one row per model, its first text the model's name, `aria-selected` on the
 * chosen one. The rows hold no buttons.
 */
export const MODEL_DIALOG: Target = {
  role: 'dialog',
  name: 'Model',
  description: 'Suno’s Model dialog',
};

/** Every row of the Model dialog, for saying which models it lists. */
export const MODEL_ROWS: Target = {
  role: 'row',
  within: MODEL_DIALOG,
  description: 'the rows of Suno’s Model dialog',
};

/**
 * A model's row in the Model dialog (TS-007-10): the row whose lead text (its first text, the
 * model's name) is `model`, compared without regard to case. "v6" is not "v6-wild", and the
 * "Create Custom Model" row's lead text is never a model's.
 */
export function modelRow(model: string): Target {
  return {
    role: 'row',
    leadText: model,
    within: MODEL_DIALOG,
    description: 'the model’s row in Suno’s Model dialog',
  };
}

/**
 * The menu the TS-006 model button opens: `role="menu"` labelled by the button, so its name is
 * the model the button shows (`shown`). It holds "Create Custom Model" (a `menuitem` that spends
 * credits, never pressed) and the models, each a `menuitemradio` with `aria-checked`.
 */
export function modelMenu(shown: string): Target {
  return { role: 'menu', name: shown, description: 'Suno’s model menu' };
}

function escaped(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * A model in the model menu (TS-006). Only a `menuitemradio` matches, so "Create Custom Model" (a
 * `menuitem`) never can. Its name runs the model's label into its badge and description ("v6 Pro
 * Powerful. …"), so the label must be the name's first word: "v6" does not match "v6-wild Pro …".
 */
export function modelItem(model: string, shown: string): Target {
  return {
    role: 'menuitemradio',
    name: new RegExp(`^${escaped(model)}(?: |$)`, 'i'),
    within: modelMenu(shown),
    description: 'the model in Suno’s model menu',
  };
}

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

/**
 * Simple mode's added sections (TS-005, `page.create-songs-simple-*.html`). The "+" button opens
 * the Add menu (Lyrics, Styles, Playlist, Image, Video, Audio, Voice); Lyrics and Styles each open
 * a submenu, "Write new" or "Use existing"; "Write new" opens a dialog titled Lyrics or Styles with
 * the text box, closed by its Close button. The section then shows as a chip above the Song
 * description: its text (whole, in the chip's title) and a "Remove <text>" button. The chip does
 * not say whether it is the lyrics or the styles, so a section is known by its text.
 */
export type SimpleSection = 'Lyrics' | 'Styles';

/**
 * The Add menu's Lyrics or Styles item, which opens its submenu. The menu itself has no name the
 * primitives read (it is labelled by the "+" button, whose only name is its `aria-label`), so the
 * item is known by its name and its submenu; no other menu on the Create form has one.
 */
export function addMenuItem(section: SimpleSection): Target {
  return {
    role: 'menuitem',
    name: section,
    popup: 'menu',
    description:
      section === 'Lyrics' ? 'the Lyrics item of the Add menu' : 'the Styles item of the Add menu',
  };
}

/** The submenu that item opens, named by the item. */
export function sectionMenu(section: SimpleSection): Target {
  return {
    role: 'menu',
    name: section,
    description:
      section === 'Lyrics' ? 'the Add menu’s Lyrics submenu' : 'the Add menu’s Styles submenu',
  };
}

/** "Write new" in that submenu ("Use existing" picks from the user's saved lyrics or styles). */
export function writeNewItem(section: SimpleSection): Target {
  return {
    role: 'menuitem',
    name: 'Write new',
    within: sectionMenu(section),
    description:
      section === 'Lyrics' ? 'Write new in the Lyrics submenu' : 'Write new in the Styles submenu',
  };
}

/** The dialog "Write new" opens, titled after its section. */
export function sectionDialog(section: SimpleSection): Target {
  return {
    role: 'dialog',
    name: section,
    description: section === 'Lyrics' ? 'the Lyrics dialog' : 'the Styles dialog',
  };
}

/**
 * The dialog's text box: Lyrics has the Lexical editor (and a Cowriter prompt beside it); Styles has
 * one text area.
 */
export function sectionEditor(section: SimpleSection): Target {
  return section === 'Lyrics'
    ? {
        role: 'textbox',
        name: 'Lyrics editor',
        within: sectionDialog('Lyrics'),
        description: 'the Lyrics editor in the Lyrics dialog',
      }
    : {
        role: 'textbox',
        within: sectionDialog('Styles'),
        description: 'the Styles box in the Styles dialog',
      };
}

/**
 * The dialog's own Close button, beside its title (the Lyrics dialog's Cowriter has a Close too).
 */
export function sectionDialogClose(section: SimpleSection): Target {
  return {
    role: 'button',
    name: 'Close',
    within: {
      around: {
        text: section,
        within: sectionDialog(section),
        description:
          section === 'Lyrics' ? 'the Lyrics dialog’s title' : 'the Styles dialog’s title',
      },
      levels: 1,
      description:
        section === 'Lyrics' ? 'the top of the Lyrics dialog' : 'the top of the Styles dialog',
    },
    description:
      section === 'Lyrics'
        ? 'the Lyrics dialog’s Close button'
        : 'the Styles dialog’s Close button',
  };
}

/** The chips above the Song description: each one's "Remove …" button, named after its text. */
export const SECTION_CHIP_REMOVE: Target = {
  role: 'button',
  name: /^Remove /,
  within: SONG_DESCRIPTION.within,
  description: 'a chip’s Remove button above the Song description',
};

/** A source chip's thumbnail in the same row: a Simple source chip has one, a section chip none. */
export const CHIP_THUMBNAIL: Target = {
  role: 'img',
  within: SONG_DESCRIPTION.within,
  description: 'a source chip’s thumbnail above the Song description',
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

const DURATION = beside(
  label('Duration', 'the Duration label in More Options'),
  'the Duration choice in More Options',
);

/**
 * Duration on Auto shows two buttons, Custom and Auto (TS-005,
 * `page.create-songs-advanced-duration-auto.html`); Custom replaces them with the slider and a text
 * box (`…-duration-custom.html`), and the way back to Auto is an icon beside the label that has no
 * role or name.
 */
export const DURATION_CUSTOM: Target = {
  role: 'button',
  name: 'Custom',
  within: DURATION,
  description: 'the Duration Custom button in More Options',
};

export const DURATION_AUTO: Target = {
  role: 'button',
  name: 'Auto',
  within: DURATION,
  description: 'the Duration Auto button in More Options',
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

/** The "Save to..." label at the foot of the Advanced form, beside the workspace the Song goes to. */
const SAVE_TO_LABEL: TextAnchor = { text: 'Save to...', description: 'the "Save to..." label' };

/**
 * The Save to button (TS-003, `page.create-source-advanced.html`): beside "Save to...", it shows
 * the workspace the clips are saved to and opens the Save to picker (`aria-haspopup="dialog"`).
 * Loading a source may change it to the source's workspace (#435, the owner's check of
 * 2026-10-17); the Simple form's snapshots show no Save to.
 */
export const SAVE_TO_BUTTON: Target = {
  role: 'button',
  popup: 'dialog',
  within: { around: SAVE_TO_LABEL, levels: 2, description: 'the "Save to..." row' },
  description: 'the Save to button beside "Save to..."',
};

/**
 * The Save to picker's text box, "Search or create..." (#435), by which the picker is known to be
 * open. What it holds is typed only, never sent with Enter, which may create a workspace.
 */
export const SAVE_TO_SEARCH: Target = {
  role: 'textbox',
  name: SAVE_TO_PICKER.field,
  description: 'the "Search or create..." box of the Save to picker',
};

/** Where the picker's rows are looked for: outwards from its text box, within the picker. */
const SAVE_TO_ROWS: Region = {
  around: SAVE_TO_SEARCH,
  levels: 6,
  description: 'the Save to picker',
};

/**
 * A workspace's row in the Save to picker, "<name> (<N> clips)" (#435): the row whose name, less
 * that count, is `workspace` exactly, white space collapsed and case kept, since Suno's names may
 * differ by case alone. The row's role was not seen live; it is looked for as an option first,
 * then as a button ({@link SAVE_TO_PICKER}).
 */
export function saveToRow(workspace: string, role: 'option' | 'button'): Target {
  return {
    role,
    name: new RegExp(`^${escaped(collapsed(workspace))} \\(\\d[\\d,]* clips?\\)$`),
    within: SAVE_TO_ROWS,
    description: 'the Song’s workspace’s row in the Save to picker',
  };
}

function collapsed(text: string): string {
  return text.replace(/\s+/g, ' ').trim();
}

/** What the Save to button shows, white space collapsed; null when the form shows no Save to. */
export function saveToShown(page: Page): string | null {
  const button = page.find(SAVE_TO_BUTTON);
  return button.kind === 'found' ? page.read(button.found).text : null;
}

/** Whether the Save to button shows `workspace` (white space collapsed, case kept). */
export function saveToShows(page: Page, workspace: string): boolean {
  return saveToShown(page) === collapsed(workspace);
}

/**
 * The picker's rows named exactly `workspace`, and the role they have: options when there are any
 * (a row that is an option holding a button counts once), else buttons.
 */
export function saveToRows(
  page: Page,
  workspace: string,
): { role: 'option' | 'button'; count: number } {
  for (const role of SAVE_TO_PICKER.rowRoles) {
    const count = page.readAll(saveToRow(workspace, role)).length;
    if (count > 0) {
      return { role, count };
    }
  }
  return { role: 'button', count: 0 };
}
