import { searchTextOf, type SongMatch, type SongMatchHighlight } from '../api/songs';

/** What each matched field is called on the page, by the API's `field`. */
const FIELD_NAMES: Readonly<Record<string, string>> = {
  title: 'Title',
  concept: 'Concept',
  shortcode: 'Shortcode',
  lyrics: 'Lyrics',
  styles: 'Styles',
  prompt: 'Prompt',
  versionName: 'Version name',
  versionNotes: 'Version notes',
  tag: 'Tag',
  comment: 'Comment',
  album: 'Album',
  playlist: 'Playlist',
  sunoTitle: 'Suno title',
  sunoTags: 'Suno tags',
  model: 'Model',
};

/** A matched field's name as the page shows it; a field the page does not know shows as sent. */
export function matchFieldName(field: string): string {
  return FIELD_NAMES[field] ?? field;
}

/**
 * What a match belongs to, as its row shows it: a Version's number (`v2.1`) or a Generation's
 * shortcode; nothing for the Song's own text, its Tags, and an Album or Playlist (whose title is the
 * excerpt itself).
 */
export function matchOwnerText(match: SongMatch): string | undefined {
  switch (match.owner?.kind) {
    case 'version':
      return match.owner.label;
    case 'generation':
      return match.owner.reference;
    default:
      return undefined;
  }
}

/** The mark a match in archived or trashed text carries; nothing for active text. */
export function matchStateText(match: SongMatch): string | undefined {
  switch (match.owner?.state) {
    case 'archived':
      return 'Archived';
    case 'trashed':
      return 'In Suno’s Trash';
    default:
      return undefined;
  }
}

/** The number of the Version a Version shortcode names (`n8-12-v2.1` → `2.1`); undefined otherwise. */
export function versionNumberOf(shortcode: string): string | undefined {
  return /-v(\d+(?:\.\d+)*)$/i.exec(shortcode)?.[1];
}

/**
 * Where choosing a match goes: a Version's match opens the Song on that Version, a Generation's opens
 * the Song with that Generation's panel open, and anything else opens the Song.
 */
export function matchAddress(songShortcode: string, match: SongMatch): string {
  const song = `/songs/${encodeURIComponent(songShortcode)}`;
  if (match.owner?.kind === 'version') {
    const number = versionNumberOf(match.owner.reference);
    return number === undefined ? song : `${song}/v/${number}`;
  }
  if (match.owner?.kind === 'generation') {
    return `${song}/generations/${encodeURIComponent(match.owner.reference)}`;
  }
  return song;
}

/** A run of an excerpt's text, matched or not. */
export interface ExcerptPart {
  text: string;
  matched: boolean;
}

/**
 * An excerpt split into runs at its highlights. Highlights outside the text are dropped, ones that
 * overlap an earlier one are cut to what is left, so every character is in exactly one run.
 */
export function excerptParts(
  text: string,
  highlights: readonly SongMatchHighlight[],
): ExcerptPart[] {
  const parts: ExcerptPart[] = [];
  let at = 0;
  const ordered = [...highlights].sort((left, right) => left.start - right.start);
  for (const highlight of ordered) {
    const start = Math.max(highlight.start, at);
    const end = Math.min(highlight.start + highlight.length, text.length);
    if (end <= start) {
      continue;
    }
    if (start > at) {
      parts.push({ text: text.slice(at, start), matched: false });
    }
    parts.push({ text: text.slice(start, end), matched: true });
    at = end;
  }
  if (at < text.length) {
    parts.push({ text: text.slice(at), matched: false });
  }
  return parts;
}

/**
 * Whether a key press is the search shortcut: `/` on its own, or Ctrl+K / ⌘K. Ignored when another
 * modifier is held, so Ctrl+Shift+K and the like stay the browser's.
 */
export function isSearchShortcut(
  event: Pick<KeyboardEvent, 'key' | 'ctrlKey' | 'metaKey' | 'altKey' | 'shiftKey'>,
): boolean {
  if (event.altKey) {
    return false;
  }
  if (event.key === '/') {
    return !event.ctrlKey && !event.metaKey;
  }
  return event.key.toLowerCase() === 'k' && !event.shiftKey && (event.ctrlKey || event.metaKey);
}

/** The `id` of the Songs table's own search box, which the shortcut focuses on the Songs page. */
export const SONGS_SEARCH_ID = 'songs-search';

/** The `id` of the header's search box. */
export const HEADER_SEARCH_ID = 'header-search';

/** Where a header search goes: the Songs table with that text and nothing else, or every Song for none. */
export function headerSearchAddress(text: string): string {
  const search = searchTextOf(text);
  return search === undefined ? '/songs' : `/songs?${new URLSearchParams({ search }).toString()}`;
}
