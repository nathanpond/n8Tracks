/**
 * The section tags the lyrics editor offers when the user types `[`, in song order. Any other tag
 * may be typed: Suno reads free text between brackets, and n8Tracks keeps it as written.
 */
export const COMMON_TAGS: readonly string[] = [
  'Intro',
  'Verse',
  'Pre-Chorus',
  'Chorus',
  'Post-Chorus',
  'Bridge',
  'Hook',
  'Break',
  'Interlude',
  'Instrumental',
  'Solo',
  'Outro',
  'End',
];

/** The common tags that start with `prefix`, ignoring case, in list order. */
export function tagsStartingWith(prefix: string): string[] {
  const lowered = prefix.toLowerCase();
  return COMMON_TAGS.filter((tag) => tag.toLowerCase().startsWith(lowered));
}
