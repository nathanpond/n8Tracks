import { Button, Loader, Stack, Text, Title } from '@mantine/core';
import {
  downloadFormatLabel,
  mediaFolderText,
  useGenerationDownloads,
} from '../api/downloadRecords';
import type { Generation } from '../api/generations';
import { formatDateTime, useConfiguredTimeZone } from '../api/timeZone';

/**
 * The files the extension downloaded from Suno for this Generation's clip (#222), newest first:
 * each with its format, file name, and when it finished, and whether a scanned audio file of that
 * name is in the media folder and attached to this Generation. A download is not an audio file: it
 * says the file reached the user's computer, which is not the media folder.
 */
export function GenerationDownloads({ generation }: { generation: Generation }) {
  const timeZone = useConfiguredTimeZone();
  const { state, reload } = useGenerationDownloads(generation.id);
  const headingId = `generation-downloads-${generation.id}`;
  return (
    <Stack
      component="section"
      gap="xs"
      aria-labelledby={headingId}
      data-testid="generation-downloads"
    >
      <Title order={3} size="h5" id={headingId}>
        Downloads
      </Title>
      {state.phase === 'loading' && <Loader size="sm" aria-label="Loading the downloads" />}
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Stack gap="xs" align="flex-start">
          <Text size="sm" role="alert">
            The downloads could not be loaded.
          </Text>
          <Button variant="default" size="compact-sm" onClick={reload}>
            Try again
          </Button>
        </Stack>
      )}
      {state.phase === 'ready' && state.data.length === 0 && (
        <Text size="sm" c="var(--n8-color-secondary-text)">
          No downloads recorded. Files the extension downloads from Suno for this clip are listed
          here.
        </Text>
      )}
      {state.phase === 'ready' && state.data.length > 0 && (
        <Stack component="ul" gap="xs" m={0} p={0} style={{ listStyle: 'none' }}>
          {state.data.map((download) => (
            <li
              key={download.id}
              data-testid="generation-download"
              data-match={download.mediaFolder.match}
            >
              <Text size="sm" fw={600} style={{ overflowWrap: 'anywhere' }}>
                {download.fileName}
              </Text>
              <Text size="xs" c="var(--n8-color-secondary-text)">
                {downloadFormatLabel(download.format)} ·{' '}
                <time dateTime={download.completedAt}>
                  {formatDateTime(download.completedAt, timeZone)}
                </time>
                {download.spentUnlock ? ' · Used a Suno download unlock' : ''}
              </Text>
              <Text size="xs" data-testid="generation-download-media">
                {mediaFolderText(download)}
              </Text>
            </li>
          ))}
        </Stack>
      )}
    </Stack>
  );
}
