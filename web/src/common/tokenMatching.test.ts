import { describe, expect, it } from 'vitest';
import { hasExactMatch, suggestTokens, type Token } from './tokenMatching';

function tokens(...names: string[]): Token[] {
  return names.map((name, index) => ({ id: `id-${String(index)}`, name }));
}

const names = (found: Token[]) => found.map((token) => token.name);

describe('suggestTokens', () => {
  const genres = tokens('Rock', 'Indie Rock', 'Folk Rock', 'Rockabilly', 'Krautrock', 'Hip-Hop');

  it('matches the start of any word, ignoring case, names it starts first, each alphabetically', () => {
    expect(names(suggestTokens(genres, [], 'rock'))).toEqual([
      'Rock',
      'Rockabilly',
      'Folk Rock',
      'Indie Rock',
    ]);
    expect(names(suggestTokens(genres, [], 'ROCKA'))).toEqual(['Rockabilly']);
    expect(names(suggestTokens(genres, [], 'hop'))).toEqual(['Hip-Hop']);
    expect(names(suggestTokens(genres, [], 'indie  r'))).toEqual(['Indie Rock']);
  });

  it('does not match inside a word', () => {
    expect(names(suggestTokens(genres, [], 'ock'))).toEqual([]);
    expect(names(suggestTokens(genres, [], 'traut'))).toEqual([]);
  });

  it('leaves out the tokens already chosen', () => {
    const chosen = genres.filter((genre) => genre.name === 'Rock' || genre.name === 'Folk Rock');
    expect(names(suggestTokens(genres, chosen, 'rock'))).toEqual(['Rockabilly', 'Indie Rock']);
  });

  it('suggests at most ten', () => {
    const many = tokens(...Array.from({ length: 14 }, (_, index) => `Pop ${String(index + 10)}`));
    const found = suggestTokens(many, [], 'pop');
    expect(found).toHaveLength(10);
    expect(found[0]?.name).toBe('Pop 10');
    expect(suggestTokens(many, [], '')).toHaveLength(10);
  });
});

describe('hasExactMatch', () => {
  it('compares whole names ignoring case and spacing', () => {
    const genres = tokens('Indie Rock');
    expect(hasExactMatch(genres, '  indie   ROCK ')).toBe(true);
    expect(hasExactMatch(genres, 'indie')).toBe(false);
    expect(hasExactMatch(tokens('Café'), 'café')).toBe(true);
  });
});
