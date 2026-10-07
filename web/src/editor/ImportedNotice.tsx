import { Text } from '@mantine/core';
import type { CreateField } from '../api/createFields';
import type { ImportedInputs } from '../api/versions';
import { Notice } from '../components/Notice';
import { importedOptionLabel, outOfRangeText } from './importedInputs';

/**
 * The small notice on a Version created from a Suno clip (#135): which values Suno returned outside
 * n8Tracks' limits, kept as Suno returned them, and which settings Suno does not return, so they
 * show n8Tracks' defaults rather than what produced the Generation. Nothing shows for a Version
 * made in n8Tracks, or for an imported one with nothing to say.
 */
export function ImportedNotice({
  imported,
  fields,
}: {
  imported: ImportedInputs | null | undefined;
  fields: readonly CreateField[];
}) {
  if (!imported || (imported.outOfRange.length === 0 && imported.notReturned.length === 0)) {
    return null;
  }

  return (
    <div data-testid="imported-notice">
      <Notice title="Imported from Suno">
        {imported.outOfRange.length > 0 && (
          <Text size="sm" data-testid="imported-out-of-range">
            Outside n8Tracks&apos; limits, kept as Suno returned them:{' '}
            {outOfRangeText(imported, fields)}.
          </Text>
        )}
        {imported.notReturned.length > 0 && (
          <Text size="sm" data-testid="imported-not-returned">
            Not returned by Suno, so shown at n8Tracks&apos; defaults:{' '}
            {imported.notReturned.map((key) => importedOptionLabel(key, fields)).join(', ')}.
          </Text>
        )}
      </Notice>
    </div>
  );
}
