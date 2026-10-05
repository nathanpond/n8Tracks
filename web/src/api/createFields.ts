import { isRecord, useResource } from './songs';

const CREATE_FIELDS_PATH = 'api/v1/suno/create-fields';

/** A Version option's value: text, a whole number, on or off, or null for nothing chosen. */
export type OptionValue = string | number | boolean | null;

/**
 * One field of Suno's Create screen, as the API serves the field inventory: its limits, range,
 * value list, and default are Suno's, so the web app restates none of them. `option` is the key of
 * the Version's `inputs` that stores it (null for one no option stores, and for lyrics and styles,
 * which are the Version's own fields); `help` is Suno's explanation, or null when none was captured.
 */
export interface CreateField {
  key: string;
  label: string;
  tab: string;
  modes: string[];
  type: string;
  values?: string[];
  default?: OptionValue;
  maxLength?: number;
  min?: number;
  max?: number;
  unit?: string;
  condition?: string;
  notes?: string;
  option: string | null;
  help: string | null;
}

/** Suno's Create-screen fields, in Suno's order, and the models a Version may name. */
export interface CreateFields {
  fields: CreateField[];
  models: string[];
}

const isOptional = (value: unknown, type: 'string' | 'number') =>
  value === undefined || typeof value === type;

function isCreateField(value: unknown): value is CreateField {
  return (
    isRecord(value) &&
    typeof value.key === 'string' &&
    typeof value.label === 'string' &&
    typeof value.tab === 'string' &&
    Array.isArray(value.modes) &&
    value.modes.every((mode) => typeof mode === 'string') &&
    typeof value.type === 'string' &&
    (value.values === undefined ||
      (Array.isArray(value.values) && value.values.every((item) => typeof item === 'string'))) &&
    isOptional(value.maxLength, 'number') &&
    isOptional(value.min, 'number') &&
    isOptional(value.max, 'number') &&
    isOptional(value.unit, 'string') &&
    (value.option === null || typeof value.option === 'string') &&
    (value.help === null || typeof value.help === 'string')
  );
}

const acceptCreateFields = (answer: unknown): CreateFields | undefined =>
  isRecord(answer) &&
  Array.isArray(answer.fields) &&
  answer.fields.every(isCreateField) &&
  Array.isArray(answer.models) &&
  answer.models.every((model) => typeof model === 'string')
    ? { fields: answer.fields, models: answer.models }
    : undefined;

/** Suno's Create-screen fields and the model list. */
export function useCreateFields() {
  return useResource(CREATE_FIELDS_PATH, acceptCreateFields);
}

/** The field stored in the Version option `option`, if there is one. */
export function fieldOf(fields: CreateFields, option: string): CreateField | undefined {
  return fields.fields.find((field) => field.option === option);
}

/** The field with the inventory key `key`, if there is one. */
export function fieldByKey(fields: CreateFields, key: string): CreateField | undefined {
  return fields.fields.find((field) => field.key === key);
}
