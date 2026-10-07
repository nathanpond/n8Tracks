import type { Generation } from '../api/generations';
import type { Version } from '../api/versions';

/**
 * Whether the table lists `version`: an Active one, or any while archived ones are shown. Unlike
 * the tree, the table does not keep an archived current Version: the selected Version is in the
 * editor and the tree whatever the table lists.
 */
export function isListed(version: Version, showArchived: boolean): boolean {
  return !version.archived || showArchived;
}

/** Whether a Version's Generation rows include `generation`: an Active one, the Selected one, or any while archived ones are shown. */
export function isGenerationListed(generation: Generation, showArchived: boolean): boolean {
  return generation.state === 'active' || generation.isSelected || showArchived;
}

/** A Song's Generations by the ID of the Version each belongs to, each list in ordinal order. */
export function generationsByVersion(
  generations: readonly Generation[],
): Map<string, Generation[]> {
  const grouped = new Map<string, Generation[]>();
  for (const generation of generations) {
    const list = grouped.get(generation.version.id) ?? [];
    list.push(generation);
    grouped.set(generation.version.id, list);
  }
  for (const list of grouped.values()) {
    list.sort((left, right) => left.ordinal - right.ordinal);
  }
  return grouped;
}
