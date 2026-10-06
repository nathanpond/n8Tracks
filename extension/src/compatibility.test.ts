import { describe, expect, it } from 'vitest';
import { compatibilityOf, isCompatible, mismatchWarning, parseVersion } from './compatibility.ts';

describe('isCompatible', () => {
  it.each([
    ['1.4.0', '1.4.7', true],
    ['1.4.7', '1.4.0', true],
    ['1.4', '1.4.9', true],
    ['1.4', '1.5', false],
    ['1.4.2', '1.5.0', false],
    ['1.9.0', '2.9.0', false],
    ['2.0.0', '1.0.0', false],
    ['1.4.0-rc.1', '1.4.3', true],
    ['1.4.0', '1.4.0-edge.abc1234', true],
    ['1.5.0-rc.1', '1.4.0', false],
    ['v1.4.0', '1.4.0', false],
    ['1', '1.0.0', false],
    ['', '1.0.0', false],
    ['1.4.0', 'not a version', false],
  ])('%s with %s is %s', (extension, application, expected) => {
    expect(isCompatible(extension, application)).toBe(expected);
  });

  it('reads major, minor, and patch, with a missing patch as 0', () => {
    expect(parseVersion('1.4')).toEqual({ major: 1, minor: 4, patch: 0 });
    expect(parseVersion(' 0.1.0-rc.1 ')).toEqual({ major: 0, minor: 1, patch: 0 });
    expect(parseVersion(null)).toBeNull();
  });
});

describe('the mismatch warning', () => {
  it('is null when the versions fit', () => {
    expect(mismatchWarning(compatibilityOf('1.4.2', '1.4.0', true))).toBeNull();
  });

  it('names both versions and says to update the extension when it is older', () => {
    expect(mismatchWarning(compatibilityOf('1.4.2', '1.5.0', false))).toBe(
      'Extension 1.4.2 is older than n8Tracks 1.5.0: update the extension.',
    );
  });

  it('says to update n8Tracks when the extension is newer', () => {
    expect(mismatchWarning(compatibilityOf('2.0.0', '1.9.3', false))).toBe(
      'Extension 2.0.0 is newer than n8Tracks 1.9.3: update n8Tracks.',
    );
  });

  it('shows an unreadable application version as unknown, and still names both', () => {
    expect(mismatchWarning(compatibilityOf('1.4.2', '0.0.0-dev+local', false))).toBe(
      'Extension 1.4.2 and n8Tracks of an unknown version (0.0.0-dev+local) may not work together: their versions could not be compared. Update whichever is older.',
    );
  });

  it('counts the server finding a mismatch as one, even when the numbers look alike', () => {
    expect(compatibilityOf('1.4.2', '1.4.0', false)).toEqual({
      kind: 'mismatch',
      extensionVersion: '1.4.2',
      applicationVersion: '1.4.0',
      update: 'unknown',
    });
  });
});
