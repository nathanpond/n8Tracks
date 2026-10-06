import { appBasePath } from './baseUrl';
import { apiFetch } from './client';
import { body, isRecord } from './songs';

const RESOLVE_PATH = 'api/v1/resolve';

/** What a stable ID or a shortcode names, as the resolve endpoint answers it. */
export interface ResolvedReference {
  entityType: 'song' | 'version' | 'generation';
  id: string;
  shortcode: string;
  status: string;
  /** The Song a Version or Generation belongs to; absent for a Song. */
  song?: { id: string; shortcode: string };
  /** The Version a Generation belongs to; absent otherwise. */
  version?: { id: string; shortcode: string };
}

function isNamed(value: unknown): boolean {
  return (
    value === undefined ||
    (isRecord(value) && typeof value.id === 'string' && typeof value.shortcode === 'string')
  );
}

function isResolvedReference(value: unknown): value is ResolvedReference {
  return (
    isRecord(value) &&
    (value.entityType === 'song' ||
      value.entityType === 'version' ||
      value.entityType === 'generation') &&
    typeof value.id === 'string' &&
    typeof value.shortcode === 'string' &&
    typeof value.status === 'string' &&
    isNamed(value.song) &&
    isNamed(value.version)
  );
}

/** How resolving ended. Never a rejection. */
export type ResolveResult =
  { kind: 'found'; resolved: ResolvedReference } | { kind: 'not-found' } | { kind: 'failed' };

/** Asks the API what `reference` (a stable ID or a complete shortcode) names. */
export async function resolveReference(
  reference: string,
  signal?: AbortSignal,
): Promise<ResolveResult> {
  if (reference === '') {
    return { kind: 'not-found' };
  }
  try {
    const response = await apiFetch(`${RESOLVE_PATH}/${encodeURIComponent(reference)}`, {
      signal,
    });
    const answer = await body(response);
    if (response.ok && isResolvedReference(answer)) {
      return { kind: 'found', resolved: answer };
    }
    return response.status === 404 ? { kind: 'not-found' } : { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/**
 * The app page that shows what a reference resolved to: the Song's page, its Song's page with the
 * Version selected, or a Generation's panel on its Song's page
 * (`/songs/<song>/generations/<shortcode>`). A path inside the app, for the router.
 */
export function pageFor(resolved: ResolvedReference): string {
  if (resolved.entityType === 'song' || resolved.song === undefined) {
    return `/songs/${resolved.shortcode}`;
  }
  if (resolved.entityType === 'generation') {
    return `/songs/${resolved.song.shortcode}/generations/${resolved.shortcode}`;
  }
  const number = resolved.shortcode.slice(`${resolved.song.shortcode}-v`.length);
  return `/songs/${resolved.song.shortcode}/v/${number}`;
}

/**
 * What the Go to box was given, once read: a reference to resolve, a Song reference and a Version
 * number (a Version page URL), or nothing this instance could show.
 */
export type GoToTarget =
  | { kind: 'reference'; reference: string }
  | { kind: 'version-page'; song: string; number: string }
  | { kind: 'none' };

function decoded(segment: string): string | undefined {
  try {
    return decodeURIComponent(segment);
  } catch {
    return undefined;
  }
}

/**
 * Reads what was typed or pasted into the Go to box, ignoring surrounding whitespace: a stable ID
 * or shortcode as it is, or a URL of this instance (`/go/<reference>`, `/songs/<reference>`,
 * `/songs/<reference>/v/<number>`, or `/songs/<reference>/generations/<generation>`, absolute or
 * from the root). A URL of another host, or of another
 * kind of page, names nothing.
 */
export function goToTarget(text: string): GoToTarget {
  const trimmed = text.trim();
  if (trimmed === '') {
    return { kind: 'none' };
  }
  const isUrl = /^[a-z][a-z0-9+.-]*:\/\//i.test(trimmed) || trimmed.startsWith('/');
  if (!isUrl) {
    return { kind: 'reference', reference: trimmed };
  }

  let url: URL;
  try {
    url = new URL(trimmed, document.baseURI);
  } catch {
    return { kind: 'none' };
  }
  if (url.origin !== new URL(document.baseURI).origin) {
    return { kind: 'none' };
  }
  const base = appBasePath();
  const prefix = base === '/' ? '/' : `${base}/`;
  if (!url.pathname.startsWith(prefix)) {
    return { kind: 'none' };
  }
  const raw = url.pathname.slice(prefix.length).split('/');
  const segments = raw.map(decoded).filter((segment) => segment !== undefined && segment !== '');
  if (segments.length !== raw.length) {
    return { kind: 'none' };
  }
  const [first, second, third, fourth] = segments;
  if (segments.length === 2 && (first === 'go' || first === 'songs') && second !== undefined) {
    return { kind: 'reference', reference: second };
  }
  if (
    segments.length === 4 &&
    first === 'songs' &&
    third === 'generations' &&
    fourth !== undefined
  ) {
    return { kind: 'reference', reference: fourth };
  }
  if (
    segments.length === 4 &&
    first === 'songs' &&
    third === 'v' &&
    second !== undefined &&
    fourth !== undefined
  ) {
    return { kind: 'version-page', song: second, number: fourth };
  }
  return { kind: 'none' };
}

/**
 * Resolves what the Go to box was given, always through the resolve endpoint. A Version page URL
 * resolves its Song first, then the Version's shortcode, so a page named by the Song's ID works too.
 */
export async function resolveGoTo(target: GoToTarget): Promise<ResolveResult> {
  switch (target.kind) {
    case 'none':
      return { kind: 'not-found' };
    case 'reference':
      return resolveReference(target.reference);
    case 'version-page': {
      const song = await resolveReference(target.song);
      if (song.kind !== 'found' || song.resolved.entityType !== 'song') {
        return song.kind === 'failed' ? song : { kind: 'not-found' };
      }
      return resolveReference(`${song.resolved.shortcode}-v${target.number}`);
    }
  }
}
