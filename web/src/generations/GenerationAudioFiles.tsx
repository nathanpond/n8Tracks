import { Button, Loader, Stack, Text, Title } from '@mantine/core';
import type { UnmatchedFile } from '../api/audioFiles';
import type { Generation } from '../api/generations';
import type { LoadState } from '../api/songs';
import {
  AudioFileActionButtons,
  AudioFilePlayback,
  AudioFileStatusMark,
  type AudioFileActions,
} from '../media/AudioFilesSection';
import {
  fileDurationText,
  formatLabel,
  originLabel,
  playsNow,
  sizeMbText,
  songFolderText,
} from '../media/songAudioFilesRules';

/**
 * The Generation panel's local audio files (#211): the Song's list filtered here to this Generation,
 * in the Song list's order (by format), each with its file name, folder, format, duration, size,
 * status (Missing and Unavailable ones listed and marked), how it was associated, what it is for
 * playback (#212: preferred, plays now, and why the two differ), and its Make preferred (or Clear
 * preferred), Change association, and Remove association actions. A Generation with none says so.
 */
export function GenerationAudioFiles({
  generation,
  files,
  onRetry,
  actions,
}: {
  generation: Generation;
  files: LoadState<UnmatchedFile[]>;
  onRetry: () => void;
  actions: AudioFileActions;
}) {
  const headingId = `generation-audio-files-${generation.id}`;
  const own =
    files.phase === 'ready'
      ? files.data.filter((file) => file.generation?.id === generation.id)
      : [];
  return (
    <Stack
      component="section"
      gap="xs"
      aria-labelledby={headingId}
      data-testid="generation-audio-files"
    >
      <Title order={3} size="h5" id={headingId}>
        Local audio files
      </Title>
      {files.phase === 'loading' && <Loader size="sm" aria-label="Loading the audio files" />}
      {(files.phase === 'error' || files.phase === 'not-found') && (
        <Stack gap="xs" align="flex-start">
          <Text size="sm" role="alert">
            The audio files could not be loaded.
          </Text>
          <Button variant="default" size="compact-sm" onClick={onRetry}>
            Try again
          </Button>
        </Stack>
      )}
      {files.phase === 'ready' && own.length === 0 && (
        <Text size="sm" c="var(--n8-color-secondary-text)" data-testid="no-generation-audio-files">
          No local audio files
        </Text>
      )}
      {own.length > 0 && (
        <Stack component="ul" gap="sm" m={0} p={0} style={{ listStyle: 'none' }}>
          {own.map((file) => (
            <li
              key={file.id}
              data-testid="generation-audio-file"
              data-file={file.path}
              data-status={file.status}
              data-preferred={String(file.isPreferred)}
              data-plays-now={String(playsNow(file))}
            >
              <Stack gap={4}>
                <Text size="sm" fw={600} style={{ overflowWrap: 'anywhere' }}>
                  {file.fileName}
                </Text>
                <Text
                  size="xs"
                  c="var(--n8-color-secondary-text)"
                  style={{ overflowWrap: 'anywhere' }}
                >
                  {songFolderText(file)} · {formatLabel(file.format)} ·{' '}
                  {fileDurationText(file.durationSeconds)} · {sizeMbText(file.sizeBytes)} ·{' '}
                  {originLabel(file.associationOrigin)}
                </Text>
                <div>
                  <AudioFileStatusMark file={file} />
                </div>
                <AudioFilePlayback file={file} files={files.phase === 'ready' ? files.data : []} />
                <AudioFileActionButtons file={file} actions={actions} />
              </Stack>
            </li>
          ))}
        </Stack>
      )}
    </Stack>
  );
}
