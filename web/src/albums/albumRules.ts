import {
  type Album,
  ALBUM_DESCRIPTION_MAXIMUM_LENGTH,
  ALBUM_RIGHTS_MAXIMUM_LENGTH,
  ALBUM_TITLE_MAXIMUM_LENGTH,
} from '../api/albums';

// The API's Album rules (AlbumRules), checked before anything is sent so the user sees them at
// once. The API checks them again and its errors are shown the same way.

const atMost = (limit: number) => `Use at most ${limit.toLocaleString('en-US')} characters.`;

/** A title's error: 1 to 300 once trimmed, one line. */
export function albumTitleError(title: string): string | undefined {
  const trimmed = title.trim();
  if (trimmed === '') {
    return 'Enter a title.';
  }
  if (/[\n\r]/.test(trimmed)) {
    return 'A title is one line, with no control characters.';
  }
  return trimmed.length > ALBUM_TITLE_MAXIMUM_LENGTH
    ? atMost(ALBUM_TITLE_MAXIMUM_LENGTH)
    : undefined;
}

/** Plain text as the API stores it: line endings as `\n`, trimmed, and null when blank. */
export function normaliseAlbumText(text: string): string | null {
  const normalised = text.replace(/\r\n?/g, '\n').trim();
  return normalised === '' ? null : normalised;
}

/** The error of plain text up to `limit` once normalised. */
function textError(text: string, limit: number): string | undefined {
  return (normaliseAlbumText(text)?.length ?? 0) > limit ? atMost(limit) : undefined;
}

export const descriptionError = (text: string) => textError(text, ALBUM_DESCRIPTION_MAXIMUM_LENGTH);

/** Copyright or publishing text's error: up to 500 once normalised. */
export const rightsError = (text: string) => textError(text, ALBUM_RIGHTS_MAXIMUM_LENGTH);

/** A partial date as the API stores it: trimmed, and null when empty. */
export function normaliseAlbumDate(date: string): string | null {
  const trimmed = date.trim();
  return trimmed === '' ? null : trimmed;
}

/** The parts of a partial date, or undefined when it is not one of the three forms. */
function dateParts(date: string): { year: number; month?: number; day?: number } | undefined {
  const match = /^(\d{4})(?:-(\d{2})(?:-(\d{2}))?)?$/.exec(date);
  if (match === null) {
    return undefined;
  }
  const [, year = '', month, day] = match;
  return {
    year: Number(year),
    ...(month === undefined ? {} : { month: Number(month) }),
    ...(day === undefined ? {} : { day: Number(day) }),
  };
}

/** A partial date's error: `YYYY`, `YYYY-MM`, or `YYYY-MM-DD`, a year from 1000 to 9999, a real day. */
export function albumDateError(date: string): string | undefined {
  const normalised = normaliseAlbumDate(date);
  if (normalised === null) {
    return undefined;
  }
  const parts = dateParts(normalised);
  if (parts === undefined) {
    return 'Enter a year (2026), a year and month (2026-03), or a full date (2026-03-01).';
  }
  if (parts.year < 1000) {
    return 'Enter a year from 1000 to 9999.';
  }
  if (parts.month !== undefined && (parts.month < 1 || parts.month > 12)) {
    return 'Enter a month from 01 to 12.';
  }
  if (parts.day !== undefined && parts.month !== undefined) {
    const days = new Date(Date.UTC(parts.year, parts.month, 0)).getUTCDate();
    if (parts.day < 1 || parts.day > days) {
      return 'That day does not exist in that month.';
    }
  }
  return undefined;
}

/**
 * A stored partial date as the user's locale writes it: the year alone, the month and year, or the
 * full date ("2026", "March 2026", "March 1, 2026" in US English). Text that is not a partial date
 * is shown as it is.
 */
export function formatAlbumDate(date: string, locale?: string): string {
  const parts = dateParts(date);
  if (parts === undefined) {
    return date;
  }
  const when = new Date(Date.UTC(parts.year, (parts.month ?? 1) - 1, parts.day ?? 1));
  return new Intl.DateTimeFormat(locale, {
    timeZone: 'UTC',
    year: 'numeric',
    ...(parts.month === undefined ? {} : { month: 'long' }),
    ...(parts.day === undefined ? {} : { day: 'numeric' }),
  }).format(when);
}

/** A UPC/EAN as the API stores it: spaces and hyphens removed, and null when nothing is left. */
export function normaliseUpc(upc: string): string | null {
  const digits = upc.replace(/[\s-]/g, '');
  return digits === '' ? null : digits;
}

/** The GS1 check digit of `payload`: weights 3 and 1 alternate from the right. */
function checkDigit(payload: string): number {
  let sum = 0;
  for (let index = 0; index < payload.length; index++) {
    const weight = (payload.length - index) % 2 === 1 ? 3 : 1;
    sum += Number(payload[index]) * weight;
  }
  return (10 - (sum % 10)) % 10;
}

/** A UPC/EAN's error: 12 or 13 digits, once spaces and hyphens are removed, with a valid check digit. */
export function upcError(upc: string): string | undefined {
  const digits = normaliseUpc(upc);
  if (digits === null) {
    return undefined;
  }
  if (!/^\d{12,13}$/.test(digits)) {
    return 'Enter a UPC of 12 digits or an EAN of 13 digits.';
  }
  return checkDigit(digits.slice(0, -1)) === Number(digits.slice(-1))
    ? undefined
    : 'The check digit is wrong: check the code for a typing mistake.';
}

/** The date a list shows for an Album: the release date, else the original release date. */
export function shownDate(
  album: Pick<Album, 'releaseDate' | 'originalReleaseDate'>,
): string | null {
  return album.releaseDate ?? album.originalReleaseDate;
}
