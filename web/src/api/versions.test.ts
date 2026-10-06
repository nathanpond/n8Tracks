import { describe, expect, it } from 'vitest';
import { testVersion } from '../test/versionServer';
import {
  isLineageKey,
  isVersionDetail,
  optionFromText,
  optionText,
  versionEditOf,
} from './versions';

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

describe("a Version's inputs with its lineage (#122)", () => {
  const lineage = {
    sources: [{ typeId: 'type', sunoAction: 'cover', external: { sunoId: 'clip' } }],
    inspiration: null,
    voice: { personaId: 'persona', name: '' },
    fileInputs: [],
  };

  it('reads a Version whose inputs hold the lineage keys beside the options', () => {
    const version = testVersion('1');
    expect(isVersionDetail({ ...version, inputs: { ...version.inputs, ...lineage } })).toBe(true);
    expect(['sources', 'inspiration', 'voice', 'fileInputs'].every(isLineageKey)).toBe(true);
    expect(isLineageKey('weirdness')).toBe(false);
  });

  it('complement: an option that is not a plain value, or a lineage key that is not an object, is refused', () => {
    const version = testVersion('1');
    expect(isVersionDetail({ ...version, inputs: { ...version.inputs, weirdness: [50] } })).toBe(
      false,
    );
    expect(isVersionDetail({ ...version, inputs: { ...version.inputs, sources: 'cover' } })).toBe(
      false,
    );
  });
});
