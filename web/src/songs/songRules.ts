import { CONCEPT_MAXIMUM_LENGTH, TITLE_MAXIMUM_LENGTH } from '../api/songs';
import { VERSION_NAME_MAXIMUM_LENGTH } from '../api/versions';

// The Song rules the New Song dialog checks before sending, as the API checks them (`SongRules`).

/** A title is one line: line breaks (a pasted paragraph) become spaces. */
export function singleLine(text: string): string {
  return text.replace(/\r\n|\r|\n/g, ' ');
}

function atMost(length: number): string {
  return `Use at most ${length.toLocaleString('en-US')} characters.`;
}

/** The title's error before it is sent, by the API's rule: required, at most 300 after trimming. */
export function titleError(title: string): string | undefined {
  const trimmed = title.trim();
  if (trimmed === '') {
    return 'Enter a title.';
  }
  return trimmed.length > TITLE_MAXIMUM_LENGTH ? atMost(TITLE_MAXIMUM_LENGTH) : undefined;
}

/** The concept's error before it is sent: optional, at most 2,000 once line endings are `\n` and it is trimmed. */
export function conceptError(concept: string): string | undefined {
  const normalised = concept.replace(/\r\n|\r/g, '\n').trim();
  return normalised.length > CONCEPT_MAXIMUM_LENGTH ? atMost(CONCEPT_MAXIMUM_LENGTH) : undefined;
}

/** A Version name's error before it is sent, by the API's rule: optional, at most 200 after trimming. */
export function nameError(name: string): string | undefined {
  return name.trim().length > VERSION_NAME_MAXIMUM_LENGTH
    ? atMost(VERSION_NAME_MAXIMUM_LENGTH)
    : undefined;
}
