import { VisuallyHidden } from '@mantine/core';
import type { SongMatch } from '../api/songs';
import { excerptParts } from './searchRules';

/**
 * A match's excerpt with the matched words highlighted (#224): each in a `<mark>`, bold as well as
 * on a highlight colour, so it does not rely on colour alone. A screen reader reads the excerpt as
 * one run of text and then which words matched, rather than breaking it at each mark. The text is
 * the user's own and is rendered as text, never as markup.
 */
export function MatchExcerpt({ excerpt }: { excerpt: SongMatch['excerpt'] }) {
  const parts = excerptParts(excerpt.text, excerpt.highlights);
  const matched = [...new Set(parts.filter((part) => part.matched).map((part) => part.text))];
  return (
    <span data-testid="match-excerpt" style={{ overflowWrap: 'anywhere' }}>
      {parts.map((part, index) =>
        part.matched ? (
          <mark
            // The parts never move: the key is their place in the excerpt.
            key={index}
            data-testid="match-highlight"
            style={{
              fontWeight: 700,
              background: 'var(--n8-highlight-background)',
              color: 'var(--n8-highlight-text)',
              borderRadius: 2,
              padding: '0 1px',
            }}
          >
            {part.text}
          </mark>
        ) : (
          <span key={index}>{part.text}</span>
        ),
      )}
      {matched.length > 0 && (
        <>
          {' '}
          <VisuallyHidden>
            {`(matched: ${matched.map((word) => `“${word}”`).join(', ')})`}
          </VisuallyHidden>
        </>
      )}
    </span>
  );
}
