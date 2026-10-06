import { describe, expect, it } from 'vitest';
import { continueAtOf, formatPosition } from '../versions/sourcesRules';
import { inputText, lineageText, pastedSunoId } from './lineage';

describe('a Version’s lineage as the editor compares it (#125)', () => {
  it('leaves out what a read adds, so a read and what was sent are the same edit', () => {
    const read = [
      {
        typeId: 't',
        sunoAction: 'cover',
        generation: {
          id: 'g',
          shortcode: 'n8-1-v1-g1',
          songId: 's',
          songShortcode: 'n8-1',
          songTitle: 'A',
          missing: false,
        },
        availability: 'ok',
        continueAtSeconds: null,
        secondaryIds: { b: '2', a: '1' },
      },
    ];
    const sent = [{ typeId: 't', generation: { id: 'g' }, secondaryIds: { a: '1', b: '2' } }];
    expect(lineageText('sources', read)).toBe(lineageText('sources', sent));
    // Complement: another target is another edit.
    expect(lineageText('sources', read)).not.toBe(
      lineageText('sources', [{ typeId: 't', generation: { id: 'h' } }]),
    );
  });

  it('treats a lineage key left out, null, and empty as the same', () => {
    expect(inputText('sources', undefined)).toBe(inputText('sources', null));
    expect(lineageText('fileInputs', [])).toBe(lineageText('fileInputs', undefined));
    expect(lineageText('inspiration', { sources: [] })).toBe(lineageText('inspiration', null));
    // File notes are one per kind, so their order is no change.
    expect(
      lineageText('fileInputs', [
        { kind: 'video', description: 'v' },
        { kind: 'image', description: 'i' },
      ]),
    ).toBe(
      lineageText('fileInputs', [
        { kind: 'image', description: 'i' },
        { kind: 'video', description: 'v' },
      ]),
    );
    // An option is still compared as its JSON.
    expect(inputText('weirdness', 50)).toBe('50');
  });

  it('takes a pasted Suno song address or a bare clip ID, and nothing else', () => {
    expect(pastedSunoId('https://suno.com/song/abc-123')).toBe('abc-123');
    expect(pastedSunoId(' https://www.suno.com/song/abc-123/?sh=x ')).toBe('abc-123');
    expect(pastedSunoId('abc-123')).toBe('abc-123');
    expect(pastedSunoId('')).toBeUndefined();
    expect(pastedSunoId('two words')).toBeUndefined();
    expect(pastedSunoId('https://example.com/song/abc')).toBeUndefined();
  });

  it('reads Extend’s position as minutes and seconds within the source’s length', () => {
    expect(continueAtOf(1, 30, 125)).toEqual({ seconds: 90 });
    expect(continueAtOf('2', '5', 125)).toEqual({ seconds: 125 });
    expect(continueAtOf(2, 5.01, 125)).toHaveProperty('error');
    expect(continueAtOf(0, 60, null)).toHaveProperty('error');
    expect(continueAtOf(-1, 0, null)).toHaveProperty('error');
    expect(continueAtOf(90, 0, null)).toEqual({ seconds: 5400 });
    expect(formatPosition(124.5)).toBe('2:04.50');
    expect(formatPosition(125)).toBe('2:05');
  });
});
