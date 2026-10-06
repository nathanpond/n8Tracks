import type { SongDeletionImpact } from '../api/songDeletion';

/** "1 Version", "3 Versions". */
function count(n: number, singular: string, plural = `${singular}s`): string {
  return `${String(n)} ${n === 1 ? singular : plural}`;
}

/**
 * What the confirmation lists deleting a Song affects, one line each, with counts: its Versions
 * (archived included), Generations, managed artwork, Album and Playlist memberships, relationships,
 * and local audio files. Every count is shown, zero included, so the list reads the same each time.
 */
export function songDeletionCounts(impact: SongDeletionImpact): string[] {
  return [
    count(impact.versionCount, 'Version'),
    count(impact.generationCount, 'Generation'),
    count(impact.artworkCount, 'managed artwork image'),
    count(impact.albumCount, 'Album membership'),
    count(impact.playlistCount, 'Playlist membership'),
    count(impact.relationshipCount, 'relationship'),
    count(impact.audioFileCount, 'local audio file'),
  ];
}

/**
 * Whether `typed` confirms deleting the Song titled `title`: equal once the typed text is trimmed,
 * and exactly otherwise (letter case and inner spaces count), as the server compares it.
 */
export function titleConfirms(typed: string, title: string): boolean {
  return typed.trim() === title;
}
