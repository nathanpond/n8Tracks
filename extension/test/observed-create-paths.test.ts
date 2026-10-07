import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { CREATE_REQUEST_PATHS, NEVER_FORWARDED } from '../src/adapter/observed.ts';
import { extensionRoot } from '../scripts/lib/build.ts';

const mapPath = join(extensionRoot, '..', 'docs', 'suno-import-field-map.json');

/** Left out on purpose: an uploaded image's bytes are never sent on (n8Tracks keeps a note only). */
const NOT_FORWARDED = new Set(['user_uploaded_images_b64']);

/** Read besides the fields: the mode marker the field map's `modeMarkers` name. */
const MODE_MARKER = 'metadata.create_mode';

interface MapEntry {
  paths?: { createRequest?: string | null };
  fileInput?: { paths?: { createRequest?: string | null } };
}

/** Every `createRequest` path of the import field map's fields. */
function mappedPaths(): string[] {
  const map = JSON.parse(readFileSync(mapPath, 'utf8')) as { fields: Record<string, MapEntry> };
  return Object.values(map.fields).flatMap((entry) =>
    [entry.paths?.createRequest, entry.fileInput?.paths?.createRequest].filter(
      (path): path is string => typeof path === 'string',
    ),
  );
}

describe('the Create request values the observer forwards (#149)', () => {
  it('are the import field map’s createRequest paths and the mode marker, and nothing else', () => {
    const expected = new Set(mappedPaths().filter((path) => !NOT_FORWARDED.has(path)));
    expected.add(MODE_MARKER);

    expect([...CREATE_REQUEST_PATHS].sort()).toEqual([...expected].sort());
    // Complement: the map does have paths the list would miss if it shrank.
    expect(mappedPaths().length).toBeGreaterThan(15);
  });

  it('never name a secret', () => {
    for (const path of CREATE_REQUEST_PATHS) {
      for (const part of path.split('.')) {
        expect(NEVER_FORWARDED.has(part)).toBe(false);
      }
    }
  });
});
