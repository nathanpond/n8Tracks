import { sunoPageOf, sunoPages, WORKSPACE_SONGS_PAGE, type PagePattern } from '../addresses.ts';
import { findProblem, PrimitiveError, type Page, type Target } from '../primitives.ts';
import { expected, OK, present, type Check, type Workflow } from '../workflow.ts';
import { SUNO_NAVIGATION } from './recognise.ts';

/**
 * The rows of Library › Songs and of the Trash (TS-003: one `rowgroup` on each page), and of a
 * workspace's songs on `/create?wid=` (#422: the list beside Suno's Create form,
 * `page.create-workspace-songs.html`).
 */
export const SONG_ROWS: Target = {
  role: 'rowgroup',
  description: "Suno's list of songs",
};

/** A playlist page's songs: a list named by its count, "8 songs" (TS-003: `page.playlist.html`). */
export const PLAYLIST_SONGS: Target = {
  role: 'list',
  name: /^[\d,]+ songs?$/,
  description: "the playlist's list of songs, named by its count",
};

/**
 * The list on the page the reader is reading: the song rows on the Library and Trash pages and on
 * a workspace's songs on the Create page (`/create?wid=`, #422), the playlist's songs on a playlist
 * page. The workspace list has no snapshot, so on that page only Suno's own navigation is
 * required; the reader's check of each response is what tells a page that does not look as
 * expected.
 */
export function listOnPage(page: Page): Check {
  if (WORKSPACE_SONGS_PAGE.matches(page.address())) {
    return present(page, SONG_ROWS);
  }
  switch (sunoPageOf(page.address())) {
    case 'library':
    case 'trash':
      return present(page, SONG_ROWS);
    case 'playlist':
      return present(page, PLAYLIST_SONGS);
    case 'workspaces':
      return present(page, SUNO_NAVIGATION);
    default:
      return expected(
        "a Suno list page: the Library, its Trash, its workspaces, a playlist, or a workspace's songs",
      );
  }
}

/**
 * Asks Suno for the next page of the list on screen, as a user would. On a workspace's songs
 * (`/create?wid=`, #422), the song list is scrolled to its end and nothing else: that feed pages
 * by scroll with a cursor, the header's small ‹ 1 › control is not Library's pager, and Suno's
 * Create form beside the list is left alone. Elsewhere, where the list
 * has Suno's pager (TS-007: Library › Songs now pages, `‹ [n] ›`, instead of loading more on
 * scroll), its next-page button is pressed, found by structure ({@link Page.pagerNext}): a pager
 * that does not look as captured stops the step, and nothing else is pressed. Where there is no
 * pager, the list is scrolled to its end (TS-003: the Trash, the workspace list, and a playlist
 * load more on scroll). The library reader runs it once per page and watches Suno's own response
 * to it.
 */
export function askForMore(page: Page): void {
  if (WORKSPACE_SONGS_PAGE.matches(page.address())) {
    const rows = page.find(SONG_ROWS);
    if (rows.kind !== 'found') {
      throw new PrimitiveError(findProblem(rows));
    }
    page.scrollListToEnd(rows.found);
    return;
  }
  const pager = page.pagerNext();
  switch (pager.kind) {
    case 'found':
      page.click(pager.found);
      return;
    case 'mismatch':
      throw new PrimitiveError(pager.expected);
    case 'none':
      page.scrollToEnd();
  }
}

const LIBRARY_LISTS = sunoPages('library', 'trash', 'workspaces', 'playlist');

/** The pages a list is read on: Library's lists, a playlist, and a workspace's songs (#422). */
const LIST_PAGES: PagePattern = {
  description: `${LIBRARY_LISTS.description}, ${WORKSPACE_SONGS_PAGE.description}`,
  matches: (address) => LIBRARY_LISTS.matches(address) || WORKSPACE_SONGS_PAGE.matches(address),
};

/** The workflow the library reader runs to ask for the next page: {@link askForMore}. */
export const loadMore: Workflow = {
  id: 'load-more',
  title: 'Load more of a Suno list',
  feature: 'sync',
  startsOn: LIST_PAGES,
  needs: [{ step: 'list', check: listOnPage }],
  steps: [
    {
      name: 'list',
      expect: ({ page }) => listOnPage(page),
      act: ({ page }) => {
        askForMore(page);
      },
      verify: () => OK,
    },
  ],
  fixtures: [
    'library-list',
    'library-trash',
    'playlist',
    'library-songs-page-1',
    'library-songs-page-2',
    'create-workspace-songs',
  ],
};
