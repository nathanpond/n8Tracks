/**
 * A playlist's songs as Suno gives them to its own page (#452, TS-007-25). Since 2026-10-31 a
 * playlist page asks Suno for none of its first songs: Suno's server renders the page and embeds the
 * answer it prefetched. That answer is read here from the text of the page's inline scripts (handed
 * over by `Page.readInlineScripts`, read only), and the later pages from the `POST /api/unified/feed`
 * answers the page itself sends as it is scrolled (`playlist-feed` in `observed.ts`). No request is
 * made, and no cookie, token, or header is read: this is Suno's own answer to the page's own load,
 * within the observe-only boundary of `docs/suno-integration.md`.
 *
 * **Where the first page is.** Next.js streams the page's React Server Components payload into
 * inline scripts, each `self.__next_f.push([1, "<text>"])`; the texts, joined in order, are
 * "flight" rows, `<hex id>:<row>`. A JSON row (one line) holds a TanStack Query dehydrated state,
 * `{ queries: [...] }`, among them the feed query, keyed
 * `[{ scope: "unifiedFeed", entity: "feed", feedId: "generic_playlist:<id>", pageSize: 50, ... }]`,
 * whose `state.data.pages[].feed` is `{ items: [{ contentType: "clip", contentItem, ... }],
 * nextCursor, itemCount, ... }`. A text row (`<hex id>:T<hex byte length>,<text>`) holds a long
 * string as it is, raw newlines and all, and the JSON refers to it as `"$<hex id>"`: lyrics can sit
 * there. Rows are therefore not split on newlines alone.
 *
 * **What a clip becomes.** The embedded `contentItem` is camelCase (`imageLargeUrl`); the observed
 * answer's `content_item` is snake_case, the shape `POST /api/feed/v3` gives and the export already
 * carries. Both are read through one mapping, {@link clipOf}: every member name is put in
 * snake_case (a name with no capital is kept as it is), and the values are kept as Suno gave them,
 * with Flight's references resolved (`"$$…"` is a string starting `$`, `"$undefined"` and a
 * reference that cannot be resolved leave the member out, `"$D…"` is a date). Nothing is added but
 * {@link PLAYLIST_SOURCE_MEMBER}, which tells n8Tracks this clip came from a playlist, whose data
 * leaves out what a library or workspace feed carries (the workspace, Suno's analysis of BPM and
 * key, and any option it omits): there, a member absent is not known, never a default, and never a
 * change (invariant 3).
 *
 * When the data cannot be found or read, the read gives a problem, and the leg stops saying the
 * playlist page changed. Nothing is made up.
 *
 * These are adapter patterns: a change to one raises `ADAPTER_VERSION`.
 */

/** A clip as the export sends it: Suno's members, in snake_case, with its ID. */
export type PlaylistClip = Record<string, unknown> & { id: string };

/**
 * The member the extension adds to each clip a playlist leg sends (#452), and its value: n8Tracks
 * then reads a member the clip lacks as not returned, never as Suno's default.
 */
export const PLAYLIST_SOURCE_MEMBER = 'n8tracks_source';
export const PLAYLIST_SOURCE = 'playlist';

/** One page of a playlist's songs: its clips, the cursor of the next page, and the playlist's size. */
export interface PlaylistFeedPage {
  clips: PlaylistClip[];
  /** The next page's cursor (`nextCursor`, "50"), or null when Suno gave none. */
  nextCursor: string | null;
  /** How many songs the playlist holds (`itemCount`), or null when Suno gave none. */
  itemCount: number | null;
}

/** A page read, or what was wrong, in words that follow "the playlist page changed: ". */
export type PlaylistDataRead = PlaylistFeedPage | { problem: string };

/** The feed ID Suno gives a playlist's songs. */
export function playlistFeedId(playlistId: string): string {
  return `generic_playlist:${playlistId}`;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

const PUSH = 'self.__next_f.push(';

/**
 * The end (exclusive) of the JSON array or object that starts at `start`, by its brackets, skipping
 * strings (and their escapes); -1 when it does not close.
 */
function balancedEnd(text: string, start: number): number {
  let depth = 0;
  let inString = false;
  for (let index = start; index < text.length; index += 1) {
    const char = text[index];
    if (inString) {
      if (char === '\\') {
        index += 1;
      } else if (char === '"') {
        inString = false;
      }
      continue;
    }
    if (char === '"') {
      inString = true;
    } else if (char === '[' || char === '{') {
      depth += 1;
    } else if (char === ']' || char === '}') {
      depth -= 1;
      if (depth === 0) {
        return index + 1;
      }
    }
  }
  return -1;
}

function parsed(text: string): unknown {
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return undefined;
  }
}

/**
 * The flight text of the page: the string of every `self.__next_f.push([1, "<text>"])` in the
 * scripts, joined in order. Pushes of another kind (the bootstrap `[0]`, form state `[2]`, binary
 * `[3]`) are left alone. Null when there is none.
 */
export function flightTextOf(scripts: readonly string[]): string | null {
  const chunks: string[] = [];
  for (const script of scripts) {
    for (let at = script.indexOf(PUSH); at >= 0; at = script.indexOf(PUSH, at + PUSH.length)) {
      const start = at + PUSH.length;
      if (script[start] !== '[') {
        continue;
      }
      const end = balancedEnd(script, start);
      const pushed = end < 0 ? undefined : parsed(script.slice(start, end));
      if (Array.isArray(pushed) && pushed[0] === 1 && typeof pushed[1] === 'string') {
        chunks.push(pushed[1]);
      }
    }
  }
  return chunks.length === 0 ? null : chunks.join('');
}

/** A flight row: a long string as it is (`T`), or a model row's text, one line. */
type FlightRow = { kind: 'text'; text: string } | { kind: 'model'; text: string };

const ROW_ID = /^[0-9a-f]+$/;

/** The UTF-8 length of one code point. */
function utf8Length(codePoint: number): number {
  return codePoint < 0x80 ? 1 : codePoint < 0x800 ? 2 : codePoint < 0x10000 ? 3 : 4;
}

/**
 * The rows of a flight text, by ID. A text row is taken by its length in UTF-8 bytes, so a newline
 * inside it does not end it; any other row ends at its newline. Reading stops at the first row that
 * is not `<hex id>:`, keeping the rows read before it.
 */
export function flightRowsOf(text: string): Map<string, FlightRow> {
  const rows = new Map<string, FlightRow>();
  let at = 0;
  while (at < text.length) {
    if (text[at] === '\n') {
      at += 1;
      continue;
    }
    const colon = text.indexOf(':', at);
    const id = colon < 0 ? '' : text.slice(at, colon);
    if (!ROW_ID.test(id)) {
      break;
    }
    at = colon + 1;
    const length = /^T([0-9a-f]+),/.exec(text.slice(at, at + 24));
    if (length?.[1] !== undefined) {
      at += length[0].length;
      let bytes = Number.parseInt(length[1], 16);
      const start = at;
      while (bytes > 0 && at < text.length) {
        const codePoint = text.codePointAt(at) ?? 0;
        bytes -= utf8Length(codePoint);
        at += codePoint > 0xffff ? 2 : 1;
      }
      rows.set(id, { kind: 'text', text: text.slice(start, at) });
      continue;
    }
    const newline = text.indexOf('\n', at);
    const end = newline < 0 ? text.length : newline;
    rows.set(id, { kind: 'model', text: text.slice(at, end) });
    at = end;
  }
  return rows;
}

/** A model row's JSON: as it is, or after its one-letter tags (`I[...]`, `HL[...]`). */
function modelOf(text: string): unknown {
  const value = parsed(text);
  if (value !== undefined) {
    return value;
  }
  const tag = /^[A-Z]{1,2}(?=[[{"])/.exec(text);
  return tag === null ? undefined : parsed(text.slice(tag[0].length));
}

/** A member Flight says is undefined, or a reference that cannot be resolved: left out. */
const ABSENT = Symbol('absent');

const REFERENCE = /^\$([0-9a-f]+)$/;
const DATE = /^\$D(.+)$/;

/** How deep references are followed into one another. */
const REFERENCE_DEPTH = 8;

/** `value` with Flight's string references resolved against `rows` ({@link ABSENT} for none). */
function resolved(value: unknown, rows: Map<string, FlightRow>, depth = 0): unknown {
  if (typeof value === 'string') {
    if (!value.startsWith('$')) {
      return value;
    }
    if (value.startsWith('$$')) {
      return value.slice(1);
    }
    const date = DATE.exec(value);
    if (date?.[1] !== undefined) {
      return date[1];
    }
    const reference = REFERENCE.exec(value);
    const row = reference?.[1] === undefined ? undefined : rows.get(reference[1]);
    if (row === undefined || depth >= REFERENCE_DEPTH) {
      // `$undefined`, a lazy or server reference, or a row the page did not hold.
      return ABSENT;
    }
    if (row.kind === 'text') {
      return row.text;
    }
    const model = modelOf(row.text);
    return model === undefined ? ABSENT : resolved(model, rows, depth + 1);
  }
  if (Array.isArray(value)) {
    return value.map((item) => resolved(item, rows, depth)).filter((item) => item !== ABSENT);
  }
  if (isRecord(value)) {
    const out: Record<string, unknown> = {};
    for (const [key, member] of Object.entries(value)) {
      const read = resolved(member, rows, depth);
      if (read !== ABSENT) {
        out[key] = read;
      }
    }
    return out;
  }
  return value;
}

/** Whether `value` is the dehydrated feed query of `feedId`. */
function isFeedQuery(value: Record<string, unknown>, feedId: string): boolean {
  const key = Array.isArray(value.queryKey) ? (value.queryKey[0] as unknown) : undefined;
  return isRecord(key) && key.feedId === feedId && isRecord(value.state);
}

/** The first object in `value`, at any depth, that is the feed query of `feedId`. */
function feedQueryIn(value: unknown, feedId: string): Record<string, unknown> | null {
  const stack: unknown[] = [value];
  while (stack.length > 0) {
    const next = stack.pop();
    if (Array.isArray(next)) {
      stack.push(...(next as unknown[]).toReversed());
    } else if (isRecord(next)) {
      if (isFeedQuery(next, feedId)) {
        return next;
      }
      stack.push(...Object.values(next).reverse());
    }
  }
  return null;
}

/**
 * The feed query of `feedId` in the flight text: in a model row that names the feed, else, should
 * the rows not be framed as expected, in a `"dehydratedState":` object found by its key and read by
 * its brackets.
 */
function feedQueryOf(text: string, rows: Map<string, FlightRow>, feedId: string) {
  const named = JSON.stringify(feedId);
  for (const row of rows.values()) {
    if (row.kind === 'model' && row.text.includes(named)) {
      const query = feedQueryIn(modelOf(row.text), feedId);
      if (query !== null) {
        return query;
      }
    }
  }
  const key = '"dehydratedState":';
  for (let at = text.indexOf(key); at >= 0; at = text.indexOf(key, at + key.length)) {
    const start = at + key.length;
    const end = text[start] === '{' ? balancedEnd(text, start) : -1;
    const query = end < 0 ? null : feedQueryIn(parsed(text.slice(start, end)), feedId);
    if (query !== null) {
      return query;
    }
  }
  return null;
}

/** A member name in snake_case: a capital becomes `_` and its small letter (`imageUrl` → `image_url`). */
export function snakeName(name: string): string {
  return name.replace(/[A-Z]/g, (capital) => `_${capital.toLowerCase()}`);
}

function snakeCased(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map(snakeCased);
  }
  if (isRecord(value)) {
    return Object.fromEntries(
      Object.entries(value).map(([key, member]) => [snakeName(key), snakeCased(member)]),
    );
  }
  return value;
}

/**
 * A playlist item's clip in the export's shape: Suno's members in snake_case, as given, and the
 * {@link PLAYLIST_SOURCE_MEMBER}; null when the item is no clip with an ID.
 */
export function clipOf(item: unknown): PlaylistClip | null {
  if (!isRecord(item)) {
    return null;
  }
  const content = item.contentItem ?? item.content_item;
  const type = item.contentType ?? item.content_type;
  if (type !== 'clip' || !isRecord(content)) {
    return null;
  }
  const clip = snakeCased(content) as Record<string, unknown>;
  if (typeof clip.id !== 'string' || clip.id === '') {
    return null;
  }
  return { ...clip, id: clip.id, [PLAYLIST_SOURCE_MEMBER]: PLAYLIST_SOURCE };
}

function member(record: Record<string, unknown>, camel: string): unknown {
  return record[camel] ?? record[snakeName(camel)];
}

/**
 * One page of a playlist's songs from its feed object (`{ items, nextCursor, itemCount, feedId }`, in
 * either casing). A feed of another playlist, an item that is no clip with an ID, or a size that is
 * not a whole number is a problem.
 */
export function feedPageOf(feed: unknown, playlistId: string): PlaylistDataRead {
  if (!isRecord(feed) || !Array.isArray(feed.items)) {
    return { problem: "the playlist's feed has no list of items" };
  }
  const named = member(feed, 'feedId');
  if (named !== undefined && named !== playlistFeedId(playlistId)) {
    return { problem: 'the feed is for another list' };
  }
  const clips: PlaylistClip[] = [];
  for (const item of feed.items) {
    const clip = clipOf(item);
    if (clip === null) {
      return { problem: 'an item of the playlist is not a song with an ID' };
    }
    clips.push(clip);
  }
  const cursor = member(feed, 'nextCursor');
  const count = member(feed, 'itemCount');
  if (count !== undefined && count !== null && !(Number.isInteger(count) && Number(count) >= 0)) {
    return { problem: "the playlist's size is not a whole number" };
  }
  return {
    clips,
    nextCursor: typeof cursor === 'string' && cursor !== '' ? cursor : null,
    itemCount: typeof count === 'number' ? count : null,
  };
}

/**
 * The first page of a playlist's songs from the data Suno embedded in the page (the scripts' text,
 * in document order). Every page the query holds is taken, in order, with the last page's cursor.
 */
export function readEmbeddedPlaylist(
  scripts: readonly string[],
  playlistId: string,
): PlaylistDataRead {
  const text = flightTextOf(scripts);
  if (text === null) {
    return { problem: 'it embeds no server data (no self.__next_f.push)' };
  }
  const feedId = playlistFeedId(playlistId);
  const rows = flightRowsOf(text);
  const query = feedQueryOf(text, rows, feedId);
  if (query === null) {
    return { problem: 'its embedded data holds no feed query for this playlist' };
  }
  const data = resolved(query.state, rows);
  const pages = isRecord(data) && isRecord(data.data) ? data.data.pages : undefined;
  if (!Array.isArray(pages) || pages.length === 0) {
    return { problem: "its feed query holds no page of the playlist's songs" };
  }
  const read: PlaylistFeedPage = { clips: [], nextCursor: null, itemCount: null };
  for (const page of pages) {
    const one = feedPageOf(isRecord(page) ? page.feed : undefined, playlistId);
    if ('problem' in one) {
      return one;
    }
    read.clips.push(...one.clips);
    read.nextCursor = one.nextCursor;
    read.itemCount = one.itemCount ?? read.itemCount;
  }
  return read;
}

/** A later page of a playlist's songs from an observed `POST /api/unified/feed` answer (`{ feed }`). */
export function observedPlaylistPage(body: unknown, playlistId: string): PlaylistDataRead {
  return feedPageOf(isRecord(body) ? body.feed : undefined, playlistId);
}
