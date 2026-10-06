import {
  ARTIST_LINK_LABEL_MAXIMUM_LENGTH,
  ARTIST_LINK_URL_MAXIMUM_LENGTH,
  ARTIST_NAME_MAXIMUM_LENGTH,
  ARTIST_NOTES_MAXIMUM_LENGTH,
  type ArtistLink,
} from '../api/artists';
import { genreNameKey, normaliseGenreName } from '../api/genres';

// The API's Artist rules (ArtistRules), checked before anything is sent so the user sees them at
// once. The API checks them again and its errors are shown the same way.

/** A name or alias as the API stores it: trimmed, inner white space as one space. */
export const normaliseArtistName = normaliseGenreName;

/** What names and aliases are compared by: normalised, NFC, ignoring case. */
export const artistNameKey = genreNameKey;

const atMost = (limit: number, what = '') =>
  `Use at most ${limit.toLocaleString('en-US')} characters${what}.`;

/** A display name's error: 1 to 200 once normalised. */
export function artistNameError(name: string): string | undefined {
  const normalised = normaliseArtistName(name);
  if (normalised === '') {
    return 'Enter a name.';
  }
  return normalised.length > ARTIST_NAME_MAXIMUM_LENGTH
    ? atMost(ARTIST_NAME_MAXIMUM_LENGTH)
    : undefined;
}

/**
 * Each alias's error, by position (undefined where it is fine): valid as a name, not the Artist's
 * own name, and not the same as an earlier alias ignoring case.
 */
export function aliasErrors(aliases: readonly string[], name: string): (string | undefined)[] {
  const nameKey = artistNameKey(name);
  const seen = new Set<string>();
  return aliases.map((alias) => {
    const error = artistNameError(alias);
    if (error !== undefined) {
      return error;
    }
    const key = artistNameKey(alias);
    if (key === nameKey) {
      return "An alias cannot be the Artist's own name.";
    }
    if (seen.has(key)) {
      return 'The Artist already has this alias.';
    }
    seen.add(key);
    return undefined;
  });
}

/** Whether `url` is an absolute http or https address, as the API requires. */
export function isWebAddress(url: string): boolean {
  const trimmed = url.trim();
  if (trimmed === '' || /\s/.test(trimmed)) {
    return false;
  }
  try {
    const parsed = new URL(trimmed);
    return (parsed.protocol === 'http:' || parsed.protocol === 'https:') && parsed.hostname !== '';
  } catch {
    return false;
  }
}

/** A link's errors: its label (up to 100) and its URL (an http or https address, up to 2,000). */
export function linkErrors(link: ArtistLink): { label?: string; url?: string } {
  const label = normaliseArtistName(link.label ?? '');
  const url = link.url.trim();
  return {
    label:
      label.length > ARTIST_LINK_LABEL_MAXIMUM_LENGTH
        ? atMost(ARTIST_LINK_LABEL_MAXIMUM_LENGTH)
        : undefined,
    url:
      url === ''
        ? 'Enter a URL.'
        : url.length > ARTIST_LINK_URL_MAXIMUM_LENGTH
          ? atMost(ARTIST_LINK_URL_MAXIMUM_LENGTH)
          : isWebAddress(url)
            ? undefined
            : 'Enter a web address starting with http:// or https://.',
  };
}

/** The notes' error: up to 10,000 once trimmed. */
export function artistNotesError(notes: string): string | undefined {
  return notes.replace(/\r\n?/g, '\n').trim().length > ARTIST_NOTES_MAXIMUM_LENGTH
    ? atMost(ARTIST_NOTES_MAXIMUM_LENGTH)
    : undefined;
}
