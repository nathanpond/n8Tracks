import {
  CONCEPT_MAXIMUM_LENGTH,
  SONG_NOTES_MAXIMUM_LENGTH,
  TITLE_MAXIMUM_LENGTH,
} from '../api/songs';
import { VERSION_NAME_MAXIMUM_LENGTH, VERSION_NOTES_MAXIMUM_LENGTH } from '../api/versions';

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

/** Version notes' error before they are sent: optional, at most 10,000 once line endings are `\n` and they are trimmed. */
export function notesError(notes: string): string | undefined {
  const normalised = notes.replace(/\r\n|\r/g, '\n').trim();
  return normalised.length > VERSION_NOTES_MAXIMUM_LENGTH
    ? atMost(VERSION_NOTES_MAXIMUM_LENGTH)
    : undefined;
}

/** A Song's notes as they are saved: line endings as `\n`, trimmed, and null when nothing is left. */
export function normaliseNotes(draft: string): string | null {
  const normalised = draft.replace(/\r\n|\r/g, '\n').trim();
  return normalised === '' ? null : normalised;
}

/** A Song's notes' error before they are sent: optional, at most 10,000 once normalised. */
export function songNotesError(notes: string): string | undefined {
  return (normaliseNotes(notes) ?? '').length > SONG_NOTES_MAXIMUM_LENGTH
    ? atMost(SONG_NOTES_MAXIMUM_LENGTH)
    : undefined;
}
