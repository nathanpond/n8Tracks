import { describe, expect, it } from 'vitest';
import type { PlaybackSourceGeneration, PlaybackSources } from '../api/playbackSources';
import {
  carryTime,
  entriesOf,
  generationHeading,
  isTypingTarget,
  nextPair,
  NO_PAIR,
  nowPlayingOfEntry,
  shortcutOf,
  stepFrom,
  switchFailedText,
  type CompareEntry,
} from './compare';
import type { NowPlaying } from './playerRules';

/**
 * The comparison rules (#220): the order and names of a Song's sources, Previous and Next with
 * their stops and wrapping, the A/B pair, the time carried over (and the shorter-audio case), and
 * which key presses are shortcuts.
 */

function file(id: string, fileName: string, format: string) {
  return {
    id,
    fileName,
    format,
    durationSeconds: 90,
    contentUrl: `/api/v1/audio-files/${id}/content`,
  };
}

function generation(
  ordinal: number,
  files: ReturnType<typeof file>[],
  rating: number | null = null,
): PlaybackSourceGeneration {
  return {
    generation: { id: `g${String(ordinal)}`, shortcode: `n8-7-v1-g${String(ordinal)}` },
    versionNumber: '1',
    rating,
    revision: 1,
    durationSeconds: 90,
    state: 'active',
    remoteState: 'present',
    files: files.map((audioFile, index) => ({ audioFile, isPlaybackFile: index === 0 })),
    sunoAudioUrl: null,
    sunoPageUrl: null,
  };
}

const SOURCES: PlaybackSources = {
  song: { id: 'song', shortcode: 'n8-7', title: 'Running in a Pack' },
  songFiles: [{ audioFile: file('m', 'Master.wav', 'wav'), isPlaybackFile: false }],
  generations: [
    generation(1, [file('1w', 'one.wav', 'wav'), file('1m', 'one.mp3', 'mp3')], 4),
    generation(2, [file('2w', 'two.wav', 'wav'), file('2x', 'two (alt).wav', 'wav')]),
    generation(3, [file('3m', 'three.m4a', 'm4a')], 1),
  ],
};

const ENTRIES = entriesOf(SOURCES);

function entry(key: string): CompareEntry {
  const found = ENTRIES.find((candidate) => candidate.key === key);
  if (found === undefined) {
    throw new Error(`No entry ${key}.`);
  }
  return found;
}

function playing(key: string, songId: string | null = 'song'): NowPlaying {
  return { ...nowPlayingOfEntry(SOURCES.song, entry(key), null), songId };
}

describe('entriesOf', () => {
  it('lists Song-level files, then each Generation in order with its playback file first', () => {
    expect(ENTRIES.map((candidate) => candidate.key)).toEqual(['m', '1w', '1m', '2w', '2x', '3m']);
    expect(ENTRIES.map((candidate) => candidate.stop)).toEqual([
      true,
      true,
      false,
      true,
      false,
      true,
    ]);
  });

  it('names Generation files by shortcode and format, adding the file name for two of one format', () => {
    expect(ENTRIES.map((candidate) => candidate.name)).toEqual([
      'Master.wav',
      'n8-7-v1-g1 · WAV',
      'n8-7-v1-g1 · MP3',
      'n8-7-v1-g2 · WAV · two.wav',
      'n8-7-v1-g2 · WAV · two (alt).wav',
      'n8-7-v1-g3 · M4A',
    ]);
    expect(SOURCES.generations.map(generationHeading)).toEqual([
      'Version 1 · n8-7-v1-g1 · 4 stars',
      'Version 1 · n8-7-v1-g2 · not rated',
      'Version 1 · n8-7-v1-g3 · 1 star',
    ]);
  });
});

describe('Suno streams (#221)', () => {
  const STREAM = 'https://d2lwuy8qc234o3.cloudfront.net/1/clip/four.m4a';
  const withStream: PlaybackSources = {
    ...SOURCES,
    generations: [
      ...SOURCES.generations,
      { ...generation(4, []), sunoAudioUrl: STREAM, sunoPageUrl: 'https://suno.com/song/four' },
    ],
  };
  const entries = entriesOf(withStream);
  const streamed = entries.at(-1);

  it('lists a Generation with no file by its Suno stream, marked as one, as a stop', () => {
    expect(entries.map((candidate) => candidate.source)).toEqual([
      'local',
      'local',
      'local',
      'local',
      'local',
      'local',
      'suno',
    ]);
    expect(streamed?.key).toBe('suno:g4');
    expect(streamed?.name).toBe('n8-7-v1-g4 · Suno stream');
    expect(streamed?.stop).toBe(true);
    expect(stepFrom(entries, '3m', 1)?.key).toBe('suno:g4');
  });

  it('switches to the stream as Suno’s address, labelled as streaming, with its Suno page', () => {
    if (streamed === undefined) {
      throw new Error('No stream entry.');
    }
    const next = nowPlayingOfEntry(withStream.song, streamed, null);
    expect(next.source).toBe('suno');
    expect(next.src).toBe(STREAM);
    expect(next.fileId).toBe('suno:g4');
    expect(next.generationId).toBe('g4');
    expect(next.sunoPageUrl).toBe('https://suno.com/song/four');
    expect(switchFailedText(next, playing('1w'))).toBe(
      'n8-7-v1-g4 · Suno stream could not be played, so the player went back to n8-7-v1-g1 · WAV.',
    );
  });

  it('never lists the stream of a Generation that has a file', () => {
    const both = entriesOf({
      ...SOURCES,
      generations: [{ ...generation(1, [file('1w', 'one.wav', 'wav')]), sunoAudioUrl: STREAM }],
    });
    expect(both.map((candidate) => candidate.source)).toEqual(['local', 'local']);
  });
});

describe('stepFrom', () => {
  it('stops once per Song-level file and once per Generation, wrapping at both ends', () => {
    expect(stepFrom(ENTRIES, 'm', 1)?.key).toBe('1w');
    expect(stepFrom(ENTRIES, '1w', 1)?.key).toBe('2w');
    expect(stepFrom(ENTRIES, '2w', 1)?.key).toBe('3m');
    expect(stepFrom(ENTRIES, '3m', 1)?.key).toBe('m');
    expect(stepFrom(ENTRIES, 'm', -1)?.key).toBe('3m');
    expect(stepFrom(ENTRIES, '2w', -1)?.key).toBe('1w');
  });

  it('moves relative to the Generation from one of its other files', () => {
    expect(stepFrom(ENTRIES, '1m', 1)?.key).toBe('2w');
    expect(stepFrom(ENTRIES, '2x', -1)?.key).toBe('1w');
  });

  it('goes to the first stop from a file no longer listed', () => {
    expect(stepFrom(ENTRIES, 'gone', 1)?.key).toBe('m');
    expect(stepFrom(ENTRIES, 'gone', -1)?.key).toBe('m');
    expect(stepFrom(ENTRIES, null, 1)?.key).toBe('m');
  });

  it('has nowhere to go with one source', () => {
    const one = entriesOf({
      ...SOURCES,
      songFiles: [],
      generations: [generation(1, [file('a', 'a.wav', 'wav')])],
    });
    expect(stepFrom(one, 'a', 1)).toBeUndefined();
    expect(stepFrom(one, 'a', -1)).toBeUndefined();
    expect(stepFrom([], 'a', 1)).toBeUndefined();
  });
});

describe('nextPair', () => {
  it('keeps the current source and the one before it, after three different sources', () => {
    let pair = nextPair(NO_PAIR, playing('1w'));
    expect(pair.previous).toBeNull();
    pair = nextPair(pair, playing('2w'));
    expect(pair.previous?.fileId).toBe('1w');
    pair = nextPair(pair, playing('3m'));
    expect(pair.current?.fileId).toBe('3m');
    expect(pair.previous?.fileId).toBe('2w');
    // A/B back: the two swap.
    pair = nextPair(pair, pair.previous ?? playing('m'));
    expect([pair.current?.fileId, pair.previous?.fileId]).toEqual(['2w', '3m']);
  });

  it('changes nothing when the same source plays again, and forgets the pair on another Song', () => {
    const pair = nextPair(nextPair(NO_PAIR, playing('1w')), playing('2w'));
    expect(nextPair(pair, playing('2w')).previous?.fileId).toBe('1w');
    expect(nextPair(pair, playing('m', 'other')).previous).toBeNull();
    expect(nextPair(pair, playing('m', null)).previous).toBeNull();
  });
});

describe('carryTime', () => {
  it('carries the time over, or starts a shorter audio at its beginning', () => {
    expect(carryTime(30, 90)).toBe(30);
    expect(carryTime(30, null)).toBe(30);
    expect(carryTime(30, 20)).toBe(0);
    expect(carryTime(30, 30)).toBe(0);
    expect(carryTime(0, 90)).toBe(0);
    expect(carryTime(Number.NaN, 90)).toBe(0);
  });
});

describe('nowPlayingOfEntry', () => {
  it('labels the source and says it was chosen only after a Song’s Play', () => {
    const fromGeneration = playing('1w');
    expect(fromGeneration.via).toBeNull();
    const next = nowPlayingOfEntry(SOURCES.song, entry('2w'), {
      ...fromGeneration,
      via: 'selected-generation',
    });
    expect(next.via).toBe('chosen');
    expect(next.generationId).toBe('g2');
    expect(nowPlayingOfEntry(SOURCES.song, entry('m'), fromGeneration).label.kind).toBe(
      'song-level',
    );
    expect(switchFailedText(playing('2w'), fromGeneration)).toBe(
      'n8-7-v1-g2 · WAV could not be played, so the player went back to n8-7-v1-g1 · WAV.',
    );
  });
});

describe('shortcutOf', () => {
  const press = (code: string, change: Partial<KeyboardEventInit> = {}) =>
    shortcutOf(new KeyboardEvent('keydown', { code, ...change }));

  it('matches the physical keys alone', () => {
    expect(press('BracketLeft')).toBe('previous');
    expect(press('BracketRight')).toBe('next');
    expect(press('Backslash')).toBe('ab');
    expect(press('KeyA')).toBeUndefined();
  });

  it('ignores auto-repeat and modifiers', () => {
    expect(press('BracketRight', { repeat: true })).toBeUndefined();
    expect(press('BracketRight', { ctrlKey: true })).toBeUndefined();
    expect(press('BracketRight', { metaKey: true })).toBeUndefined();
    expect(press('BracketRight', { altKey: true })).toBeUndefined();
    expect(press('BracketRight', { shiftKey: true })).toBeUndefined();
  });
});

describe('isTypingTarget', () => {
  it('is true in text fields, text areas, and editors, and false on buttons and sliders', () => {
    const text = document.createElement('input');
    const range = document.createElement('input');
    range.type = 'range';
    const area = document.createElement('textarea');
    const editor = document.createElement('div');
    editor.setAttribute('contenteditable', 'true');
    const inside = document.createElement('span');
    editor.appendChild(inside);
    const button = document.createElement('button');
    expect(isTypingTarget(text)).toBe(true);
    expect(isTypingTarget(area)).toBe(true);
    expect(isTypingTarget(inside)).toBe(true);
    expect(isTypingTarget(range)).toBe(false);
    expect(isTypingTarget(button)).toBe(false);
    expect(isTypingTarget(null)).toBe(false);
  });
});
