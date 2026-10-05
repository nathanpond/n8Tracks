import { describe, expect, it } from 'vitest';
import { tagsStartingWith } from './commonTags';
import { analyseLine, analyseLyrics, type LyricsWarningKind } from './lyricsLanguage';

/** The highlighted text of a line, as `tag:[Verse]` or `parenthetical:(ooh)`. */
function spans(line: string): string[] {
  return analyseLine(line).tokens.map(
    (token) => `${token.kind}:${line.slice(token.from, token.to)}`,
  );
}

/** The warnings of a line, as `kind@offset`. */
function warnings(line: string): string[] {
  return analyseLine(line).warnings.map((found) => `${found.kind}@${String(found.from)}`);
}

describe('tags', () => {
  it('highlights common and invented tags', () => {
    expect(spans('[Verse]')).toEqual(['tag:[Verse]']);
    expect(spans('[Whisper softly, building]')).toEqual(['tag:[Whisper softly, building]']);
    expect(spans('Go [Chorus] now')).toEqual(['tag:[Chorus]']);
    expect(warnings('[Whisper softly, building]')).toEqual([]);
  });

  it('highlights adjacent tags separately', () => {
    expect(spans('[Verse][Chorus] [Bridge]')).toEqual([
      'tag:[Verse]',
      'tag:[Chorus]',
      'tag:[Bridge]',
    ]);
  });

  it('keeps parentheses inside a tag as part of it', () => {
    expect(spans('[Verse (soft)]')).toEqual(['tag:[Verse (soft)]']);
    expect(warnings('[Verse (soft]')).toEqual([]);
    expect(spans('[Verse (soft]')).toEqual(['tag:[Verse (soft]']);
  });

  it('warns on nested brackets as unmatched', () => {
    expect(spans('[a [b] c]')).toEqual(['tag:[b]']);
    expect(warnings('[a [b] c]')).toEqual(['unclosed-bracket@0', 'unopened-bracket@8']);
  });

  it('warns on an empty or blank tag', () => {
    expect(warnings('[]')).toEqual(['empty-tag@0']);
    expect(warnings('[ \t ]')).toEqual(['empty-tag@0']);
    const [empty] = analyseLine('x [] y').warnings;
    expect(empty?.to).toBe(4);
  });
});

describe('parentheticals', () => {
  it('highlights backing vocals outside tags', () => {
    expect(spans('Run (ooh, yeah)')).toEqual(['parenthetical:(ooh, yeah)']);
    expect(spans('(ooh) and (aah)')).toEqual(['parenthetical:(ooh)', 'parenthetical:(aah)']);
  });

  it('keeps nested parentheses inside the outermost one', () => {
    expect(spans('(a (b) c)')).toEqual(['parenthetical:(a (b) c)']);
  });

  it('still highlights a tag inside a parenthetical', () => {
    expect(spans('(ooh [Chorus])')).toEqual(['parenthetical:(ooh [Chorus])', 'tag:[Chorus]']);
    expect(warnings('(ooh [Chorus])')).toEqual([]);
  });

  it('does not warn on an empty parenthetical', () => {
    expect(warnings('()')).toEqual([]);
    expect(spans('()')).toEqual(['parenthetical:()']);
  });
});

describe('warnings', () => {
  const cases: [string, LyricsWarningKind, number][] = [
    ['[Bridge', 'unclosed-bracket', 0],
    ['Bridge]', 'unopened-bracket', 6],
    ['(ooh', 'unclosed-parenthesis', 0],
    ['ooh)', 'unopened-parenthesis', 3],
    ['[ ]', 'empty-tag', 0],
  ];

  it.each(cases)('warns on %s as %s', (line, kind, from) => {
    const [found, ...rest] = analyseLine(line).warnings;
    expect(rest).toEqual([]);
    expect(found).toMatchObject({ kind, from });
    expect(found?.message).not.toBe('');
  });

  it('gives each unmatched delimiter of overlapping pairs its own warning', () => {
    expect(warnings('(ooh [Chorus)')).toEqual(['unclosed-parenthesis@0', 'unclosed-bracket@5']);
    expect(warnings('[a (b] c)')).toEqual(['unopened-parenthesis@8']);
  });

  it('matches within a line only: a bracket open at the end of a line is warned there', () => {
    const analysis = analyseLyrics('[Verse\n]\n(ooh\n)');

    expect(analysis.tokens).toEqual([]);
    expect(analysis.warnings.map((found) => `${found.kind}@${String(found.line)}`)).toEqual([
      'unclosed-bracket@1',
      'unopened-bracket@2',
      'unclosed-parenthesis@3',
      'unopened-parenthesis@4',
    ]);
  });
});

describe('a whole text', () => {
  it('gives offsets within the text, and line numbers from 1', () => {
    const text = '[Verse]\nRun (ooh)\n\n[Bridge';
    const analysis = analyseLyrics(text);

    expect(analysis.tokens.map((token) => text.slice(token.from, token.to))).toEqual([
      '[Verse]',
      '(ooh)',
    ]);
    expect(analysis.warnings).toEqual([expect.objectContaining({ line: 4, from: 19, to: 20 })]);
  });

  it('is not thrown off by emoji, right-to-left script, or combining characters', () => {
    const text = '\u{1F3B8}\u{1F3B8} [Verse] é (שלום)\nמה [פזמון]';
    const analysis = analyseLyrics(text);

    expect(analysis.tokens.map((token) => text.slice(token.from, token.to))).toEqual([
      '[Verse]',
      '(שלום)',
      '[פזמון]',
    ]);
    expect(analysis.warnings).toEqual([]);
  });
});

describe('the common tags', () => {
  it('are offered by prefix, ignoring case', () => {
    expect(tagsStartingWith('')).toHaveLength(13);
    expect(tagsStartingWith('ch')).toEqual(['Chorus']);
    expect(tagsStartingWith('P')).toEqual(['Pre-Chorus', 'Post-Chorus']);
    expect(tagsStartingWith('Whisper')).toEqual([]);
  });
});
