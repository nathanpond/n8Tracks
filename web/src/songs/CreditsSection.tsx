import { Anchor, Button, Group, Stack, Text } from '@mantine/core';
import { useState } from 'react';
import { Link } from 'react-router';
import type { FieldValue } from '../api/saves';
import {
  FEATURED_ARTISTS_MAXIMUM,
  readSong,
  type Song,
  type SongArtist,
  type SongCredits,
} from '../api/songs';
import { ArtistPicker } from '../common/ArtistPicker';
import { saveError } from '../common/useInPlaceEdit';
import type { SaveOutcome } from '../common/useRevisionedSave';
import { CREDITS_KEY, creditsValue, makePrimary, moveFeatured } from './creditsField';

type SaveFields = (edit: Readonly<Record<string, FieldValue>>) => Promise<SaveOutcome>;

/** An Artist's name, linking to its page. */
function ArtistLink({ artist }: { artist: SongArtist }) {
  return (
    <Anchor component={Link} to={`/artists/${artist.id}`} size="sm" underline="always">
      {artist.name}
    </Anchor>
  );
}

/**
 * The Song's credits: one primary Artist or none, and featured Artists in the order the user
 * gives them (Move up, Move down), each chosen from the user's Artists as they type or created on
 * the spot. "Make primary" swaps a featured Artist in, the previous primary becoming the first
 * featured one; choosing an Artist already featured as primary does the same. Each change saves
 * the Song's whole new credits under its revision; an Artist that no longer exists is refused, and
 * the Song's credits are read again.
 */
export function CreditsSection({
  song,
  saveFields,
  onSong,
}: {
  song: Song;
  saveFields: SaveFields;
  onSong: (song: Song) => void;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const { primary, featured } = song.credits;
  const full = featured.length >= FEATURED_ARTISTS_MAXIMUM;

  const change = async (next: SongCredits) => {
    setBusy(true);
    setError(undefined);
    const outcome = await saveFields({ [CREDITS_KEY]: creditsValue(next) });
    const refused =
      outcome.kind === 'invalid'
        ? [...(outcome.errors.primaryArtistId ?? []), ...(outcome.errors.featuredArtistIds ?? [])]
        : [];
    if (refused.length > 0) {
      const current = await readSong(song.id);
      if (current !== undefined) {
        onSong(current);
      }
      setError(`${refused.join(' ')} The credits have been read again.`);
    } else {
      setError(saveError(outcome, CREDITS_KEY));
    }
    setBusy(false);
  };

  const choosePrimary = (artist: SongArtist) => {
    void change(
      featured.some((other) => other.id === artist.id)
        ? makePrimary(song.credits, artist)
        : { primary: artist, featured },
    );
  };

  return (
    <Stack gap="sm" data-testid="song-credits">
      <Stack gap={4} role="group" aria-labelledby="song-primary-artist">
        <Text fw={500} size="sm" id="song-primary-artist">
          Primary Artist
        </Text>
        {primary === null ? (
          <Text size="sm" c="var(--n8-color-secondary-text)" data-testid="primary-artist">
            None
          </Text>
        ) : (
          <Group gap="xs" data-testid="primary-artist">
            <ArtistLink artist={primary} />
            <Button
              variant="subtle"
              size="compact-xs"
              aria-label={`Remove primary Artist ${primary.name}`}
              disabled={busy}
              onClick={() => {
                void change({ primary: null, featured });
              }}
            >
              Remove
            </Button>
          </Group>
        )}
        <ArtistPicker
          label={primary === null ? 'Choose the primary Artist' : 'Change the primary Artist'}
          exclude={primary === null ? [] : [primary.id]}
          allowCreate
          busy={busy}
          onChoose={choosePrimary}
        />
      </Stack>

      <Stack gap={4} role="group" aria-labelledby="song-featured-artists">
        <Text fw={500} size="sm" id="song-featured-artists">
          Featured Artists
        </Text>
        {featured.length === 0 ? (
          <Text size="sm" c="var(--n8-color-secondary-text)">
            None
          </Text>
        ) : (
          <ol aria-labelledby="song-featured-artists" style={{ margin: 0, paddingInlineStart: 20 }}>
            {featured.map((artist, index) => (
              <li key={artist.id} data-featured-artist={artist.name}>
                <Group gap={4} wrap="wrap">
                  <ArtistLink artist={artist} />
                  <Button
                    variant="subtle"
                    size="compact-xs"
                    aria-label={`Move ${artist.name} up`}
                    disabled={busy || index === 0}
                    onClick={() => {
                      void change(moveFeatured(song.credits, index, index - 1));
                    }}
                  >
                    Up
                  </Button>
                  <Button
                    variant="subtle"
                    size="compact-xs"
                    aria-label={`Move ${artist.name} down`}
                    disabled={busy || index === featured.length - 1}
                    onClick={() => {
                      void change(moveFeatured(song.credits, index, index + 1));
                    }}
                  >
                    Down
                  </Button>
                  <Button
                    variant="subtle"
                    size="compact-xs"
                    aria-label={`Make ${artist.name} primary`}
                    disabled={busy}
                    onClick={() => {
                      void change(makePrimary(song.credits, artist));
                    }}
                  >
                    Make primary
                  </Button>
                  <Button
                    variant="subtle"
                    size="compact-xs"
                    aria-label={`Remove featured Artist ${artist.name}`}
                    disabled={busy}
                    onClick={() => {
                      void change({
                        primary,
                        featured: featured.filter((other) => other.id !== artist.id),
                      });
                    }}
                  >
                    Remove
                  </Button>
                </Group>
              </li>
            ))}
          </ol>
        )}
        <ArtistPicker
          label="Add a featured Artist"
          description={
            full
              ? `A Song has at most ${String(FEATURED_ARTISTS_MAXIMUM)} featured Artists.`
              : undefined
          }
          exclude={[...(primary === null ? [] : [primary.id]), ...featured.map((a) => a.id)]}
          allowCreate
          busy={busy}
          disabled={full}
          onChoose={(artist) => {
            void change({ primary, featured: [...featured, artist] });
          }}
        />
      </Stack>
      {error !== undefined && (
        <Text size="sm" c="var(--mantine-color-error)" role="alert">
          {error}
        </Text>
      )}
    </Stack>
  );
}
