import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import {
  createSaveQueue,
  differingFields,
  type ComparedField,
  type FieldValue,
  type Revisioned,
  type SaveResult,
} from '../api/saves';
import { ConflictDialog, type ConflictRow } from './ConflictDialog';

/** A compared field and how the conflict dialog shows a value of it. */
export interface SavedField<T> extends ComparedField<T> {
  show: (value: FieldValue) => ReactNode;
}

/** How a save ended, once any conflict has been resolved. */
export type SaveOutcome =
  /** Stored; the record has been replaced with the saved one. */
  | { kind: 'saved' }
  /** The user chose the current record over their change, which is dropped. */
  | { kind: 'reloaded' }
  /** The user went back to editing; the current record (and its revision) is loaded. */
  | { kind: 'keep-editing' }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'failed' };

type Choice = 'reload' | 'reapply' | 'keep-editing';

/** How many times in a row a save is retried, unasked, on a newer revision that differs in no compared field. */
const SILENT_RETRIES = 5;

/**
 * The shared save helper of the web UI: saves one field of a revisioned record and routes a stale
 * save to the {@link ConflictDialog}. Saves of one record run one after another, each based on the
 * revision the one before it left, so a user never conflicts with their own save in flight.
 *
 * A refused save compares the record it was based on with the current one, field by field. When no
 * compared field differs (or only the saved field, already holding the value being saved) it is
 * retried on the current revision without asking; otherwise the dialog asks the user, and asks
 * again if reapplying meets another change. Render `dialog` once in the page.
 */
export function useRevisionedSave<T extends Revisioned>({
  record,
  onRecord,
  fields,
  send,
  subject,
}: {
  /** The record as the page has it now. */
  record: T;
  /** Takes a newer record: one just saved, or the current one a conflict brought back. */
  onRecord: (record: T) => void;
  fields: readonly SavedField<T>[];
  /** Saves `value` into the field `key`, based on `base`'s revision. */
  send: (base: T, key: string, value: FieldValue) => Promise<SaveResult<T>>;
  /** What the record is, as a sentence names it: "This Song". */
  subject: string;
}): { save: (key: string, value: FieldValue) => Promise<SaveOutcome>; dialog: ReactNode } {
  const latest = useRef(record);
  const options = useRef({ onRecord, fields, send });
  const [enqueue] = useState(createSaveQueue);
  const [conflict, setConflict] = useState<{
    rows: ConflictRow[];
    resolve: (choice: Choice) => void;
  } | null>(null);
  // The rows of the last conflict stay in the dialog while it closes, so it does not empty first.
  const [rows, setRows] = useState<ConflictRow[]>([]);

  useEffect(() => {
    latest.current = record;
  }, [record]);
  useEffect(() => {
    options.current = { onRecord, fields, send };
  }, [onRecord, fields, send]);

  const save = useCallback(
    (key: string, value: FieldValue) =>
      enqueue(async (): Promise<SaveOutcome> => {
        const adopt = (next: T) => {
          latest.current = next;
          options.current.onRecord(next);
        };
        let base = latest.current;
        let silent = 0;
        for (;;) {
          const result = await options.current.send(base, key, value);
          if (result.kind !== 'conflict') {
            if (result.kind === 'saved') {
              adopt(result.record);
              return { kind: 'saved' };
            }
            return result;
          }

          const current = result.current;
          adopt(current);
          const { fields } = options.current;
          const differing = differingFields(fields, base, current, { key, value });
          if (differing.length === 0) {
            silent += 1;
            if (silent > SILENT_RETRIES) {
              return { kind: 'failed' };
            }
            base = current;
            continue;
          }

          const rows = fields.flatMap((field): ConflictRow[] => {
            const changedElsewhere = differing.includes(field);
            const mine = field.key === key;
            if (!changedElsewhere && !mine) {
              return [];
            }
            return [
              {
                key: field.key,
                label: field.label,
                yours: field.show(mine ? value : field.read(base)),
                current: field.show(field.read(current)),
                change: changedElsewhere && mine ? 'both' : mine ? 'yours' : 'elsewhere',
              },
            ];
          });
          const choice = await new Promise<Choice>((resolve) => {
            setRows(rows);
            setConflict({ rows, resolve });
          });
          if (choice === 'reload') {
            return { kind: 'reloaded' };
          }
          if (choice === 'keep-editing') {
            return { kind: 'keep-editing' };
          }
          base = current;
          silent = 0;
        }
      }),
    [enqueue],
  );

  const choose = (choice: Choice) => {
    if (conflict) {
      setConflict(null);
      conflict.resolve(choice);
    }
  };

  const dialog = (
    <ConflictDialog
      opened={conflict !== null}
      subject={subject}
      rows={conflict?.rows ?? rows}
      onReload={() => {
        choose('reload');
      }}
      onReapply={() => {
        choose('reapply');
      }}
      onKeepEditing={() => {
        choose('keep-editing');
      }}
    />
  );

  return { save, dialog };
}
