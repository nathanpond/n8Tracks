import { Anchor, Button, Group, Loader, Paper, Stack, Text, Title } from '@mantine/core';
import { Link, useLocation, useParams } from 'react-router';
import { useSong } from '../api/songs';
import { Notice } from '../components/Notice';
import { StateBadge } from './SongParts';
import type { FromSongs } from './SongsPage';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

function isFromSongs(value: unknown): value is FromSongs {
  return (
    typeof value === 'object' &&
    value !== null &&
    'songsSearch' in value &&
    typeof value.songsSearch === 'string'
  );
}

/** Back to the Songs table, in the view this page was opened from when it was opened from one. */
function BackToSongs() {
  const location: { state: unknown } = useLocation();
  const search = isFromSongs(location.state) ? location.state.songsSearch : '';
  return (
    <Anchor component={Link} to={`/songs${search}`} size="sm">
      ← Songs
    </Anchor>
  );
}

/**
 * A Song: its shortcode, title, concept, and workflow state. It is found by the shortcode in the
 * page URL (`/songs/n8-12`), or by its ID. Later stories add editing, the Version tree, and the
 * editor to this page.
 */
export function SongPage() {
  const { reference = '' } = useParams();
  const { state, reload } = useSong(reference);

  return (
    <Stack gap="lg">
      <BackToSongs />
      {state.phase === 'loading' && <Loader aria-label="Loading the Song" />}
      {state.phase === 'not-found' && (
        <>
          <Title order={2}>Song not found</Title>
          <Text>There is no Song {reference}. It may have a different shortcode.</Text>
        </>
      )}
      {state.phase === 'error' && (
        <>
          <Title order={2}>Song</Title>
          <Notice title="The Song could not be loaded">
            <Text>{FAILED_MESSAGE}</Text>
            <div>
              <Button variant="default" size="xs" onClick={reload}>
                Try again
              </Button>
            </div>
          </Notice>
        </>
      )}
      {state.phase === 'ready' && (
        <>
          <Stack gap={4}>
            <Text
              ff="monospace"
              size="sm"
              c="var(--n8-color-secondary-text)"
              data-testid="shortcode"
            >
              {state.data.shortcode}
            </Text>
            <Group gap="sm" align="center">
              <Title order={2} style={{ overflowWrap: 'anywhere' }}>
                {state.data.title}
              </Title>
              <StateBadge name={state.data.state.name} colour={state.data.state.colour} />
            </Group>
          </Stack>
          <Paper p="md" withBorder component="section" aria-labelledby="song-concept">
            <Stack gap={4}>
              <Title order={3} size="h5" id="song-concept">
                Concept
              </Title>
              {state.data.concept === null ? (
                <Text c="var(--n8-color-secondary-text)">No concept yet.</Text>
              ) : (
                <Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>
                  {state.data.concept}
                </Text>
              )}
            </Stack>
          </Paper>
        </>
      )}
    </Stack>
  );
}
