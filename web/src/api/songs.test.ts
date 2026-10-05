import { describe, expect, it } from 'vitest';
import { songListParameters, songQueryFrom } from './songs';
import { formatRelativeTime } from './timeZone';

const STATE = '01a10a6e-dc80-7000-8000-000000000001';
const GENRE = '0199b1a0-0000-7000-a000-000000000001';

describe('the Songs view in the URL', () => {
  it('leaves the defaults out: newest first, every state, the first page', () => {
    const query = songQueryFrom(new URLSearchParams());

    expect(query).toEqual({ sort: 'updated', direction: 'desc', states: [], genres: [], page: 1 });
    expect(songListParameters(query).toString()).toBe('');
  });

  it('reads and writes the same parameters the list takes', () => {
    const search = `sort=title&direction=desc&state=${STATE}&genre=${GENRE}&genre=none&page=3`;

    expect(songListParameters(songQueryFrom(new URLSearchParams(search))).toString()).toBe(search);
  });

  it('starts a title sort A to Z', () => {
    expect(songQueryFrom(new URLSearchParams('sort=title')).direction).toBe('asc');
  });

  it('ignores what it does not understand', () => {
    const query = songQueryFrom(
      new URLSearchParams(
        `sort=shortcode&direction=up&page=-2&state=${STATE}&state=${STATE}&genre=${GENRE}&genre=${GENRE}`,
      ),
    );

    expect(query).toEqual({
      sort: 'updated',
      direction: 'desc',
      states: [STATE],
      genres: [GENRE],
      page: 1,
    });
  });
});

describe('formatRelativeTime', () => {
  const now = new Date('2026-10-05T12:00:00Z');
  const before = (seconds: number) => new Date(now.getTime() - seconds * 1000).toISOString();
  const format = (seconds: number, unit: Intl.RelativeTimeFormatUnit) =>
    new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' }).format(-seconds, unit);

  it.each([
    [10, format(0, 'second')],
    [5 * 60, format(5, 'minute')],
    [90 * 60, format(1, 'hour')],
    [26 * 60 * 60, format(1, 'day')],
    [14 * 24 * 60 * 60, format(2, 'week')],
    [400 * 24 * 60 * 60, format(1, 'year')],
  ])('puts %i seconds ago in the largest whole unit', (seconds, expected) => {
    expect(formatRelativeTime(before(seconds), now)).toBe(expected);
  });
});
