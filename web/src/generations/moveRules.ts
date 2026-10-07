import type { Generation } from '../api/generations';
import type { Song } from '../api/songs';

/** What moves with a Generation to a new Song (#123), as the warning lists it. */
export const MOVED_WITH_IT = ['its rating', 'its comments', 'its image', 'its Suno data'];

/** The title a new Song from `generation` starts with: its Suno title, or its Song's title when it has none. */
export function proposedTitle(generation: Generation, song: Song): string {
  const title = generation.title?.trim();
  return title === undefined || title === '' ? song.title : title;
}
