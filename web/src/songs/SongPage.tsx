import { Anchor, Button, Group, Loader, Stack, Text, Title } from '@mantine/core';
import { useCallback, useMemo, useRef, useState } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router';
import { ownArtwork } from '../api/artwork';
import { deletedSongOf, type DeletedSong } from '../api/songDeletion';
import type { FieldValue } from '../api/saves';
import {
  setSongCredits,
  updateSong,
  useSong,
  useWorkflowStates,
  type Song,
  type SongEdit,
} from '../api/songs';
import { formatDateTime, useConfiguredTimeZone } from '../api/timeZone';
import { useSongVersions } from '../api/versions';
import { ARTWORK_CROP_KEY, ARTWORK_KEY, cropOf, cropValue } from '../common/artworkField';
import { cropText } from '../common/cropRules';
import { ConflictValue } from '../common/ConflictDialog';
import { useRevisionedSave, type SavedField } from '../common/useRevisionedSave';
import { Notice } from '../components/Notice';
import { DeleteSongDialog, type DeletedSongNotice } from './DeleteSongDialog';
import { DetailsPanel, SongDetails } from './DetailsPanel';
import { CREDITS_KEY, creditsOf, creditsText, creditsValue } from './creditsField';
import { DETAILS_PANEL_ID, useDetailsPanel } from './detailsPanelState';
import {
  explicitLabel,
  isReleaseKey,
  LINKS_KEY,
  linksText,
  linksValue,
  RELEASE_LABELS,
  RELEASE_TEXT_MEMBERS,
  releaseEditOf,
  releaseKey,
} from './details/releaseField';
import { alphabetical, GENRES_KEY, genresOf, genresValue, mergeGenres } from './genreField';
import { alphabeticalTags, mergeTags, TAGS_KEY, tagsOf, tagsValue } from './tagField';
import { SongHeader } from './SongHeader';
import { StateBadge } from './SongParts';
import { SongVersions } from './SongVersions';
import type { FromSongs, SongsNotice } from './SongsPage';

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

/** The Songs table's view this page was opened from (its search), or '' for the default view. */
function useSongsSearch(): string {
  const location: { state: unknown } = useLocation();
  return isFromSongs(location.state) ? location.state.songsSearch : '';
}

/** Back to the Songs table, in the view this page was opened from when it was opened from one. */
function BackToSongs() {
  const search = useSongsSearch();
  return (
    <Anchor component={Link} to={`/songs${search}`} size="sm">
      ← Songs
    </Anchor>
  );
}

/** A Song edit as the shared save helper holds it, as the API's PATCH takes it (credits aside). */
function songEditOf(edit: Readonly<Record<string, FieldValue>>): SongEdit {
  const { [GENRES_KEY]: genres, [TAGS_KEY]: tags, [ARTWORK_CROP_KEY]: crop, ...rest } = edit;
  const fields = Object.fromEntries(
    Object.entries(rest).filter(([key]) => key !== CREDITS_KEY && !isReleaseKey(key)),
  );
  const release = releaseEditOf(edit);
  return {
    ...fields,
    ...(genres === undefined ? {} : { genreIds: genresOf(genres).map((genre) => genre.id) }),
    ...(tags === undefined ? {} : { tagIds: tagsOf(tags).map((tag) => tag.id) }),
    ...(release === undefined ? {} : { release }),
    ...(crop === undefined ? {} : { artworkCrop: cropOf(crop) }),
  };
}

/** The release members as the shared save helper holds them, each named for the conflict dialog. */
const RELEASE_FIELDS: SavedField<Song>[] = [
  ...RELEASE_TEXT_MEMBERS.map((member): SavedField<Song> => ({
    key: releaseKey(member),
    label: RELEASE_LABELS[member],
    read: (record) => record.release[member],
    show: (value) => <ConflictValue value={member === 'explicit' ? explicitLabel(value) : value} />,
  })),
  {
    key: LINKS_KEY,
    label: RELEASE_LABELS.links,
    read: (record) => linksValue(record.release.links),
    show: (value) => <ConflictValue value={linksText(value)} />,
  },
];

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
  const [deleting, setDeleting] = useState(false);
  const navigate = useNavigate();
  const songsSearch = useSongsSearch();

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
    const showCredits = (value: FieldValue) => (
      <ConflictValue value={creditsText(creditsOf(value))} />
    );
    const showTags = (value: FieldValue) => {
      const names = alphabeticalTags(tagsOf(value)).map((tag) => tag.name);
      return <ConflictValue value={names.length === 0 ? null : names.join(', ')} />;
    };
    return [
      { key: 'title', label: 'Title', read: (record) => record.title, show: showText },
      { key: 'concept', label: 'Concept', read: (record) => record.concept, show: showText },
      { key: 'stateId', label: 'State', read: (record) => record.state.id, show: showState },
      {
        key: CREDITS_KEY,
        label: 'Artists',
        read: (record) => creditsValue(record.credits),
        show: showCredits,
      },
      {
        key: GENRES_KEY,
        label: 'Genres',
        read: (record) => genresValue(record.genres),
        show: showGenres,
        merge: mergeGenres,
      },
      {
        key: TAGS_KEY,
        label: 'Tags',
        read: (record) => tagsValue(record.tags),
        show: showTags,
        merge: mergeTags,
      },
      { key: 'notes', label: 'Notes', read: (record) => record.notes, show: showText },
      ...RELEASE_FIELDS,
      {
        key: ARTWORK_KEY,
        label: 'Artwork',
        // Only the Song's own artwork is its field: a Selected Generation's image it shows is not.
        read: (record) => ownArtwork(record.artwork)?.assetId ?? null,
        show: (value) => <ConflictValue value={value === null ? null : 'An uploaded image'} />,
      },
      {
        key: ARTWORK_CROP_KEY,
        label: 'Artwork crop',
        read: (record) => cropValue(ownArtwork(record.artwork)?.crop ?? null),
        show: (value) => {
          const crop = cropOf(value);
          return <ConflictValue value={crop === null ? 'Centred' : cropText(crop)} />;
        },
      },
    ];
  }, [states]);

  // Credits are their own write (`PUT …/credits`) under the same revision; the Details panel
  // saves them on their own, and anything else in the same edit goes in the PATCH first.
  const send = useCallback(async (base: Song, edit: Readonly<Record<string, FieldValue>>) => {
    if (!Object.hasOwn(edit, CREDITS_KEY)) {
      return updateSong(base, songEditOf(edit));
    }
    const credits = creditsOf(edit[CREDITS_KEY] ?? null);
    if (Object.keys(edit).length === 1) {
      return setSongCredits(base, credits);
    }
    const patched = await updateSong(base, songEditOf(edit));
    return patched.kind === 'saved' ? setSongCredits(patched.record, credits) : patched;
  }, []);

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

  const deleted = (notice: DeletedSongNotice) => {
    const state: SongsNotice = { deletedSong: notice };
    void navigate(`/songs${songsSearch}`, { state });
  };

  const actions = (
    <Group gap="xs" wrap="nowrap">
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
      <Button
        variant="default"
        size="compact-sm"
        onClick={() => {
          setDeleting(true);
        }}
      >
        Delete Song
      </Button>
    </Group>
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
      <DeleteSongDialog
        song={song}
        opened={deleting}
        onClose={() => {
          setDeleting(false);
        }}
        onDeleted={deleted}
      />
    </Group>
  );
}

/** A Song deleted within its retention period: named, with when, instead of the plain not-found. */
function DeletedSongState({ deleted }: { deleted: DeletedSong }) {
  const timeZone = useConfiguredTimeZone();
  return (
    <>
      <Title order={2}>This Song was deleted</Title>
      <Text data-testid="song-deleted" style={{ overflowWrap: 'anywhere' }}>
        {deleted.shortcode} “{deleted.title}” was deleted on{' '}
        {formatDateTime(deleted.deletedAt, timeZone)}. Its shortcode is never used for another Song.
      </Text>
    </>
  );
}

/**
 * A Song: its shortcode, title, concept, and workflow state, each edited in place, and its Version
 * tree beside the selected Version. It is found by the shortcode in the page URL (`/songs/n8-12`),
 * or by its ID; `/songs/n8-12/v/1.1` selects a Version. A Song deleted within its retention period
 * says so, naming it, and its header's Delete Song opens the confirmation; once deleted, the Songs
 * table opens with a notice naming it.
 */
export function SongPage() {
  const { reference = '' } = useParams();
  const { state, reload } = useSong(reference);
  const deletedSong = state.phase === 'not-found' ? deletedSongOf(state.problem) : undefined;

  return (
    <Stack gap="lg">
      <BackToSongs />
      {state.phase === 'loading' && <Loader aria-label="Loading the Song" />}
      {state.phase === 'not-found' && deletedSong === undefined && (
        <>
          <Title order={2}>Song not found</Title>
          <Text>There is no Song {reference}. It may have a different shortcode.</Text>
        </>
      )}
      {deletedSong !== undefined && <DeletedSongState deleted={deletedSong} />}
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
