/**
 * A stand-in for Suno's Create form behaviour over a TS-003 page snapshot (#146 tests). The
 * snapshots are static HTML, so nothing answers a click or a key; this does what TS-003 recorded
 * the live form doing, and nothing more:
 *
 * - a tab, pressed, becomes the selected one of its tab list;
 * - a section header (Lyrics, Styles, More Options), pressed, opens or closes its section;
 * - a choice button (`data-selected`, or `aria-pressed` on the Speech form), pressed, becomes the
 *   selected one of its group; pressed again, Vocal Gender's is deselected (None), while an Off/On
 *   pair or Sounds' Type keeps one selected;
 * - a slider moves one step per arrow key (Duration by 5 seconds), between its minimum and
 *   maximum; Home and End do nothing (TS-003);
 * - the Lexical lyrics editor takes the browser's editing commands (`execCommand`), which jsdom
 *   lacks: with everything selected, text replaces it; `insertParagraph` starts a new line.
 *
 * And what TS-005 recorded (each part's markup is taken from its sanitized snapshot):
 *
 * - Simple's "+" opens the Add menu; its Lyrics or Styles item opens the "Write new / Use existing"
 *   submenu; Write new opens the Lyrics or Styles dialog, empty; its Close closes it, and what it
 *   holds shows as a chip above the Song description (its text, and "Remove <text>");
 * - Duration's Custom replaces the Custom and Auto buttons with the slider at 3:00 and its box;
 * - the Sounds Key button opens the Key popover; a note or Any becomes the one chosen; Apply closes
 *   it and the button shows the key ("F# min"; a major key as "C maj", which TS-005 did not capture).
 *
 * And what TS-006 recorded (the menu's markup is taken from its sanitized snapshot; what follows a
 * choice was not captured, and is done here as Base UI's menus do it):
 *
 * - the model button opens the model menu, labelled by the button, the model it shows checked; the
 *   button pressed again closes it;
 * - a model (`menuitemradio`) pressed becomes the one checked, the button shows its label, and the
 *   menu closes. "Create Custom Model" does nothing here: the adapter must never reach it.
 *
 * And what #427 recorded live (2026-10-11, TS-007-9 and TS-007-10):
 *
 * - with `deferredSliders`, a slider applies an arrow key a task after it, from the value it had
 *   when the key came, so keys sent back to back count once (five keys moved 50 to 51);
 * - a model button that opens a dialog (`aria-haspopup="dialog"`) opens the Model dialog (markup
 *   from `page.create-songs-advanced-model-dialog.html`), the model it shows selected; pressed
 *   again it closes it, as Escape does; a model's row pressed becomes the one selected and the
 *   button shows its name, and the dialog closes unless `modelDialogStaysOpen` (what follows a
 *   choice was not seen live). The "Create Custom Model" row does nothing here.
 *
 * And what the owner saw of Create's Save to picker (#435, 2026-10-17; its markup is the constructed
 * `page.create-songs-advanced-save-to-picker.html`, and what follows a choice was not seen live):
 *
 * - the Save to button (`aria-haspopup="dialog"` beside "Save to...") opens the picker, its rows
 *   `saveToRows` when given; pressed again, or on Escape, the picker closes;
 * - typing into "Search or create..." shows only the rows whose name holds the text, case aside;
 * - a row pressed sets the button to its name, and the picker closes unless `saveToStaysOpen`.
 *   Its "+" does nothing here: the adapter must never reach it.
 *
 * And what the owner captured of the "Add a song to mashup" dialog (#438, TS-007-18, 2026-10-19;
 * its markup is `page.create-source-mashup-add-song-dialog.html`, built from that capture, and
 * what follows a choice was not seen live):
 *
 * - "Add another song to Mashup" opens the dialog, its rows `mashupRows` when given; its Close, or
 *   Escape, closes it;
 * - typing into its "Search clips" box shows only the rows whose title holds the text, case aside,
 *   and calls `onMashupSearch` with the text and the rows shown;
 * - a row's "Add <title> to mashup" (Select) adds that song's player to the Mashup's region, and
 *   the dialog closes unless `mashupDialogStaysOpen`. Its tabs, filter, sort, liked filter, the
 *   rows themselves, play, Edit title, and like do nothing here: the adapter must never reach them.
 *
 * Text boxes need no stand-in: the native value setter works in jsdom.
 */

import { snapshotHtml } from './snapshots.ts';

const STEP: Readonly<Record<string, number>> = { Duration: 5 };

export interface StandInOptions {
  /** Sliders apply a key a task later, from the value they had when it came (TS-007-9). */
  deferredSliders?: boolean;
  /** The Model dialog stays open after a model is chosen, until Escape (TS-007-10). */
  modelDialogStaysOpen?: boolean;
  /** The Save to picker's rows (#435), as "<name> (<N> clips)"; the snapshot's unless given. */
  saveToRows?: readonly { name: string; clips: number }[];
  /** The Save to picker stays open after a row is chosen, until Escape. */
  saveToStaysOpen?: boolean;
  /** The Mashup dialog's rows (#438), each a song's title and Suno ID; the snapshot's unless given. */
  mashupRows?: readonly MashupRow[];
  /** Called when the Mashup dialog's search box is typed into, with the rows it then shows. */
  onMashupSearch?: (query: string, shown: readonly MashupRow[]) => void;
  /** The Mashup dialog stays open after a song's Select, until Close or Escape. */
  mashupDialogStaysOpen?: boolean;
}

/** A song in the Mashup dialog's list (#438). */
export interface MashupRow {
  title: string;
  sunoId: string;
}

export interface StandIn {
  /** The commands the editor was given, for tests: `insertText`, `insertParagraph`, `delete`. */
  commands: string[];
  stop(): void;
}

function selectInGroup(button: Element): void {
  const group = button.parentElement;
  const attribute = button.hasAttribute('data-selected') ? 'data-selected' : 'aria-pressed';
  const selected = button.getAttribute(attribute) === 'true';
  const names = [...(group?.children ?? [])].map((sibling) => sibling.textContent.trim());
  const deselectable = names.includes('Male') && names.includes('Female');
  if (selected) {
    if (deselectable) {
      button.setAttribute(attribute, 'false');
    }
    return;
  }
  for (const sibling of group?.children ?? []) {
    if (sibling.hasAttribute(attribute)) {
      sibling.setAttribute(attribute, String(sibling === button));
    }
  }
}

function toggleSection(header: Element): void {
  const open = header.getAttribute('aria-expanded') !== 'true';
  header.setAttribute('aria-expanded', String(open));
  const content = header.parentElement?.nextElementSibling;
  if (content?.hasAttribute('aria-hidden') === true) {
    content.setAttribute('aria-hidden', String(!open));
  }
}

function moveSlider(slider: Element, key: string): void {
  const now = Number(slider.getAttribute('aria-valuenow'));
  const minimum = Number(slider.getAttribute('aria-valuemin'));
  const maximum = Number(slider.getAttribute('aria-valuemax'));
  const step = STEP[slider.getAttribute('aria-label') ?? ''] ?? 1;
  const next = key === 'ArrowRight' ? Math.min(maximum, now + step) : Math.max(minimum, now - step);
  slider.setAttribute('aria-valuenow', String(next));
}

function editorCommand(document: Document, command: string, value: string | undefined): boolean {
  const selection = document.getSelection();
  const anchor = selection?.anchorNode ?? null;
  const element = anchor instanceof Element ? anchor : (anchor?.parentElement ?? null);
  const editor = element?.closest('[contenteditable="true"]') ?? null;
  if (selection === null || editor === null) {
    return false;
  }
  if (!selection.isCollapsed) {
    editor.replaceChildren();
    selection.collapse(editor, 0);
  }
  if (command === 'delete') {
    return true;
  }
  const last = editor.lastElementChild ?? editor.appendChild(document.createElement('p'));
  if (command === 'insertParagraph') {
    editor.appendChild(document.createElement('p'));
    return true;
  }
  last.append(document.createTextNode(value ?? ''));
  return true;
}

/** The first element `selector` finds in a snapshot, as a fresh element of `document`. */
function fromSnapshot(document: Document, snapshot: string, selector: string, index = 0): Element {
  const template = document.createElement('template');
  template.innerHTML = snapshotHtml(snapshot);
  const found = template.content.querySelectorAll(selector)[index];
  if (found === undefined) {
    throw new Error(`no ${selector} in ${snapshot}`);
  }
  return document.importNode(found, true);
}

function idOf(element: Element, fallback: string): string {
  if (element.id === '') {
    element.id = fallback;
  }
  return element.id;
}

/** The text a Lyrics dialog's editor or a Styles dialog's text area holds. */
function dialogText(dialog: Element): string {
  const area = dialog.querySelector('textarea:not([aria-label="Cowriter prompt"])');
  if (area !== null) {
    return (area as HTMLTextAreaElement).value;
  }
  const editor = dialog.querySelector('[contenteditable="true"]');
  return [...(editor?.children ?? [])].map((line) => line.textContent).join('\n');
}

/** Simple's Add menu, its submenus, its Lyrics and Styles dialogs, and their chips (TS-005). */
function simpleSections(document: Document, target: Element): boolean {
  const add = target.closest('button[aria-label="Add"][aria-haspopup="menu"]');
  if (add !== null) {
    if (document.querySelector('[role="menu"]') === null) {
      const menu = fromSnapshot(document, 'create-songs-simple-add-menu', '[role="menu"]');
      menu.setAttribute('aria-labelledby', idOf(add, 'stand-in-add'));
      document.body.append(menu);
      add.setAttribute('aria-expanded', 'true');
    }
    return true;
  }
  const item = target.closest('[role="menuitem"][aria-haspopup="menu"]');
  const section = item?.textContent.trim();
  if (item !== null && (section === 'Lyrics' || section === 'Styles')) {
    const submenu = fromSnapshot(
      document,
      'create-songs-simple-lyrics-submenu',
      '[role="menu"]',
      1,
    );
    submenu.setAttribute('aria-labelledby', idOf(item, `stand-in-${section}`));
    document.body.append(submenu);
    item.setAttribute('aria-expanded', 'true');
    return true;
  }
  const write = target.closest('[role="menuitem"]');
  if (write !== null && write.textContent.trim() === 'Write new') {
    const labelledBy = write.closest('[role="menu"]')?.getAttribute('aria-labelledby') ?? '';
    const chosen = document.getElementById(labelledBy)?.textContent.trim();
    for (const menu of document.querySelectorAll('[role="menu"]')) {
      menu.remove();
    }
    const dialog = fromSnapshot(
      document,
      chosen === 'Styles'
        ? 'create-songs-simple-styles-dialog'
        : 'create-songs-simple-lyrics-dialog',
      '[role="dialog"]',
    );
    // Write new starts empty.
    for (const area of dialog.querySelectorAll('textarea')) {
      area.value = '';
      area.textContent = '';
    }
    dialog.querySelector('[contenteditable="true"]')?.replaceChildren();
    document.body.append(dialog);
    return true;
  }
  const close = target.closest('button[aria-label="Close"]');
  const dialog = close?.closest('[role="dialog"]');
  const title = dialog?.getAttribute('aria-label');
  if (dialog !== null && dialog !== undefined && (title === 'Lyrics' || title === 'Styles')) {
    const text = dialogText(dialog);
    dialog.remove();
    if (text.trim() !== '') {
      const box = document.querySelector('button[aria-label="Add"]')?.parentElement?.parentElement;
      let row = [...(box?.children ?? [])].find(
        (child) => child.querySelector('[data-thumb]') !== null,
      );
      if (row === undefined) {
        row = document.createElement('div');
        box?.prepend(row);
      }
      const chip = document.createElement('div');
      chip.innerHTML =
        '<span data-thumb="true"><span></span><span></span></span><button type="button"></button>';
      chip.querySelector('[data-thumb]')?.setAttribute('title', text);
      const label = chip.querySelector('[data-thumb] span:last-child');
      if (label !== null) {
        label.textContent = text;
      }
      chip.querySelector('button')?.setAttribute('aria-label', `Remove ${text}`);
      row.append(chip);
    }
    return true;
  }
  return false;
}

/** Duration's Custom: the buttons give way to the slider at 3:00 and its box (TS-005). */
function durationCustom(document: Document, target: Element): boolean {
  const button = target.closest('button[data-selected]');
  if (button?.textContent.trim() !== 'Custom') {
    return false;
  }
  const group = button.parentElement;
  const slider = document.createElement('div');
  slider.innerHTML =
    '<div role="slider" aria-label="Duration" aria-valuenow="180" aria-valuemin="10" aria-valuemax="360" aria-valuetext="3 minutes" aria-disabled="false" tabindex="0"></div><input inputmode="decimal" aria-label="Duration" type="text" value="3:00">';
  group?.replaceWith(...slider.children);
  return true;
}

/** The Sounds Key button and its popover (TS-005). */
function keyPicker(document: Document, target: Element): boolean {
  const opener = target.closest('button[aria-haspopup="dialog"]');
  const row = opener?.parentElement?.parentElement;
  if (
    opener !== null &&
    [...(row?.querySelectorAll('span') ?? [])].some((span) => span.textContent.trim() === 'Key')
  ) {
    if (
      document.querySelector('[role="dialog"]:not([aria-label]):not([aria-labelledby])') === null
    ) {
      const popover = fromSnapshot(
        document,
        'create-sounds-key-popover-fsharp-minor',
        '[role="dialog"]',
      );
      const label = opener.textContent.trim();
      const [note, scale] = label.split(' ');
      for (const choice of popover.querySelectorAll('button[data-selected]')) {
        const name = choice.textContent.trim();
        choice.setAttribute('data-selected', String(name === (label === 'Any' ? 'Any' : note)));
      }
      for (const tab of popover.querySelectorAll('[role="tab"]')) {
        tab.setAttribute(
          'aria-selected',
          String((scale === 'min') === (tab.textContent.trim() === 'Minor')),
        );
      }
      popover.setAttribute('data-stand-in-key', idOf(opener, 'stand-in-key'));
      document.body.append(popover);
      opener.setAttribute('aria-expanded', 'true');
    }
    return true;
  }
  const popover = target.closest('[role="dialog"][data-stand-in-key]');
  if (popover === null) {
    return false;
  }
  const choice = target.closest('button[data-selected]');
  if (choice !== null) {
    for (const other of popover.querySelectorAll('button[data-selected]')) {
      other.setAttribute('data-selected', String(other === choice));
    }
    return true;
  }
  const apply = target.closest('button');
  if (apply?.textContent.trim() === 'Apply') {
    const chosen =
      popover.querySelector('button[data-selected="true"]')?.textContent.trim() ?? 'Any';
    const minor =
      popover.querySelector('[role="tab"][aria-selected="true"]')?.textContent.trim() === 'Minor';
    const key = document.getElementById(popover.getAttribute('data-stand-in-key') ?? '');
    if (key !== null) {
      key.textContent = chosen === 'Any' ? 'Any' : `${chosen} ${minor ? 'min' : 'maj'}`;
      key.setAttribute('aria-expanded', 'false');
    }
    popover.remove();
    return true;
  }
  return false;
}

/** A model menu item's label: its first text ("v6" of "v6 Pro Powerful. …"). */
function modelLabelOf(item: Element): string {
  return item.querySelector('.truncate')?.textContent.trim() ?? '';
}

/** Sets the text the model button shows, leaving its icon. */
function showModel(button: Element, label: string): void {
  const content = button.querySelector('.hxc-btn-content') ?? button;
  const text = [...content.childNodes].find((node) => node.nodeType === node.TEXT_NODE);
  if (text === undefined) {
    content.prepend(label);
  } else {
    text.textContent = label;
  }
}

/** The model button and the model menu (TS-006). */
function modelPicker(document: Document, target: Element): boolean {
  const button = target.closest('button[aria-haspopup="menu"]');
  if (button !== null && /^v\d/i.test(button.textContent.trim())) {
    const id = idOf(button, 'stand-in-model');
    const open = document.querySelector(`[role="menu"][aria-labelledby="${id}"]`);
    if (open !== null) {
      open.closest('[data-base-ui-portal]')?.remove();
      open.remove();
      button.setAttribute('aria-expanded', 'false');
      return true;
    }
    const menu = fromSnapshot(document, 'create-songs-model-menu', '[role="menu"]');
    menu.setAttribute('aria-labelledby', id);
    const shown = button.textContent.trim().toLowerCase();
    for (const item of menu.querySelectorAll('[role="menuitemradio"]')) {
      item.setAttribute('aria-checked', String(modelLabelOf(item).toLowerCase() === shown));
    }
    document.body.append(menu);
    button.setAttribute('aria-expanded', 'true');
    return true;
  }
  const item = target.closest('[role="menuitemradio"]');
  const menu = item?.closest('[role="menu"]');
  const opener = document.getElementById(menu?.getAttribute('aria-labelledby') ?? '');
  if (item === null || menu === null || menu === undefined || opener === null) {
    return false;
  }
  for (const other of menu.querySelectorAll('[role="menuitemradio"]')) {
    other.setAttribute('aria-checked', String(other === item));
  }
  showModel(opener, modelLabelOf(item));
  opener.setAttribute('aria-expanded', 'false');
  menu.closest('[data-base-ui-portal]')?.remove();
  menu.remove();
  return true;
}

/** The open Model dialog (TS-007-10), or null. */
function openModelDialog(document: Document): Element | null {
  return (
    [...document.querySelectorAll('[role="dialog"]')].find(
      (dialog) => dialog.querySelector('h2')?.textContent.trim() === 'Model',
    ) ?? null
  );
}

/** A Model dialog row's name: its first text. */
function rowNameOf(row: Element): string {
  return row.querySelector('span')?.textContent.trim() ?? '';
}

function closeModelDialog(document: Document, dialog: Element): void {
  document
    .querySelector(`button[aria-haspopup="dialog"][aria-controls="${dialog.id}"]`)
    ?.setAttribute('aria-expanded', 'false');
  (dialog.closest('[data-base-ui-portal]') ?? dialog).remove();
}

/** The model button that opens a dialog, and the Model dialog (TS-007-10). */
function modelDialogPicker(document: Document, target: Element, staysOpen: boolean): boolean {
  const button = target.closest('button[aria-haspopup="dialog"]');
  if (button !== null && /^v\d/i.test(button.textContent.trim())) {
    const open = openModelDialog(document);
    if (open !== null) {
      closeModelDialog(document, open);
      return true;
    }
    const portal = fromSnapshot(document, 'create-songs-advanced-model-dialog', '#_r_mp_');
    const dialog = portal.querySelector('[role="dialog"]');
    const shown = button.textContent.trim().toLowerCase();
    for (const row of portal.querySelectorAll('[role="row"][aria-selected]')) {
      row.setAttribute('aria-selected', String(rowNameOf(row).toLowerCase() === shown));
    }
    button.setAttribute('aria-controls', dialog?.id ?? '');
    document.body.append(portal);
    button.setAttribute('aria-expanded', 'true');
    return true;
  }
  const dialog = openModelDialog(document);
  const row = target.closest('[role="row"][aria-selected]');
  if (dialog === null || row === null || !dialog.contains(row)) {
    return false;
  }
  for (const other of dialog.querySelectorAll('[role="row"][aria-selected]')) {
    other.setAttribute('aria-selected', String(other === row));
  }
  const opener = document.querySelector(
    `button[aria-haspopup="dialog"][aria-controls="${dialog.id}"]`,
  );
  if (opener !== null) {
    showModel(opener, rowNameOf(row));
  }
  if (!staysOpen) {
    closeModelDialog(document, dialog);
  }
  return true;
}

/** The open Save to picker (#435), or null. */
function openSaveToPicker(document: Document): Element | null {
  return (
    document
      .querySelector('input[placeholder="Search or create..."]')
      ?.closest('[role="dialog"]') ?? null
  );
}

/** The Save to button: the one beside the "Save to..." label. */
function saveToButtonOf(target: Element): Element | null {
  const button = target.closest('button[aria-haspopup="dialog"]');
  const label = button?.parentElement?.querySelector('span');
  return button !== null && label?.textContent.trim() === 'Save to...' ? button : null;
}

function closeSaveToPicker(document: Document, picker: Element): void {
  document
    .querySelector(`button[aria-haspopup="dialog"][aria-controls="${picker.id}"]`)
    ?.setAttribute('aria-expanded', 'false');
  (picker.closest('[data-base-ui-portal]') ?? picker).remove();
}

/** A Save to row's workspace name: its first text. */
function saveToRowName(row: Element): string {
  return row.querySelector('span')?.textContent.trim() ?? '';
}

/** The Save to button and its picker (#435). */
function saveToPicker(document: Document, target: Element, options: StandInOptions): boolean {
  const button = saveToButtonOf(target);
  if (button !== null) {
    const open = openSaveToPicker(document);
    if (open !== null) {
      closeSaveToPicker(document, open);
      return true;
    }
    const portal = fromSnapshot(
      document,
      'create-songs-advanced-save-to-picker',
      '[data-base-ui-portal]',
    );
    const list = portal.querySelector('.overflow-y-auto');
    const template = list?.querySelector('button');
    if (
      options.saveToRows !== undefined &&
      list !== null &&
      template !== null &&
      template !== undefined
    ) {
      const rows = options.saveToRows.map(({ name, clips }) => {
        const row = template.cloneNode(true) as Element;
        const [nameSpan, countSpan] = row.querySelectorAll('span');
        if (nameSpan !== undefined && countSpan !== undefined) {
          nameSpan.textContent = name;
          countSpan.textContent = `(${String(clips)} clips)`;
        }
        return row;
      });
      list.replaceChildren(...rows);
    }
    const picker = portal.querySelector('[role="dialog"]');
    button.setAttribute('aria-controls', picker?.id ?? '');
    button.setAttribute('aria-expanded', 'true');
    document.body.append(portal);
    return true;
  }
  const picker = openSaveToPicker(document);
  const row = target.closest('button');
  if (picker === null || row === null || !picker.contains(row) || saveToRowName(row) === '') {
    return false;
  }
  const opener = document.querySelector(
    `button[aria-haspopup="dialog"][aria-controls="${picker.id}"]`,
  );
  if (opener !== null) {
    showModel(opener, saveToRowName(row));
  }
  if (options.saveToStaysOpen !== true) {
    closeSaveToPicker(document, picker);
  }
  return true;
}

/** Typing into the Save to picker's box shows only the rows whose name holds the text. */
function filterSaveTo(event: Event): void {
  const box = event.target as Element;
  if (box.getAttribute('placeholder') !== 'Search or create...') {
    return;
  }
  const typed = (box as HTMLInputElement).value.trim().toLowerCase();
  for (const row of box.closest('[role="dialog"]')?.querySelectorAll('.overflow-y-auto > button') ??
    []) {
    if (saveToRowName(row).toLowerCase().includes(typed)) {
      row.removeAttribute('hidden');
    } else {
      row.setAttribute('hidden', '');
    }
  }
}

const MASHUP_SEARCH_BOX = '[role="dialog"] input[aria-label="Search clips"]';
const MASHUP_ROW = '[role="rowgroup"] > [role="button"]';
const MASHUP_ADD = /^Add .+ to mashup$/;

/** The open Mashup dialog (#438), or null. */
function openMashupDialog(document: Document): Element | null {
  return document.querySelector(MASHUP_SEARCH_BOX)?.closest('[role="dialog"]') ?? null;
}

function closeMashupDialog(dialog: Element): void {
  (dialog.closest('[data-base-ui-portal]') ?? dialog).remove();
}

/** A Mashup dialog row's song: its link's text and the clip ID its link leads to. */
function mashupRowOf(row: Element): MashupRow {
  const link = row.querySelector('a[href^="/song/"]');
  return {
    title: link?.textContent.trim() ?? '',
    sunoId: link?.getAttribute('href')?.slice('/song/'.length) ?? '',
  };
}

/** The Mashup dialog's rows, shown or not. */
function mashupRowsOf(dialog: Element): Element[] {
  return [...dialog.querySelectorAll(MASHUP_ROW)];
}

/** A row of the snapshot's, made the song's: its names, its thumbnail, and its link. */
function mashupRowFor(template: Element, { title, sunoId }: MashupRow): Element {
  const row = template.cloneNode(true) as Element;
  row.setAttribute('aria-label', `${title}. Double-tap or press Enter to add to mashup.`);
  row.querySelector('[aria-label^="Play "]')?.setAttribute('aria-label', `Play ${title}`);
  const image = row.querySelector('img');
  image?.setAttribute('alt', `${title} artwork`);
  image?.setAttribute('src', `https://cdn2.suno.ai/image_${sunoId}.jpeg`);
  const link = row.querySelector('a[href^="/song/"]');
  link?.setAttribute('href', `/song/${sunoId}`);
  if (link !== null) {
    link.textContent = title;
  }
  row.querySelector('[aria-label^="Like "]')?.setAttribute('aria-label', `Like ${title}`);
  row.querySelector('[aria-label^="Add "]')?.setAttribute('aria-label', `Add ${title} to mashup`);
  return row;
}

/** "Add another song to Mashup", the dialog it opens, and a song's Add in it (#438). */
function mashupPicker(document: Document, target: Element, options: StandInOptions): boolean {
  const add = target.closest('button[aria-label="Add another song to mashup"]');
  if (add !== null) {
    if (openMashupDialog(document) !== null) {
      return true;
    }
    const portal = fromSnapshot(
      document,
      'create-source-mashup-add-song-dialog',
      '[data-base-ui-portal]',
    );
    const list = portal.querySelector('[role="rowgroup"]');
    const template = list?.querySelector(':scope > [role="button"]');
    if (
      options.mashupRows !== undefined &&
      list !== null &&
      template !== null &&
      template !== undefined
    ) {
      list.replaceChildren(...options.mashupRows.map((song) => mashupRowFor(template, song)));
    }
    document.body.append(portal);
    return true;
  }
  const dialog = openMashupDialog(document);
  const control = target.closest('button, [role="button"]');
  if (dialog === null || control === null || !dialog.contains(control)) {
    return false;
  }
  const name = control.getAttribute('aria-label') ?? '';
  if (name === 'Close') {
    closeMashupDialog(dialog);
    return true;
  }
  if (control.localName !== 'button' || !MASHUP_ADD.test(name)) {
    // Tabs, filter, sort, the liked filter, a row itself, play, Edit title, and like.
    return true;
  }
  const row = mashupRowsOf(dialog).find((each) => each.contains(control));
  const region = document.querySelector('[role="region"][aria-label^="Mashup songs"]');
  const player = region?.querySelector('[role="button"]')?.cloneNode(true) as Element | undefined;
  if (row !== undefined && region !== null && player !== undefined) {
    const song = mashupRowOf(row);
    player.setAttribute('aria-label', `Play "${song.title}"`);
    player
      .querySelector('img')
      ?.setAttribute('src', `https://cdn2.suno.ai/image_${song.sunoId}.jpeg`);
    region.append(player);
    region.setAttribute('aria-label', 'Mashup songs. 2 of 2 songs selected.');
  }
  if (options.mashupDialogStaysOpen !== true) {
    closeMashupDialog(dialog);
  }
  return true;
}

/** Typing into the Mashup dialog's search box shows only the rows whose title holds the text. */
function filterMashup(event: Event, options: StandInOptions): void {
  const box = event.target as Element;
  if (!box.matches(MASHUP_SEARCH_BOX)) {
    return;
  }
  const typed = (box as HTMLInputElement).value.trim().toLowerCase();
  const shown: MashupRow[] = [];
  for (const row of mashupRowsOf(box.closest('[role="dialog"]') ?? box)) {
    const song = mashupRowOf(row);
    if (song.title.toLowerCase().includes(typed)) {
      row.removeAttribute('hidden');
      shown.push(song);
    } else {
      row.setAttribute('hidden', '');
    }
  }
  options.onMashupSearch?.((box as HTMLInputElement).value, shown);
}

/** Starts the stand-in on `document`; `stop` removes it (and the editor commands). */
export function standInForSuno(document: Document, options: StandInOptions = {}): StandIn {
  const commands: string[] = [];
  const onClick = (event: Event) => {
    const target = event.target as Element;
    if (
      mashupPicker(document, target, options) ||
      saveToPicker(document, target, options) ||
      modelDialogPicker(document, target, options.modelDialogStaysOpen === true) ||
      modelPicker(document, target) ||
      simpleSections(document, target) ||
      durationCustom(document, target) ||
      keyPicker(document, target)
    ) {
      return;
    }
    const tab = target.closest('[role="tab"]');
    if (tab !== null) {
      for (const sibling of tab.parentElement?.querySelectorAll('[role="tab"]') ?? []) {
        sibling.setAttribute('aria-selected', String(sibling === tab));
      }
      return;
    }
    const header = target.closest('[role="button"][aria-expanded]');
    if (header !== null && header.localName !== 'button') {
      toggleSection(header);
      return;
    }
    const choice = target.closest('button[data-selected], button[aria-pressed]');
    if (choice !== null) {
      selectInGroup(choice);
    }
  };
  const onKey = (event: Event) => {
    const key = (event as KeyboardEvent).key;
    if (key === 'Escape') {
      const dialog = openModelDialog(document);
      if (dialog !== null) {
        closeModelDialog(document, dialog);
      }
      const picker = openSaveToPicker(document);
      if (picker !== null) {
        closeSaveToPicker(document, picker);
      }
      const mashup = openMashupDialog(document);
      if (mashup !== null) {
        closeMashupDialog(mashup);
      }
      return;
    }
    const slider = (event.target as Element).closest('[role="slider"]');
    if (slider !== null && (key === 'ArrowRight' || key === 'ArrowLeft')) {
      if (options.deferredSliders === true) {
        // Suno's sliders apply a key a task later, from the value they had when it came.
        const from = slider.getAttribute('aria-valuenow');
        setTimeout(() => {
          slider.setAttribute('aria-valuenow', from ?? '');
          moveSlider(slider, key);
        }, 0);
      } else {
        moveSlider(slider, key);
      }
    }
  };
  const onInput = (event: Event) => {
    filterSaveTo(event);
    filterMashup(event, options);
  };
  document.addEventListener('click', onClick);
  document.addEventListener('keydown', onKey);
  document.addEventListener('input', onInput);
  Object.defineProperty(document, 'execCommand', {
    configurable: true,
    value: (command: string, _ui: boolean, value?: string) => {
      commands.push(command);
      return editorCommand(document, command, value);
    },
  });
  return {
    commands,
    stop: () => {
      document.removeEventListener('click', onClick);
      document.removeEventListener('keydown', onKey);
      document.removeEventListener('input', onInput);
      Reflect.deleteProperty(document, 'execCommand');
    },
  };
}
