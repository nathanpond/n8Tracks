import { describe, expect, it } from 'vitest';
import { explicitLabel, isrcError, linksText, normaliseIsrc, releaseEditOf } from './releaseField';

describe('the release field helpers', () => {
  it('normalise an ISRC as the API does: upper case, no spaces or hyphens, no leading ISRC', () => {
    expect(normaliseIsrc('us-s1z-99-00001')).toBe('USS1Z9900001');
    expect(normaliseIsrc(' ISRC US S1Z 99 00001 ')).toBe('USS1Z9900001');
    expect(normaliseIsrc('ISRC10000001')).toBe('ISRC10000001');
    expect(normaliseIsrc(' - ')).toBeNull();
  });

  it('check an ISRC by the API’s rule', () => {
    expect(isrcError('US-S1Z-99-00001')).toBeUndefined();
    expect(isrcError('')).toBeUndefined();
    expect(isrcError('US-S1Z-99-0000')).toBe(
      'An ISRC has 12 characters once spaces and hyphens are left out; this has 11.',
    );
    expect(isrcError('1SS1Z9900001')).toBe(
      'Enter an ISRC as two letters, three letters or digits, and seven digits (such as US-S1Z-99-00001).',
    );
  });

  it('turn the save helper’s release keys into the PATCH’s release object', () => {
    expect(releaseEditOf({ title: 'A' })).toBeUndefined();
    expect(
      releaseEditOf({
        title: 'A',
        'release.isrc': 'USS1Z9900001',
        'release.explicit': null,
        'release.links': '[{"label":null,"url":"https://a.example"}]',
      }),
    ).toEqual({
      isrc: 'USS1Z9900001',
      explicit: null,
      links: [{ label: null, url: 'https://a.example' }],
    });
    expect(releaseEditOf({ 'release.links': null })).toEqual({ links: [] });
  });

  it('write the explicit flag and links for the conflict dialog', () => {
    expect([explicitLabel('explicit'), explicitLabel('clean'), explicitLabel(null)]).toEqual([
      'Explicit',
      'Clean',
      'Not set',
    ]);
    expect(linksText('[]')).toBeNull();
    expect(
      linksText(
        '[{"label":"Spotify","url":"https://s.example"},{"label":null,"url":"https://b.example"}]',
      ),
    ).toBe('Spotify: https://s.example, https://b.example');
  });
});
