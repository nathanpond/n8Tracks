import { Group, UnstyledButton } from '@mantine/core';
import { useRef, type KeyboardEvent } from 'react';
import { RATING_MAXIMUM } from '../api/generations';
import { ratingForKey } from './evaluationRules';

const STARS = Array.from({ length: RATING_MAXIMUM }, (_, index) => index + 1);

/** "1 star", "4 stars". */
function starsLabel(stars: number): string {
  return stars === 1 ? '1 star' : `${String(stars)} stars`;
}

/**
 * One to five stars as a radio group named `label`: each star is a radio ("4 stars") and the
 * checked one is the rating, so a screen reader announces the value as focus moves. Clicking a
 * star sets that rating, and clicking (or pressing Space or Enter on) the current one clears it.
 * The arrow keys change the rating at once, one star at a time, and focus follows it; Home and End
 * go to one and five stars; Delete clears it. One tab stop: the checked star, or the first.
 */
export function StarRating({
  value,
  label,
  onChange,
  size = 'md',
}: {
  value: number | null;
  label: string;
  onChange: (rating: number | null) => void;
  size?: 'sm' | 'md';
}) {
  const buttons = useRef<(HTMLButtonElement | null)[]>([]);

  const choose = (rating: number | null) => {
    onChange(rating);
    buttons.current[(rating ?? 1) - 1]?.focus();
  };

  const onKeyDown = (event: KeyboardEvent<HTMLButtonElement>) => {
    const next = ratingForKey(event.key, value);
    if (next === undefined) {
      return;
    }
    event.preventDefault();
    if (next !== value) {
      choose(next);
    }
  };

  return (
    <Group gap={0} wrap="nowrap" role="radiogroup" aria-label={label} data-testid="star-rating">
      {STARS.map((count) => {
        const checked = value === count;
        const filled = value !== null && count <= value;
        return (
          <UnstyledButton
            key={count}
            ref={(element: HTMLButtonElement | null) => {
              buttons.current[count - 1] = element;
            }}
            role="radio"
            aria-checked={checked}
            aria-label={starsLabel(count)}
            tabIndex={checked || (value === null && count === 1) ? 0 : -1}
            onClick={() => {
              choose(checked ? null : count);
            }}
            onKeyDown={onKeyDown}
            fz={size === 'sm' ? 'md' : 'xl'}
            lh={1}
            px={2}
            py={4}
            c={filled ? undefined : 'var(--n8-color-secondary-text)'}
            style={{ borderRadius: 'var(--mantine-radius-sm)' }}
          >
            <span aria-hidden="true">{filled ? '★' : '☆'}</span>
          </UnstyledButton>
        );
      })}
    </Group>
  );
}
