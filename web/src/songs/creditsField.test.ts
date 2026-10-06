import { describe, expect, it } from 'vitest';
import type { SongCredits } from '../api/songs';
import { creditsOf, creditsText, creditsValue, makePrimary, moveFeatured } from './creditsField';

const A = { id: 'a', name: 'n8' };
const B = { id: 'b', name: 'Guest' };
const C = { id: 'c', name: 'Choir' };

describe('a Song’s credits as a saved field', () => {
  it('reads back what it wrote, and nothing from anything unreadable', () => {
    const credits: SongCredits = { primary: A, featured: [B, C] };
    expect(creditsOf(creditsValue(credits))).toEqual(credits);
    expect(creditsValue(credits)).toBe(creditsValue({ primary: A, featured: [B, C] }));
    expect(creditsValue(credits)).not.toBe(creditsValue({ primary: A, featured: [C, B] }));
    expect(creditsOf(null)).toEqual({ primary: null, featured: [] });
    expect(creditsOf('not json')).toEqual({ primary: null, featured: [] });
  });

  it('says the credits in words', () => {
    expect(creditsText({ primary: null, featured: [] })).toBeNull();
    expect(creditsText({ primary: A, featured: [] })).toBe('n8');
    expect(creditsText({ primary: A, featured: [B] })).toBe('n8, featuring Guest');
    expect(creditsText({ primary: null, featured: [B, C, A] })).toBe(
      'Featuring Guest, Choir and n8',
    );
  });

  it('makes a featured Artist primary, the previous primary becoming the first featured', () => {
    expect(makePrimary({ primary: A, featured: [B, C] }, C)).toEqual({
      primary: C,
      featured: [A, B],
    });
    expect(makePrimary({ primary: null, featured: [B, C] }, C)).toEqual({
      primary: C,
      featured: [B],
    });
  });

  it('moves a featured Artist and ignores a move past either end', () => {
    const credits: SongCredits = { primary: A, featured: [B, C] };
    expect(moveFeatured(credits, 1, 0).featured).toEqual([C, B]);
    expect(moveFeatured(credits, 0, 2)).toBe(credits);
    expect(moveFeatured(credits, 0, -1)).toBe(credits);
  });
});
