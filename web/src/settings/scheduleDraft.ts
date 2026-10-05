import { scheduleErrors, type ScheduleSettings } from '../api/backups';

/** A schedule being edited: `keep` is what the number field holds, which may be empty text. */
export type ScheduleDraft = Omit<ScheduleSettings, 'keep'> & { keep: number | string };

export type ScheduleFieldErrors = Partial<Record<keyof ScheduleSettings, string>>;

/** The draft as settings, or undefined with the field errors that stop it being one. */
export function settingsOf(draft: ScheduleDraft): {
  settings: ScheduleSettings | undefined;
  errors: ScheduleFieldErrors;
} {
  const errors = scheduleErrors(draft);
  if (Object.keys(errors).length > 0 || typeof draft.keep !== 'number') {
    return { settings: undefined, errors };
  }
  return { settings: { ...draft, keep: draft.keep }, errors };
}

/** The first message of each field the API refused, keyed by the field's own name (any prefix removed). */
export function apiFieldErrors(errors: Record<string, string[]>, prefix = ''): ScheduleFieldErrors {
  const fields: (keyof ScheduleSettings)[] = ['enabled', 'frequency', 'time', 'keep'];
  const found: ScheduleFieldErrors = {};
  for (const field of fields) {
    const message = errors[`${prefix}${field}`]?.[0];
    if (message !== undefined) {
      found[field] = message;
    }
  }
  return found;
}
