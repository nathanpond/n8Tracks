// Checks for the Suno fixtures and the import field map (spike TS-003, #127).
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';

export const repoRoot = path.resolve(import.meta.dirname, '../../..');
export const fixturesDirectory = path.join(repoRoot, 'extension/fixtures/suno');
export const inventoryPath = path.join(repoRoot, 'docs/suno-create-field-inventory.json');
export const importMapPath = path.join(repoRoot, 'docs/suno-import-field-map.json');
// Local only: docs/.notes/ is gitignored, so CI never has this file.
export const redactionTermsPath = path.join(repoRoot, 'docs/.notes/suno/redaction-terms.txt');

const uuidShape = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi;
const placeholderUuid = /^00000000-0000-4000-8000-[0-9a-f]{12}$/;
const addressWithQuery = /https?:\/\/[^\s"'<>()]*\?[^\s"'<>()]+/g;

export interface Finding {
  file: string;
  problem: string;
}

interface KeyedFile {
  fields: Record<string, unknown> | { key: string }[];
}

function keysOf(file: KeyedFile): string[] {
  return Array.isArray(file.fields)
    ? file.fields.map((field) => field.key)
    : Object.keys(file.fields);
}

/** Inventory keys with no entry in the import field map. */
export function missingImportMapKeys(inventory: KeyedFile, importMap: KeyedFile): string[] {
  const mapped = new Set(keysOf(importMap));
  return keysOf(inventory).filter((key) => !mapped.has(key));
}

/** Problems in one fixture's text: real-looking IDs, query strings, and any listed term. */
export function scanFixtureText(file: string, text: string, terms: readonly string[]): Finding[] {
  const findings: Finding[] = [];
  for (const match of text.matchAll(uuidShape)) {
    if (!placeholderUuid.test(match[0].toLowerCase())) {
      findings.push({ file, problem: `identifier that is not a placeholder: ${match[0]}` });
    }
  }
  for (const match of text.matchAll(addressWithQuery)) {
    findings.push({ file, problem: `address with a query string: ${match[0].slice(0, 80)}` });
  }
  const lowered = text.toLowerCase();
  for (const term of terms) {
    if (lowered.includes(term.toLowerCase())) {
      // The term itself is private, so only its length is reported.
      findings.push({ file, problem: `contains a redaction term (${String(term.length)} chars)` });
    }
  }
  return findings;
}

/** Reads the local term list; returns undefined when it is absent (as in CI). */
export function readRedactionTerms(termsPath: string = redactionTermsPath): string[] | undefined {
  if (!existsSync(termsPath)) {
    return undefined;
  }
  return readFileSync(termsPath, 'utf8')
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length >= 4 && !line.startsWith('#'));
}

export function scanFixtures(directory: string, terms: readonly string[]): Finding[] {
  return readdirSync(directory)
    .filter((name) => name !== 'README.md')
    .flatMap((name) =>
      scanFixtureText(name, readFileSync(path.join(directory, name), 'utf8'), terms),
    );
}
