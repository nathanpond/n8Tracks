/**
 * What the page observer (`page/observe.ts`) forwards, and how it decides. The observer runs in
 * Suno's own page and wraps its `fetch`; for each response whose address is one of the lists
 * below (TS-003), it forwards a copy to the content script. It never sends a request of its own,
 * never reads a header, and never forwards `token`, `create_session_token`, or `user_tier`
 * (invariant 6), at any depth of a request or a response.
 *
 * These are adapter patterns: a change to one raises `ADAPTER_VERSION`.
 */

/**
 * The lists the library reader reads (TS-003, "How lists are paged"), the answer to creating a
 * workspace, which gives Generate on Suno the new workspace's ID (#145), and the answer to the
 * user's own Create click (#149), which the extension never makes itself (invariant 4).
 */
export type ObservedKind =
  | 'library-feed'
  | 'trash'
  | 'workspaces'
  | 'playlists'
  | 'playlist-feed'
  | 'workspace-created'
  | 'create';

/** One Suno list response: method and path on Suno's API host. */
interface ListPattern {
  kind: ObservedKind;
  method: 'GET' | 'POST';
  path: string;
}

/**
 * The responses the observer forwards (TS-003): the feed of Library › Songs or a workspace, the
 * Trash list, the workspace list, the playlist list, a playlist's songs, and the new workspace
 * from the list's inline "Create new workspace" row (`project.create.response.json`).
 */
export const OBSERVED_LISTS: readonly ListPattern[] = [
  { kind: 'library-feed', method: 'POST', path: '/api/feed/v3' },
  { kind: 'trash', method: 'GET', path: '/api/clips/trashed_v2' },
  { kind: 'workspaces', method: 'GET', path: '/api/project/me' },
  { kind: 'playlists', method: 'GET', path: '/api/playlist/me' },
  { kind: 'playlist-feed', method: 'POST', path: '/api/unified/feed' },
  { kind: 'workspace-created', method: 'POST', path: '/api/project' },
  { kind: 'create', method: 'POST', path: '/api/generate/v2-web' },
];

/**
 * The Create request's values n8Tracks reads (#149): the import field map's `createRequest` paths
 * (`docs/suno-import-field-map.json`; `test/observed-create-paths.test.ts` keeps the two in step) and
 * the mode marker, and nothing else. `user_uploaded_images_b64` is left out: an uploaded image's bytes
 * are never sent on (n8Tracks keeps only a note of a file input). `token`, `create_session_token`, and
 * `user_tier` are never among them (invariant 6).
 */
export const CREATE_REQUEST_PATHS: readonly string[] = [
  'cover_clip_id',
  'duration',
  'gpt_description_prompt',
  'metadata.backing_music',
  'metadata.control_sliders.aug_creativity',
  'metadata.control_sliders.style_weight',
  'metadata.control_sliders.weirdness_constraint',
  'metadata.create_mode',
  'metadata.is_max_mode',
  'metadata.sound_configs.user_key',
  'metadata.sound_configs.user_loop',
  'metadata.sound_configs.user_tempo',
  'metadata.vocal_gender',
  'mv',
  'negative_tags',
  'persona_id',
  'project_id',
  'prompt',
  'tags',
  'title',
  'use_personalization',
];

/** Never forwarded, wherever they appear (TS-003: the Create request carries them). */
export const NEVER_FORWARDED: ReadonlySet<string> = new Set([
  'token',
  'create_session_token',
  'user_tier',
]);

/**
 * The request fields the reader needs: paging and the filters the page applied. Everything else
 * the page sent stays in the page.
 */
const REQUEST_FIELDS = ['cursor', 'limit', 'filters', 'feed_id', 'page_size'] as const;
const QUERY_FIELDS = ['cursor', 'page', 'limit'] as const;

/** What the reader is told of the request that brought a response. */
export interface ObservedRequest {
  /** The paging cursor sent (body or query), or null on a first page. */
  cursor: string | null;
  /** A page number sent in the query (`/api/project/me?page=N`), or null. */
  page: number | null;
  /** The feed's filters as sent (`filters.user`, `filters.workspace`, `disliked`, ...), or null. */
  filters: unknown;
  /** The unified feed's ID (`generic_playlist:<id>`), or null. */
  feedId: string | null;
}

/** The message the observer posts to the content script. */
export interface ObservedMessage {
  source: typeof OBSERVER_SOURCE;
  type: 'observed';
  kind: ObservedKind;
  request: ObservedRequest;
  body: unknown;
  /**
   * For a Create (#149): the values the page sent at {@link CREATE_REQUEST_PATHS}, nested as sent, or
   * null when the request body could not be read. Absent for every other kind.
   */
  submitted?: Record<string, unknown> | null;
}

/** The content script's message asking the observer for what it saw before the script started. */
export interface ObserverReady {
  source: typeof CONTENT_SOURCE;
  type: 'observer-ready';
}

/** The `source` of the observer's messages. */
export const OBSERVER_SOURCE = 'n8tracks-observer';
/** The `source` of the content script's messages to the observer. */
export const CONTENT_SOURCE = 'n8tracks-suno-content';

/** How many responses the observer keeps for a content script that starts after them. */
export const OBSERVER_BUFFER = 20;

const SUNO_HOST = /(^|\.)suno\.com$/;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * The list a request is for, or null when the observer leaves it alone: only HTTPS on suno.com or
 * one of its subdomains (Suno's API is `studio-api-prod.suno.com`), by exact path and method.
 */
export function observedKindOf(address: string, method: string, base: string): ObservedKind | null {
  let url: URL;
  try {
    url = new URL(address, base);
  } catch {
    return null;
  }
  if (url.protocol !== 'https:' || !SUNO_HOST.test(url.hostname)) {
    return null;
  }
  const path = url.pathname.replace(/\/+$/, '');
  const verb = method.toUpperCase();
  return OBSERVED_LISTS.find((list) => list.path === path && list.method === verb)?.kind ?? null;
}

/** A copy of `value` with every member named in {@link NEVER_FORWARDED} left out, at any depth. */
export function withoutSecrets(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map(withoutSecrets);
  }
  if (isRecord(value)) {
    return Object.fromEntries(
      Object.entries(value)
        .filter(([key]) => !NEVER_FORWARDED.has(key))
        .map(([key, member]) => [key, withoutSecrets(member)]),
    );
  }
  return value;
}

function textOrNull(value: unknown): string | null {
  return typeof value === 'string' && value !== '' ? value : null;
}

/**
 * What the reader needs of a request: the paging fields from its JSON body and its query, and
 * nothing else. A body that is not JSON text is read as empty.
 */
export function observedRequestOf(address: string, base: string, body: unknown): ObservedRequest {
  const sent = bodyFields(body);
  const query = queryFields(address, base);
  const page = Number(query.page);
  return withoutSecrets({
    cursor: textOrNull(sent.cursor) ?? textOrNull(query.cursor),
    page: query.page !== undefined && Number.isInteger(page) ? page : null,
    filters: sent.filters ?? null,
    feedId: textOrNull(sent.feed_id),
  }) as ObservedRequest;
}

/** The paging fields of a JSON request body; nothing for any other body. */
function bodyFields(body: unknown): Record<string, unknown> {
  if (typeof body !== 'string') {
    return {};
  }
  try {
    const parsed: unknown = JSON.parse(body);
    return isRecord(parsed)
      ? Object.fromEntries(
          REQUEST_FIELDS.filter((field) => field in parsed).map((field) => [field, parsed[field]]),
        )
      : {};
  } catch {
    return {};
  }
}

/** The paging fields of a request's query. */
function queryFields(address: string, base: string): Partial<Record<string, string>> {
  try {
    const params = new URL(address, base).searchParams;
    return Object.fromEntries(
      QUERY_FIELDS.filter((field) => params.has(field)).map((field) => [
        field,
        params.get(field) ?? '',
      ]),
    );
  } catch {
    return {};
  }
}

/**
 * The values a Create request body sent at {@link CREATE_REQUEST_PATHS}, nested as sent (a path the
 * body lacks is left out), and nothing else; null when the body is not JSON text of an object.
 */
export function submittedOf(body: unknown): Record<string, unknown> | null {
  if (typeof body !== 'string') {
    return null;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(body);
  } catch {
    return null;
  }
  if (!isRecord(parsed)) {
    return null;
  }
  const submitted: Record<string, unknown> = {};
  for (const path of CREATE_REQUEST_PATHS) {
    const parts = path.split('.');
    let value: unknown = parsed;
    for (const part of parts) {
      value = isRecord(value) && Object.hasOwn(value, part) ? value[part] : undefined;
    }
    if (value === undefined || isRecord(value) || NEVER_FORWARDED.has(parts.at(-1) ?? '')) {
      continue;
    }
    let into = submitted;
    for (const part of parts.slice(0, -1)) {
      const next = into[part];
      into = isRecord(next) ? next : (into[part] = {});
    }
    into[parts.at(-1) ?? ''] = Array.isArray(value) ? withoutSecrets(value) : value;
  }
  return submitted;
}

/** Whether `value` is a message from the observer, as the content script receives it. */
export function isObservedMessage(value: unknown): value is ObservedMessage {
  return (
    isRecord(value) &&
    value.source === OBSERVER_SOURCE &&
    value.type === 'observed' &&
    OBSERVED_LISTS.some((list) => list.kind === value.kind) &&
    isRecord(value.request)
  );
}

/** Whether `value` is the content script asking the observer for what it saw. */
export function isObserverReady(value: unknown): value is ObserverReady {
  return isRecord(value) && value.source === CONTENT_SOURCE && value.type === 'observer-ready';
}
