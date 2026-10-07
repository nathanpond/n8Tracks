import type { GenerationDeletionImpact } from '../api/generations';

/** How long a deleted Generation can be restored, as the confirmation says it. */
export const RETENTION_DAYS = 30;

function count(n: number, one: string, many: string): string {
  return `${String(n)} ${n === 1 ? one : many}`;
}

/**
 * What the confirmation for deleting a Generation states (#124), one sentence per line: what goes
 * with it (named by shortcode), the Versions that use it as a source, that nothing in Suno changes,
 * and how long it can be restored.
 */
export function generationDeletionSummary(impact: GenerationDeletionImpact): string[] {
  const comments = impact.commentCount === 0 ? 'none' : String(impact.commentCount);
  const artwork =
    impact.artworkCount === 0 ? 'none' : count(impact.artworkCount, 'image', 'images');
  const lines = [
    `Generation ${impact.shortcode} will be deleted from n8Tracks with its rating, its comments (${comments}), and its Suno artwork (${artwork}).`,
  ];
  if (impact.sourceVersionCount > 0) {
    lines.push(
      `${count(impact.sourceVersionCount, 'Version uses', 'Versions use')} it as a source; ${impact.sourceVersionCount === 1 ? 'it' : 'they'} will show it as Deleted.`,
    );
  }
  lines.push('Nothing in Suno is changed.');
  lines.push(`It can be restored for ${String(RETENTION_DAYS)} days.`);
  return lines;
}
