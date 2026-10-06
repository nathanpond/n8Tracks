import { Anchor, Button, Fieldset, Group, List, Loader, Stack, Text, Title } from '@mantine/core';
import { useCallback, useState } from 'react';
import { Link, useLocation, useParams } from 'react-router';
import {
  ALBUM_DESCRIPTION_MAXIMUM_LENGTH,
  ALBUM_LINK_MAXIMUM_COUNT,
  ALBUM_RIGHTS_MAXIMUM_LENGTH,
  ALBUM_TITLE_MAXIMUM_LENGTH,
  updateAlbum,
  useAlbum,
  type Album,
  type AlbumEdit,
  type AlbumLink,
} from '../api/albums';
import type { FieldValue, SaveResult } from '../api/saves';
import { ArtistPicker } from '../common/ArtistPicker';
import { LinksEditor } from '../common/LinksEditor';
import { SavedTextField, type Save } from '../common/SavedTextField';
import { saveError } from '../common/useInPlaceEdit';
import { useRevisionedSave, type SavedField } from '../common/useRevisionedSave';
import { Notice } from '../components/Notice';
import {
  albumDateError,
  albumTitleError,
  descriptionError,
  formatAlbumDate,
  normaliseAlbumDate,
  normaliseAlbumText,
  normaliseUpc,
  rightsError,
  upcError,
} from './albumRules';
import { TrackList } from './TrackList';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

const DATE_DESCRIPTION =
  'A year (2026), a year and month (2026-03), or a full date (2026-03-01). Leave it empty for none.';

function isFromAlbums(value: unknown): value is { albumsSearch: string } {
  return (
    typeof value === 'object' &&
    value !== null &&
    'albumsSearch' in value &&
    typeof value.albumsSearch === 'string'
  );
}

/** Back to the Albums list, in the view this page was opened from when it was opened from one. */
function BackToAlbums() {
  const location: { state: unknown } = useLocation();
  const search = isFromAlbums(location.state) ? location.state.albumsSearch : '';
  return (
    <Anchor component={Link} to={`/albums${search}`} size="sm">
      ← Albums
    </Anchor>
  );
}

/** Lists and the Album Artist are compared and saved as JSON text, the shared save helper's field values. */
const jsonValue = (value: unknown) => JSON.stringify(value);

function readJson<T>(value: FieldValue, fallback: T): T {
  return value === null ? fallback : (JSON.parse(value) as T);
}

const none = (value: FieldValue) => value ?? 'None';

const FIELDS: readonly SavedField<Album>[] = [
  { key: 'title', label: 'Title', read: (album) => album.title, show: (value) => value },
  { key: 'description', label: 'Description', read: (album) => album.description, show: none },
  {
    key: 'albumArtist',
    label: 'Album Artist',
    read: (album) => (album.albumArtist === null ? null : jsonValue(album.albumArtist)),
    show: (value) => readJson<{ name: string } | null>(value, null)?.name ?? 'None',
  },
  {
    key: 'releaseDate',
    label: 'Release date',
    read: (album) => album.releaseDate,
    show: (value) => (value === null ? 'None' : formatAlbumDate(value)),
  },
  {
    key: 'originalReleaseDate',
    label: 'Original release date',
    read: (album) => album.originalReleaseDate,
    show: (value) => (value === null ? 'None' : formatAlbumDate(value)),
  },
  { key: 'upc', label: 'UPC/EAN', read: (album) => album.upc, show: none },
  { key: 'copyright', label: 'Copyright', read: (album) => album.copyright, show: none },
  { key: 'publishing', label: 'Publishing', read: (album) => album.publishing, show: none },
  {
    key: 'links',
    label: 'Links',
    read: (album) => jsonValue(album.links),
    show: (value) =>
      readJson<AlbumLink[]>(value, [])
        .map((link) => (link.label === null ? link.url : `${link.label}: ${link.url}`))
        .join(', ') || 'None',
  },
];

/** The PATCH body for what the save helper holds. */
function albumEditOf(edit: Readonly<Record<string, FieldValue>>): AlbumEdit {
  const result: AlbumEdit = {};
  for (const [key, value] of Object.entries(edit)) {
    switch (key) {
      case 'title':
        result.title = value ?? '';
        break;
      case 'albumArtist':
        result.albumArtistId = readJson<{ id: string } | null>(value, null)?.id ?? null;
        break;
      case 'links':
        result.links = readJson<AlbumLink[]>(value, []);
        break;
      case 'description':
      case 'releaseDate':
      case 'originalReleaseDate':
      case 'upc':
      case 'copyright':
      case 'publishing':
        result[key] = value;
        break;
    }
  }
  return result;
}

/** A partial date field, with the date as it is shown once saved. */
function DateField({
  field,
  label,
  value,
  save,
}: {
  field: 'releaseDate' | 'originalReleaseDate';
  label: string;
  value: string | null;
  save: Save;
}) {
  return (
    <SavedTextField
      key={value ?? ''}
      field={field}
      label={label}
      description={DATE_DESCRIPTION}
      value={value}
      check={albumDateError}
      normalise={normaliseAlbumDate}
      save={save}
    >
      {value !== null && (
        <Text size="sm" data-testid={`${field}-shown`}>
          Shown as {formatAlbumDate(value)}
        </Text>
      )}
    </SavedTextField>
  );
}

/** The Album Artist: shown with a Clear action, and chosen (or created) with the Artist picker, each saved at once. */
function AlbumArtistField({ album, save }: { album: Album; save: Save }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const artist = album.albumArtist;

  const choose = async (next: { id: string; name: string } | null) => {
    setBusy(true);
    setError(undefined);
    const outcome = await save('albumArtist', next === null ? null : jsonValue(next));
    setBusy(false);
    setError(saveError(outcome, 'albumArtistId'));
  };

  return (
    <Stack gap="xs" role="group" aria-labelledby="album-artist-label">
      <Text fw={500} size="sm" id="album-artist-label">
        Album Artist
      </Text>
      <Text size="xs" c="var(--n8-color-secondary-text)">
        Who the Album is credited to as a whole, independent of its Songs&apos; credits.
      </Text>
      <Group gap="xs" data-testid="album-artist">
        {artist === null ? (
          <Text size="sm">No Album Artist.</Text>
        ) : (
          <>
            <Anchor component={Link} to={`/artists/${artist.id}`} size="sm" underline="always">
              {artist.name}
            </Anchor>
            <Button
              variant="default"
              size="compact-sm"
              disabled={busy}
              onClick={() => {
                void choose(null);
              }}
            >
              Clear the Album Artist
            </Button>
          </>
        )}
      </Group>
      <ArtistPicker
        label={artist === null ? 'Choose an Album Artist' : 'Choose another Album Artist'}
        exclude={artist === null ? [] : [artist.id]}
        allowCreate
        busy={busy}
        error={error}
        onChoose={(chosen) => {
          void choose(chosen);
        }}
      />
    </Stack>
  );
}

/** The duplicate UPC/EAN warning, while another Album has the same code. */
function UpcWarning({ album }: { album: Album }) {
  const warnings = album.warnings.filter((warning) => warning.field === 'upc');
  if (warnings.length === 0) {
    return null;
  }
  return (
    <div data-testid="upc-warning">
      {warnings.map((warning) => (
        <Notice key={warning.code} title={warning.message}>
          <Text size="sm">The code is kept. Check that it belongs to this Album:</Text>
          <List size="sm">
            {warning.albums.map((other) => (
              <List.Item key={other.id}>
                <Anchor component={Link} to={`/albums/${other.id}`} underline="always">
                  {other.title}
                </Anchor>
              </List.Item>
            ))}
          </List>
        </Notice>
      ))}
    </div>
  );
}

/** The Album's page once it has loaded: each field saved on its own through the shared save helper. */
function LoadedAlbum({ initial }: { initial: Album }) {
  const [album, setAlbum] = useState(initial);
  const [status, setStatus] = useState<'idle' | 'saving' | 'saved' | 'failed'>('idle');

  const send = useCallback(
    (base: Album, edit: Readonly<Record<string, FieldValue>>): Promise<SaveResult<Album>> =>
      updateAlbum(base, albumEditOf(edit)),
    [],
  );
  const { save, dialog } = useRevisionedSave({
    record: album,
    onRecord: setAlbum,
    fields: FIELDS,
    send,
    subject: 'This Album',
  });

  const saveField: Save = useCallback(
    async (key, value) => {
      setStatus('saving');
      const outcome = await save(key, value);
      setStatus(outcome.kind === 'saved' ? 'saved' : outcome.kind === 'failed' ? 'failed' : 'idle');
      return outcome;
    },
    [save],
  );

  return (
    <Stack gap="lg">
      <BackToAlbums />
      <Title order={2}>{album.title}</Title>
      <div role="status" data-testid="album-save-status">
        {status === 'saving' && <Text size="sm">Saving…</Text>}
        {status === 'saved' && <Text size="sm">Saved.</Text>}
        {status === 'failed' && (
          <Notice title="Not saved">
            <Text>{FAILED_MESSAGE}</Text>
          </Notice>
        )}
      </div>

      <Stack gap="md" maw={720}>
        <SavedTextField
          key={`title ${album.title}`}
          field="title"
          label="Title"
          description={`One line, up to ${String(ALBUM_TITLE_MAXIMUM_LENGTH)} characters. Titles do not have to be unique.`}
          value={album.title}
          check={albumTitleError}
          normalise={(draft) => draft.trim()}
          save={saveField}
          required
        />
        <AlbumArtistField album={album} save={saveField} />
        <SavedTextField
          key={`description ${album.description ?? ''}`}
          field="description"
          label="Description"
          description={`Plain text, up to ${ALBUM_DESCRIPTION_MAXIMUM_LENGTH.toLocaleString('en-US')} characters.`}
          value={album.description}
          check={descriptionError}
          normalise={normaliseAlbumText}
          save={saveField}
          multiline
        />

        <Fieldset legend="Release details">
          <Stack gap="md">
            <DateField
              field="releaseDate"
              label="Release date"
              value={album.releaseDate}
              save={saveField}
            />
            <DateField
              field="originalReleaseDate"
              label="Original release date"
              value={album.originalReleaseDate}
              save={saveField}
            />
            <SavedTextField
              key={`upc ${album.upc ?? ''}`}
              field="upc"
              label="UPC/EAN"
              description="A 12-digit UPC or a 13-digit EAN. Spaces and hyphens are ignored."
              value={album.upc}
              check={upcError}
              normalise={normaliseUpc}
              save={saveField}
            >
              <UpcWarning album={album} />
            </SavedTextField>
            <SavedTextField
              key={`copyright ${album.copyright ?? ''}`}
              field="copyright"
              label="Copyright"
              description={`Plain text, up to ${String(ALBUM_RIGHTS_MAXIMUM_LENGTH)} characters.`}
              value={album.copyright}
              check={rightsError}
              normalise={normaliseAlbumText}
              save={saveField}
              multiline
            />
            <SavedTextField
              key={`publishing ${album.publishing ?? ''}`}
              field="publishing"
              label="Publishing"
              description={`Plain text, up to ${String(ALBUM_RIGHTS_MAXIMUM_LENGTH)} characters.`}
              value={album.publishing}
              check={rightsError}
              normalise={normaliseAlbumText}
              save={saveField}
              multiline
            />
          </Stack>
        </Fieldset>

        <LinksEditor
          key={jsonValue(album.links)}
          links={album.links}
          field="links"
          maximum={ALBUM_LINK_MAXIMUM_COUNT}
          empty="No links. Add the Album's pages elsewhere, each with an optional label."
          save={saveField}
        />
      </Stack>

      <TrackList album={album} onAlbum={setAlbum} />

      {dialog}
    </Stack>
  );
}

/**
 * An Album's page (`/albums/<id>`): its title, Album Artist, description, release details, and
 * links, each saved on its own under the Album's revision. A UPC/EAN another Album has is kept and
 * warned about while it applies. Its tracks are arranged by disc and track number (TrackList), each
 * change also under the Album's revision.
 */
export function AlbumPage() {
  const { id = '' } = useParams();
  const { state, reload } = useAlbum(id);

  if (state.phase === 'loading') {
    return <Loader aria-label="Loading the Album" />;
  }
  if (state.phase === 'not-found') {
    return (
      <Stack gap="md">
        <BackToAlbums />
        <Title order={2}>No such Album</Title>
        <Text>There is no Album at this address. It may have been typed wrongly.</Text>
      </Stack>
    );
  }
  if (state.phase === 'error') {
    return (
      <Stack gap="md">
        <BackToAlbums />
        <Notice title="The Album could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <Group>
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
          </Group>
        </Notice>
      </Stack>
    );
  }
  return <LoadedAlbum key={state.data.id} initial={state.data} />;
}
