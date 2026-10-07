import { sunoPageOf, sunoPages } from '../addresses.ts';
import type { Page, Target } from '../primitives.ts';
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
 * Makes Suno ask for the next page of the list on screen, by scrolling to its end (TS-003: every
 * list the reader reads loads more on scroll). The library reader runs it once per page, and
 * watches Suno's own response to it; it reads and presses nothing.
 */
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
        page.scrollToEnd();
      },
      verify: () => OK,
    },
  ],
  fixtures: ['library-list', 'library-trash', 'playlist'],
};
