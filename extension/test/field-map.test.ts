import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { FIELD_MAP, type FieldMapEntry } from '../src/adapter/fieldMap.ts';
import { extensionRoot } from '../scripts/lib/build.ts';

const documentPath = join(extensionRoot, '..', 'docs', 'suno-adapter-field-map.md');

/** The entries of the document's tables: first column the entry, second the field, third `how`. */
function documentEntries(markdown: string): FieldMapEntry[] {
  return markdown
    .split('\n')
    .filter((line) => /^\|\s*`/.test(line))
    .map((line) => {
      const cells = line
        .trim()
        .replace(/^\||\|$/g, '')
        .split('|')
        .map((cell) => cell.trim().replace(/^`|`$/g, ''));
      return {
        entry: cells[0] ?? '',
        field: cells[1] ?? '',
        how: (cells[2] ?? '') as FieldMapEntry['how'],
      };
    });
}

/** Every way the document and the code differ: a missing or extra entry, a field, or a `how`. */
function differences(markdown: string, code: readonly FieldMapEntry[]): string[] {
  const documented = new Map(documentEntries(markdown).map((entry) => [entry.entry, entry]));
  const declared = new Map(code.map((entry) => [entry.entry, entry]));
  const problems: string[] = [];
  for (const [key, entry] of documented) {
    const mine = declared.get(key);
    if (mine === undefined) {
      problems.push(`${key} is in the document but not in fieldMap.ts`);
    } else if (mine.how !== entry.how || mine.field !== entry.field) {
      problems.push(
        `${key}: the document says ${entry.field}/${entry.how}, fieldMap.ts says ${mine.field}/${mine.how}`,
      );
    }
  }
  for (const key of declared.keys()) {
    if (!documented.has(key)) {
      problems.push(`${key} is in fieldMap.ts but not in the document`);
    }
  }
  if (documented.size !== documentEntries(markdown).length) {
    problems.push('the document lists an entry twice');
  }
  return problems;
}

describe('the adapter field map', () => {
  const markdown = readFileSync(documentPath, 'utf8');

  it('matches docs/suno-adapter-field-map.md entry for entry', () => {
    expect(differences(markdown, FIELD_MAP)).toEqual([]);
    expect(FIELD_MAP).toHaveLength(38);
    expect(markdown).toContain(`${String(FIELD_MAP.length)} entries.`);
  });

  it('fails when one how in a copy of the document is changed', () => {
    const changed = markdown.replace(
      '| `songs.advanced.styles` | `styles` | fill |',
      '| `songs.advanced.styles` | `styles` | manual |',
    );

    expect(changed).not.toBe(markdown);
    expect(differences(changed, FIELD_MAP)).toEqual([
      'songs.advanced.styles: the document says styles/manual, fieldMap.ts says styles/fill',
    ]);
  });

  it('fails on a missing and an extra entry', () => {
    const missing = markdown.replace(/^\| `sounds\.single\.sound_scale`.*\n/m, '');
    const extra = [
      ...FIELD_MAP,
      { entry: 'songs.simple.extra', field: 'extra', how: 'fill' as const },
    ];

    expect(differences(missing, FIELD_MAP)).toEqual([
      'sounds.single.sound_scale is in fieldMap.ts but not in the document',
    ]);
    expect(differences(markdown, extra)).toEqual([
      'songs.simple.extra is in fieldMap.ts but not in the document',
    ]);
  });
});
