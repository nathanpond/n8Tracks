import { describe, expect, it } from 'vitest';
import type { Version } from '../api/versions';
import { compareNumbers, nestVersions, type VersionNode } from './versionNesting';

function version(number: string, archived = false): Version {
  return {
    id: `id-${number}`,
    songId: 'song',
    number,
    shortcode: `n8-1-v${number}`,
    name: null,
    notes: null,
    archived,
    current: false,
    createdAt: '2026-10-01T09:00:00Z',
    updatedAt: '2026-10-01T09:00:00Z',
    revision: 1,
  };
}

/** The tree as text: each node's number, its children in brackets. */
function shape(nodes: VersionNode[]): string {
  return nodes
    .map((node) =>
      node.children.length === 0
        ? node.version.number
        : `${node.version.number}[${shape(node.children)}]`,
    )
    .join(' ');
}

describe('nesting Versions by number', () => {
  it('puts each Version under its parent, siblings in numeric order', () => {
    const versions = ['2', '1.10', '1', '1.2', '1.2.1', '10', '1.1'].map((n) => version(n));
    expect(shape(nestVersions(versions))).toBe('1[1.1 1.2[1.2.1] 1.10] 2 10');
  });

  it('draws a Version whose parent is missing under the nearest existing ancestor', () => {
    const versions = ['1', '1.3.1', '1.3.1.4', '2.1', '3.2.5'].map((n) => version(n));
    expect(shape(nestVersions(versions))).toBe('1[1.3.1[1.3.1.4]] 2.1 3.2.5');
  });

  it('draws the children of a hidden Version under its nearest shown ancestor', () => {
    const versions = [version('1'), version('1.1', true), version('1.1.1'), version('2', true)];
    expect(shape(nestVersions(versions, (v) => !v.archived))).toBe('1[1.1.1]');
    expect(shape(nestVersions(versions))).toBe('1[1.1[1.1.1]] 2');
  });

  it('orders numbers part by part', () => {
    expect(['1.10', '1.2', '2', '1', '1.2.1'].sort(compareNumbers)).toEqual([
      '1',
      '1.2',
      '1.2.1',
      '1.10',
      '2',
    ]);
  });
});
