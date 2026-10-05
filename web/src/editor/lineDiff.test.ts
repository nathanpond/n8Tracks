import { describe, expect, it } from 'vitest';
import { hasChanges, lineDiff } from './lineDiff';

describe('lineDiff', () => {
  it('keeps shared lines and marks the rest removed (before) or added (after), removed first', () => {
    expect(lineDiff('[Verse]\nOld line\nKept', '[Verse]\nNew line\nKept\nExtra')).toEqual([
      { kind: 'same', text: '[Verse]' },
      { kind: 'removed', text: 'Old line' },
      { kind: 'added', text: 'New line' },
      { kind: 'same', text: 'Kept' },
      { kind: 'added', text: 'Extra' },
    ]);
  });

  it('treats an empty text as no lines and ignores one final line break', () => {
    expect(lineDiff('', 'One\n')).toEqual([{ kind: 'added', text: 'One' }]);
    expect(lineDiff('One\nTwo\n', '')).toEqual([
      { kind: 'removed', text: 'One' },
      { kind: 'removed', text: 'Two' },
    ]);
    expect(lineDiff('', '')).toEqual([]);
    expect(hasChanges(lineDiff('Same\n', 'Same'))).toBe(false);
  });

  it('keeps blank lines and repeated lines as lines', () => {
    const diff = lineDiff('a\n\na\nb', 'a\nb\n\na');
    expect(diff.filter((line) => line.kind === 'same').map((line) => line.text)).toHaveLength(3);
    // Every line of both texts appears exactly once.
    expect(diff.filter((line) => line.kind !== 'added').map((line) => line.text)).toEqual([
      'a',
      '',
      'a',
      'b',
    ]);
    expect(diff.filter((line) => line.kind !== 'removed').map((line) => line.text)).toEqual([
      'a',
      'b',
      '',
      'a',
    ]);
  });

  it('compares a complete rewrite as every line removed and every line added', () => {
    const diff = lineDiff('First verse\nline two', 'Rewritten\nentirely');
    expect(hasChanges(diff)).toBe(true);
    expect(diff.map((line) => line.kind)).toEqual(['removed', 'removed', 'added', 'added']);
  });
});
