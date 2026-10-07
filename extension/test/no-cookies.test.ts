import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import { extensionRoot } from '../scripts/lib/build.ts';

const sourceRoot = join(extensionRoot, 'src');

/** Every file under `src/`, tests included: none may read cookies, even to check for them. */
function sourceFiles(directory: string): string[] {
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name);
    return statSync(path).isDirectory() ? sourceFiles(path) : [path];
  });
}

/** What would read or ask for cookies, written so that this file does not match itself. */
const forbidden = [
  ['document', 'cookie'].join('.'),
  ['chrome', 'cookies'].join('.'),
  ['browser', 'cookies'].join('.'),
];

describe('the extension source', () => {
  const files = sourceFiles(sourceRoot);

  it('never reads document cookies or the cookies API', () => {
    const found = files.flatMap((file) => {
      const text = readFileSync(file, 'utf8');
      return forbidden
        .filter((pattern) => text.includes(pattern))
        .map((pattern) => `${relative(extensionRoot, file)}: ${pattern}`);
    });

    expect(found).toEqual([]);
    // Complement: the scan read the source it guards.
    expect(files.map((file) => relative(sourceRoot, file))).toContain('background/apiClient.ts');
    expect(files.length).toBeGreaterThan(10);
  });

  it('never asks for the cookies permission', () => {
    const manifest = JSON.parse(readFileSync(join(extensionRoot, 'manifest.json'), 'utf8')) as {
      permissions?: string[];
      optional_permissions?: string[];
    };

    expect([
      ...(manifest.permissions ?? []),
      ...(manifest.optional_permissions ?? []),
    ]).not.toContain('cookies');
  });
});
