import { describe, expect, it } from 'vitest';
import { testGeneration, testSongAudioFile } from '../test/versionServer';
import {
  clockText,
  DEFAULT_VOLUME,
  detailOf,
  nowPlayingOfFile,
  positionText,
  readVolume,
  seekTarget,
  storeVolume,
  titleOf,
  unplayableFileReason,
  unplayableReason,
  versionNumberOf,
  VOLUME_STORAGE_KEY,
} from './playerRules';

describe('the player rules', () => {
  it('reads the Version number out of a Version or Generation shortcode', () => {
    expect(versionNumberOf('n8-12-v1.1-g3')).toBe('1.1');
    expect(versionNumberOf('n8-12-v2')).toBe('2');
    expect(versionNumberOf('N8-7-V10.2.1-G12')).toBe('10.2.1');
    expect(versionNumberOf('n8-12')).toBe('');
  });

  it('labels a Generation file, a Song-level file, and a file with no Song as the bar shows them', () => {
    const generation = testGeneration('1.2', 3);
    const ofGeneration = nowPlayingOfFile(testSongAudioFile('Take.m4a', generation));
    expect(ofGeneration.generationId).toBe(generation.id);
    expect(titleOf(ofGeneration.label)).toBe('Running in a Pack');
    expect(detailOf(ofGeneration.label)).toBe('Version 1.2 · n8-7-v1.2-g3 · M4A');

    const songLevel = nowPlayingOfFile(testSongAudioFile('Masters/Final.wav', null));
    expect(songLevel.generationId).toBeNull();
    expect(titleOf(songLevel.label)).toBe('Running in a Pack');
    expect(detailOf(songLevel.label)).toBe('Song-level file · Final.wav');

    const loose = nowPlayingOfFile(
      testSongAudioFile('loose.mp3', null, { song: null, associationOrigin: null }),
    );
    expect(titleOf(loose.label)).toBe('loose.mp3');
    expect(detailOf(loose.label)).toBe('MP3');
    expect(loose.src).toBe(
      new URL(`api/v1/audio-files/${loose.fileId}/content`, document.baseURI).toString(),
    );
  });

  it('writes times as minutes and seconds, rounding down, with hours past an hour', () => {
    expect(clockText(0)).toBe('0:00');
    expect(clockText(65.9)).toBe('1:05');
    expect(clockText(3725)).toBe('1:02:05');
    expect(clockText(null)).toBe('–:––');
    expect(clockText(Number.NaN)).toBe('–:––');
    expect(positionText(65, 187)).toBe('1:05 of 3:07');
    expect(positionText(5, null)).toBe('0:05 of unknown length');
  });

  it('moves the seek bar 5 seconds by arrow keys and 30 by page keys, within the track', () => {
    expect(seekTarget('ArrowRight', 10, 100)).toBe(15);
    expect(seekTarget('ArrowUp', 98, 100)).toBe(100);
    expect(seekTarget('ArrowLeft', 3, 100)).toBe(0);
    expect(seekTarget('ArrowDown', 10, 100)).toBe(5);
    expect(seekTarget('PageUp', 10, 100)).toBe(40);
    expect(seekTarget('PageDown', 10, 100)).toBe(0);
    expect(seekTarget('Home', 50, 100)).toBe(0);
    expect(seekTarget('End', 50, 100)).toBe(100);
    expect(seekTarget('Enter', 50, 100)).toBeUndefined();
    // Before the length is known it cannot move forward.
    expect(seekTarget('ArrowRight', 10, null)).toBe(10);
  });

  it('remembers the volume and mute, and falls back to the default for anything unreadable', () => {
    expect(readVolume()).toEqual(DEFAULT_VOLUME);
    storeVolume({ volume: 0.25, muted: true });
    expect(readVolume()).toEqual({ volume: 0.25, muted: true });
    window.localStorage.setItem(VOLUME_STORAGE_KEY, '{"volume":7,"muted":false}');
    expect(readVolume()).toEqual({ volume: 1, muted: false });
    window.localStorage.setItem(VOLUME_STORAGE_KEY, 'not json');
    expect(readVolume()).toEqual(DEFAULT_VOLUME);
    window.localStorage.setItem(VOLUME_STORAGE_KEY, '{"volume":"loud"}');
    expect(readVolume()).toEqual(DEFAULT_VOLUME);
  });

  it('gives the reason a Play control is disabled, from what the server decided', () => {
    const playable = testGeneration('1', 1, { playback: { playable: true, reason: null } });
    expect(unplayableReason(playable)).toBeUndefined();
    expect(unplayableReason(testGeneration('1', 1))).toBe(
      'Nothing to play: this Generation has no local audio file.',
    );
    const files = (count: number, missing: number, unavailable: number) =>
      testGeneration('1', 1, { audioFiles: { count, missing, unavailable, formats: ['wav'] } });
    expect(unplayableReason(files(2, 2, 0))).toBe('Nothing to play: its audio files are Missing.');
    expect(unplayableReason(files(2, 0, 2))).toBe(
      'Nothing to play: the media folder cannot be read.',
    );
    expect(unplayableFileReason('available')).toBeUndefined();
    expect(unplayableFileReason('missing')).toBe(
      'Cannot play: the file is Missing from the media folder.',
    );
  });
});
