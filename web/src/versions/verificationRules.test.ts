import { describe, expect, it } from 'vitest';
import { entryDetail, entryLabel, modeLabel, valueText } from './verificationRules';

/** The Version page's words for the Speech and Sounds summaries (#147). */
describe('the verification summary’s words for Speech and Sounds', () => {
  it('names each Speech and Sounds field as Suno’s form does', () => {
    expect(
      [
        'speech.simple.speech_prompt',
        'speech.advanced.speech_script',
        'speech.advanced.speech_tone',
        'speech.advanced.speech_vocal_gender',
        'speech.advanced.speech_background_music',
        'speech.advanced.speech_variety',
        'sounds.single.sounds_model',
        'sounds.single.sound_description',
        'sounds.single.sound_type',
        'sounds.single.sound_bpm',
        'sounds.single.sound_key',
        'sounds.single.sound_scale',
      ].map(entryLabel),
    ).toEqual([
      'Speech description',
      'Script',
      'Tone',
      'Vocal Gender',
      'Background music',
      'Variety',
      'Model',
      'Sound',
      'Type',
      'BPM',
      'Key',
      'Key scale',
    ]);
    // Complement: a key with no label is shown as it is.
    expect(entryLabel('sounds.single.crop')).toBe('sounds.single.crop');
  });

  it('names Sounds’ one form, and Speech’s modes as Songs’', () => {
    expect([modeLabel('simple'), modeLabel('advanced'), modeLabel('single')]).toEqual([
      'Simple',
      'Advanced',
      'Sounds',
    ]);
  });

  it('words Speech’s Variety by its step, and an empty BPM as none', () => {
    expect(valueText('speech.advanced.speech_variety', 4)).toBe('max');
    expect(
      entryDetail({
        key: 'sounds.single.sound_bpm',
        outcome: 'failed',
        expected: null,
        found: 120,
      }),
    ).toBe('expected none, found 120');
  });
});
