import type { Version } from '../api/versions';

/** A Version in the tree, with the Versions drawn under it. */
export interface VersionNode {
  version: Version;
  children: VersionNode[];
}

function parts(number: string): number[] {
  return number.split('.').map(Number);
}

/** Tree order: part by part, numerically (`1.2` before `1.10`), a parent before its children. */
export function compareNumbers(left: string, right: string): number {
  const a = parts(left);
  const b = parts(right);
  for (let index = 0; index < Math.min(a.length, b.length); index++) {
    const difference = (a[index] ?? 0) - (b[index] ?? 0);
    if (difference !== 0) {
      return difference;
    }
  }
  return a.length - b.length;
}

/** The numbers above `number`, nearest first: `1.3.2` → `1.3`, `1`. */
function ancestors(number: string): string[] {
  const all = parts(number);
  const result: string[] = [];
  for (let length = all.length - 1; length >= 1; length--) {
    result.push(all.slice(0, length).join('.'));
  }
  return result;
}

/**
 * Nests the Versions `shown` keeps by their numbers: each goes under its nearest ancestor that is
 * drawn, so a Version whose parent is missing (or hidden) is drawn at the nearest existing ancestor,
 * or at the top level when there is none. Siblings are in numeric order.
 */
export function nestVersions(
  versions: Version[],
  shown: (version: Version) => boolean = () => true,
): VersionNode[] {
  const drawn = versions
    .filter(shown)
    .sort((left, right) => compareNumbers(left.number, right.number));
  const nodes = new Map<string, VersionNode>();
  const roots: VersionNode[] = [];
  for (const version of drawn) {
    const node: VersionNode = { version, children: [] };
    nodes.set(version.number, node);
    const parent = ancestors(version.number)
      .map((number) => nodes.get(number))
      .find((candidate) => candidate !== undefined);
    (parent?.children ?? roots).push(node);
  }
  return roots;
}
