import { UNTITLED } from './clips.ts';
import type { DownloadFormat } from './selection.ts';

/**
 * The name a downloaded file is saved under (#216): `<Artist> - <Title> (suno-<Suno ID>).<ext>`,
 * or `<Title> (suno-<Suno ID>).<ext>` when there is no Artist. The Suno ID token is what n8Tracks'
 * matcher (#206, `SunoIdMatcher`) finds once the file is in the media folder, so it is always kept
 * whole; the shared examples in `extension/fixtures/filenames.json` are read by both sides. Pure:
 * the same clip and format always give the same name.
 */

/** The browser's download folder's subfolder the files go to. */
export const DOWNLOAD_FOLDER = 'n8Tracks';

/** The most UTF-16 units a whole name may have. */
export const MAX_NAME_UNITS = 180;

/** The most bytes of UTF-8 a whole name may have. */
export const MAX_NAME_BYTES = 240;

/** What a file name is made from: a plan entry's clip and format. */
export interface NamedClip {
  sunoId: string;
  /** Suno's title. */
  title: string;
  /** The Suno creator's `display_name`. */
  displayName: string;
  /** The Song's primary Artist in n8Tracks, or null. */
  artist: string | null;
  format: DownloadFormat;
}

/**
 * Each format's extension, and what follows the token: the streaming-quality M4A says `stream`, so
 * it is told apart from the M4A Suno prepares.
 */
const ENDINGS: Readonly<Record<DownloadFormat, string>> = {
  wav: '.wav',
  mp3: '.mp3',
  m4a: '.m4a',
  'm4a-stream': ' stream.m4a',
};

/** Characters not allowed in a file name on Windows, macOS, or Linux: replaced with `_`. */
const NOT_ALLOWED = /[<>:"/\\|?*]/g;

/** White space that is a control character (tab, line breaks): read as a space. */
const CONTROL_SPACE = /[\t\n\v\f\r\u0085\u2028\u2029]/g;

/**
 * Control characters, and the bidirectional controls that can make a name read differently from
 * what it is (an extension shown in the middle): removed.
 */
const CONTROLS = /[\p{Cc}‎‏‪-‮⁦-⁩]/gu;

/** Windows' reserved device names, which a name may not be, whatever its extension. */
const RESERVED = /^(con|prn|aux|nul|com[0-9¹²³]|lpt[0-9¹²³])$/i;

/** Leading and trailing dots and white space. */
const EDGES = /^[\s.]+|[\s.]+$/gu;

const segmenter = new Intl.Segmenter(undefined, { granularity: 'grapheme' });
const encoder = new TextEncoder();

/** A part of the name made safe: NFC, no controls, no characters any system refuses, no edges. */
export function cleanPart(text: string): string {
  return text
    .normalize('NFC')
    .replace(CONTROL_SPACE, ' ')
    .replace(CONTROLS, '')
    .replace(NOT_ALLOWED, '_')
    .replace(EDGES, '');
}

/** The Suno ID token: the ID as it is, lower case, so the matcher finds it. */
export function sunoIdToken(sunoId: string): string {
  return `(suno-${sunoId.toLowerCase()})`;
}

function graphemes(text: string): string[] {
  return [...segmenter.segment(text)].map((part) => part.segment);
}

function fits(name: string): boolean {
  return name.length <= MAX_NAME_UNITS && encoder.encode(name).length <= MAX_NAME_BYTES;
}

function readable(artist: string, title: string): string {
  const name = artist === '' ? title : `${artist} - ${title}`;
  // A Windows device name (`CON`, `nul.txt`) gets a leading underscore.
  const stem = (name.split('.')[0] ?? '').trimEnd();
  return RESERVED.test(stem) ? `_${name}` : name;
}

/**
 * The file name for one clip in one format. The Artist is the Song's primary Artist in n8Tracks,
 * else the Suno display name, else none; the title is Suno's, else "Untitled". When the name is too
 * long, the title is cut first, then the Artist, a whole character at a time; the token and the
 * extension never are.
 */
export function fileNameFor(clip: NamedClip): string {
  const fromN8Tracks = cleanPart(clip.artist ?? '');
  let artist = fromN8Tracks === '' ? cleanPart(clip.displayName) : fromN8Tracks;
  const cleanTitle = cleanPart(clip.title);
  let title = graphemes(cleanTitle === '' ? UNTITLED : cleanTitle);
  const fixed = ` ${sunoIdToken(clip.sunoId)}${ENDINGS[clip.format]}`;
  const nameOf = () => `${readable(artist, title.join('').trimEnd())}${fixed}`;

  let artistParts = graphemes(artist);
  while (!fits(nameOf())) {
    if (title.length > 1) {
      title = title.slice(0, -1);
    } else if (artistParts.length > 0) {
      artistParts = artistParts.slice(0, -1);
      artist = artistParts.join('').trimEnd();
    } else {
      break;
    }
  }
  return nameOf();
}

/** The path handed to the browser's downloads interface: the subfolder and the name. */
export function downloadPathFor(clip: NamedClip): string {
  return `${DOWNLOAD_FOLDER}/${fileNameFor(clip)}`;
}
