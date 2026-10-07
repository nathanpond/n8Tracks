import { describe, expect, it } from 'vitest';
import shared from '../../fixtures/filenames.json' with { type: 'json' };
import {
  cleanPart,
  downloadPathFor,
  fileNameFor,
  MAX_NAME_BYTES,
  MAX_NAME_UNITS,
  type NamedClip,
} from './fileName.ts';
import { isDownloadFormat } from './selection.ts';

const ID = '0c90d621-e30c-4c76-814a-e1fdeb500582';
const TOKEN = `(suno-${ID})`;

function clip(change: Partial<NamedClip> = {}): NamedClip {
  return {
    sunoId: ID,
    title: 'Song Title',
    displayName: 'someone',
    artist: 'Song Artist',
    format: 'wav',
    ...change,
  };
}

const bytes = (text: string) => new TextEncoder().encode(text).length;

describe('the name a downloaded file is saved under', () => {
  it("is the PRD's example: Artist, title, and the Suno ID token", () => {
    expect(fileNameFor(clip())).toBe(`Song Artist - Song Title ${TOKEN}.wav`);
    expect(downloadPathFor(clip())).toBe(`n8Tracks/Song Artist - Song Title ${TOKEN}.wav`);
  });

  it('takes the Artist from n8Tracks first, then the Suno display name, then none', () => {
    expect(fileNameFor(clip({ artist: null }))).toBe(`someone - Song Title ${TOKEN}.wav`);
    expect(fileNameFor(clip({ artist: '  ' }))).toBe(`someone - Song Title ${TOKEN}.wav`);
    expect(fileNameFor(clip({ artist: null, displayName: '' }))).toBe(`Song Title ${TOKEN}.wav`);
    expect(fileNameFor(clip({ artist: null, displayName: ' . ' }))).toBe(`Song Title ${TOKEN}.wav`);
  });

  it('gives each format its extension, and the stream its own word', () => {
    expect(fileNameFor(clip({ format: 'mp3' }))).toMatch(/\)\.mp3$/);
    expect(fileNameFor(clip({ format: 'm4a' }))).toMatch(/\)\.m4a$/);
    expect(fileNameFor(clip({ format: 'm4a-stream' }))).toMatch(/\) stream\.m4a$/);
  });

  it('replaces the characters Windows, macOS, or Linux refuse with _', () => {
    expect(fileNameFor(clip({ artist: 'AC/DC', title: 'a<b>c:d"e\\f|g?h*i' }))).toBe(
      `AC_DC - a_b_c_d_e_f_g_h_i ${TOKEN}.wav`,
    );
  });

  it('removes control characters and bidirectional controls, and reads line breaks as spaces', () => {
    expect(fileNameFor(clip({ title: 'One\u0000Two\nThree‮evil\u0007' }))).toBe(
      `Song Artist - OneTwo Threeevil ${TOKEN}.wav`,
    );
  });

  it('removes leading and trailing dots and spaces, and calls an empty title Untitled', () => {
    expect(fileNameFor(clip({ artist: ' .hidden ', title: 'Ends with a dot. ' }))).toBe(
      `hidden - Ends with a dot ${TOKEN}.wav`,
    );
    expect(fileNameFor(clip({ title: ' ... ' }))).toBe(`Song Artist - Untitled ${TOKEN}.wav`);
    expect(fileNameFor(clip({ title: '' }))).toBe(`Song Artist - Untitled ${TOKEN}.wav`);
  });

  it('puts _ before a Windows device name', () => {
    expect(fileNameFor(clip({ artist: '', displayName: '', title: 'con' }))).toBe(
      `_con ${TOKEN}.wav`,
    );
    expect(fileNameFor(clip({ artist: 'LPT1', title: 'x' }))).toBe(`LPT1 - x ${TOKEN}.wav`);
    expect(fileNameFor(clip({ artist: 'nul.txt', title: 'x' }))).toBe(`_nul.txt - x ${TOKEN}.wav`);
  });

  it('cuts a 300-character title to fit 180 units, keeping the token and extension whole', () => {
    const name = fileNameFor(clip({ title: 'y'.repeat(300) }));
    expect(name.length).toBe(MAX_NAME_UNITS);
    expect(name.startsWith('Song Artist - yyy')).toBe(true);
    expect(name.endsWith(` ${TOKEN}.wav`)).toBe(true);
  });

  it('cuts the Artist only once the title is down to one character', () => {
    const name = fileNameFor(clip({ artist: 'a'.repeat(300), title: 'Title' }));
    expect(name.length).toBe(MAX_NAME_UNITS);
    expect(name.endsWith(` - T ${TOKEN}.wav`)).toBe(true);
  });

  it('keeps within 240 bytes of UTF-8, cutting on whole characters (emoji, right-to-left text)', () => {
    const name = fileNameFor(clip({ artist: 'שלום', title: '🎵👨‍👩‍👧'.repeat(60) }));
    expect(bytes(name)).toBeLessThanOrEqual(MAX_NAME_BYTES);
    expect(name.length).toBeLessThanOrEqual(MAX_NAME_UNITS);
    // The family emoji is one character of several code points: it is never split.
    const readable = name.slice(0, name.indexOf(` ${TOKEN}`));
    expect(readable.replace(/^שלום - /, '').replace(/(🎵|👨‍👩‍👧)/gu, '')).toBe('');
    expect(name.endsWith(` ${TOKEN}.wav`)).toBe(true);
  });

  it('keeps emoji and right-to-left text as they are, normalised to NFC', () => {
    expect(fileNameFor(clip({ artist: 'Café', title: 'שלום 🎵' }))).toBe(
      `Café - שלום 🎵 ${TOKEN}.wav`,
    );
    expect(cleanPart('Café')).toBe('Café'.normalize('NFC'));
  });

  it('keeps names that differ only by case apart, by the ID', () => {
    const one = fileNameFor(clip({ title: 'Song' }));
    const two = fileNameFor(
      clip({ title: 'SONG', sunoId: '6f1e2d3c-4b5a-4987-a6b5-c4d3e2f1a0b9' }),
    );
    expect(one.toLowerCase()).not.toBe(two.toLowerCase());
    expect(one).toContain('Song (suno-');
    expect(two).toContain('SONG (suno-');
  });

  it('writes the Suno ID in lower case', () => {
    expect(fileNameFor(clip({ sunoId: ID.toUpperCase() }))).toBe(
      `Song Artist - Song Title ${TOKEN}.wav`,
    );
  });
});

interface SharedName {
  fileName: string;
  sunoIds: string[];
  note: string;
  from?: NamedClip;
  derived?: 'numbered' | 'truncated';
}

/** What the browser does to a second file of the same name: ` (1)` before the extension. */
function numbered(name: string): string {
  const dot = name.lastIndexOf('.');
  return `${name.slice(0, dot)} (1)${name.slice(dot)}`;
}

describe("the names shared with n8Tracks' matcher (fixtures/filenames.json, #206)", () => {
  const produced = (shared.names as SharedName[]).flatMap((name) =>
    name.from === undefined ? [] : [{ ...name, from: name.from }],
  );

  it('lists the names this downloader produces, each with its Suno ID, the browser-numbered form too', () => {
    expect(produced.length).toBeGreaterThanOrEqual(8);
    expect(produced.some((name) => name.derived === 'numbered')).toBe(true);
    for (const name of produced) {
      const from = name.from;
      expect(isDownloadFormat(from.format), name.note).toBe(true);
      const made = fileNameFor(from);
      if (name.derived === 'truncated') {
        continue;
      }
      expect(name.fileName, name.note).toBe(name.derived === 'numbered' ? numbered(made) : made);
      expect(name.sunoIds, name.note).toEqual([from.sunoId.toLowerCase()]);
    }
  });

  it('lists, as a complement, a produced name with its ID cut by hand, which matches nothing', () => {
    const cut = produced.filter((name) => name.derived === 'truncated');
    expect(cut.length).toBeGreaterThan(0);
    for (const name of cut) {
      const from = name.from;
      const id = from.sunoId.toLowerCase();
      expect(name.fileName).toBe(fileNameFor(from).replace(id, id.slice(0, -1)));
      expect(name.sunoIds).toEqual([]);
    }
  });
});
