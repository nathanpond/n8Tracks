import { describe, expect, it } from 'vitest';
import { optionFromText, optionText, versionEditOf } from './versions';

describe('an edit of a Version', () => {
  it('sends each option the save helper holds as inputs.<key> in inputs, as its value', () => {
    expect(
      versionEditOf({
        name: 'Take two',
        'inputs.weirdness': optionText(80),
        'inputs.vocalGender': optionText(null),
        'inputs.maxMode': optionText(true),
        'inputs.title': optionText('For Suno'),
      }),
    ).toEqual({
      name: 'Take two',
      inputs: { weirdness: 80, vocalGender: null, maxMode: true, title: 'For Suno' },
    });
  });

  it('complement: without options, sends no inputs', () => {
    expect(versionEditOf({ lyrics: 'la', notes: null })).toEqual({ lyrics: 'la', notes: null });
  });

  it('holds an option as its JSON, so 80 and "80" differ', () => {
    expect(optionText(80)).not.toBe(optionText('80'));
    expect(optionFromText(optionText('80'))).toBe('80');
    expect(optionText(undefined)).toBe('null');
  });
});
