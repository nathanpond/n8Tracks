import {
  Anchor,
  Badge,
  Button,
  Group,
  Loader,
  Paper,
  Stack,
  Table,
  Text,
  Title,
  VisuallyHidden,
} from '@mantine/core';
import { Link } from 'react-router';
import { UNMATCHED_PATH, type UnmatchedFile } from '../api/audioFiles';
import type { Generation } from '../api/generations';
import type { LoadState } from '../api/songs';
import type { Version } from '../api/versions';
import { Notice } from '../components/Notice';
import { PlayButton } from '../player/PlayButton';
import {
  fileDurationText,
  formatLabel,
  originLabel,
  playsNow,
  preferenceNote,
  preferenceOwnerText,
  sizeMbText,
  songFolderText,
  statusText,
} from './songAudioFilesRules';

/**
 * What a listed file offers: #210's association, opened from the Song page, and #212's choice of the
 * file that plays for its Generation or, Song-level, for the Song.
 */
export interface AudioFileActions {
  busy: boolean;
  /** Opens the association dialog with the file's current association. */
  onChange: (file: UnmatchedFile) => void;
  onRemove: (file: UnmatchedFile) => void;
  /** Makes the file its Generation's (or, Song-level, its Song's) preferred file. */
  onPrefer: (file: UnmatchedFile) => void;
  /** Clears the choice the file holds, so the automatic choice plays. */
  onClearPreferred: (file: UnmatchedFile) => void;
}

/**
 * What a file is for playback (#212): "Preferred" when it is its owner's choice, "Plays now" when it is
 * what plays for its Generation (or, Song-level, for the Song), "Plays for the Song" when a Generation's
 * file is also what the Song plays, and, when the preferred file is not the one playing, why and what
 * plays instead. Nothing for a file that is none of these.
 */
export function AudioFilePlayback({
  file,
  files,
}: {
  file: UnmatchedFile;
  files: readonly UnmatchedFile[];
}) {
  const now = playsNow(file);
  const forSong = file.generation !== null && file.playsForSong === true;
  const note = preferenceNote(file, files);
  if (!file.isPreferred && !now && !forSong) {
    return null;
  }
  return (
    <Stack gap={4}>
      <Group gap={6} wrap="wrap">
        {file.isPreferred && (
          <Badge size="sm" variant="light" radius="sm" tt="none" data-testid="audio-file-preferred">
            Preferred
          </Badge>
        )}
        {now && (
          <Badge
            size="sm"
            variant="outline"
            radius="sm"
            tt="none"
            data-testid="audio-file-plays-now"
          >
            Plays now
          </Badge>
        )}
        {forSong && (
          <Badge
            size="sm"
            variant="outline"
            radius="sm"
            tt="none"
            data-testid="audio-file-plays-for-song"
          >
            Plays for the Song
          </Badge>
        )}
      </Group>
      {note !== undefined && (
        <Text size="xs" data-testid="audio-file-preference-note">
          {note}
        </Text>
      )}
    </Stack>
  );
}

/**
 * A file's status: Available as plain text; Missing or Unavailable as a filled badge, so a file that
 * cannot be played is never mistaken for one that can.
 */
export function AudioFileStatusMark({ file }: { file: UnmatchedFile }) {
  return file.status === 'available' ? (
    <Text size="sm" span data-testid="file-status" data-status={file.status}>
      {statusText(file.status)}
    </Text>
  ) : (
    <Badge
      variant="filled"
      radius="sm"
      tt="none"
      data-testid="file-status"
      data-status={file.status}
    >
      {statusText(file.status)}
    </Badge>
  );
}

/** The Change association and Remove association actions of one file. */
export function AudioFileActionButtons({
  file,
  actions,
}: {
  file: UnmatchedFile;
  actions: AudioFileActions;
}) {
  return (
    <Group gap="xs" wrap="wrap">
      {file.isPreferred ? (
        <Button
          size="compact-xs"
          variant="default"
          disabled={actions.busy}
          aria-label={`Clear preferred file of ${preferenceOwnerText(file)}: ${file.fileName}`}
          onClick={() => {
            actions.onClearPreferred(file);
          }}
        >
          Clear preferred
        </Button>
      ) : (
        <Button
          size="compact-xs"
          variant="default"
          disabled={actions.busy}
          aria-label={`Make preferred for ${preferenceOwnerText(file)}: ${file.fileName}`}
          onClick={() => {
            actions.onPrefer(file);
          }}
        >
          Make preferred
        </Button>
      )}
      <Button
        size="compact-xs"
        variant="default"
        disabled={actions.busy}
        aria-label={`Change association: ${file.fileName}`}
        onClick={() => {
          actions.onChange(file);
        }}
      >
        Change association
      </Button>
      <Button
        size="compact-xs"
        variant="default"
        disabled={actions.busy}
        aria-label={`Remove association: ${file.fileName}`}
        onClick={() => {
          actions.onRemove(file);
        }}
      >
        Remove association
      </Button>
    </Group>
  );
}

/** Whether the file's Generation, or that Generation's Version, is archived. */
function isArchivedOwner(
  file: UnmatchedFile,
  generations: readonly Generation[],
  versions: readonly Version[],
): boolean {
  const generation = generations.find((candidate) => candidate.id === file.generation?.id);
  if (generation === undefined) {
    return false;
  }
  const version = versions.find((candidate) => candidate.id === generation.version.id);
  return generation.state === 'archived' || version?.archived === true;
}

/** Where a file belongs: its Generation's shortcode, which opens the Generation panel, or "Song-level". */
function Owner({
  file,
  archived,
  generationLink,
}: {
  file: UnmatchedFile;
  archived: boolean;
  generationLink: (shortcode: string) => string;
}) {
  if (file.generation === null) {
    return (
      <Text size="sm" span data-testid="audio-file-generation" data-generation="">
        Song-level
      </Text>
    );
  }
  return (
    <Group
      gap={6}
      wrap="nowrap"
      data-testid="audio-file-generation"
      data-generation={file.generation.shortcode}
    >
      <Anchor
        component={Link}
        to={generationLink(file.generation.shortcode)}
        ff="monospace"
        size="sm"
      >
        {file.generation.shortcode}
      </Anchor>
      {archived && (
        <Badge size="sm" variant="default" radius="sm" tt="none">
          Archived
        </Badge>
      )}
    </Group>
  );
}

/**
 * The Song page's Audio Files section (#211), below the Versions table: every local audio file
 * associated with the Song, Song-level files first, then by Version tree order, Generation ordinal,
 * and format, as the API orders them. Each row has its file name, folder (relative to the media
 * folder, `/` for its root), format, duration, size, status (a Missing or Unavailable file is listed
 * and marked, never hidden), the Generation it belongs to (its shortcode opens the Generation panel;
 * one archived, or of an archived Version, is marked) or "Song-level", how it was associated, what
 * it is for playback (#212: preferred, plays now, and why the two differ), and its Make preferred (or
 * Clear preferred), Change association, and Remove association actions, and (#218) its Play control,
 * which plays that file (disabled while it is Missing or Unavailable). A Song with none says so and
 * links to Unmatched Files. `announcement` and `problem` say what the last action did.
 */
export function AudioFilesSection({
  files,
  onRetry,
  generations,
  versions,
  generationLink,
  actions,
  announcement,
  problem,
}: {
  files: LoadState<UnmatchedFile[]>;
  onRetry: () => void;
  generations: readonly Generation[];
  versions: readonly Version[];
  generationLink: (shortcode: string) => string;
  actions: AudioFileActions;
  announcement: string | undefined;
  problem: string | undefined;
}) {
  return (
    <Paper
      p="md"
      withBorder
      component="section"
      aria-labelledby="audio-files-heading"
      data-testid="song-audio-files"
    >
      <Stack gap="sm">
        <Title order={3} size="h4" id="audio-files-heading">
          Audio Files
        </Title>
        <Text size="sm" role="status" data-testid="audio-files-announcement">
          {announcement ?? ''}
        </Text>
        {problem !== undefined && (
          <Text size="sm" role="alert" c="var(--mantine-color-error)">
            {problem}
          </Text>
        )}
        {files.phase === 'loading' && <Loader size="sm" aria-label="Loading the audio files" />}
        {(files.phase === 'error' || files.phase === 'not-found') && (
          <Notice title="The audio files could not be loaded">
            <Text>
              n8Tracks did not answer as expected. Check that it is running and try again.
            </Text>
            <div>
              <Button variant="default" size="xs" onClick={onRetry}>
                Try again
              </Button>
            </div>
          </Notice>
        )}
        {files.phase === 'ready' && files.data.length === 0 && (
          <Text size="sm" data-testid="no-audio-files">
            No local audio files.{' '}
            <Anchor component={Link} to={UNMATCHED_PATH} underline="always">
              Open Unmatched Files
            </Anchor>{' '}
            to associate one with this Song.
          </Text>
        )}
        {files.phase === 'ready' && files.data.length > 0 && (
          <Table.ScrollContainer minWidth={1120}>
            <Table withTableBorder verticalSpacing="xs" aria-labelledby="audio-files-heading">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th scope="col">
                    <VisuallyHidden>Play</VisuallyHidden>
                  </Table.Th>
                  <Table.Th scope="col">File</Table.Th>
                  <Table.Th scope="col">Folder</Table.Th>
                  <Table.Th scope="col">Format</Table.Th>
                  <Table.Th scope="col">Duration</Table.Th>
                  <Table.Th scope="col">Size</Table.Th>
                  <Table.Th scope="col">Status</Table.Th>
                  <Table.Th scope="col">Playback</Table.Th>
                  <Table.Th scope="col">Generation</Table.Th>
                  <Table.Th scope="col">Associated</Table.Th>
                  <Table.Th scope="col">
                    <VisuallyHidden>Actions</VisuallyHidden>
                  </Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {files.data.map((file) => (
                  <Table.Tr
                    key={file.id}
                    data-testid="song-audio-file"
                    data-file={file.path}
                    data-status={file.status}
                    data-preferred={String(file.isPreferred)}
                    data-plays-now={String(playsNow(file))}
                  >
                    <Table.Td>
                      <PlayButton target={{ kind: 'file', file }} />
                    </Table.Td>
                    <Table.Th scope="row" fw="normal" style={{ wordBreak: 'break-word' }}>
                      <Text size="sm" fw={600} span>
                        {file.fileName}
                      </Text>
                    </Table.Th>
                    <Table.Td style={{ wordBreak: 'break-word' }}>{songFolderText(file)}</Table.Td>
                    <Table.Td>{formatLabel(file.format)}</Table.Td>
                    <Table.Td style={{ whiteSpace: 'nowrap' }}>
                      {fileDurationText(file.durationSeconds)}
                    </Table.Td>
                    <Table.Td style={{ whiteSpace: 'nowrap' }}>
                      {sizeMbText(file.sizeBytes)}
                    </Table.Td>
                    <Table.Td>
                      <AudioFileStatusMark file={file} />
                    </Table.Td>
                    <Table.Td style={{ minWidth: 160 }}>
                      <AudioFilePlayback file={file} files={files.data} />
                    </Table.Td>
                    <Table.Td style={{ whiteSpace: 'nowrap' }}>
                      <Owner
                        file={file}
                        archived={isArchivedOwner(file, generations, versions)}
                        generationLink={generationLink}
                      />
                    </Table.Td>
                    <Table.Td style={{ whiteSpace: 'nowrap' }} data-testid="audio-file-origin">
                      {originLabel(file.associationOrigin)}
                    </Table.Td>
                    <Table.Td>
                      <AudioFileActionButtons file={file} actions={actions} />
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </Stack>
    </Paper>
  );
}
