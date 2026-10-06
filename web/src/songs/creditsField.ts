import type { FieldValue } from '../api/saves';
import { isSongCredits, type SongArtist, type SongCredits } from '../api/songs';

// A Song's credits as one field of the shared save helper (`useRevisionedSave`), whose values are
// text: the primary Artist and the featured Artists in order, as JSON, so equal credits are equal
// text. Unlike Genres and Tags, the order is the user's, so a conflict on the credits is shown in the
// conflict dialog rather than merged.

/** The edit key of a Song's credits. The page sends it with `PUT …/credits`, not the Song's PATCH. */
export const CREDITS_KEY = 'credits';

const NO_CREDITS: SongCredits = { primary: null, featured: [] };

function plain(artist: SongArtist): SongArtist {
  return { id: artist.id, name: artist.name };
}

/** Credits as the field's value. */
export function creditsValue(credits: SongCredits): string {
  return JSON.stringify({
    primary: credits.primary === null ? null : plain(credits.primary),
    featured: credits.featured.map(plain),
  });
}

/** The credits a field value holds (none for null or anything unreadable). */
export function creditsOf(value: FieldValue): SongCredits {
  if (value === null) {
    return NO_CREDITS;
  }
  try {
    const parsed: unknown = JSON.parse(value);
    return isSongCredits(parsed) ? parsed : NO_CREDITS;
  } catch {
    return NO_CREDITS;
  }
}

/** Credits in words: "n8, featuring A and B"; null when there are none. */
export function creditsText(credits: SongCredits): string | null {
  const featured = credits.featured.map((artist) => artist.name);
  const last = featured.pop();
  const featuring =
    last === undefined ? '' : featured.length === 0 ? last : `${featured.join(', ')} and ${last}`;
  if (credits.primary === null) {
    return featuring === '' ? null : `Featuring ${featuring}`;
  }
  return featuring === ''
    ? credits.primary.name
    : `${credits.primary.name}, featuring ${featuring}`;
}

/** `artist` made primary: the previous primary, if any, becomes the first featured Artist. */
export function makePrimary(credits: SongCredits, artist: SongArtist): SongCredits {
  const rest = credits.featured.filter((featured) => featured.id !== artist.id);
  return {
    primary: plain(artist),
    featured:
      credits.primary === null || credits.primary.id === artist.id
        ? rest
        : [plain(credits.primary), ...rest],
  };
}

/** `credits` with the featured Artist at `from` moved to `to`. */
export function moveFeatured(credits: SongCredits, from: number, to: number): SongCredits {
  const featured = [...credits.featured];
  const [moved] = featured.splice(from, 1);
  if (moved === undefined || to < 0 || to > featured.length) {
    return credits;
  }
  featured.splice(to, 0, moved);
  return { ...credits, featured };
}
