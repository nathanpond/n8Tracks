import { describe, expect, it } from 'vitest';
import { parseProductVersion, resolveProductVersion } from './version.ts';

describe('parseProductVersion', () => {
  it('reads a release version', () => {
    expect(parseProductVersion('0.1.0\n', 'VERSION')).toEqual({
      full: '0.1.0',
      numeric: '0.1.0',
      isPreRelease: false,
    });
  });

  it('splits a pre-release version into its numeric part and the full string', () => {
    expect(parseProductVersion('1.2.3-edge.abc1234', 'VERSION')).toEqual({
      full: '1.2.3-edge.abc1234',
      numeric: '1.2.3',
      isPreRelease: true,
    });
  });

  it.each([
    '',
    '1.2',
    '1.2.3.4',
    'v1.2.3',
    '1.2.3-',
    '1.2.3+build',
    '1.2.x',
    '1.02.3',
    '1.2.65536',
  ])('rejects %j, naming where it came from', (text) => {
    expect(() => parseProductVersion(text, 'The VERSION file')).toThrow('The VERSION file must');
  });
});

describe('resolveProductVersion', () => {
  it('uses the VERSION file when the override is not set', () => {
    expect(resolveProductVersion({}, '0.1.0\n').full).toBe('0.1.0');
  });

  it('prefers N8TRACKS_VERSION when it is set', () => {
    const version = resolveProductVersion({ N8TRACKS_VERSION: '0.1.0-edge.abc1234' }, '0.1.0\n');

    expect(version).toEqual({ full: '0.1.0-edge.abc1234', numeric: '0.1.0', isPreRelease: true });
  });

  it.each(['', '   '])('treats an override of %j as unset', (override) => {
    expect(resolveProductVersion({ N8TRACKS_VERSION: override }, '0.1.0\n').full).toBe('0.1.0');
  });

  it('rejects a malformed override instead of falling back to the file', () => {
    expect(() => resolveProductVersion({ N8TRACKS_VERSION: 'edge' }, '0.1.0\n')).toThrow(
      'N8TRACKS_VERSION must',
    );
  });
});
