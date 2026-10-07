import { describe, expect, it } from 'vitest';
import type { ImportChoice } from '../api/sunoImports';
import { testRecord, toNewSong } from '../test/importServer';
import {
  choiceText,
  commitReasonText,
  createdText,
  diffValueText,
  durationText,
  excludedKindsText,
  fieldsText,
  filterFrom,
  groupRecords,
  outcomesText,
  reasonText,
  reasonsText,
  statusResultText,
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

describe('the result of a confirmed import (#140)', () => {
  it('says what was created, with plurals', () => {
    expect(createdText({ songs: 1, versions: 2, generations: 3 })).toBe(
      'Created 1 Song, 2 Versions, and 3 Generations.',
    );
    expect(createdText({ songs: 0, versions: 0, generations: 0 })).toBe(
      'Created 0 Songs, 0 Versions, and 0 Generations.',
    );
  });

  it('says which Generations took Suno’s final status (#314), leaving out a kind none had', () => {
    expect(statusResultText([{ status: 'complete' }])).toBe(
      'Updated Suno’s status: 1 Generation finished in Suno.',
    );
    expect(statusResultText([{ status: 'error' }, { status: 'error' }])).toBe(
      'Updated Suno’s status: 2 Generations failed in Suno.',
    );
  });

  it('counts the outcomes, leaving out those no record had', () => {
    expect(
      outcomesText([{ outcome: 'created' }, { outcome: 'created' }, { outcome: 'linked' }]),
    ).toBe('2 records imported, 1 record already linked.');
    expect(outcomesText([])).toBe('No records.');
  });

  it('words each commit reason, falling back to the review’s words', () => {
    expect(commitReasonText('tombstoned')).toMatch(/deleted from n8Tracks/);
    expect(commitReasonText('number_taken')).toMatch(/took the next one/);
    expect(commitReasonText('invalid_clip')).toMatch(/could not keep/);
    expect(commitReasonText('inputs_differ')).toMatch(/no longer match/);
    expect(commitReasonText('target_missing')).toBe('the Song or Version chosen no longer exists');
  });
});

describe('Changed and Conflict records (#141)', () => {
  it('says what a resolution choice will do', () => {
    const changed = (choice: ImportChoice) =>
      choiceText(testRecord('c', { class: 'changed', choice }));
    expect(changed({ action: 'skip' })).toBe('Left as it is');
    expect(changed({ action: 'apply', acceptFields: [] })).toBe('Keep n8Tracks’ data');
    expect(changed({ action: 'apply', acceptFields: ['title', 'tags', 'key'] })).toBe(
      'Take Title, Style tags and Key from Suno',
    );
    const conflict = testRecord('k', {
      class: 'conflict',
      choice: { action: 'moveToNewVersion', acceptFields: ['title'] },
    });
    expect(choiceText(conflict)).toBe('Move to a new Version, and take Title from Suno');
    expect(choiceText({ ...conflict, choice: { action: 'keep', acceptFields: [] } })).toBe(
      'Keep it where it is',
    );
  });

  it('writes diffed values and outcomes in words', () => {
    expect(diffValueText('duration', 125)).toBe('2:05');
    expect(diffValueText('averageBpm', 120.5)).toBe('120.5');
    expect(diffValueText('title', null)).toBe('None');
    expect(fieldsText(['imageUrl'])).toBe('Cover image');
    expect(
      outcomesText([
        { outcome: 'updated' },
        { outcome: 'declined' },
        { outcome: 'kept' },
        { outcome: 'moved' },
      ]),
    ).toBe(
      '1 record updated from Suno, 1 record kept as they were, 1 record kept on their Version, 1 record moved to a new Version.',
    );
    expect(reasonText('field_not_changed')).toMatch(/does not differ/);
  });
});
