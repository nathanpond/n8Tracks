import { useRef, useState } from 'react';
import type { FieldValue } from '../api/saves';
import type { SaveOutcome } from './useRevisionedSave';

// Editing one field of a record in place, shared by the Song header and the Version details.

export const SAVE_FAILED_MESSAGE =
  'Not saved: n8Tracks did not answer as expected. Check that it is running and try again.';

/** The message a save that did not go through leaves on its field. */
export function saveError(outcome: SaveOutcome, field: string): string | undefined {
  switch (outcome.kind) {
    case 'invalid':
      return outcome.errors[field]?.join(' ') ?? SAVE_FAILED_MESSAGE;
    case 'failed':
      return SAVE_FAILED_MESSAGE;
    default:
      return undefined;
  }
}

/** Focuses a field as it opens: the user just asked to edit it, so their typing goes there. */
export function focusOnMount(element: HTMLElement | null) {
  element?.focus();
}

/**
 * Editing one text field in place. `start` opens it with the current value; `commit` (Enter or
 * blur) checks the draft, sends it unless it changes nothing, and closes it once saved or once the
 * user took the current record instead; `cancel` (Escape) closes it unsaved. A refused or failed
 * save keeps it open with the draft and an error. A blur while a save is in flight (the conflict
 * dialog taking focus) is ignored.
 */
export function useInPlaceEdit({
  field,
  current,
  normalise,
  check,
  save,
}: {
  field: string;
  current: FieldValue;
  normalise: (draft: string) => FieldValue;
  check: (draft: string) => string | undefined;
  save: (key: string, value: FieldValue) => Promise<SaveOutcome>;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');
  const [error, setError] = useState<string | undefined>();
  const [saving, setSaving] = useState(false);
  const open = useRef(false);
  const busy = useRef(false);

  const start = () => {
    open.current = true;
    setDraft(current ?? '');
    setError(undefined);
    setEditing(true);
  };

  const close = () => {
    open.current = false;
    setEditing(false);
    setError(undefined);
  };

  const commit = async () => {
    if (!open.current || busy.current) {
      return;
    }
    const problem = check(draft);
    if (problem !== undefined) {
      setError(problem);
      return;
    }
    const value = normalise(draft);
    if (value === current) {
      close();
      return;
    }

    busy.current = true;
    setSaving(true);
    setError(undefined);
    const outcome = await save(field, value);
    busy.current = false;
    setSaving(false);
    if (outcome.kind === 'saved' || outcome.kind === 'reloaded') {
      close();
    } else {
      setError(saveError(outcome, field));
    }
  };

  const cancel = () => {
    if (!busy.current) {
      close();
    }
  };

  return { editing, draft, setDraft, error, saving, start, commit, cancel };
}
