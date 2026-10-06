import { Anchor, Button, Group, List, NativeSelect, Radio, Stack, Text } from '@mantine/core';
import { useState } from 'react';
import { Link } from 'react-router';
import { useLanguages } from '../../api/languages';
import {
  SONG_LINK_MAXIMUM_COUNT,
  SONG_RIGHTS_MAXIMUM_LENGTH,
  type Song,
  type SongRelease,
} from '../../api/songs';
import {
  albumDateError,
  formatAlbumDate,
  normaliseAlbumDate,
  normaliseAlbumText,
  rightsError,
} from '../../albums/albumRules';
import { LinksEditor } from '../../common/LinksEditor';
import { SavedTextField, type Save } from '../../common/SavedTextField';
import { saveError } from '../../common/useInPlaceEdit';
import { Notice } from '../../components/Notice';
import { isrcError, LINKS_KEY, linksValue, normaliseIsrc, releaseKey } from './releaseField';

const DATE_DESCRIPTION = 'A year (2026), a year and month (2026-03), or a full date (2026-03-01).';

/** The explicit flag's radio value while it is not set. */
const NOT_SET = 'none';

/** A partial date, saved when it loses focus, with the date as it is shown once saved. */
function DateField({
  member,
  label,
  release,
  save,
}: {
  member: 'releaseDate' | 'originalReleaseDate';
  label: string;
  release: SongRelease;
  save: Save;
}) {
  const value = release[member];
  return (
    <SavedTextField
      key={value ?? ''}
      field={releaseKey(member)}
      label={label}
      description={DATE_DESCRIPTION}
      value={value}
      check={albumDateError}
      normalise={normaliseAlbumDate}
      save={save}
    >
      {value !== null && (
        <Text size="sm" data-testid={`${member}-shown`}>
          Shown as {formatAlbumDate(value)}
        </Text>
      )}
    </SavedTextField>
  );
}

/**
 * Explicit, clean, or not set: a choice saved as soon as it is made, and shown as made while it is
 * saved (the Song's own value again once the save ends, saved or not).
 */
function ExplicitField({ release, save }: { release: SongRelease; save: Save }) {
  const [pending, setPending] = useState<string>();
  const [error, setError] = useState<string>();
  const field = releaseKey('explicit');
  const busy = pending !== undefined;

  const choose = async (value: string) => {
    setPending(value);
    setError(undefined);
    const outcome = await save(field, value === NOT_SET ? null : value);
    setPending(undefined);
    setError(saveError(outcome, field));
  };

  return (
    <Radio.Group
      label="Explicit content"
      value={pending ?? release.explicit ?? NOT_SET}
      onChange={(value) => {
        void choose(value);
      }}
      error={error}
    >
      <Group gap="md" mt={4}>
        <Radio value={NOT_SET} label="Not set" disabled={busy} />
        <Radio value="explicit" label="Explicit" disabled={busy} />
        <Radio value="clean" label="Clean" disabled={busy} />
      </Group>
    </Radio.Group>
  );
}

/** The language, chosen by name from the API's list and saved as its code as soon as it is chosen (shown as chosen while it is saved). */
function LanguageField({ release, save }: { release: SongRelease; save: Save }) {
  const { state, reload } = useLanguages();
  const [pending, setPending] = useState<string>();
  const [error, setError] = useState<string>();
  const field = releaseKey('language');
  const languages = state.phase === 'ready' ? state.data : undefined;
  const busy = pending !== undefined;
  const current = release.language;
  const data = [
    { value: '', label: 'Not set' },
    ...(languages ?? []).map((language) => ({ value: language.code, label: language.name })),
    // A code the list does not hold (or the list not loaded yet) still shows as chosen.
    ...(current !== null && !(languages ?? []).some((language) => language.code === current)
      ? [{ value: current, label: current }]
      : []),
  ];

  const choose = async (value: string) => {
    setPending(value);
    setError(undefined);
    const outcome = await save(field, value === '' ? null : value);
    setPending(undefined);
    setError(saveError(outcome, field));
  };

  return (
    <Stack gap={4}>
      <NativeSelect
        label="Language"
        description="The language of the words; No linguistic content for an instrumental."
        data={data}
        value={pending ?? current ?? ''}
        disabled={busy || languages === undefined}
        onChange={(event) => {
          void choose(event.currentTarget.value);
        }}
        error={error}
        aria-invalid={error !== undefined}
      />
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Group gap="xs">
          <Text size="sm" c="var(--mantine-color-error)">
            The language list could not be loaded.
          </Text>
          <Button variant="default" size="compact-xs" onClick={reload}>
            Try again
          </Button>
        </Group>
      )}
    </Stack>
  );
}

/** The duplicate ISRC warning, while another Song has the same code. */
function IsrcWarning({ song }: { song: Song }) {
  const warnings = song.warnings.filter((warning) => warning.field === releaseKey('isrc'));
  if (warnings.length === 0) {
    return null;
  }
  return (
    <div data-testid="isrc-warning">
      {warnings.map((warning) => (
        <Notice key={warning.code} title={warning.message}>
          <Text size="sm">The ISRC is kept. Check that it belongs to this Song:</Text>
          <List size="sm">
            {warning.songs.map((other) => (
              <List.Item key={other.id}>
                <Anchor component={Link} to={`/songs/${other.shortcode}`} underline="always">
                  {other.title}
                </Anchor>{' '}
                ({other.shortcode})
              </List.Item>
            ))}
          </List>
        </Notice>
      ))}
    </div>
  );
}

/**
 * The Song's release details in the Details panel: release and original release dates (partial
 * dates, shown as entered), whether it is explicit or clean, copyright and publishing text, its
 * ISRC (warned about while another Song has it), its language, and streaming or distribution
 * links. Each is saved on its own through the Song's shared save, under its revision.
 */
export function ReleaseSection({ song, save }: { song: Song; save: Save }) {
  const release = song.release;
  return (
    <Stack gap="sm" role="group" aria-labelledby="song-release">
      <Text fw={500} size="sm" id="song-release">
        Release
      </Text>
      <DateField member="releaseDate" label="Release date" release={release} save={save} />
      <DateField
        member="originalReleaseDate"
        label="Original release date"
        release={release}
        save={save}
      />
      <ExplicitField release={release} save={save} />
      <SavedTextField
        key={`isrc ${release.isrc ?? ''}`}
        field={releaseKey('isrc')}
        label="ISRC"
        description="Twelve characters, such as US-S1Z-99-00001. Case, spaces, and hyphens are ignored."
        value={release.isrc}
        check={isrcError}
        normalise={normaliseIsrc}
        save={save}
      >
        <IsrcWarning song={song} />
      </SavedTextField>
      <LanguageField release={release} save={save} />
      <SavedTextField
        key={`copyright ${release.copyright ?? ''}`}
        field={releaseKey('copyright')}
        label="Copyright"
        description={`Plain text, up to ${String(SONG_RIGHTS_MAXIMUM_LENGTH)} characters.`}
        value={release.copyright}
        check={rightsError}
        normalise={normaliseAlbumText}
        save={save}
        multiline
      />
      <SavedTextField
        key={`publishing ${release.publishing ?? ''}`}
        field={releaseKey('publishing')}
        label="Publishing"
        description={`Plain text, up to ${String(SONG_RIGHTS_MAXIMUM_LENGTH)} characters.`}
        value={release.publishing}
        check={rightsError}
        normalise={normaliseAlbumText}
        save={save}
        multiline
      />
      <LinksEditor
        key={linksValue(release.links)}
        links={release.links}
        field={LINKS_KEY}
        maximum={SONG_LINK_MAXIMUM_COUNT}
        empty="No links. Add where the Song is streamed or distributed, each with an optional label."
        save={save}
      />
    </Stack>
  );
}
