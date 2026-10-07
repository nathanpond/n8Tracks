import type { ObservedCreate } from '../api/generationRequests';

/** Option names Suno writes differently from the words of their key. */
const SPECIAL_LABELS: Readonly<Record<string, string>> = {
  sources: 'Sources',
  songMode: 'Mode',
  speechMode: 'Mode',
  kind: 'Kind',
  simplePrompt: 'Song description',
  speechPrompt: 'Speech description',
  durationMode: 'Duration',
  durationSeconds: 'Duration (seconds)',
  soundBpm: 'BPM',
};

/** An option's name as the Create form shows it, from its key (`styleInfluence` → "Style Influence"). */
export function observedOptionLabel(key: string): string {
  const special = SPECIAL_LABELS[key];
  if (special !== undefined) {
    return special;
  }
  return key
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .split(' ')
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
    .join(' ');
}

/** A list of options' names, in the order given. */
export function optionList(keys: readonly string[]): string {
  return keys.map(observedOptionLabel).join(', ');
}

/** What one Create came to, in a sentence (#149). */
export function observedText(observed: ObservedCreate): string {
  const count = observed.generations.length;
  const generations = `${String(count)} ${count === 1 ? 'Generation' : 'Generations'}`;
  switch (observed.outcome) {
    case 'attached':
      return `${generations} recorded on this request’s Version ${observed.version?.number ?? ''}, which is now frozen.`;
    case 'branched':
      return `You changed the form, so ${generations.toLowerCase()} went to a new Version ${observed.version?.number ?? ''}, made from what was submitted; this Version is unchanged.`;
    case 'none':
      return 'No Generation was recorded: every clip was skipped.';
  }
}

/** Why a clip was skipped, in plain words. */
export function skipReasonText(reason: string): string {
  switch (reason) {
    case 'already_linked':
      return 'already in n8Tracks';
    case 'tombstoned':
      return 'deleted from n8Tracks, so it stays deleted';
    default:
      return reason;
  }
}
