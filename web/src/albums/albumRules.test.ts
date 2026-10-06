import { describe, expect, it } from 'vitest';
import {
  albumDateError,
  albumTitleError,
  formatAlbumDate,
  normaliseUpc,
  shownDate,
  upcError,
} from './albumRules';

describe('the Album rules', () => {
  it('checks a title: 1 to 300 once trimmed', () => {
    expect(albumTitleError('  Pack EP ')).toBeUndefined();
    expect(albumTitleError('   ')).toBe('Enter a title.');
    expect(albumTitleError(` ${'a'.repeat(300)} `)).toBeUndefined();
    expect(albumTitleError('a'.repeat(301))).toBe('Use at most 300 characters.');
  });

  it('takes a year, a year and month, or a full date, and refuses anything else', () => {
    for (const date of ['2026', '2026-03', '2026-03-01', '2024-02-29', ' 1000 ', '']) {
      expect(albumDateError(date)).toBeUndefined();
    }
    expect(albumDateError('2026-3')).toMatch(/^Enter a year \(2026\)/);
    expect(albumDateError('March 2026')).toMatch(/^Enter a year \(2026\)/);
    expect(albumDateError('0999')).toBe('Enter a year from 1000 to 9999.');
    expect(albumDateError('2026-13')).toBe('Enter a month from 01 to 12.');
    expect(albumDateError('2025-02-29')).toBe('That day does not exist in that month.');
    expect(albumDateError('2026-04-31')).toBe('That day does not exist in that month.');
  });

  it('shows a partial date localised, as precisely as it was entered', () => {
    expect(formatAlbumDate('2026', 'en-GB')).toBe('2026');
    expect(formatAlbumDate('2026-03', 'en-GB')).toBe('March 2026');
    expect(formatAlbumDate('2026-03-01', 'en-GB')).toBe('1 March 2026');
    expect(formatAlbumDate('2026-03-01', 'en-US')).toBe('March 1, 2026');
  });

  it('shows the release date, else the original release date', () => {
    expect(shownDate({ releaseDate: '2026', originalReleaseDate: '1999' })).toBe('2026');
    expect(shownDate({ releaseDate: null, originalReleaseDate: '1999' })).toBe('1999');
    expect(shownDate({ releaseDate: null, originalReleaseDate: null })).toBeNull();
  });

  it('checks a UPC/EAN: 12 or 13 digits with a valid check digit, spaces and hyphens ignored', () => {
    expect(upcError('036000291452')).toBeUndefined();
    expect(upcError('0 36000-29145 2')).toBeUndefined();
    expect(upcError('4006381333931')).toBeUndefined();
    expect(upcError('')).toBeUndefined();
    expect(upcError('036000291453')).toBe(
      'The check digit is wrong: check the code for a typing mistake.',
    );
    expect(upcError('12345')).toBe('Enter a UPC of 12 digits or an EAN of 13 digits.');
    expect(upcError('03600029145X')).toBe('Enter a UPC of 12 digits or an EAN of 13 digits.');
    expect(normaliseUpc(' 400-6381 333931 ')).toBe('4006381333931');
    expect(normaliseUpc(' - ')).toBeNull();
  });
});
