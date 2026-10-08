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
 * Text boxes need no stand-in: the native value setter works in jsdom.
 */

import { snapshotHtml } from './snapshots.ts';

const STEP: Readonly<Record<string, number>> = { Duration: 5 };

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

/** Starts the stand-in on `document`; `stop` removes it (and the editor commands). */
export function standInForSuno(document: Document): StandIn {
  const commands: string[] = [];
  const onClick = (event: Event) => {
    const target = event.target as Element;
    if (
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
    const slider = (event.target as Element).closest('[role="slider"]');
    if (slider !== null && (key === 'ArrowRight' || key === 'ArrowLeft')) {
      moveSlider(slider, key);
    }
  };
  document.addEventListener('click', onClick);
  document.addEventListener('keydown', onKey);
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
      Reflect.deleteProperty(document, 'execCommand');
    },
  };
}
