import type { AlbumTrackPlace } from '../api/albums';

// How the Album page changes its tracks before sending the whole list (the API's full-list PUT).
// The order is disc, then track number. Dragging, Move up, and Move down stay within a disc and
// renumber it 1, 2, 3…; a typed number is kept as typed (it may leave a gap); Renumber closes
// gaps; "Move to disc" puts a track at the end of another disc and renumbers the disc it left. A
// disc with no tracks disappears and the later discs close up.

/** The tracks in the Album's order: by disc, then track number. */
export function ordered<T extends AlbumTrackPlace>(tracks: readonly T[]): T[] {
  return [...tracks].sort((a, b) => a.disc - b.disc || a.track - b.track);
}

/** The Album's discs in order, each with its tracks in track-number order. */
export function discsOf<T extends AlbumTrackPlace>(
  tracks: readonly T[],
): { disc: number; tracks: T[] }[] {
  const discs: { disc: number; tracks: T[] }[] = [];
  for (const track of ordered(tracks)) {
    const last = discs.at(-1);
    if (last?.disc === track.disc) {
      last.tracks.push(track);
    } else {
      discs.push({ disc: track.disc, tracks: [track] });
    }
  }
  return discs;
}

/** Just the places, as the API takes them. */
export function placesOf(tracks: readonly AlbumTrackPlace[]): AlbumTrackPlace[] {
  return ordered(tracks).map(({ songId, disc, track }) => ({ songId, disc, track }));
}

/** The tracks with their discs numbered 1, 2, 3… in order, so an emptied disc closes up. */
function closeDiscGaps(tracks: readonly AlbumTrackPlace[]): AlbumTrackPlace[] {
  const numbers = [...new Set(tracks.map((track) => track.disc))].sort((a, b) => a - b);
  return placesOf(tracks.map((track) => ({ ...track, disc: numbers.indexOf(track.disc) + 1 })));
}

/** `disc`'s tracks numbered 1, 2, 3… in the order given. */
function numbered(disc: number, tracks: readonly AlbumTrackPlace[]): AlbumTrackPlace[] {
  return tracks.map((track, index) => ({ songId: track.songId, disc, track: index + 1 }));
}

/**
 * The track of `songId` moved to `index` (from 0) within its disc, the disc renumbered 1, 2, 3…
 * in its new order. Other discs are unchanged.
 */
export function moveWithinDisc(
  tracks: readonly AlbumTrackPlace[],
  songId: string,
  index: number,
): AlbumTrackPlace[] {
  const moving = tracks.find((track) => track.songId === songId);
  if (moving === undefined) {
    return placesOf(tracks);
  }
  const disc = ordered(tracks.filter((track) => track.disc === moving.disc));
  const rest = disc.filter((track) => track.songId !== songId);
  const target = Math.max(0, Math.min(index, rest.length));
  const reordered = [...rest.slice(0, target), moving, ...rest.slice(target)];
  return placesOf([
    ...tracks.filter((track) => track.disc !== moving.disc),
    ...numbered(moving.disc, reordered),
  ]);
}

/**
 * The track of `songId` moved to the end of disc `disc` (a disc after the last starts a new one),
 * with the next track number there. The disc it left is renumbered 1, 2, 3…, and disappears when
 * empty, the later discs closing up.
 */
export function moveToDisc(
  tracks: readonly AlbumTrackPlace[],
  songId: string,
  disc: number,
): AlbumTrackPlace[] {
  const moving = tracks.find((track) => track.songId === songId);
  if (moving === undefined || moving.disc === disc) {
    return placesOf(tracks);
  }
  const left = ordered(
    tracks.filter((track) => track.disc === moving.disc && track.songId !== songId),
  );
  const others = tracks.filter((track) => track.disc !== moving.disc);
  const last = Math.max(0, ...others.filter((track) => track.disc === disc).map((t) => t.track));
  return closeDiscGaps([
    ...others,
    ...numbered(moving.disc, left),
    { songId, disc, track: last + 1 },
  ]);
}

/** Every disc's tracks numbered 1, 2, 3… in their current order, closing any gaps. */
export function renumber(tracks: readonly AlbumTrackPlace[]): AlbumTrackPlace[] {
  return discsOf(tracks).flatMap((group) => numbered(group.disc, group.tracks));
}

/** The track of `songId` given track number `track`, as typed; the rest unchanged. */
export function withTrackNumber(
  tracks: readonly AlbumTrackPlace[],
  songId: string,
  track: number,
): AlbumTrackPlace[] {
  return placesOf(tracks.map((item) => (item.songId === songId ? { ...item, track } : item)));
}

/** Whether every disc is numbered 1, 2, 3… already, so Renumber would change nothing. */
export function isNumberedInOrder(tracks: readonly AlbumTrackPlace[]): boolean {
  return discsOf(tracks).every((group) =>
    group.tracks.every((track, index) => track.track === index + 1),
  );
}
