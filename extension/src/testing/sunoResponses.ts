/** The TS-003 JSON fixtures in `fixtures/suno/`, by file name without `.json`. */
const responses = import.meta.glob<unknown>('../../fixtures/suno/*.json', {
  import: 'default',
  eager: true,
});

/** A fresh copy of a fixture: `sunoFixture('feed-v3.library-page-1.response')`. */
export function sunoFixture(name: string): unknown {
  const value = responses[`../../fixtures/suno/${name}.json`];
  if (value === undefined) {
    throw new Error(`There is no fixture ${name}.json.`);
  }
  return structuredClone(value);
}

/** A fixture read as a JSON object. */
export function sunoObject(name: string): Record<string, unknown> {
  const value = sunoFixture(name);
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new Error(`The fixture ${name}.json is not an object.`);
  }
  return value as Record<string, unknown>;
}
