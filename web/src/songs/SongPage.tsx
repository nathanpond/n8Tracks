import { Anchor, Button, Group, Loader, Stack, Text, Title } from '@mantine/core';
import { useCallback, useMemo, useRef, useState } from 'react';
import { Link, useLocation, useParams } from 'react-router';
import type { FieldValue } from '../api/saves';
import { updateSong, useSong, useWorkflowStates, type Song, type SongEdit } from '../api/songs';
import { useSongVersions } from '../api/versions';
import { ConflictValue } from '../common/ConflictDialog';
import { useRevisionedSave, type SavedField } from '../common/useRevisionedSave';
import { Notice } from '../components/Notice';
import { DetailsPanel, SongDetails } from './DetailsPanel';
import { DETAILS_PANEL_ID, useDetailsPanel } from './detailsPanelState';
import { alphabetical, GENRES_KEY, genresOf, genresValue, mergeGenres } from './genreField';
import { SongHeader } from './SongHeader';
import { StateBadge } from './SongParts';
import { SongVersions } from './SongVersions';
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

/** A Song edit as the shared save helper holds it, as the API's PATCH takes it. */
function songEditOf(edit: Readonly<Record<string, FieldValue>>): SongEdit {
  const { [GENRES_KEY]: genres, ...fields } = edit;
  return {
    ...fields,
    ...(genres === undefined ? {} : { genreIds: genresOf(genres).map((genre) => genre.id) }),
  };
}

/**
 * A loaded Song, kept as the page's own copy from then on: each save, and each refused save's
 * current Song, replaces it, so the next save is based on the newest revision the page has seen.
 * Every edit of the Song itself (the header's fields and the Details panel's) goes through one
 * {@link useRevisionedSave}, so they are saved one after another and share one conflict dialog.
 */
function LoadedSong({ loaded }: { loaded: Song }) {
  const [song, setSong] = useState(loaded);
  const { state, reload } = useSongVersions(loaded.id);
  const { state: statesState } = useWorkflowStates();
  const states = statesState.phase === 'ready' ? statesState.data : undefined;
  const panel = useDetailsPanel();
  const detailsControl = useRef<HTMLButtonElement>(null);

  const fields = useMemo((): SavedField<Song>[] => {
    const showState = (id: FieldValue) => {
      const state = states?.find((candidate) => candidate.id === id);
      return state ? <StateBadge name={state.name} colour={state.colour} /> : (id ?? '');
    };
    const showText = (value: FieldValue) => <ConflictValue value={value} />;
    const showGenres = (value: FieldValue) => {
      const names = alphabetical(genresOf(value)).map((genre) => genre.name);
      return <ConflictValue value={names.length === 0 ? null : names.join(', ')} />;
    };
    return [
      { key: 'title', label: 'Title', read: (record) => record.title, show: showText },
      { key: 'concept', label: 'Concept', read: (record) => record.concept, show: showText },
      { key: 'stateId', label: 'State', read: (record) => record.state.id, show: showState },
      {
        key: GENRES_KEY,
        label: 'Genres',
        read: (record) => genresValue(record.genres),
        show: showGenres,
        merge: mergeGenres,
      },
      { key: 'notes', label: 'Notes', read: (record) => record.notes, show: showText },
    ];
  }, [states]);

  const send = useCallback(
    (base: Song, edit: Readonly<Record<string, FieldValue>>) => updateSong(base, songEditOf(edit)),
    [],
  );

  const { save, saveFields, dialog } = useRevisionedSave({
    record: song,
    onRecord: setSong,
    fields,
    send,
    subject: 'This Song',
  });

  const closePanel = () => {
    panel.close();
    // Beside the editor the panel's close control goes away with it; focus goes back to the
    // control that opens it. (The overlay gives focus back on its own.)
    if (!panel.narrow) {
      detailsControl.current?.focus();
    }
  };

  const actions = (
    <Button
      ref={detailsControl}
      variant={panel.open ? 'light' : 'default'}
      size="compact-sm"
      aria-expanded={panel.open}
      aria-controls={panel.open ? DETAILS_PANEL_ID : undefined}
      onClick={panel.toggle}
    >
      Details
    </Button>
  );

  return (
    <Group align="flex-start" gap="lg" wrap="nowrap">
      <Stack gap="lg" style={{ flex: '1 1 0', minWidth: 0 }}>
        <SongHeader song={song} states={states} save={save} actions={actions} />
        {state.phase === 'loading' && <Loader aria-label="Loading the Versions" />}
        {(state.phase === 'error' || state.phase === 'not-found') && (
          <Notice title="The Versions could not be loaded">
            <Text>{FAILED_MESSAGE}</Text>
            <div>
              <Button variant="default" size="xs" onClick={reload}>
                Try again
              </Button>
            </div>
          </Notice>
        )}
        {state.phase === 'ready' && (
          <SongVersions song={song} loaded={state.data} onSong={setSong} />
        )}
      </Stack>
      <DetailsPanel open={panel.open} narrow={panel.narrow} onClose={closePanel}>
        <SongDetails song={song} saveFields={saveFields} onSong={setSong} />
      </DetailsPanel>
      {dialog}
    </Group>
  );
}

/**
 * A Song: its shortcode, title, concept, and workflow state, each edited in place, and its Version
 * tree beside the selected Version. It is found by the shortcode in the page URL (`/songs/n8-12`),
 * or by its ID; `/songs/n8-12/v/1.1` selects a Version. A later story adds the editor.
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
