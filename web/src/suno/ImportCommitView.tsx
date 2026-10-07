import { Anchor, Loader, Progress, Stack, Table, Text, Title } from '@mantine/core';
import { Link } from 'react-router';
import { useCommitJob, type CommitJob, type SunoImport } from '../api/sunoImports';
import { remoteResultText } from '../api/sunoRemoteStates';
import { Notice } from '../components/Notice';
import {
  commitReasonText,
  createdText,
  outcomesText,
  stateText,
  statusResultText,
} from './importReviewRules';

/**
 * An import being confirmed or confirmed (#140): while the commit job runs, its progress (the page may
 * be left; the job goes on, and the page shows the result when it is opened again); at the end, what was
 * created, with links to the Songs, and each record that was not imported as chosen, with the reason.
 * `onChanged` reads the export again once the job ends (it is then committed, or back for review).
 */
export function ImportCommitView({
  exported,
  onChanged,
}: {
  exported: SunoImport;
  onChanged: () => void;
}) {
  const jobId = exported.jobId ?? null;
  const { job, gone } = useCommitJob(jobId, (finished: CommitJob) => {
    if (exported.state === 'committing' || finished.status === 'failed') {
      onChanged();
    }
  });

  if (exported.state === 'committing') {
    const progress = job?.progress ?? 0;
    return (
      <Stack gap="sm" data-testid="import-state" data-state="committing">
        <Title order={3}>This import is being confirmed</Title>
        <Text>
          n8Tracks is copying the chosen records into your catalog. You can leave this page: the
          import goes on, and this page shows the result when you come back.
        </Text>
        <Progress.Root size="lg" data-testid="import-progress">
          <Progress.Section value={progress} aria-label="Import progress" />
        </Progress.Root>
        <Text size="sm" data-testid="import-progress-text">
          {job?.message ?? 'Starting…'}
        </Text>
      </Stack>
    );
  }

  if (job?.status === 'failed') {
    return (
      <Notice title="The import stopped">
        <Text>
          Nothing more was copied, and the import is back for review: confirm it again to copy the
          rest.
        </Text>
      </Notice>
    );
  }

  const result = job?.result ?? null;
  if (result === null) {
    if (jobId !== null && !gone && job === null) {
      return <Loader aria-label="Loading the result" />;
    }
    const { title, text } = stateText(exported.state);
    return (
      <Stack gap="sm" data-testid="import-state" data-state={exported.state}>
        <Title order={3}>{title}</Title>
        <Text>{text}</Text>
      </Stack>
    );
  }

  const unusual = result.records.filter(
    (record) =>
      record.outcome === 'failed' || record.reason !== undefined || record.note !== undefined,
  );
  return (
    <Stack gap="md" data-testid="import-state" data-state="committed">
      <Title order={3}>This import was confirmed</Title>
      <Text data-testid="commit-created">{createdText(result.created)}</Text>
      <Text data-testid="commit-outcomes">{outcomesText(result.records)}</Text>
      {result.remoteStates !== undefined && result.remoteStates.length > 0 && (
        <Text data-testid="commit-remote-states">{remoteResultText(result.remoteStates)}</Text>
      )}
      {result.statuses !== undefined && result.statuses.length > 0 && (
        <Text data-testid="commit-statuses">{statusResultText(result.statuses)}</Text>
      )}
      {result.songs.length > 0 && (
        <Stack gap={4} component="section" aria-labelledby="commit-songs-title">
          <Title order={4} id="commit-songs-title">
            Songs
          </Title>
          <ul data-testid="commit-songs">
            {result.songs.map((song) => (
              <li key={song.id}>
                <Anchor component={Link} to={`/songs/${song.shortcode}`} underline="always">
                  {song.shortcode} “{song.title}”
                </Anchor>
                {song.created ? ' (new)' : ''}
              </li>
            ))}
          </ul>
        </Stack>
      )}
      {unusual.length > 0 && (
        <Stack gap={4} component="section" aria-labelledby="commit-notes-title">
          <Title order={4} id="commit-notes-title">
            Not imported as chosen
          </Title>
          <Table data-testid="commit-notes">
            <Table.Thead>
              <Table.Tr>
                <Table.Th scope="col">Suno ID</Table.Th>
                <Table.Th scope="col">What happened</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {unusual.map((record) => (
                <Table.Tr key={record.sunoId} data-commit-record={record.sunoId}>
                  <Table.Td>{record.sunoId}</Table.Td>
                  <Table.Td>
                    {record.outcome === 'failed' ? 'Not imported: ' : ''}
                    {record.reason !== undefined ? commitReasonText(record.reason) : ''}
                    {record.note === 'artwork_missing'
                      ? 'Imported without its cover image, which had gone.'
                      : ''}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Stack>
      )}
    </Stack>
  );
}
