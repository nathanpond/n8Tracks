import { Stack, Textarea, TextInput } from '@mantine/core';
import { useState, type ReactNode } from 'react';
import type { FieldValue } from '../api/saves';
import { saveError } from './useInPlaceEdit';
import type { SaveOutcome } from './useRevisionedSave';

/** Saves one field of a record through the shared save helper (`useRevisionedSave`'s `save`). */
export type Save = (key: string, value: FieldValue) => Promise<SaveOutcome>;

/**
 * One text field of a record (an Album, a Playlist), saved on its own when it loses focus (or, on
 * one line, on Enter): checked first by the client rule, sent only when its normalised value
 * differs from the record's. Mount it afresh (by key) each time the record's value changes, so it
 * always starts from what is stored.
 */
export function SavedTextField({
  field,
  label,
  description,
  value,
  check,
  normalise,
  save,
  multiline = false,
  required = false,
  children,
}: {
  field: string;
  label: string;
  description: string;
  value: string | null;
  check: (draft: string) => string | undefined;
  normalise: (draft: string) => FieldValue;
  save: Save;
  multiline?: boolean;
  required?: boolean;
  children?: ReactNode;
}) {
  const [draft, setDraft] = useState(value ?? '');
  const [error, setError] = useState<string>();
  const [saving, setSaving] = useState(false);

  const commit = async () => {
    const problem = check(draft);
    if (problem !== undefined) {
      setError(problem);
      return;
    }
    const next = normalise(draft);
    if (next === value) {
      setError(undefined);
      return;
    }
    setSaving(true);
    const outcome = await save(field, next);
    setSaving(false);
    setError(saveError(outcome, field));
  };

  const common = {
    label,
    description,
    required,
    value: draft,
    error,
    'aria-invalid': error !== undefined,
    readOnly: saving,
    onBlur: () => {
      void commit();
    },
  };
  return (
    <Stack gap={4}>
      {multiline ? (
        <Textarea
          {...common}
          rows={4}
          resize="vertical"
          onChange={(event) => {
            setDraft(event.currentTarget.value);
          }}
        />
      ) : (
        <TextInput
          {...common}
          onChange={(event) => {
            setDraft(event.currentTarget.value);
          }}
          onKeyDown={(event) => {
            if (event.key === 'Enter') {
              event.preventDefault();
              void commit();
            }
          }}
        />
      )}
      {children}
    </Stack>
  );
}
