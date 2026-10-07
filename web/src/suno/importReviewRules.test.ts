import { describe, expect, it } from 'vitest';
import { testRecord, toNewSong } from '../test/importServer';
import {
  choiceText,
  durationText,
  excludedKindsText,
  filterFrom,
  groupRecords,
  reasonsText,
  targetText,
} from './importReviewRules';

describe('the review rules', () => {
  it('groups records going to the same target where the first of them is, and lists the others alone', () => {
    const records = [
      toNewSong(testRecord('a'), 'new:1', 'One'),
      testRecord('skip'),
      toNewSong(testRecord('b'), 'new:2', 'Two'),
      toNewSong(testRecord('c'), 'new:1', 'One'),
    ];

    const groups = groupRecords(records);

    expect(
      groups.map((group) => [group.heading, group.records.map((record) => record.sunoId)]),
    ).toEqual([
      ['New Song “One”', ['a', 'c']],
      [null, ['skip']],
      ['New Song “Two”', ['b']],
    ]);
  });

  it('names stems once among the kinds Suno’s filters left out, and keeps an unknown filter by its name', () => {
    expect(
      excludedKindsText(['disliked', 'fromStudioProject', 'stem', 'stemComplement', 'other']),
    ).toEqual(['disliked songs', 'clips made in Studio projects', 'stems', 'other']);
  });

  it('says what happens to a record by its class and choice', () => {
    expect(choiceText(testRecord('n'))).toBe('Skip this time');
    expect(choiceText(testRecord('i', { class: 'ignored' }))).toBe('Don’t copy');
    expect(choiceText(testRecord('n', { choice: { action: 'ignore' } }))).toBe('Don’t copy');
    expect(choiceText(toNewSong(testRecord('d', { class: 'deleted' }), 'new:1', 'Back'))).toBe(
      'Reimport: New Song “Back”',
    );
    expect(choiceText(testRecord('l', { class: 'linked' }))).toBe('Already in n8Tracks');
    expect(choiceText(testRecord('c', { class: 'conflict' }))).toBe('Left as it is');
  });

  it('names a new Version of a Song, and a Version that is gone', () => {
    const song = { id: 's', key: null, shortcode: 'n8-3', title: 'Song' };
    const parent = { id: 'p', number: '1', shortcode: 'n8-3-v1', isFrozen: true };
    expect(
      targetText({ kind: 'newVersion', key: 'new:2', song, version: null, parent, number: '1.1' }),
    ).toBe('New Version 1.1 of n8-3 “Song”, from n8-3-v1');
    expect(
      targetText({
        kind: 'newVersion',
        key: 'new:2',
        song: { id: null, key: 'new:1', shortcode: null, title: 'Fresh' },
        version: null,
        parent: null,
        number: '2',
      }),
    ).toBe('New Version 2 of new Song “Fresh”');
    expect(
      targetText({ kind: 'version', key: null, song, version: null, parent: null, number: null }),
    ).toBe('A Version that no longer exists');
  });

  it('reads the filters from the address, leaving out empty and unknown values', () => {
    expect(filterFrom(new URLSearchParams('class=deleted&workspace=studio&q=rain&page=2'))).toEqual(
      {
        class: 'deleted',
        workspace: 'studio',
        q: 'rain',
      },
    );
    expect(filterFrom(new URLSearchParams('class=everything&playlist='))).toEqual({});
  });

  it('writes a length as minutes and seconds, and reasons in words', () => {
    expect(durationText(125.4)).toBe('2:05');
    expect(durationText(null)).toBe('');
    expect(reasonsText(['invalid_number', 'something_new'])).toBe(
      'that Version number is taken or not allowed; something_new',
    );
  });
});
