// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { loadSnapshot } from '../../testing/snapshots.ts';
import { checkWorkflow } from '../registry.ts';
import { listOnPage, loadMore } from './loadMore.ts';

afterEach(() => {
  document.body.innerHTML = '';
});

describe('the load-more workflow', () => {
  it.each([
    ['library-list', 'https://suno.com/me'],
    ['library-trash', 'https://suno.com/me/trash'],
    ['playlist', 'https://suno.com/playlist/00000000-0000-4000-8000-00000000012b'],
    // The workspace list has no snapshot; the Library page around it has Suno's navigation.
    ['library-list', 'https://suno.com/me/workspaces'],
  ])('finds the list on the %s snapshot at %s', (snapshot, address) => {
    const page = loadSnapshot(snapshot, address);

    expect(listOnPage(page)).toEqual({ ok: true });
    expect(checkWorkflow(loadMore, page).state).toBe('ready');
  });

  it('is not working where the list is missing, and not checked off a list page', () => {
    expect(listOnPage(loadSnapshot('library-trash', 'https://suno.com/playlist/x'))).toEqual({
      ok: false,
      expected: "the playlist's list of songs, named by its count",
    });
    expect(
      checkWorkflow(loadMore, loadSnapshot('library-list', 'https://suno.com/create')).state,
    ).toBe('not-checked');
  });
});
