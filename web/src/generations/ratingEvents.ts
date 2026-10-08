import { generationOf, type Generation } from '../api/generations';

/**
 * The window event a saved rating sends (#220), so a rating made in the player bar shows on the Song
 * page at once, and one made on the Song page reaches the bar's copy (with the revision to send next).
 */
export const GENERATION_RATED_EVENT = 'n8tracks:generation-rated';

/** Says that `generation` was saved with a new rating, as n8Tracks answered it. */
export function announceRated(generation: Generation): void {
  window.dispatchEvent(new CustomEvent<Generation>(GENERATION_RATED_EVENT, { detail: generation }));
}

/** The Generation a rating event carries, or undefined when the event is not one. */
export function ratedGenerationOf(event: Event): Generation | undefined {
  return event instanceof CustomEvent ? generationOf(event.detail) : undefined;
}
