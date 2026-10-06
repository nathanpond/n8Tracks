// How the TokenPicker suggests: the matching rules, kept apart from the component so they can be
// tested on their own and reused by each list the picker serves (Genres, Tags, Artists…).

/** Something a picker offers and holds: an ID and the name shown. */
export interface Token {
  id: string;
  name: string;
}

/** How many suggestions the picker shows at most. */
export const SUGGESTION_LIMIT = 10;

/** Text as names are compared: trimmed, inner white space as one space, NFC, ignoring case. */
export function comparable(text: string): string {
  return text.trim().replace(/\s+/g, ' ').normalize('NFC').toLocaleLowerCase();
}

/**
 * Where `query` starts a word of `name`, ignoring case: 0 when it starts the name, a later index
 * when it starts another word (after a character that is not a letter or digit), or -1.
 */
function wordStart(name: string, query: string): number {
  for (let index = 0; index < name.length; index += 1) {
    const atWordStart = index === 0 || !/[\p{L}\p{N}]/u.test(name.charAt(index - 1));
    if (atWordStart && name.startsWith(query, index)) {
      return index;
    }
  }
  return -1;
}

/**
 * The suggestions for `query`: tokens from `options` whose name has a word starting with it,
 * ignoring case, leaving out those already `chosen` (by ID). Names the query starts come first,
 * then names where it starts a later word, each alphabetically; at most `limit`. An empty query
 * suggests the first names alphabetically.
 */
export function suggestTokens<T extends Token>(
  options: readonly T[],
  chosen: readonly Token[],
  query: string,
  limit = SUGGESTION_LIMIT,
): T[] {
  const taken = new Set(chosen.map((token) => token.id));
  const wanted = comparable(query);
  const ranked: { token: T; rank: number; key: string }[] = [];
  for (const token of options) {
    if (taken.has(token.id)) {
      continue;
    }
    const key = comparable(token.name);
    const start = wanted === '' ? 0 : wordStart(key, wanted);
    if (start >= 0) {
      ranked.push({ token, rank: start === 0 ? 0 : 1, key });
    }
  }
  ranked.sort(
    (a, b) => a.rank - b.rank || a.key.localeCompare(b.key) || a.token.id.localeCompare(b.token.id),
  );
  return ranked.slice(0, limit).map((entry) => entry.token);
}

/** Whether a token in `options` (chosen or not) has exactly `query` as its name, ignoring case and spacing. */
export function hasExactMatch(options: readonly Token[], query: string): boolean {
  const wanted = comparable(query);
  return options.some((token) => comparable(token.name) === wanted);
}
