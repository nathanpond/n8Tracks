/**
 * Suno's addresses, as the adapter knows them. Every Suno address pattern the extension uses is
 * here (the adapter story): a change to one raises `ADAPTER_VERSION`.
 */

/** The only Suno site the extension asks for, and where the Suno content script runs. */
export const SUNO_ORIGIN_PATTERN = 'https://suno.com/*';

const SUNO_ORIGIN = 'https://suno.com';

/** The kinds of Suno page a workflow can start on, by address (TS-003 snapshots). */
export type SunoPage = 'create' | 'library' | 'trash' | 'playlist' | 'other';

/** A set of Suno pages a workflow starts on, with plain words for the panel. */
export interface PagePattern {
  /** "any suno.com page", "the Create page". */
  description: string;
  matches(address: URL): boolean;
}

/** Whether `address` is on suno.com. */
export function isSunoAddress(address: URL): boolean {
  return address.origin === SUNO_ORIGIN;
}

/** Which kind of Suno page `address` shows; `other` for any page the adapter does not know. */
export function sunoPageOf(address: URL): SunoPage | null {
  if (!isSunoAddress(address)) {
    return null;
  }
  const path = address.pathname.replace(/\/+$/, '');
  if (path === '/create') {
    return 'create';
  }
  if (path === '/me/trash') {
    return 'trash';
  }
  if (path === '/me') {
    return 'library';
  }
  if (/^\/playlist\/[^/]+$/.test(path)) {
    return 'playlist';
  }
  return 'other';
}

/** Any page on suno.com. */
export const ANY_SUNO_PAGE: PagePattern = {
  description: 'any suno.com page',
  matches: (address) => isSunoAddress(address),
};

const PAGE_WORDS: Record<Exclude<SunoPage, 'other'>, string> = {
  create: 'the Create page',
  library: 'the Library',
  trash: 'the Library trash',
  playlist: 'a playlist page',
};

/** One kind of Suno page. */
export function sunoPage(page: Exclude<SunoPage, 'other'>): PagePattern {
  return {
    description: PAGE_WORDS[page],
    matches: (address) => sunoPageOf(address) === page,
  };
}
