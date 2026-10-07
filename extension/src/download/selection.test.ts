import { describe, expect, it } from 'vitest';
import { sunoObject } from '../testing/sunoResponses.ts';
import { downloadUsageOf, remainingUnlocks } from './allowance.ts';
import {
  clipOf,
  createdText,
  durationText,
  UNAVAILABLE,
  UNTITLED,
  type DownloadClip,
} from './clips.ts';
import { DownloadSelection, FORMATS, NO_FILTER } from './selection.ts';

function clip(id: string, change: Partial<DownloadClip> = {}): DownloadClip {
  return {
    sunoId: id,
    title: `Song ${id}`,
    displayName: 'Maker',
    durationSeconds: 120,
    createdAt: '2026-10-01T00:00:00.000Z',
    workspace: { id: 'w1', name: 'Studio' },
    unlocked: false,
    hidden: false,
    unavailable: null,
    hasStream: true,
    ...change,
  };
}

function libraryClips(): Record<string, unknown>[] {
  return [
    ...(sunoObject('feed-v3.library-page-1.response').clips as Record<string, unknown>[]),
    ...(sunoObject('feed-v3.library-page-2.response').clips as Record<string, unknown>[]),
  ];
}

describe('a clip as the Download view reads it from the library feed', () => {
  it('keeps the title, creator, duration, date, workspace, unlock state, and the stream', () => {
    const raw = libraryClips()[0] ?? {};
    const read = clipOf(raw);

    expect(read).toEqual({
      sunoId: '00000000-0000-4000-8000-000000000118',
      title: raw.title,
      displayName: raw.display_name,
      durationSeconds: 243.5353125,
      createdAt: '2026-10-01T17:52:08.520Z',
      workspace: {
        id: '00000000-0000-4000-8000-00000000011c',
        name: (raw.project as { name: string }).name,
      },
      unlocked: false,
      hidden: false,
      unavailable: null,
      hasStream: true,
    });
    expect(durationText(read?.durationSeconds ?? null)).toBe('4:04');
    expect(createdText(read?.createdAt ?? null)).toBe('2026-10-01');
  });

  it('cannot select a clip still generating, in the Trash, failed, or without audio; a hidden one can', () => {
    const [first] = libraryClips();
    const trashed = (sunoObject('clips-trashed-v2.response').clips as Record<string, unknown>[])[0];
    const submitted = (
      sunoObject('generate-v2-web.response').clips as Record<string, unknown>[]
    )[0];

    expect(clipOf(trashed)?.unavailable).toBe(UNAVAILABLE.trashed);
    expect(clipOf(submitted)?.unavailable).toBe(UNAVAILABLE.generating);
    expect(clipOf({ ...first, status: 'streaming' })?.unavailable).toBe(UNAVAILABLE.generating);
    expect(clipOf({ ...first, status: 'error' })?.unavailable).toBe(UNAVAILABLE.failed);
    expect(clipOf({ ...first, media_urls: [] })?.unavailable).toBe(UNAVAILABLE.noAudio);
    expect(clipOf({ ...first, is_hidden: true })).toMatchObject({
      hidden: true,
      unavailable: null,
    });
    expect(clipOf({ ...first, is_download_unlocked: true })?.unlocked).toBe(true);
  });

  it('calls an untitled clip Untitled, and reads no clip from a record without an ID', () => {
    const [first] = libraryClips();

    expect(clipOf({ ...first, title: '  ' })?.title).toBe(UNTITLED);
    expect(clipOf({ ...first, id: '' })).toBeNull();
    expect(clipOf('clip')).toBeNull();
  });
});

describe('the selection', () => {
  it('selects clips one by one and lets them go, never one that cannot be selected', () => {
    const selection = new DownloadSelection();
    selection.add([clip('a'), clip('b'), clip('g', { unavailable: UNAVAILABLE.generating })]);

    expect(selection.toggle('a', true)).toBe(true);
    expect(selection.toggle('g', true)).toBe(false);
    expect(selection.toggle('unknown', true)).toBe(false);
    expect(selection.selected().map((item) => item.sunoId)).toEqual(['a']);

    selection.toggle('a', false);
    expect(selection.selected()).toEqual([]);
  });

  it('selects all shown under a filter only, and only once the list was read to its end', () => {
    const selection = new DownloadSelection();
    selection.add([
      clip('a', { title: 'Morning light' }),
      clip('b', { title: 'Night drive', workspace: { id: 'w2', name: 'Demos' } }),
      clip('c', { title: 'Morning rain', unavailable: UNAVAILABLE.trashed }),
      clip('d', { title: 'Evening', workspace: { id: 'w2', name: 'Demos' } }),
    ]);
    const morning = { workspaceId: null, text: 'MORNING' };

    // Read part-way: Select all does nothing.
    expect(selection.selectAllShown(morning)).toBe(0);
    selection.finish(true);

    expect(selection.shown(morning).map((item) => item.sunoId)).toEqual(['a', 'c']);
    expect(selection.selectAllShown(morning)).toBe(1);
    expect(selection.selected().map((item) => item.sunoId)).toEqual(['a']);

    selection.selectAllShown({ workspaceId: 'w2', text: '' });
    expect(selection.selected().map((item) => item.sunoId)).toEqual(['a', 'b', 'd']);
    // The selection survives a filter change, and the summary can say how many it hides.
    expect(selection.hiddenByFilter({ workspaceId: 'w2', text: '' })).toBe(1);
    expect(selection.workspaces()).toEqual([
      { id: 'w2', name: 'Demos' },
      { id: 'w1', name: 'Studio' },
    ]);
  });

  it('clears everything selected', () => {
    const selection = new DownloadSelection();
    selection.add([clip('a'), clip('b')]);
    selection.finish(true);
    selection.selectAllShown(NO_FILTER);

    selection.clear();

    expect(selection.selected()).toEqual([]);
    expect(selection.selectedIds()).toEqual([]);
  });

  it('keeps the selection over a Refresh for the clips still there', () => {
    const selection = new DownloadSelection();
    selection.add([clip('a'), clip('b'), clip('c')]);
    selection.finish(true);
    selection.selectAllShown(NO_FILTER);

    selection.restart(selection.selectedIds());
    selection.add([clip('a'), clip('c', { unavailable: UNAVAILABLE.trashed })]);
    // Part-way through the read, b may still come.
    expect(selection.selectedIds().sort()).toEqual(['a', 'b']);
    selection.finish(true);

    expect(selection.selected().map((item) => item.sunoId)).toEqual(['a']);
    expect(selection.selectedIds()).toEqual(['a']);
  });
});

describe('the file plan', () => {
  it('has a file per selected clip and chosen format, with what the downloader needs', () => {
    const selection = new DownloadSelection();
    selection.add([clip('a', { unlocked: true }), clip('b', { title: 'Untitled' }), clip('c')]);
    selection.toggle('a', true);
    selection.toggle('b', true);
    selection.setFormats(['mp3', 'wav']);

    const plan = selection.plan((id) => (id === 'a' ? 'The Artist' : null));

    expect(plan).toEqual([
      {
        sunoId: 'a',
        title: 'Song a',
        displayName: 'Maker',
        artist: 'The Artist',
        format: 'wav',
        unlocked: true,
      },
      {
        sunoId: 'a',
        title: 'Song a',
        displayName: 'Maker',
        artist: 'The Artist',
        format: 'mp3',
        unlocked: true,
      },
      {
        sunoId: 'b',
        title: 'Untitled',
        displayName: 'Maker',
        artist: null,
        format: 'wav',
        unlocked: false,
      },
      {
        sunoId: 'b',
        title: 'Untitled',
        displayName: 'Maker',
        artist: null,
        format: 'mp3',
        unlocked: false,
      },
    ]);
  });

  it('leaves out a format a clip cannot be had in: the stream needs its address', () => {
    const selection = new DownloadSelection();
    selection.add([clip('a'), clip('b', { hasStream: false })]);
    selection.toggle('a', true);
    selection.toggle('b', true);
    selection.setFormats(['m4a-stream', 'm4a']);

    expect(selection.plan().map((entry) => `${entry.sunoId}:${entry.format}`)).toEqual([
      'a:m4a',
      'a:m4a-stream',
      'b:m4a',
    ]);
  });

  it('counts one unlock per selected clip not yet unlocked for WAV, MP3, or M4A, and none for the stream', () => {
    const selection = new DownloadSelection();
    selection.add([clip('a', { unlocked: true }), clip('b'), clip('c')]);
    for (const id of ['a', 'b', 'c']) {
      selection.toggle(id, true);
    }

    selection.setFormats(['m4a-stream']);
    expect(selection.unlocksNeeded()).toBe(0);
    selection.setFormats(['wav', 'mp3', 'm4a']);
    expect(selection.unlocksNeeded()).toBe(2);
    expect(selection.plan()).toHaveLength(9);
  });

  it('offers the four formats, the stream labelled lower quality and needing no unlock', () => {
    expect(FORMATS.map((choice) => [choice.format, choice.label, choice.unlock])).toEqual([
      ['wav', 'WAV', true],
      ['mp3', 'MP3', true],
      ['m4a', 'M4A', true],
      ['m4a-stream', 'M4A (streaming quality)', false],
    ]);
    expect(FORMATS[3]?.note).toBe('Lower quality; needs no Suno unlock.');
  });
});

describe('the download allowance', () => {
  it('reads the counts from the billing answer, and what remains', () => {
    const usage = downloadUsageOf(sunoObject('billing-info.download-excerpt.response'));

    expect(usage).toEqual({ used: 0, limit: 60, additional: 0 });
    expect(remainingUnlocks({ used: 58, limit: 60, additional: 3 })).toBe(5);
    expect(remainingUnlocks({ used: 61, limit: 60, additional: 0 })).toBe(0);
    expect(downloadUsageOf({ download_usage: { current_period_downloads_used: 1 } })).toBeNull();
    expect(downloadUsageOf(null)).toBeNull();
  });
});
