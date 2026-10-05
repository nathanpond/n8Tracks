import { Anchor, Button, Loader, Stack, Text, Title } from '@mantine/core';
import { useState } from 'react';
import { Link, useLocation, useParams } from 'react-router';
import { useSong, type Song } from '../api/songs';
import { Notice } from '../components/Notice';
import { SongHeader } from './SongHeader';
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
 * A loaded Song, kept as the page's own copy from then on: each save, and each refused save's
 * current Song, replaces it, so the next save is based on the newest revision the page has seen.
 */
function LoadedSong({ loaded }: { loaded: Song }) {
  const [song, setSong] = useState(loaded);
  return <SongHeader song={song} onSong={setSong} />;
}

/**
 * A Song: its shortcode, title, concept, and workflow state, each edited in place. It is found by
 * the shortcode in the page URL (`/songs/n8-12`), or by its ID. Later stories add the Version tree
 * and the editor to this page.
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
      {state.phase === 'ready' && <LoadedSong key={state.data.id} loaded={state.data} />}
    </Stack>
  );
}
