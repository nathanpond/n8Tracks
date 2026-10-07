import type { CreateField } from '../api/createFields';
import type { ImportedInputs } from '../api/versions';

/** The words for an option the API names `key`: its field's label, else the key itself. */
export function importedOptionLabel(key: string, fields: readonly CreateField[]): string {
  const field = fields.find((candidate) =>
    key === 'lyrics' || key === 'styles' ? candidate.key === key : candidate.option === key,
  );
  return field?.label ?? key;
}

/** The out-of-range options as the notice lists them, an unknown choice with what Suno returned. */
export function outOfRangeText(imported: ImportedInputs, fields: readonly CreateField[]): string {
  return imported.outOfRange
    .map((key) => {
      const label = importedOptionLabel(key, fields);
      const raw = imported.rawValues[key];
      return raw === undefined ? label : `${label} (Suno returned ${raw})`;
    })
    .join(', ');
}
