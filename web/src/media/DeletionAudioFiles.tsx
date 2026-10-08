import { Stack, Text } from '@mantine/core';
import type { LocalAudioFiles } from '../api/localAudioFiles';
import { deletionAudioFileLines, type DeletedOwner } from './deletionAudioFileRules';

/**
 * A deletion confirmation's local audio files (#213): how many, that they stay on disk and will appear
 * in Unmatched Files, and what a restore will not bring back. Nothing at all when there are none.
 */
export function DeletionAudioFiles({
  counts,
  owner,
}: {
  counts: LocalAudioFiles;
  owner: DeletedOwner;
}) {
  const lines = deletionAudioFileLines(counts, owner);
  return lines.length === 0 ? null : (
    <Stack gap={4} data-testid="deletion-audio-files">
      {lines.map((line) => (
        <Text key={line} size="sm">
          {line}
        </Text>
      ))}
    </Stack>
  );
}
