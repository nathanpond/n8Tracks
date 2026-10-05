// The lyrics "language": Suno's bracketed tags (`[Verse]`, `[Whisper softly, building]`) and
// parenthetical backing vocals (`(ooh, yeah)`), and the warnings about delimiters that do not pair
// up. Everything is matched within one line. Offsets are UTF-16 code units, as JavaScript strings
// and CodeMirror count them; the delimiters are all single code units, so emoji, combining marks,
// and right-to-left text never split or shift a match.
//
// The rules:
// - A tag runs from `[` to the next `]` on the line. Inside a tag, parentheses are part of the tag
//   (`[Verse (soft)]` is one tag), and another `[` means the first was never closed: it is warned on
//   and the new one starts the tag (`[a [b] c]` warns on the first `[` and the last `]`).
// - A parenthetical runs from `(` to its matching `)` outside tags; nested parentheses belong to the
//   outermost one. A tag inside a parenthetical is still a tag.
// - Every delimiter left without a partner gets its own warning on its line: a `]` or `)` with
//   nothing open, and a `[` or `(` still open at the end of the line. Brackets and parentheses that
//   overlap (`(ooh [Chorus)`) therefore warn on each unmatched one.
// - A tag with nothing but white space between its brackets (`[]`, `[ ]`) is warned on as empty.
//   An empty parenthetical `()` is not.
// Warnings are advice: they never stop the text from being saved.

/** A highlighted span: a tag or a parenthetical. */
export interface LyricsToken {
  kind: 'tag' | 'parenthetical';
  /** Offset of the opening delimiter. */
  from: number;
  /** Offset just after the closing delimiter. */
  to: number;
}

export type LyricsWarningKind =
  | 'unclosed-bracket'
  | 'unopened-bracket'
  | 'unclosed-parenthesis'
  | 'unopened-parenthesis'
  | 'empty-tag';

/** Something on a line that probably is not what the writer meant. */
export interface LyricsWarning {
  kind: LyricsWarningKind;
  /** Offset of the start of what is warned on. */
  from: number;
  /** Offset just after it. */
  to: number;
  message: string;
}

/** What one line holds. Offsets are within the line. */
export interface LineAnalysis {
  tokens: LyricsToken[];
  warnings: LyricsWarning[];
}

/** What a whole text holds, line by line. Offsets are within the whole text. */
export interface LyricsAnalysis {
  tokens: LyricsToken[];
  /** Each warning with its line number, from 1, in text order. */
  warnings: (LyricsWarning & { line: number })[];
}

export const WARNING_MESSAGES: Record<LyricsWarningKind, string> = {
  'unclosed-bracket': 'This [ is not closed on its line. A tag opens and closes on one line.',
  'unopened-bracket': 'This ] closes nothing: there is no [ before it on its line.',
  'unclosed-parenthesis': 'This ( is not closed on its line.',
  'unopened-parenthesis': 'This ) closes nothing: there is no ( before it on its line.',
  'empty-tag': 'This tag is empty. Put a section name, such as Verse, between the brackets.',
};

function warning(kind: LyricsWarningKind, from: number, to = from + 1): LyricsWarning {
  return { kind, from, to, message: WARNING_MESSAGES[kind] };
}

/** The tags, parentheticals, and warnings of one line (which holds no line break). */
export function analyseLine(line: string): LineAnalysis {
  const tokens: LyricsToken[] = [];
  const warnings: LyricsWarning[] = [];
  let tagStart: number | undefined;
  const openParentheses: number[] = [];

  for (let index = 0; index < line.length; index++) {
    const character = line[index];
    if (character === '[') {
      if (tagStart !== undefined) {
        warnings.push(warning('unclosed-bracket', tagStart));
      }
      tagStart = index;
    } else if (character === ']') {
      if (tagStart === undefined) {
        warnings.push(warning('unopened-bracket', index));
        continue;
      }
      tokens.push({ kind: 'tag', from: tagStart, to: index + 1 });
      if (line.slice(tagStart + 1, index).trim() === '') {
        warnings.push(warning('empty-tag', tagStart, index + 1));
      }
      tagStart = undefined;
    } else if (tagStart !== undefined) {
      // Inside a tag, parentheses are part of it.
      continue;
    } else if (character === '(') {
      openParentheses.push(index);
    } else if (character === ')') {
      const opened = openParentheses.pop();
      if (opened === undefined) {
        warnings.push(warning('unopened-parenthesis', index));
      } else if (openParentheses.length === 0) {
        tokens.push({ kind: 'parenthetical', from: opened, to: index + 1 });
      }
    }
  }

  if (tagStart !== undefined) {
    warnings.push(warning('unclosed-bracket', tagStart));
  }
  for (const opened of openParentheses) {
    warnings.push(warning('unclosed-parenthesis', opened));
  }

  const byPosition = (a: { from: number }, b: { from: number }) => a.from - b.from;
  return { tokens: tokens.sort(byPosition), warnings: warnings.sort(byPosition) };
}

/** The tags, parentheticals, and warnings of a whole text, whose lines end in `\n`. */
export function analyseLyrics(text: string): LyricsAnalysis {
  const tokens: LyricsToken[] = [];
  const warnings: LyricsAnalysis['warnings'] = [];
  let offset = 0;
  text.split('\n').forEach((line, index) => {
    const analysis = analyseLine(line);
    for (const token of analysis.tokens) {
      tokens.push({ ...token, from: token.from + offset, to: token.to + offset });
    }
    for (const found of analysis.warnings) {
      warnings.push({
        ...found,
        from: found.from + offset,
        to: found.to + offset,
        line: index + 1,
      });
    }
    offset += line.length + 1;
  });
  return { tokens, warnings };
}
