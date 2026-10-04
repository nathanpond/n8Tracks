import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { strFromU8, unzipSync } from 'fflate';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { packageExtension, zipFileName } from './package.ts';

describe('packageExtension', () => {
  let root: string;
  let dist: string;

  beforeEach(() => {
    root = mkdtempSync(join(tmpdir(), 'n8tracks-extension-package-'));
    dist = join(root, 'dist');
    mkdirSync(join(dist, 'popup'), { recursive: true });
    writeFileSync(join(dist, 'manifest.json'), '{"name":"n8Tracks"}');
    writeFileSync(join(dist, 'popup', 'popup.html'), '<!doctype html>');
  });

  afterEach(() => {
    rmSync(root, { recursive: true, force: true });
  });

  it('names the zip after the full version', () => {
    expect(zipFileName('0.1.0-edge.abc1234')).toBe('n8tracks-extension-0.1.0-edge.abc1234.zip');
  });

  it('puts the contents of dist at the root of the zip', () => {
    const zipPath = packageExtension(dist, root, '0.1.0');

    expect(zipPath).toBe(join(root, 'n8tracks-extension-0.1.0.zip'));
    const entries = unzipSync(readFileSync(zipPath));
    expect(Object.keys(entries).toSorted()).toEqual(['manifest.json', 'popup/popup.html']);
    expect(strFromU8(entries['manifest.json'] ?? new Uint8Array())).toBe('{"name":"n8Tracks"}');
  });

  it('deletes the zips of other versions and nothing else', () => {
    writeFileSync(join(root, 'n8tracks-extension-0.0.9.zip'), 'old');
    writeFileSync(join(root, 'notes.zip'), 'keep');

    packageExtension(dist, root, '0.1.0');

    expect(readdirSync(root).toSorted()).toEqual([
      'dist',
      'n8tracks-extension-0.1.0.zip',
      'notes.zip',
    ]);
  });

  it('refuses a folder that is not a built extension', () => {
    rmSync(join(dist, 'manifest.json'));

    expect(() => packageExtension(dist, root, '0.1.0')).toThrow('run the build first');
    expect(existsSync(join(root, 'n8tracks-extension-0.1.0.zip'))).toBe(false);
  });
});
