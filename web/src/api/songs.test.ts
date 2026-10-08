import { describe, expect, it } from 'vitest';
import { activeFilterCount, songListParameters, songQueryFrom } from './songs';
import { formatRelativeTime } from './timeZone';

const STATE = '01a10a6e-dc80-7000-8000-000000000001';
const GENRE = '0199b1a0-0000-7000-a000-000000000001';
const TAG = '0199b1a0-0000-7000-b000-000000000001';
const ARTIST = '01a10e00-0000-7000-8000-000000000001';

describe('the Songs view in the URL', () => {
  it('leaves the defaults out: newest first, every state, the first page', () => {
    const query = songQueryFrom(new URLSearchParams());

    expect(query).toEqual({
      sort: 'updated',
      direction: 'desc',
      states: [],
      genres: [],
      tags: [],
      artists: [],
      page: 1,
    });
    expect(songListParameters(query).toString()).toBe('');
  });

  it('reads and writes the same parameters the list takes', () => {
    const search = `sort=title&direction=desc&state=${STATE}&genre=${GENRE}&genre=none&tag=${TAG}&tag=none&artist=${ARTIST}&artist=none&page=3`;

    expect(songListParameters(songQueryFrom(new URLSearchParams(search))).toString()).toBe(search);
  });

  it('reads and writes every #225 filter, after the existing ones and the search', () => {
    const album = '0199b1a0-0000-7000-c000-000000000001';
    const playlist = '0199b1a0-0000-7000-d000-000000000001';
    const search =
      `search=lantern&state=${STATE}&tag=${TAG}&tag=none&tagMode=any&archived=active&album=${album}` +
      `&playlist=${playlist}&model=chirp-v4&model=chirp-v5&createdFrom=2026-01-10&createdTo=2026-01-20` +
      '&minRating=4&rated=none&selected=no&audio=unavailable&page=2';

    const query = songQueryFrom(new URLSearchParams(search));
    expect(query).toMatchObject({
      tagMode: 'any',
      archived: 'active',
      album,
      playlist,
      models: ['chirp-v4', 'chirp-v5'],
      createdFrom: '2026-01-10',
      createdTo: '2026-01-20',
      minRating: 4,
      rated: 'none',
      selected: 'no',
      audio: 'unavailable',
    });
    expect(activeFilterCount(query)).toBe(14);
    expect(songListParameters(query).toString()).toBe(search);
  });

  it('reads and writes the #228 generations filter after the #225 ones, and counts it', () => {
    const search = 'archived=active&selected=no&generations=some';

    const query = songQueryFrom(new URLSearchParams(search));
    expect(query).toMatchObject({ archived: 'active', selected: 'no', generations: 'some' });
    expect(activeFilterCount(query)).toBe(3);
    expect(songListParameters(query).toString()).toBe(search);
    expect(songQueryFrom(new URLSearchParams('generations=none')).generations).toBe('none');
    // A value the list would refuse is left out.
    expect(songQueryFrom(new URLSearchParams('generations=all')).generations).toBeUndefined();
  });

  it('leaves out filter values the list would refuse, so a hand-edited address still shows a table', () => {
    const query = songQueryFrom(
      new URLSearchParams(
        'tagMode=some&archived=both&model=&createdFrom=20260110&createdTo=2026-1-2&minRating=6&rated=yes&selected=maybe&audio=missing',
      ),
    );

    expect(songListParameters(query).toString()).toBe('');
    expect(activeFilterCount(query)).toBe(0);
    // All of the Tags is the default and is not written; any of them needs Tags to mean anything.
    expect(songListParameters({ ...query, tagMode: 'any' }).toString()).toBe('');
  });

  it('reads and writes a search, ordered by relevance unless a sort is chosen', () => {
    const searched = songQueryFrom(new URLSearchParams('search=lantern+song&page=2'));

    expect(searched).toEqual({
      sort: 'relevance',
      direction: 'desc',
      states: [],
      genres: [],
      tags: [],
      artists: [],
      search: 'lantern song',
      page: 2,
    });
    // Relevance is never sent: the API orders a search by it when no sort is given.
    expect(songListParameters(searched).toString()).toBe('search=lantern+song&page=2');
    const sorted = 'search=lantern&sort=updated&direction=asc';
    expect(songListParameters(songQueryFrom(new URLSearchParams(sorted))).toString()).toBe(sorted);
    expect(songQueryFrom(new URLSearchParams('search=lantern&sort=updated')).sort).toBe('updated');
    // An address cannot ask for relevance, and without a search there is none.
    expect(songQueryFrom(new URLSearchParams('sort=relevance')).sort).toBe('updated');
    expect(songListParameters({ ...searched, search: undefined }).toString()).toBe('page=2');
  });

  it('reads a blank search as none, and cuts a long one to 200 characters', () => {
    expect(songQueryFrom(new URLSearchParams('search=++')).search).toBeUndefined();
    expect(songQueryFrom(new URLSearchParams(`search=${'b'.repeat(300)}`)).search).toBe(
      'b'.repeat(200),
    );
  });

  it('starts a title sort A to Z', () => {
    expect(songQueryFrom(new URLSearchParams('sort=title')).direction).toBe('asc');
  });

  it('ignores what it does not understand', () => {
    const query = songQueryFrom(
      new URLSearchParams(
        `sort=shortcode&direction=up&page=-2&state=${STATE}&state=${STATE}&genre=${GENRE}&genre=${GENRE}&tag=${TAG}&tag=${TAG}&artist=${ARTIST}&artist=${ARTIST}`,
      ),
    );

    expect(query).toEqual({
      sort: 'updated',
      direction: 'desc',
      states: [STATE],
      genres: [GENRE],
      tags: [TAG],
      artists: [ARTIST],
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
