import type { Page, Target } from './primitives.ts';
import { SONG_LINK, SONG_ROW } from './workflows/loadMore.ts';

/**
 * A playlist page (`/playlist/<id>`) as a playlist sync reads it (#449, TS-007-24). Since
 * 2026-10-31 the page makes no request for its songs: Suno renders them on its server. The songs'
 * IDs are read from the rows on the page (a `role=group` holding its song's `/song/<id>` link,
 * TS-007-23), never from Suno's embedded page data, and the playlist's size from its header's
 * "N songs". Reading presses nothing.
 */

const COUNT = /^(\d[\d,]*) songs?$/;

/** The playlist's list of songs, named by its size ("24 songs"), as captured; not relied on alone. */
export const PLAYLIST_SONG_LIST: Target = {
  role: 'list',
  name: COUNT,
  description: 'the playlist\'s list of songs, named "N songs"',
};

/** The playlist's size in its header: an element whose whole text is "N songs". */
export const PLAYLIST_SONG_COUNT: Target = {
  role: 'text',
  name: COUNT,
  description: 'the playlist\'s count of songs ("N songs") in its header',
};

/** What the page shows of the playlist: its songs' IDs, in order, each once, and its count. */
export interface PlaylistPageRead {
  clipIds: string[];
  /** How many songs the page says the playlist holds; null when it says none, or two counts that differ. */
  songCount: number | null;
}

function countOf(text: string): number | null {
  const match = COUNT.exec(text.trim());
  return match?.[1] === undefined ? null : Number(match[1].replaceAll(',', ''));
}

/** The playlist's count: from its list's name, else from its header's text, when they agree. */
function songCountOf(page: Page): number | null {
  const counts = new Set<number>();
  for (const list of page.readAll(PLAYLIST_SONG_LIST)) {
    const count = countOf(list.name);
    if (count !== null) {
      counts.add(count);
    }
  }
  for (const text of page.readAll(PLAYLIST_SONG_COUNT)) {
    const count = countOf(text.reading.text);
    if (count !== null) {
      counts.add(count);
    }
  }
  const [only, ...others] = [...counts];
  return only === undefined || others.length > 0 ? null : only;
}

/** Reads the playlist's rows and count from the page as it is now. Reads only. */
export function readPlaylistPage(page: Page): PlaylistPageRead {
  const clipIds: string[] = [];
  for (const links of page.readLinks(SONG_ROW, SONG_LINK)) {
    // A row holds one song's link (TS-007-23); its first is its song.
    // A clip ID is a UUID: its path segment is kept as it is.
    const id = links[0]?.split('/')[2] ?? '';
    if (id !== '' && !clipIds.includes(id)) {
      clipIds.push(id);
    }
  }
  return { clipIds, songCount: songCountOf(page) };
}
