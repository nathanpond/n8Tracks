/** One line of a comparison: in both texts, only in the later one (added), or only in the earlier one (removed). */
export interface DiffLine {
  kind: 'same' | 'added' | 'removed';
  text: string;
}

/** The lines of a text; an empty text has none, and a final line break does not start another. */
function linesOf(text: string): string[] {
  if (text === '') {
    return [];
  }
  const lines = text.split('\n');
  if (lines[lines.length - 1] === '') {
    lines.pop();
  }
  return lines;
}

/**
 * A line-by-line comparison of `before` with `after`: the longest run of lines the two share, in
 * order, is kept as `same`; every other line of `before` is `removed` and of `after` is `added`.
 * In each changed stretch the removed lines come before the added ones. Lyrics are at most a few
 * hundred lines, so the plain table method is fast enough.
 */
export function lineDiff(before: string, after: string): DiffLine[] {
  const a = linesOf(before);
  const b = linesOf(after);
  const width = b.length + 1;
  // lengths[i * width + j]: the longest shared run of a[i..] and b[j..].
  const lengths = new Uint32Array((a.length + 1) * width);
  for (let i = a.length - 1; i >= 0; i--) {
    for (let j = b.length - 1; j >= 0; j--) {
      lengths[i * width + j] =
        a[i] === b[j]
          ? (lengths[(i + 1) * width + j + 1] ?? 0) + 1
          : Math.max(lengths[(i + 1) * width + j] ?? 0, lengths[i * width + j + 1] ?? 0);
    }
  }

  const result: DiffLine[] = [];
  let i = 0;
  let j = 0;
  while (i < a.length || j < b.length) {
    const left = a[i];
    const right = b[j];
    if (left !== undefined && right !== undefined && left === right) {
      result.push({ kind: 'same', text: left });
      i++;
      j++;
    } else if (
      left !== undefined &&
      (right === undefined ||
        (lengths[(i + 1) * width + j] ?? 0) >= (lengths[i * width + j + 1] ?? 0))
    ) {
      result.push({ kind: 'removed', text: left });
      i++;
    } else if (right !== undefined) {
      result.push({ kind: 'added', text: right });
      j++;
    }
  }
  return result;
}

/** Whether a comparison has any added or removed line. */
export function hasChanges(diff: readonly DiffLine[]): boolean {
  return diff.some((line) => line.kind !== 'same');
}
