import { sunoPageOf, sunoPages } from '../addresses.ts';
import { PrimitiveError, type Page, type Target } from '../primitives.ts';
import { expected, OK, present, type Check, type Workflow } from '../workflow.ts';
import { SUNO_NAVIGATION } from './recognise.ts';

/** The rows of Library › Songs and of the Trash (TS-003: one `rowgroup` on each page). */
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
 * The list on the page the reader is reading: the song rows on the Library and Trash pages, the
 * playlist's songs on a playlist page. The workspace list has no snapshot, so on that page only
 * Suno's own navigation is required; the reader's check of each response is what tells a page that
 * does not look as expected.
 */
export function listOnPage(page: Page): Check {
  switch (sunoPageOf(page.address())) {
    case 'library':
    case 'trash':
      return present(page, SONG_ROWS);
    case 'playlist':
      return present(page, PLAYLIST_SONGS);
    case 'workspaces':
      return present(page, SUNO_NAVIGATION);
    default:
      return expected('a Suno list page: the Library, its Trash, its workspaces, or a playlist');
  }
}

/**
 * Asks Suno for the next page of the list on screen, as a user would. Where the list has Suno's
 * pager (TS-007: Library › Songs now pages, `‹ [n] ›`, instead of loading more on scroll), its
 * next-page button is pressed, found by structure ({@link Page.pagerNext}): a pager that does not
 * look as captured stops the step, and nothing else is pressed. Where there is no pager, the list is
 * scrolled to its end (TS-003: the Trash, the workspace list, and a playlist load more on scroll).
 * The library reader runs it once per page and watches Suno's own response to it.
 */
export function askForMore(page: Page): void {
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

/** The workflow the library reader runs to ask for the next page: {@link askForMore}. */
export const loadMore: Workflow = {
  id: 'load-more',
  title: 'Load more of a Suno list',
  feature: 'sync',
  startsOn: sunoPages('library', 'trash', 'workspaces', 'playlist'),
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
  ],
};
