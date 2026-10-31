import { sunoPageOf, sunoPages, WORKSPACE_SONGS_PAGE, type PagePattern } from '../addresses.ts';
import { PrimitiveError, type Page, type Target } from '../primitives.ts';
import { expected, OK, present, type Check, type Workflow } from '../workflow.ts';
import { SUNO_NAVIGATION } from './recognise.ts';

/** Any song's link: `<a href="/song/<clip ID>">`, one in each row of Suno's song lists. */
export const SONG_LINK: Target = {
  role: 'link',
  address: /^\/song\/[^/]+$/,
  description: "a song's link",
};

/**
 * One row of Suno's song lists: a `role=group` holding its song's link (TS-007-23). The rows of
 * Library › Songs, the Trash, a workspace's songs on `/create?wid=` (#422), and a playlist page are
 * all `div.clip-row[role=group]`, named by the song's title, with `data-clip-status` and a link to
 * `/song/<clip ID>` inside. Since Suno's redesign seen on 2026-10-31 they sit in plain `div`s: the
 * `rowgroup` that held them (TS-003) and the playlist's list named "N songs" are gone or no longer
 * to be relied on. Suno also lays out empty placeholders ahead of the rows it has drawn
 * (`role=group`, named "Untitled", nothing inside), which hold no link and are not rows here.
 */
export const SONG_ROW: Target = {
  role: 'group',
  holds: SONG_LINK,
  description: "a row of Suno's list of songs",
};

/** Suno's list of songs is on the page: at least one row of it (TS-007-23). Reads only. */
export function songRowsOnPage(page: Page): Check {
  return page.readAll(SONG_ROW).length > 0 ? OK : expected("Suno's list of songs");
}

/**
 * The list on the page the reader is reading: the song rows on the Library and Trash pages, on a
 * workspace's songs on the Create page (`/create?wid=`, #422), and on a playlist page (whose rows
 * the reader reads from the page, TS-007-24). On the workspace list and the playlist list (#449)
 * only Suno's own navigation is required; the reader's check of each response is what tells a page
 * that does not look as expected.
 */
export function listOnPage(page: Page): Check {
  if (WORKSPACE_SONGS_PAGE.matches(page.address())) {
    return songRowsOnPage(page);
  }
  switch (sunoPageOf(page.address())) {
    case 'library':
    case 'trash':
    case 'playlist':
      return songRowsOnPage(page);
    case 'workspaces':
    case 'playlists':
      return present(page, SUNO_NAVIGATION);
    default:
      return expected(
        "a Suno list page: the Library, its Trash, its workspaces, a playlist, or a workspace's songs",
      );
  }
}

/**
 * Asks Suno for the next page of the list on screen, as a user would. Where the list has Suno's
 * pager (TS-007: Library › Songs pages, `Page ‹ [#] ›`; TS-007-23: a workspace's songs on
 * `/create?wid=` page by the `‹ [n] ›` in the list's header, and scrolling that list no longer
 * loads more), its next-page button is pressed, found by structure ({@link Page.pagerNext}): a
 * pager that does not look as captured stops the step, and nothing else is pressed. Suno's Create
 * form beside a workspace's songs is left alone. On a workspace's songs the pager is required: a
 * page without one stops the step rather than scrolling for a page that would not come. Where
 * there is no pager, the page is scrolled to its end (TS-003: the Trash, the workspace list, and a
 * playlist load more on scroll). The library reader runs it once per page and watches Suno's own
 * response to it.
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
      if (WORKSPACE_SONGS_PAGE.matches(page.address())) {
        throw new PrimitiveError(
          "Suno's pager (‹, the page number, ›) in the header of the workspace's songs",
        );
      }
      page.scrollToEnd();
  }
}

const LIBRARY_LISTS = sunoPages('library', 'trash', 'workspaces', 'playlists', 'playlist');

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
    'library-songs-groups',
    'library-trash-groups',
    'playlist-groups',
    'library-workspaces',
    'create-workspace-songs-groups',
  ],
};
