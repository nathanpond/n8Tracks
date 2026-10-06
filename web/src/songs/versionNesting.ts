import type { Version } from '../api/versions';

/**
 * A node of the tree: a Version with the nodes drawn under it, or a "Deleted Version" placeholder
 * (no `version`) that only holds the place of a deleted Version's number above its descendants.
 */
export interface VersionNode {
  /** Unique in the tree: the Version's ID, or `deleted-<number>` for a placeholder. */
  key: string;
  number: string;
  /** The Version; undefined for a placeholder. */
  version: Version | undefined;
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

/** The nodes left once every placeholder with nothing drawn under it is dropped. */
function withoutEmptyPlaceholders(nodes: VersionNode[]): VersionNode[] {
  return nodes.flatMap((node) => {
    const children = withoutEmptyPlaceholders(node.children);
    return node.version === undefined && children.length === 0 ? [] : [{ ...node, children }];
  });
}

/**
 * Nests the Versions `shown` keeps by their numbers: each goes under its nearest ancestor that is
 * drawn, so a Version whose parent is missing (or hidden) is drawn at the nearest existing ancestor,
 * or at the top level when there is none. `placeholders` are deleted Versions' numbers, drawn as
 * "Deleted Version" nodes in their place, with their descendants under them, only while something
 * is drawn under them. Siblings are in numeric order.
 */
export function nestVersions(
  versions: Version[],
  shown: (version: Version) => boolean = () => true,
  placeholders: readonly string[] = [],
): VersionNode[] {
  const drawn: VersionNode[] = [
    ...versions
      .filter(shown)
      .map((version) => ({ key: version.id, number: version.number, version, children: [] })),
    ...placeholders
      .filter((number) => !versions.some((version) => version.number === number))
      .map((number) => ({ key: `deleted-${number}`, number, version: undefined, children: [] })),
  ].sort((left, right) => compareNumbers(left.number, right.number));
  const nodes = new Map<string, VersionNode>();
  const roots: VersionNode[] = [];
  for (const node of drawn) {
    nodes.set(node.number, node);
    const parent = ancestors(node.number)
      .map((number) => nodes.get(number))
      .find((candidate) => candidate !== undefined);
    (parent?.children ?? roots).push(node);
  }
  return withoutEmptyPlaceholders(roots);
}

/** A placeholder's accessible name and text. */
export function placeholderLabel(number: string): string {
  return `Deleted Version ${number}`;
}

/** A Version's accessible name: its number, name, and whether it is current or archived. */
export function versionLabel(version: Version): string {
  return [
    `Version ${version.number}`,
    version.name,
    version.current ? 'current working Version' : null,
    version.archived ? 'archived' : null,
    version.isFrozen ? 'frozen' : null,
  ]
    .filter((part) => part !== null)
    .join(', ');
}
