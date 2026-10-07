/**
 * Suno's addresses, as the adapter knows them. Every Suno address pattern the extension uses is
 * here (the adapter story): a change to one raises `ADAPTER_VERSION`.
 */

/** The only Suno site the extension asks for, and where the Suno content script runs. */
export const SUNO_ORIGIN_PATTERN = 'https://suno.com/*';

const SUNO_ORIGIN = 'https://suno.com';

/** The kinds of Suno page a workflow can start on, by address (TS-003 snapshots). */
export type SunoPage = 'create' | 'library' | 'trash' | 'workspaces' | 'playlist' | 'other';

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
  if (path === '/me/workspaces') {
    return 'workspaces';
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
  workspaces: "the Library's workspace list",
  playlist: 'a playlist page',
};

/** One kind of Suno page. */
export function sunoPage(page: Exclude<SunoPage, 'other'>): PagePattern {
  return {
    description: PAGE_WORDS[page],
    matches: (address) => sunoPageOf(address) === page,
  };
}

/** Any of these kinds of Suno page. */
export function sunoPages(...pages: Exclude<SunoPage, 'other'>[]): PagePattern {
  return {
    description: pages.map((page) => PAGE_WORDS[page]).join(', '),
    matches: (address) => pages.some((page) => sunoPageOf(address) === page),
  };
}

/**
 * The address of a list the library reader opens (TS-003): the library's songs (`/me`), its
 * Trash (`/me/trash`), its workspace list (`/me/workspaces`), and a playlist (`/playlist/<id>`).
 * A playlist ID is Suno's, so it is encoded as one path segment.
 */
export function sunoListAddress(
  list: { page: 'library' | 'trash' | 'workspaces' } | { page: 'playlist'; id: string },
): URL {
  switch (list.page) {
    case 'library':
      return new URL('/me', SUNO_ORIGIN);
    case 'trash':
      return new URL('/me/trash', SUNO_ORIGIN);
    case 'workspaces':
      return new URL('/me/workspaces', SUNO_ORIGIN);
    case 'playlist':
      return new URL(`/playlist/${encodeURIComponent(list.id)}`, SUNO_ORIGIN);
  }
}

/**
 * The hosts Suno serves cover images from, taken from the image addresses in the TS-003 fixtures
 * (`image_url`, `image_large_url`). TS-003 read them with no cookies or credentials
 * (`Access-Control-Allow-Origin: *`). The cover-image read (#152) requests nothing else.
 */
export const SUNO_IMAGE_HOSTS: readonly string[] = ['cdn2.suno.ai'];

/** Whether `address` is a cover image on a listed Suno image host, over HTTPS on its own port. */
export function isSunoImageAddress(address: URL): boolean {
  return (
    address.protocol === 'https:' &&
    address.port === '' &&
    address.username === '' &&
    address.password === '' &&
    SUNO_IMAGE_HOSTS.includes(address.hostname)
  );
}
