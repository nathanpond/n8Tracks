import type { Artist, ArtistCreditChoice } from '../api/artists';

/** "2 Songs", "1 Song". */
function countText(count: number, noun: string): string {
  return `${count.toLocaleString('en-US')} ${noun}${count === 1 ? '' : 's'}`;
}

/** "2 Songs and 1 Album", "1 Song", "3 Albums". */
export function creditCountText(artist: Pick<Artist, 'songCount' | 'albumCount'>): string {
  const parts = [
    ...(artist.songCount > 0 ? [countText(artist.songCount, 'Song')] : []),
    ...(artist.albumCount > 0 ? [countText(artist.albumCount, 'Album')] : []),
  ];
  return parts.join(' and ');
}

/** What became of the credits, for the notice the Artists list shows. */
export function deletionNote(choice: ArtistCreditChoice): string {
  switch (choice.kind) {
    case 'reassign':
      return `Its credits went to “${choice.to.name}”. No Song or Album was deleted.`;
    case 'remove':
      return 'Its credits were removed. No Song or Album was deleted.';
    case 'none':
      return 'No Song or Album was deleted.';
  }
}
