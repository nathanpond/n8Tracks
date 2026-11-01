// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { loadSnapshot } from '../testing/snapshots.ts';
import { sunoObject } from '../testing/sunoResponses.ts';
import {
  clipOf,
  feedPageOf,
  flightRowsOf,
  flightTextOf,
  observedPlaylistPage,
  PLAYLIST_SOURCE,
  PLAYLIST_SOURCE_MEMBER,
  readEmbeddedPlaylist,
  snakeName,
  type PlaylistFeedPage,
} from './playlistData.ts';

/** The synthetic playlist of the fixtures (#452): 77 songs, 50 of them embedded in its page. */
const PLAYLIST = '00000000-0000-4000-8000-000000000600';
const ADDRESS = `https://suno.com/playlist/${PLAYLIST}`;

function clipId(index: number): string {
  return `00000000-0000-4000-8000-${(0x601 + index).toString(16).padStart(12, '0')}`;
}

afterEach(() => {
  document.body.innerHTML = '';
});

function embedded(): PlaylistFeedPage {
  const page = loadSnapshot('playlist-embedded', ADDRESS);
  const read = readEmbeddedPlaylist(page.readInlineScripts(), PLAYLIST);
  if ('problem' in read) {
    throw new Error(read.problem);
  }
  return read;
}

/** A page's scripts holding `text` as its flight data, pushed in one piece. */
function scriptsWith(text: string): string[] {
  return [
    '(self.__next_f=self.__next_f||[]).push([0])',
    `self.__next_f.push(${JSON.stringify([1, text])})`,
  ];
}

describe("a playlist's first page, from the data Suno embeds in its page (#452, TS-007-25)", () => {
  it('reads the feed query of the playlist: 50 songs, the next cursor, and the size', () => {
    const read = embedded();

    expect(read.clips).toHaveLength(50);
    expect(read.clips.map((clip) => clip.id)).toEqual(
      Array.from({ length: 50 }, (_, index) => clipId(index)),
    );
    expect(read.nextCursor).toBe('50');
    expect(read.itemCount).toBe(77);
  });

  it('reads the inline scripts only, and changes nothing on the page', () => {
    const page = loadSnapshot('playlist-embedded', ADDRESS);
    const before = document.body.innerHTML;

    const scripts = page.readInlineScripts();

    // The script with a `src` is left out; the bootstrap, form-state, and six data pushes are read.
    expect(scripts).toHaveLength(8);
    expect(scripts.every((text) => text.includes('self.__next_f'))).toBe(true);
    expect(document.body.innerHTML).toBe(before);
  });

  it("puts each camelCase clip in the export's snake_case shape, values as given, and marks its source", () => {
    const [first, , dollar, edited] = embedded().clips;

    expect(first).toMatchObject({
      id: clipId(0),
      status: 'complete',
      title: 'Synthetic Song 01',
      entity_type: 'song_schema',
      image_url: `https://cdn2.suno.ai/image_${clipId(0)}.jpeg`,
      image_large_url: `https://cdn2.suno.ai/image_large_${clipId(0)}.jpeg`,
      major_model_version: 'v6',
      model_name: 'chirp-hawk',
      media_urls: [
        {
          url: `https://d2lwuy8qc234o3.cloudfront.net/1/clip/${clipId(0)}.m4a`,
          content_type: 'm4a-opus',
        },
      ],
      metadata: {
        tags: 'synthetic pop, bright synths, test',
        duration: 120.5,
        has_stem: false,
        vox_render_mode: 'strict',
        can_publish_with_vocal: true,
      },
      is_trashed: false,
      is_hidden: false,
      created_at: '2026-10-01T12:00:00.000Z',
      batch_index: 0,
      [PLAYLIST_SOURCE_MEMBER]: PLAYLIST_SOURCE,
    });
    // Flight's "$$…" is a string starting "$"; "$undefined" leaves the member out.
    expect(dollar?.title).toBe('$ Dollar Sign Song');
    expect(first).not.toHaveProperty('download_unlocked_at');
    // Lineage members keep their meaning under their feed names.
    expect(edited?.metadata).toMatchObject({ edited_clip_id: clipId(2), task: 'edit_crop' });
    // What a playlist's data does not carry stays absent: no workspace, no BPM or key, no badge.
    for (const clip of embedded().clips) {
      expect(clip).not.toHaveProperty('project');
      expect(clip.metadata).not.toHaveProperty('key');
      expect(clip.metadata).not.toHaveProperty('avg_bpm');
      expect(clip.metadata).not.toHaveProperty('model_badges');
      expect(Object.keys(clip).filter((key) => /[A-Z]/.test(key))).toEqual([]);
    }
  });

  it('reads lyrics Flight sent as a text row, raw newlines and all, by its length in UTF-8 bytes', () => {
    const [first, second] = embedded().clips;
    const lyrics = (first?.metadata as Record<string, unknown>).prompt as string;

    expect(lyrics.startsWith('[Intro]\nSynthetic lyrics 1\n\n[Verse]\n')).toBe(true);
    expect(lyrics).toContain('café ✓ 🎵\n');
    expect(lyrics.endsWith('[Outro]\nThe end\n')).toBe(true);
    expect(lyrics.length).toBeGreaterThan(1024);
    // A short one is inline in the JSON row.
    expect((second?.metadata as Record<string, unknown>).prompt).toBe(
      'Short made-up lyrics 2\nSecond line',
    );
  });

  it('maps the camelCase page data and the snake_case observed answer onto the same members', () => {
    const fromPage = embedded().clips[0] ?? {};
    const observed = observedPlaylistPage(
      sunoObject('unified-feed.playlist-page-2.response'),
      PLAYLIST,
    );
    if ('problem' in observed) {
      throw new Error(observed.problem);
    }
    const [fromFeed] = observed.clips;
    const keys = (clip: Record<string, unknown>) =>
      [
        ...Object.keys(clip),
        ...Object.keys(clip.metadata as object).map((key) => `metadata.${key}`),
      ].toSorted();

    // Page 1 leaves out `downloadUnlockedAt` ("$undefined"); the observed answer has no such member.
    expect(keys(fromPage)).toEqual(keys(fromFeed ?? {}));
  });
});

describe('when the embedded data is missing or not as expected (#452)', () => {
  it('says the page embeds no server data when it has no Flight pushes', () => {
    const page = loadSnapshot('playlist-groups', ADDRESS);

    expect(readEmbeddedPlaylist(page.readInlineScripts(), PLAYLIST)).toEqual({
      problem: 'it embeds no server data (no self.__next_f.push)',
    });
  });

  it('says there is no feed query when the dehydrated state holds none for this playlist', () => {
    const page = loadSnapshot('playlist-embedded-no-feed', ADDRESS);
    expect(readEmbeddedPlaylist(page.readInlineScripts(), PLAYLIST)).toEqual({
      problem: 'its embedded data holds no feed query for this playlist',
    });

    // The query of another playlist is not this one's.
    const other = loadSnapshot('playlist-embedded', ADDRESS);
    expect(
      readEmbeddedPlaylist(other.readInlineScripts(), '00000000-0000-4000-8000-0000000006fe'),
    ).toMatchObject({ problem: expect.stringContaining('no feed query') as unknown });
  });

  it('says what is wrong with a feed it found, and makes up nothing', () => {
    const query = (feed: unknown) =>
      `5:${JSON.stringify({
        dehydratedState: {
          queries: [
            {
              state: { data: { pages: [{ feed }] } },
              queryKey: [{ feedId: `generic_playlist:${PLAYLIST}` }],
            },
          ],
        },
      })}\n`;

    expect(readEmbeddedPlaylist(scriptsWith(query({ items: 'none' })), PLAYLIST)).toEqual({
      problem: "the playlist's feed has no list of items",
    });
    expect(
      readEmbeddedPlaylist(
        scriptsWith(query({ items: [{ contentType: 'playlist', contentItem: { id: 'x' } }] })),
        PLAYLIST,
      ),
    ).toEqual({ problem: 'an item of the playlist is not a song with an ID' });
    expect(
      readEmbeddedPlaylist(
        scriptsWith(query({ items: [{ contentType: 'clip', contentItem: { id: '$9' } }] })),
        PLAYLIST,
      ),
    ).toEqual({ problem: 'an item of the playlist is not a song with an ID' });
    expect(
      readEmbeddedPlaylist(scriptsWith(query({ items: [], itemCount: 'many' })), PLAYLIST),
    ).toEqual({ problem: "the playlist's size is not a whole number" });
    expect(
      readEmbeddedPlaylist(
        scriptsWith(
          `5:${JSON.stringify({ queryKey: [{ feedId: `generic_playlist:${PLAYLIST}` }], state: {} })}\n`,
        ),
        PLAYLIST,
      ),
    ).toEqual({ problem: "its feed query holds no page of the playlist's songs" });
  });

  it('finds the dehydrated state by its key when the rows are not framed as expected', () => {
    const feed = { items: [{ contentType: 'clip', contentItem: { id: 's1', title: 'One' } }] };
    const state = {
      queries: [
        {
          state: { data: { pages: [{ feed }] } },
          queryKey: [{ feedId: `generic_playlist:${PLAYLIST}` }],
        },
      ],
    };
    const text = `not a row\n5:{"dehydratedState":${JSON.stringify(state)}}\n`;

    expect(readEmbeddedPlaylist(scriptsWith(text), PLAYLIST)).toMatchObject({
      clips: [{ id: 's1', title: 'One' }],
      nextCursor: null,
      itemCount: null,
    });
  });
});

describe("Flight's framing (#452)", () => {
  it('joins the pushes of kind 1 in order and leaves the others alone', () => {
    expect(
      flightTextOf([
        'self.__next_f.push([0])',
        'self.__next_f.push([1,"a:\\"x"])',
        'self.__next_f.push([2,null]);self.__next_f.push([1,"y\\"\\n"])',
        'self.__next_f.push([3,"AAAA"])',
      ]),
    ).toBe('a:"xy"\n');
    expect(flightTextOf(['console.log(1)'])).toBeNull();
  });

  it('takes a text row by its UTF-8 length, so its newlines do not end it', () => {
    const lyrics = 'line one\nline twö ✓\n🎵 three';
    const length = new TextEncoder().encode(lyrics).length.toString(16);
    const rows = flightRowsOf(`1:"$Sreact.fragment"\n2:T${length},${lyrics}3:["$2"]\n`);

    expect(rows.get('2')).toEqual({ kind: 'text', text: lyrics });
    expect(rows.get('3')).toEqual({ kind: 'model', text: '["$2"]' });
    expect(rows.get('1')).toEqual({ kind: 'model', text: '"$Sreact.fragment"' });
  });

  it('resolves a reference to a model row, and leaves out one it cannot resolve', () => {
    const text = [
      `7:{"id":"s1","title":"$8","metadata":{"prompt":"$9","tags":"$Lz"}}`,
      `8:"Referred title"`,
      `5:${JSON.stringify({
        queries: [
          {
            state: {
              data: { pages: [{ feed: { items: [{ contentType: 'clip', contentItem: '$7' }] } }] },
            },
            queryKey: [{ feedId: `generic_playlist:${PLAYLIST}` }],
          },
        ],
      })}`,
      '',
    ].join('\n');

    expect(readEmbeddedPlaylist(scriptsWith(text), PLAYLIST)).toMatchObject({
      clips: [{ id: 's1', title: 'Referred title', metadata: {} }],
    });
  });

  it('puts member names in snake_case, keeping a name with no capital', () => {
    expect(snakeName('imageLargeUrl')).toBe('image_large_url');
    expect(snakeName('image_large_url')).toBe('image_large_url');
    expect(snakeName('id')).toBe('id');
    expect(clipOf({ contentType: 'clip', contentItem: { id: '' } })).toBeNull();
  });
});

describe("a playlist's later pages, from the answers the page sends as it is scrolled (#452)", () => {
  it('reads the snake_case observed answer: the rest of the songs, the size, and no next cursor', () => {
    const read = observedPlaylistPage(
      sunoObject('unified-feed.playlist-page-2.response'),
      PLAYLIST,
    );

    expect(read).toMatchObject({ nextCursor: null, itemCount: 77 });
    const clips = 'problem' in read ? [] : read.clips;
    expect(clips.map((clip) => clip.id)).toEqual(
      Array.from({ length: 27 }, (_, index) => clipId(50 + index)),
    );
    expect(clips[0]).toMatchObject({ [PLAYLIST_SOURCE_MEMBER]: PLAYLIST_SOURCE });
  });

  it("reads TS-003's playlist answer as it was, and refuses another list's feed", () => {
    const body = sunoObject('unified-feed.playlist.response');
    const items = (body.feed as { items: { content_item: Record<string, unknown> }[] }).items;

    const read = observedPlaylistPage(body, '00000000-0000-4000-8000-00000000012b');
    expect(read).toMatchObject({ nextCursor: null });
    expect('problem' in read ? [] : read.clips).toEqual(
      items.map((item) => ({ ...item.content_item, [PLAYLIST_SOURCE_MEMBER]: PLAYLIST_SOURCE })),
    );
    expect(observedPlaylistPage(body, PLAYLIST)).toEqual({
      problem: 'the feed is for another list',
    });
    expect(feedPageOf(undefined, PLAYLIST)).toEqual({
      problem: "the playlist's feed has no list of items",
    });
  });
});
