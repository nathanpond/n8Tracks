/**
 * A stand-in for Suno's Create form behaviour over a TS-003 page snapshot (#146 tests). The
 * snapshots are static HTML, so nothing answers a click or a key; this does what TS-003 recorded
 * the live form doing, and nothing more:
 *
 * - a tab, pressed, becomes the selected one of its tab list;
 * - a section header (Lyrics, Styles, More Options), pressed, opens or closes its section;
 * - a choice button (`data-selected`), pressed, becomes the selected one of its group; pressed
 *   again, Vocal Gender's is deselected (None), while an Off/On pair keeps one selected;
 * - a slider moves one step per arrow key (Duration by 5 seconds), between its minimum and
 *   maximum; Home and End do nothing (TS-003);
 * - the Lexical lyrics editor takes the browser's editing commands (`execCommand`), which jsdom
 *   lacks: with everything selected, text replaces it; `insertParagraph` starts a new line.
 *
 * Text boxes need no stand-in: the native value setter works in jsdom.
 */

const STEP: Readonly<Record<string, number>> = { Duration: 5 };

export interface StandIn {
  /** The commands the editor was given, for tests: `insertText`, `insertParagraph`, `delete`. */
  commands: string[];
  stop(): void;
}

function selectInGroup(button: Element): void {
  const group = button.parentElement;
  const selected = button.getAttribute('data-selected') === 'true';
  const names = [...(group?.children ?? [])].map((sibling) => sibling.textContent.trim());
  const deselectable = names.includes('Male') && names.includes('Female');
  if (selected) {
    if (deselectable) {
      button.setAttribute('data-selected', 'false');
    }
    return;
  }
  for (const sibling of group?.children ?? []) {
    if (sibling.hasAttribute('data-selected')) {
      sibling.setAttribute('data-selected', String(sibling === button));
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

/** Starts the stand-in on `document`; `stop` removes it (and the editor commands). */
export function standInForSuno(document: Document): StandIn {
  const commands: string[] = [];
  const onClick = (event: Event) => {
    const target = event.target as Element;
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
    const choice = target.closest('button[data-selected]');
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
