import type { DeletionImpact } from '../api/versions';

/** "1 Generation", "2 Generations". */
function count(n: number, noun: string): string {
  return `${String(n)} ${noun}${n === 1 ? '' : 's'}`;
}

/**
 * What the confirmation says deleting `number` does: permanent; its Generations go with it; its
 * descendants remain, each keeping its number, under a placeholder; and, for a Song's only
 * Version, that a new blank Version is created and made current.
 */
export function deletionSummary(number: string, impact: DeletionImpact): string[] {
  const lines = [`Version ${number} and its editing history are deleted permanently.`];
  if (impact.generationCount > 0) {
    lines.push(
      `Its ${count(impact.generationCount, 'Generation')} ${impact.generationCount === 1 ? 'is' : 'are'} deleted with it.`,
    );
  }
  if (impact.remainingDescendantCount > 0) {
    lines.push(
      `${count(impact.remainingDescendantCount, 'descendant Version')} will remain, keeping ${impact.remainingDescendantCount === 1 ? 'its number' : 'their numbers'}, under a “Deleted Version ${number}” placeholder.`,
    );
  } else {
    lines.push('No descendant Versions will remain.');
  }
  if (impact.isLastVersion) {
    lines.push(
      'This is the Song’s only Version, so a new blank Version is created and becomes the current one.',
    );
  }
  lines.push('Its number is never used again.');
  return lines;
}
