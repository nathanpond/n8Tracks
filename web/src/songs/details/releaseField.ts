import type { FieldValue } from '../../api/saves';
import type { SongLink, SongRelease } from '../../api/songs';

// A Song's release details as the shared save helper holds them: one field per member, keyed as
// the API keys its errors (`release.isrc`), each a text value; links are JSON text.

export const RELEASE_PREFIX = 'release.';

/** The text members of a release, in the order the Release section shows them. */
export const RELEASE_TEXT_MEMBERS = [
  'releaseDate',
  'originalReleaseDate',
  'explicit',
  'copyright',
  'publishing',
  'isrc',
  'language',
] as const;

export type ReleaseTextMember = (typeof RELEASE_TEXT_MEMBERS)[number];

/** The save helper's key (and the API's error key) of a release member. */
export const releaseKey = (member: ReleaseTextMember | 'links') => `${RELEASE_PREFIX}${member}`;

export const LINKS_KEY = releaseKey('links');

/** A Song's links as the save helper holds them. */
export const linksValue = (links: readonly SongLink[]) => JSON.stringify(links);

function linksOf(value: FieldValue): SongLink[] {
  return value === null ? [] : (JSON.parse(value) as SongLink[]);
}

/** Whether a save helper key is a release member's. */
export const isReleaseKey = (key: string) => key.startsWith(RELEASE_PREFIX);

/** The `release` object of the PATCH for the release members among `edit`; undefined when there are none. */
export function releaseEditOf(
  edit: Readonly<Record<string, FieldValue>>,
): Partial<SongRelease> | undefined {
  const release: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(edit)) {
    if (!isReleaseKey(key)) {
      continue;
    }
    const member = key.slice(RELEASE_PREFIX.length);
    release[member] = member === 'links' ? linksOf(value) : value;
  }
  return Object.keys(release).length === 0 ? undefined : release;
}

/** The labels the conflict dialog names each member by. */
export const RELEASE_LABELS: Record<ReleaseTextMember | 'links', string> = {
  releaseDate: 'Release date',
  originalReleaseDate: 'Original release date',
  explicit: 'Explicit content',
  copyright: 'Copyright',
  publishing: 'Publishing',
  isrc: 'ISRC',
  language: 'Language',
  links: 'Links',
};

/** How the explicit flag reads: Explicit, Clean, or Not set. */
export function explicitLabel(value: FieldValue): string {
  return value === 'explicit' ? 'Explicit' : value === 'clean' ? 'Clean' : 'Not set';
}

/** Links as the conflict dialog writes them. */
export function linksText(value: FieldValue): string | null {
  const links = linksOf(value);
  return links.length === 0
    ? null
    : links
        .map((link) => (link.label === null ? link.url : `${link.label}: ${link.url}`))
        .join(', ');
}

/** The length of an ISRC. */
export const ISRC_LENGTH = 12;

/**
 * An ISRC as the API stores it: upper case, spaces and hyphens left out, and a leading `ISRC` left
 * out when more than 12 characters remain with it; null when nothing is left.
 */
export function normaliseIsrc(isrc: string): string | null {
  let code = isrc.replace(/[\s-]/g, '').toUpperCase();
  if (code.length > ISRC_LENGTH && code.startsWith('ISRC')) {
    code = code.slice(4);
  }
  return code === '' ? null : code;
}

/** An ISRC's error, by the API's rule: two letters, three letters or digits, and seven digits. */
export function isrcError(isrc: string): string | undefined {
  const code = normaliseIsrc(isrc);
  if (code === null) {
    return undefined;
  }
  if (code.length !== ISRC_LENGTH) {
    return `An ISRC has ${String(ISRC_LENGTH)} characters once spaces and hyphens are left out; this has ${String(code.length)}.`;
  }
  return /^[A-Z]{2}[A-Z0-9]{3}[0-9]{7}$/.test(code)
    ? undefined
    : 'Enter an ISRC as two letters, three letters or digits, and seven digits (such as US-S1Z-99-00001).';
}
