import { describe, expect, it } from 'vitest';
import type { SongMatch } from '../api/songs';
import {
  excerptParts,
  headerSearchAddress,
  isSearchShortcut,
  matchAddress,
  matchFieldName,
  matchOwnerText,
  matchStateText,
  versionNumberOf,
} from './searchRules';

function match(owner: SongMatch['owner'], field = 'lyrics'): SongMatch {
  return { field, owner, excerpt: { text: 'x', highlights: [] } };
}

const key = (overrides: Partial<KeyboardEvent>) => ({
  key: '',
  ctrlKey: false,
  metaKey: false,
  altKey: false,
  shiftKey: false,
  ...overrides,
});

describe('search rules', () => {
  it('splits an excerpt at its highlights, whatever order or overlap they come in', () => {
    expect(excerptParts('a lantern swinging', [{ start: 2, length: 7 }])).toEqual([
      { text: 'a ', matched: false },
      { text: 'lantern', matched: true },
      { text: ' swinging', matched: false },
    ]);
    expect(
      excerptParts('one two three', [
        { start: 8, length: 5 },
        { start: 0, length: 3 },
        { start: 1, length: 1 },
        { start: 20, length: 2 },
      ]),
    ).toEqual([
      { text: 'one', matched: true },
      { text: ' two ', matched: false },
      { text: 'three', matched: true },
    ]);
    expect(excerptParts('plain', [])).toEqual([{ text: 'plain', matched: false }]);
  });

  it('opens a Version’s match on that Version, a Generation’s on its panel, and the rest on the Song', () => {
    const version = {
      kind: 'version',
      reference: 'n8-12-v2.1',
      label: 'v2.1',
      state: 'active',
    } as const;
    const generation = {
      kind: 'generation',
      reference: 'n8-12-v2.1-g3',
      label: 'v2.1-g3',
      state: 'trashed',
    } as const;
    const album = { kind: 'album', reference: 'abc', label: 'Night', state: 'active' } as const;

    expect(matchAddress('n8-12', match(version))).toBe('/songs/n8-12/v/2.1');
    expect(matchAddress('n8-12', match(generation))).toBe('/songs/n8-12/generations/n8-12-v2.1-g3');
    expect(matchAddress('n8-12', match(album, 'album'))).toBe('/songs/n8-12');
    expect(matchAddress('n8-12', match(null, 'title'))).toBe('/songs/n8-12');

    expect(matchOwnerText(match(version))).toBe('v2.1');
    expect(matchOwnerText(match(generation))).toBe('n8-12-v2.1-g3');
    expect(matchOwnerText(match(album, 'album'))).toBeUndefined();
    expect(matchStateText(match(generation))).toBe('In Suno’s Trash');
    expect(matchStateText(match({ ...version, state: 'archived' }))).toBe('Archived');
    expect(matchStateText(match(version))).toBeUndefined();
    expect(versionNumberOf('n8-12-v10.2.3')).toBe('10.2.3');
    expect(versionNumberOf('n8-12')).toBeUndefined();
  });

  it('names fields as the page shows them', () => {
    expect(matchFieldName('versionNotes')).toBe('Version notes');
    expect(matchFieldName('sunoTags')).toBe('Suno tags');
    expect(matchFieldName('somethingNew')).toBe('somethingNew');
  });

  it('takes / and Ctrl+K or ⌘K as the shortcut, and nothing else', () => {
    expect(isSearchShortcut(key({ key: '/' }))).toBe(true);
    // A keyboard that needs Shift for a slash still gives the shortcut.
    expect(isSearchShortcut(key({ key: '/', shiftKey: true }))).toBe(true);
    expect(isSearchShortcut(key({ key: 'k', ctrlKey: true }))).toBe(true);
    expect(isSearchShortcut(key({ key: 'K', metaKey: true }))).toBe(true);
    expect(isSearchShortcut(key({ key: 'k' }))).toBe(false);
    expect(isSearchShortcut(key({ key: 'k', ctrlKey: true, shiftKey: true }))).toBe(false);
    expect(isSearchShortcut(key({ key: '/', ctrlKey: true }))).toBe(false);
    expect(isSearchShortcut(key({ key: '/', altKey: true }))).toBe(false);
  });

  it('sends a header search to the Songs table alone, cut to 200 characters', () => {
    expect(headerSearchAddress('lantern song')).toBe('/songs?search=lantern+song');
    expect(headerSearchAddress('   ')).toBe('/songs');
    expect(headerSearchAddress('')).toBe('/songs');
    const long = 'a'.repeat(250);
    expect(headerSearchAddress(long)).toBe(`/songs?search=${'a'.repeat(200)}`);
  });
});
