import { describe, expect, it } from 'vitest';
import { genresOf, genresValue, mergeGenres } from './genreField';

const FOLK = { id: 'a', name: 'Folk' };
const ROCK = { id: 'b', name: 'Rock' };
const JAZZ = { id: 'c', name: 'Jazz' };

describe('a Song’s Genres as a saved field', () => {
  it('is the same value for the same set in any order', () => {
    expect(genresValue([ROCK, FOLK])).toBe(genresValue([FOLK, ROCK]));
    expect(genresOf(genresValue([ROCK, FOLK]))).toEqual([FOLK, ROCK]);
    expect(genresOf(null)).toEqual([]);
  });

  it('reapplies the user’s add onto a set changed elsewhere', () => {
    // Loaded with Folk; the user added Rock; meanwhile Jazz was added elsewhere.
    const merged = mergeGenres(
      genresValue([FOLK]),
      genresValue([FOLK, JAZZ]),
      genresValue([FOLK, ROCK]),
    );
    expect(merged).toBe(genresValue([FOLK, JAZZ, ROCK]));
  });

  it('reapplies the user’s removal onto a set changed elsewhere', () => {
    // Loaded with Folk and Rock; the user took Rock off; meanwhile Folk was taken off and Jazz added.
    const merged = mergeGenres(
      genresValue([FOLK, ROCK]),
      genresValue([JAZZ, ROCK]),
      genresValue([FOLK]),
    );
    expect(merged).toBe(genresValue([JAZZ]));
  });
});
