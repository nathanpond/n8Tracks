// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { loadSnapshot } from '../testing/snapshots.ts';
import { readPlaylistPage } from './playlistPage.ts';

const PLAYLIST = 'https://suno.com/playlist/00000000-0000-4000-8000-00000000050a';

afterEach(() => {
  document.body.innerHTML = '';
});

describe('a playlist page as a playlist sync reads it (#449, TS-007-24)', () => {
  it("reads the song IDs of its rows, in order, and the count in its header, from Suno's redesigned page", () => {
    const page = loadSnapshot('playlist-groups', PLAYLIST);
    const before = document.body.innerHTML;

    expect(readPlaylistPage(page)).toEqual({
      clipIds: [
        '00000000-0000-4000-8000-00000000050c',
        '00000000-0000-4000-8000-00000000050e',
        '00000000-0000-4000-8000-00000000050f',
      ],
      songCount: 24,
    });
    // Reading changes nothing on the page.
    expect(document.body.innerHTML).toBe(before);
  });

  it('takes the count from the header alone when the list is no longer named, and none when two counts differ', () => {
    const page = loadSnapshot('playlist-groups', PLAYLIST);
    document.querySelector('ul[aria-label]')?.removeAttribute('aria-label');
    expect(readPlaylistPage(page).songCount).toBe(24);

    document.querySelector('ul')?.setAttribute('aria-label', '25 songs');
    expect(readPlaylistPage(page).songCount).toBeNull();
  });

  it('reads one song from a row of one, "1 song", and nothing from a page with no rows', () => {
    const page = loadSnapshot('playlist-groups', PLAYLIST);
    const rows = [...document.querySelectorAll('[role="group"]')];
    for (const row of rows.slice(1)) {
      row.remove();
    }
    for (const element of document.querySelectorAll('ul[aria-label], span')) {
      if (/^\d+ songs?$/.test(element.getAttribute('aria-label') ?? element.textContent)) {
        if (element.hasAttribute('aria-label')) {
          element.setAttribute('aria-label', '1 song');
        } else {
          element.textContent = '1 song';
        }
      }
    }
    expect(readPlaylistPage(page)).toEqual({
      clipIds: ['00000000-0000-4000-8000-00000000050c'],
      songCount: 1,
    });

    const empty = loadSnapshot('library-workspaces', PLAYLIST);
    expect(readPlaylistPage(empty)).toEqual({ clipIds: [], songCount: null });
  });
});
