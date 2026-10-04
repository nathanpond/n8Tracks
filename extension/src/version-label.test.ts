import { describe, expect, it } from 'vitest';
import { displayVersion, versionLabel } from './version-label.ts';

describe('versionLabel', () => {
  it('prefixes the version with v', () => {
    expect(versionLabel('0.1.0')).toBe('v0.1.0');
  });

  it('keeps a pre-release suffix', () => {
    expect(versionLabel('0.1.0-edge.abc1234')).toBe('v0.1.0-edge.abc1234');
  });
});

describe('displayVersion', () => {
  it('is the manifest version for a release build', () => {
    expect(displayVersion({ name: 'n8Tracks', version: '0.1.0' })).toBe('0.1.0');
  });

  it('is the version name for a pre-release build', () => {
    expect(
      displayVersion({ name: 'n8Tracks', version: '0.1.0', version_name: '0.1.0-edge.abc1234' }),
    ).toBe('0.1.0-edge.abc1234');
  });
});
