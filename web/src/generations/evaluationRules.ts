import { COMMENT_MAXIMUM_LENGTH, RATING_MAXIMUM, type Generation } from '../api/generations';

/** What a comment's text box says about its length: the count, and the error when it is too long. */
export function commentLength(draft: string): { counter: string; error: string | undefined } {
  const length = draft.trim().length;
  const over = length - COMMENT_MAXIMUM_LENGTH;
  return {
    counter: `${length.toLocaleString('en-US')} of ${COMMENT_MAXIMUM_LENGTH.toLocaleString('en-US')} characters`,
    error:
      over > 0
        ? `This comment is ${over.toLocaleString('en-US')} ${over === 1 ? 'character' : 'characters'} too long. Shorten it to save it.`
        : undefined,
  };
}

/** Whether `draft` can be saved as a comment: some text, and not too long. */
export function canSaveComment(draft: string): boolean {
  const length = draft.trim().length;
  return length > 0 && length <= COMMENT_MAXIMUM_LENGTH;
}

/**
 * The rating a key press asks for, from `value`: the arrow keys go up or down one star (down from
 * one star clears it), Home and End go to one and five stars, Delete and Backspace clear it.
 * Undefined for any other key.
 */
export function ratingForKey(key: string, value: number | null): number | null | undefined {
  switch (key) {
    case 'ArrowRight':
    case 'ArrowUp':
      return Math.min(RATING_MAXIMUM, (value ?? 0) + 1);
    case 'ArrowLeft':
    case 'ArrowDown':
      return value === null || value <= 1 ? null : value - 1;
    case 'Home':
      return 1;
    case 'End':
      return RATING_MAXIMUM;
    case 'Delete':
    case 'Backspace':
      return null;
    default:
      return undefined;
  }
}

/** The label of the control that archives or reactivates a Generation. */
export function stateActionLabel(generation: Generation): string {
  return generation.state === 'archived' ? 'Reactivate' : 'Archive';
}

/** The label of the control that selects a Generation for the Song, or clears that. */
export function selectionActionLabel(generation: Generation): string {
  return generation.isSelected ? 'Clear the selection' : 'Select for the Song';
}
