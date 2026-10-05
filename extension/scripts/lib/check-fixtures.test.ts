import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import {
  fixturesDirectory,
  importMapPath,
  inventoryPath,
  missingImportMapKeys,
  readRedactionTerms,
  scanFixtures,
  scanFixtureText,
} from './check-fixtures.ts';

describe('missingImportMapKeys', () => {
  it('names inventory keys the map lacks', () => {
    const inventory = { fields: [{ key: 'title' }, { key: 'weirdness' }] };
    expect(missingImportMapKeys(inventory, { fields: { title: {} } })).toEqual(['weirdness']);
  });
});

describe('scanFixtureText', () => {
  it('accepts placeholders and addresses without query strings', () => {
    const text =
      '{"id":"00000000-0000-4000-8000-000000000101","url":"https://cdn2.suno.ai/image.jpeg"}';
    expect(scanFixtureText('a.json', text, [])).toEqual([]);
  });

  it('reports a real-looking identifier, a query string, and a listed term without echoing the term', () => {
    const text =
      'id 3f2a9c1e-1111-4abc-9def-0123456789ab at https://x.example/a?sig=1 by Someone Private';
    const problems = scanFixtureText('a.json', text, ['someone private']).map(
      (finding) => finding.problem,
    );
    expect(problems).toHaveLength(3);
    expect(problems.join(' ')).not.toContain('Someone');
  });
});

describe('the committed Suno fixtures', () => {
  it('map every Create-screen inventory key in the import field map', () => {
    const read = (file: string) =>
      JSON.parse(readFileSync(file, 'utf8')) as Parameters<typeof missingImportMapKeys>[0];
    expect(missingImportMapKeys(read(inventoryPath), read(importMapPath))).toEqual([]);
  });

  it('carry no real identifiers, query strings, or (locally) redaction terms', () => {
    const terms = readRedactionTerms();
    if (terms === undefined) {
      console.log('No local redaction-terms.txt: checked identifiers and query strings only.');
    }
    expect(scanFixtures(fixturesDirectory, terms ?? [])).toEqual([]);
  });
});
