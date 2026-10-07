import { Anchor, Button, Loader, Stack, Text, Title } from '@mantine/core';
import { Link, Navigate } from 'react-router';
import { importPath, useCurrentImport, type SunoImport } from '../api/sunoImports';
import { formatDateTime, useConfiguredTimeZone } from '../api/timeZone';
import { Notice } from '../components/Notice';
import { recordCountText } from './importReviewRules';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

/** What became of the last export when none waits, in a sentence; nothing for one never ended. */
function lastText(last: SunoImport, timeZone: string): string | undefined {
  const when = formatDateTime(last.capturedAt, timeZone);
  switch (last.state) {
    case 'discarded':
      return `Your last sync, read from Suno ${when}, was discarded. Nothing in your catalog changed.`;
    case 'failed':
      return `Your last sync, read from Suno ${when}, could not be prepared. Nothing in your catalog changed.`;
    case 'expired':
      return `Your last sync, read from Suno ${when}, was not reviewed within seven days and was thrown away. Nothing in your catalog changed.`;
    case 'committed':
      return `Your last import, read from Suno ${when}, was confirmed.`;
    default:
      return undefined;
  }
}

/**
 * The Suno entry of the sidebar (#139), at `/suno/imports`: it opens the export waiting for review, or
 * explains how to start a sync from the extension, saying what became of the last one (a discarded sync
 * is noticed here).
 */
export function SunoImportsPage() {
  const timeZone = useConfiguredTimeZone();
  const { state, reload } = useCurrentImport();

  if (state.phase === 'ready' && state.data.waiting !== null) {
    return <Navigate to={importPath(state.data.waiting.id)} replace />;
  }

  const last = state.phase === 'ready' ? state.data.last : null;
  const notice = last === null ? undefined : lastText(last, timeZone);
  return (
    <Stack gap="lg">
      <Title order={2}>Suno import</Title>
      {state.phase === 'loading' && <Loader aria-label="Loading the Suno import" />}
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Notice title="The Suno import could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <div>
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
          </div>
        </Notice>
      )}
      {state.phase === 'ready' && (
        <>
          {notice !== undefined && <Text data-testid="last-import">{notice}</Text>}
          <Text data-testid="no-import-waiting">No import is waiting for review.</Text>
          <Text>
            To bring your Suno songs into n8Tracks, open Suno in a browser where the n8Tracks
            extension is connected, open the n8Tracks panel, and choose Sync. When the sync ends,
            its records are listed here for you to review; nothing is added to your catalog until
            you confirm.
          </Text>
        </>
      )}
    </Stack>
  );
}

/** Settings → Suno: whether an export is waiting for review, with a link to it. */
export function ImportWaitingSection() {
  const { state } = useCurrentImport();
  return (
    <Stack gap="xs" component="section" aria-labelledby="import-waiting-title">
      <Title order={3} id="import-waiting-title">
        Import
      </Title>
      {state.phase === 'loading' && <Loader size="sm" aria-label="Loading the Suno import" />}
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Text>Whether an import is waiting could not be loaded.</Text>
      )}
      {state.phase === 'ready' && (
        <Text data-testid="import-waiting">
          {state.data.waiting === null ? (
            'No import is waiting for review.'
          ) : (
            <>
              An import of {recordCountText(state.data.waiting.counts.total ?? 0)} is waiting for
              review.{' '}
              <Anchor component={Link} to={importPath(state.data.waiting.id)} underline="always">
                Review the import
              </Anchor>
            </>
          )}
        </Text>
      )}
    </Stack>
  );
}
