import { describe, expect, it } from 'vitest';
import {
  discsOf,
  isNumberedInOrder,
  moveToDisc,
  moveWithinDisc,
  renumber,
  withTrackNumber,
} from './trackOrder';

const place = (songId: string, disc: number, track: number) => ({ songId, disc, track });

/** Each place as `song disc.track`, in the Album's order. */
const shown = (places: { songId: string; disc: number; track: number }[]) =>
  places.map((item) => `${item.songId} ${String(item.disc)}.${String(item.track)}`);

const PACK = [place('a', 1, 1), place('b', 1, 2), place('c', 1, 3), place('d', 2, 1)];

describe('track order', () => {
  it('groups tracks by disc, each in track-number order', () => {
    const discs = discsOf([place('c', 2, 1), place('b', 1, 9), place('a', 1, 2)]);
    expect(discs.map((disc) => [disc.disc, disc.tracks.map((track) => track.songId)])).toEqual([
      [1, ['a', 'b']],
      [2, ['c']],
    ]);
  });

  it('moves a track within its disc and renumbers that disc 1, 2, 3…', () => {
    expect(shown(moveWithinDisc(PACK, 'c', 0))).toEqual(['c 1.1', 'a 1.2', 'b 1.3', 'd 2.1']);
    expect(shown(moveWithinDisc(PACK, 'a', 1))).toEqual(['b 1.1', 'a 1.2', 'c 1.3', 'd 2.1']);

    // A disc with a gap is renumbered too; other discs keep their numbers.
    const gapped = [place('a', 1, 2), place('b', 1, 7), place('d', 2, 5)];
    expect(shown(moveWithinDisc(gapped, 'b', 0))).toEqual(['b 1.1', 'a 1.2', 'd 2.5']);
  });

  it('moves a track to the end of another disc, renumbering the disc it left', () => {
    expect(shown(moveToDisc(PACK, 'a', 2))).toEqual(['b 1.1', 'c 1.2', 'd 2.1', 'a 2.2']);

    // A new disc after the last.
    expect(shown(moveToDisc(PACK, 'b', 3))).toEqual(['a 1.1', 'c 1.2', 'd 2.1', 'b 3.1']);

    // A disc left empty disappears and the later discs close up.
    expect(shown(moveToDisc(PACK, 'd', 1))).toEqual(['a 1.1', 'b 1.2', 'c 1.3', 'd 1.4']);
    expect(
      shown(moveToDisc([place('a', 1, 1), place('b', 2, 1), place('c', 2, 2)], 'a', 3)),
    ).toEqual(['b 1.1', 'c 1.2', 'a 2.1']);
  });

  it('keeps a typed number as typed, and Renumber closes gaps', () => {
    const typed = withTrackNumber(PACK, 'a', 9);
    expect(shown(typed)).toEqual(['b 1.2', 'c 1.3', 'a 1.9', 'd 2.1']);
    expect(isNumberedInOrder(typed)).toBe(false);
    expect(shown(renumber(typed))).toEqual(['b 1.1', 'c 1.2', 'a 1.3', 'd 2.1']);
    expect(isNumberedInOrder(PACK)).toBe(true);
  });
});
