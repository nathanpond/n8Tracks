import { apiFetch } from './client';
import { genreNameError, genreNameKey, normaliseGenreName } from './genres';
import { body, isErrorMap, isRecord, useResource } from './songs';

const TAGS_PATH = 'api/v1/tags';

/** A Tag of the user's list, as the API answers it: its palette colour and how many Songs have it. */
export interface Tag {
  id: string;
  name: string;
  /** The name of one of the twelve palette colours (`stateColours`). */
  colour: string;
  songCount: number;
}

export function isTag(value: unknown): value is Tag {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.name === 'string' &&
    typeof value.colour === 'string' &&
    typeof value.songCount === 'number'
  );
}

const acceptTags = (answer: unknown) =>
  isRecord(answer) && Array.isArray(answer.items) && answer.items.every(isTag)
    ? answer.items
    : undefined;

/** Every Tag, alphabetically, with colours and Song counts. */
export function useTags() {
  return useResource(TAGS_PATH, acceptTags);
}

/** A Tag name as the API stores it: the Genre rule (trimmed, inner white space as one space). */
export const normaliseTagName = normaliseGenreName;

/** What Tag names are compared by, as the API compares them: normalised, NFC, ignoring case. */
export const tagNameKey = genreNameKey;

/** A new Tag name's error before it is sent, by the API's rule: 1 to 50 once normalised. */
export const tagNameError = genreNameError;

/** How creating a Tag ended. An existing Tag with the name (in any letter case) is `created` too. */
export type CreateTagResult =
  | { kind: 'created'; tag: Tag }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'failed' };

/** Creates a Tag in the next palette colour, or answers the one that already has the name. */
export async function createTag(name: string): Promise<CreateTagResult> {
  try {
    const response = await apiFetch(TAGS_PATH, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name }),
    });
    const answer = await body(response);
    if ((response.status === 201 || response.status === 200) && isTag(answer)) {
      return { kind: 'created', tag: answer };
    }
    if (
      response.status === 422 &&
      isRecord(answer) &&
      answer.code === 'validation_failed' &&
      isErrorMap(answer.errors)
    ) {
      return { kind: 'invalid', errors: answer.errors };
    }
    return { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}
