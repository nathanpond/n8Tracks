import {
  autocompletion,
  completionKeymap,
  type Completion,
  type CompletionContext,
  type CompletionResult,
} from '@codemirror/autocomplete';
import { defaultKeymap, history, historyKeymap, insertTab } from '@codemirror/commands';
import { EditorState, RangeSet, StateField, type Extension } from '@codemirror/state';
import { Decoration, EditorView, GutterMarker, ViewPlugin, gutter, keymap } from '@codemirror/view';
import { tagsStartingWith } from './commonTags';
import { analyseLyrics, type LyricsAnalysis } from './lyricsLanguage';

// The CodeMirror side of the lyrics editor: highlighting and warnings drawn from the one tokeniser
// in `lyricsLanguage.ts`, the `[` autocomplete, the keys, and the look. The warnings use a gutter of
// their own rather than @codemirror/lint's, because its markers cannot take keyboard focus and its
// checks run after a delay; here every change is analysed at once, synchronously.

/** The analysis of the document, kept in step with every change. */
export const lyricsAnalysis = StateField.define<LyricsAnalysis>({
  create: (state) => analyseLyrics(state.doc.toString()),
  update: (analysis, transaction) =>
    transaction.docChanged ? analyseLyrics(transaction.newDoc.toString()) : analysis,
});

const tagMark = Decoration.mark({ class: 'cm-lyrics-tag' });
const parentheticalMark = Decoration.mark({ class: 'cm-lyrics-parenthetical' });
const warningMark = Decoration.mark({ class: 'cm-lyrics-warning' });

const highlighting = EditorView.decorations.compute([lyricsAnalysis], (state) => {
  const { tokens, warnings } = state.field(lyricsAnalysis);
  return Decoration.set(
    [
      ...tokens.map((token) =>
        (token.kind === 'tag' ? tagMark : parentheticalMark).range(token.from, token.to),
      ),
      ...warnings.map((found) => warningMark.range(found.from, found.to)),
    ],
    true,
  );
});

/** A line's warnings, as a focusable marker whose explanation shows on hover and focus. */
class WarningMarker extends GutterMarker {
  readonly line: number;
  readonly position: number;
  readonly messages: readonly string[];

  constructor(line: number, position: number, messages: readonly string[]) {
    super();
    this.line = line;
    this.position = position;
    this.messages = messages;
  }

  override eq(other: GutterMarker): boolean {
    return (
      other instanceof WarningMarker &&
      other.line === this.line &&
      other.position === this.position &&
      other.messages.join('\n') === this.messages.join('\n')
    );
  }

  override toDOM(view: EditorView): Node {
    const explanation = this.messages.join(' ');
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'cm-lyrics-warning-marker';
    button.setAttribute('aria-label', `Warning, line ${String(this.line)}: ${explanation}`);
    const icon = document.createElement('span');
    icon.setAttribute('aria-hidden', 'true');
    icon.textContent = '⚠';
    const tip = document.createElement('span');
    tip.className = 'cm-lyrics-warning-tip';
    tip.setAttribute('aria-hidden', 'true');
    tip.textContent = explanation;
    button.append(icon, tip);
    button.addEventListener('click', () => {
      view.dispatch({ selection: { anchor: this.position }, scrollIntoView: true });
      view.focus();
    });
    return button;
  }
}

const warningGutter = gutter({
  class: 'cm-lyrics-warning-gutter',
  markers: (view) => {
    const { warnings } = view.state.field(lyricsAnalysis);
    const byLine = new Map<number, { position: number; messages: string[] }>();
    for (const found of warnings) {
      const entry = byLine.get(found.line);
      if (entry === undefined) {
        byLine.set(found.line, { position: found.from, messages: [found.message] });
      } else {
        entry.messages.push(found.message);
      }
    }
    return RangeSet.of(
      [...byLine].map(([line, entry]) =>
        new WarningMarker(line, entry.position, entry.messages).range(
          view.state.doc.line(line).from,
        ),
      ),
      true,
    );
  },
  initialSpacer: () => new WarningMarker(0, 0, []),
});

/**
 * CodeMirror hides its gutters from assistive technology, as they usually hold line numbers. Here
 * the gutter holds the warning markers, which are buttons a keyboard can reach, so it is exposed
 * (a focusable control inside `aria-hidden` is an accessibility failure).
 */
const exposedGutter = ViewPlugin.define((view) => {
  const expose = () => {
    view.dom.querySelector('.cm-gutters')?.removeAttribute('aria-hidden');
  };
  expose();
  return { update: expose };
});

/**
 * Escape, then Tab within two seconds, moves focus out of the editor instead of inserting a tab.
 * CodeMirror does this itself only when no key binding handled the Escape; closing the tag list is
 * one, so it is set here on every Escape, whatever else the key did.
 */
const escapeThenTab = EditorView.domEventObservers({
  keydown: (event, view) => {
    if (event.key === 'Escape') {
      view.setTabFocusMode(2000);
    }
  },
});

/**
 * The common tags for the `[` just before the cursor, matched by prefix ignoring case. Choosing one
 * replaces the `[` and what follows it with the whole `[Tag]`, taking in a `]` that already follows
 * rather than adding a second.
 */
export function tagCompletions(context: CompletionContext): CompletionResult | null {
  const typed = context.matchBefore(/\[[^[\]\n]*$/);
  if (typed === null) {
    return null;
  }
  const matches = tagsStartingWith(typed.text.slice(1));
  if (matches.length === 0) {
    return null;
  }
  const options = matches.map((tag): Completion => ({
    label: tag,
    apply: (view, _completion, from, to) => {
      const end = view.state.sliceDoc(to, to + 1) === ']' ? to + 1 : to;
      const insert = `[${tag}]`;
      view.dispatch({
        changes: { from, to: end, insert },
        selection: { anchor: from + insert.length },
        userEvent: 'input.complete',
      });
    },
  }));
  return { from: typed.from, to: context.pos, options, filter: false };
}

const look = EditorView.theme({
  '&': {
    backgroundColor: 'var(--mantine-color-body)',
    color: 'var(--mantine-color-text)',
    border: '1px solid var(--mantine-color-default-border)',
    borderRadius: 'var(--mantine-radius-sm)',
  },
  '&.cm-focused': {
    outline: '2px solid var(--mantine-primary-color-filled)',
    outlineOffset: '1px',
  },
  '.cm-scroller': { fontFamily: 'var(--mantine-font-family-monospace)', lineHeight: '1.6' },
  '.cm-content': { minHeight: '14rem', padding: '8px 0', caretColor: 'var(--mantine-color-text)' },
  '.cm-cursor, .cm-dropCursor': { borderLeftColor: 'var(--mantine-color-text)' },
  '.cm-gutters': {
    backgroundColor: 'var(--mantine-color-body)',
    color: 'var(--n8-lyrics-warning)',
    border: 'none',
  },
  '.cm-lyrics-warning-gutter': { minWidth: '1.75rem' },
  '.cm-lyrics-tag': { color: 'var(--n8-lyrics-tag)', fontWeight: '700' },
  '.cm-lyrics-parenthetical': { color: 'var(--n8-lyrics-parenthetical)', fontStyle: 'italic' },
  '.cm-lyrics-warning': {
    textDecoration: 'underline wavy var(--n8-lyrics-warning)',
    textDecorationThickness: '2px',
    textUnderlineOffset: '3px',
  },
  '.cm-lyrics-warning-marker': {
    position: 'relative',
    font: 'inherit',
    color: 'var(--n8-lyrics-warning)',
    background: 'none',
    border: 'none',
    padding: '0 4px',
    cursor: 'pointer',
  },
  '.cm-lyrics-warning-marker:focus-visible': {
    outline: '2px solid var(--mantine-primary-color-filled)',
  },
  '.cm-lyrics-warning-tip': {
    display: 'none',
    position: 'absolute',
    left: '100%',
    top: '0',
    zIndex: '300',
    width: 'max-content',
    maxWidth: '20rem',
    padding: '4px 8px',
    whiteSpace: 'normal',
    textAlign: 'start',
    fontFamily: 'var(--mantine-font-family)',
    fontSize: 'var(--mantine-font-size-sm)',
    color: 'var(--mantine-color-text)',
    backgroundColor: 'var(--mantine-color-body)',
    border: '1px solid var(--n8-lyrics-warning)',
    borderRadius: 'var(--mantine-radius-sm)',
  },
  '.cm-lyrics-warning-marker:hover .cm-lyrics-warning-tip, .cm-lyrics-warning-marker:focus .cm-lyrics-warning-tip':
    { display: 'block' },
  '.cm-tooltip': {
    backgroundColor: 'var(--mantine-color-body)',
    color: 'var(--mantine-color-text)',
    border: '1px solid var(--mantine-color-default-border)',
  },
  // The list holds at most the thirteen common tags: shown whole, it never scrolls, so it needs no
  // keyboard access of its own (the editor moves through it with aria-activedescendant).
  '.cm-tooltip.cm-tooltip-autocomplete > ul': { maxHeight: 'none', overflow: 'visible' },
  '.cm-tooltip-autocomplete > ul > li[aria-selected]': {
    backgroundColor: 'var(--mantine-primary-color-filled)',
    color: 'var(--mantine-color-white)',
  },
});

/**
 * Everything the lyrics editor is made of. Tab inserts a tab character; Escape then Tab moves focus
 * on (CodeMirror's own tab-focus escape). Long lines wrap; there are no line numbers.
 */
export function lyricsExtensions({
  labelledBy,
  describedBy,
  onChange,
}: {
  labelledBy: string;
  describedBy: string;
  onChange: (text: string) => void;
}): Extension {
  return [
    lyricsAnalysis,
    highlighting,
    warningGutter,
    exposedGutter,
    history(),
    autocompletion({ override: [tagCompletions], icons: false }),
    keymap.of([
      { key: 'Tab', run: insertTab },
      ...completionKeymap,
      ...historyKeymap,
      ...defaultKeymap,
    ]),
    escapeThenTab,
    EditorView.lineWrapping,
    EditorState.allowMultipleSelections.of(false),
    EditorView.contentAttributes.of({
      'aria-labelledby': labelledBy,
      'aria-describedby': describedBy,
      'aria-multiline': 'true',
      spellcheck: 'true',
    }),
    EditorView.updateListener.of((update) => {
      if (update.docChanged) {
        onChange(update.state.doc.toString());
      }
    }),
    look,
  ];
}
