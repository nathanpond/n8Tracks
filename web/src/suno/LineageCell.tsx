import { Anchor, Button, Stack, Text } from '@mantine/core';
import { Link } from 'react-router';
import type { ImportChoice, ImportLineage } from '../api/sunoImports';
import { includable, includeChoice, lineageLines } from './lineageRules';

/**
 * A record's Lineage cell in the import review (#153): what its clip was made from ("Cover of <title>:
 * not in this sync", "Mashup of A + B", "Voice: X"), a link to each source that is a Generation
 * already, and, for a source that is a record of this export not chosen for import, a control that
 * includes it: it changes that record's choice only. Empty for a record made from nothing.
 */
export function LineageCell({
  lineage,
  nextKey,
  busy,
  onInclude,
}: {
  lineage: ImportLineage | null | undefined;
  nextKey: string;
  busy: boolean;
  onInclude: (sunoId: string, choice: ImportChoice) => void;
}) {
  const lines = lineageLines(lineage);
  if (lineage === null || lineage === undefined || lines.length === 0) {
    return null;
  }
  const linked = lineage.sources.filter((source) => source.generation !== null);
  return (
    <Stack gap={2} data-testid="record-lineage">
      {lines.map((line) => (
        <Text key={line} size="sm" data-testid="lineage-line">
          {line}
        </Text>
      ))}
      {linked.map((source) =>
        source.generation === null ? null : (
          <Anchor
            key={`${source.group}-${source.sunoId}`}
            component={Link}
            size="sm"
            underline="always"
            to={`/songs/${source.generation.songShortcode}/generations/${source.generation.shortcode}`}
          >
            {source.title}: Generation {source.generation.shortcode}
          </Anchor>
        ),
      )}
      {includable(lineage).map((source) =>
        source.record === null ? null : (
          <div key={`include-${source.sunoId}`}>
            <Button
              variant="default"
              size="compact-xs"
              disabled={busy}
              aria-label={`Include ${source.title} in this import`}
              onClick={() => {
                if (source.record !== null) {
                  onInclude(source.record.sunoId, includeChoice(source.record, nextKey));
                }
              }}
            >
              Include {source.title}
            </Button>
          </div>
        ),
      )}
    </Stack>
  );
}
