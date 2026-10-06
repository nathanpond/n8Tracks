import { Anchor, Button, Group, Loader, Paper, Stack, Text, Title } from '@mantine/core';
import { useState } from 'react';
import { Link } from 'react-router';
import { setDefaultArtist, useCatalogSettings, type CatalogSettings } from '../api/catalogSettings';
import type { SongArtist } from '../api/songs';
import { ArtistPicker } from '../common/ArtistPicker';
import { Notice } from '../components/Notice';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

const CONFLICT_MESSAGE =
  'The default Artist was changed somewhere else, so it has been reloaded. Choose again if it is not what you want.';

/** The default Artist once the settings have loaded: shown, changed, and cleared, each saved at once. */
function DefaultArtist({ initial }: { initial: CatalogSettings }) {
  const [settings, setSettings] = useState(initial);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState<{ kind: 'saved' | 'error'; text: string } | null>(null);
  const artist = settings.defaultArtist;

  const save = async (next: SongArtist | null) => {
    setSaving(true);
    setMessage(null);
    const result = await setDefaultArtist(settings, next?.id ?? null);
    setSaving(false);
    switch (result.kind) {
      case 'saved':
        setSettings(result.record);
        setMessage({
          kind: 'saved',
          text:
            result.record.defaultArtist === null
              ? 'New Songs are no longer credited to a default Artist.'
              : `New Songs are credited to ${result.record.defaultArtist.name}.`,
        });
        return;
      case 'conflict':
        setSettings(result.current);
        setMessage({ kind: 'error', text: CONFLICT_MESSAGE });
        return;
      case 'invalid':
        setMessage({
          kind: 'error',
          text: result.errors.defaultArtistId?.join(' ') ?? FAILED_MESSAGE,
        });
        return;
      case 'failed':
        setMessage({ kind: 'error', text: FAILED_MESSAGE });
    }
  };

  return (
    <Paper p="md" withBorder>
      <Stack gap="sm" role="group" aria-labelledby="default-artist-heading">
        <Title order={3} size="h4" id="default-artist-heading">
          Default Artist
        </Title>
        <Text size="sm">
          New Songs are credited to the default Artist as their primary Artist, unless you choose
          another, or none, when you create one. Changing it never changes existing Songs. Songs
          imported from Suno never get it.
        </Text>
        <Group gap="xs" data-testid="default-artist">
          {artist === null ? (
            <Text size="sm">No default Artist.</Text>
          ) : (
            <>
              <Anchor component={Link} to={`/artists/${artist.id}`} size="sm" underline="always">
                {artist.name}
              </Anchor>
              <Button
                variant="default"
                size="compact-sm"
                disabled={saving}
                onClick={() => {
                  void save(null);
                }}
              >
                Clear the default
              </Button>
            </>
          )}
        </Group>
        <ArtistPicker
          label={artist === null ? 'Choose a default Artist' : 'Choose another default Artist'}
          exclude={artist === null ? [] : [artist.id]}
          busy={saving}
          onChoose={(chosen) => {
            void save(chosen);
          }}
        />
        <div role="status">
          {message !== null && (
            <Text size="sm" c={message.kind === 'error' ? 'var(--mantine-color-error)' : undefined}>
              {message.text}
            </Text>
          )}
        </div>
      </Stack>
    </Paper>
  );
}

/** Settings → Catalog: how the catalog fills itself in for you, starting with the default Artist for new Songs. */
export function CatalogPage() {
  const { state, reload } = useCatalogSettings();
  return (
    <Stack gap="lg">
      <Title order={2}>Catalog</Title>
      {state.phase === 'loading' && <Loader aria-label="Loading the catalog settings" />}
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Notice title="The catalog settings could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <div>
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
          </div>
        </Notice>
      )}
      {state.phase === 'ready' && <DefaultArtist initial={state.data} />}
    </Stack>
  );
}
