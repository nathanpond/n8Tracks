import type { CreateField } from '../api/createFields';
import type { SettingValue, SourceSettingsPlan, UnknownReason } from '../api/sourceSettings';
import { choiceLabel } from '../songs/inputs/optionFormat';
import type { SettingChoice } from './SourceSettingsDialog';

// How a plan of a source's settings (#431) is written on the page: what each setting is called, its
// value, and what was filled or not known. Which settings are filled or asked about is the API's.

const OWN_LABELS: Readonly<Record<string, string>> = {
  lyrics: 'Lyrics',
  styles: 'Styles',
  voice: 'Voice',
};

/** The field an option is held for, if any. */
function fieldOf(key: string, fields: readonly CreateField[]): CreateField | undefined {
  return fields.find((candidate) => candidate.option === key);
}

/** What a setting is called on the page; a Speech's or Sound's says whose, as the conflict view does. */
export function settingLabel(key: string, fields: readonly CreateField[]): string {
  const own = OWN_LABELS[key];
  if (own !== undefined) {
    return own;
  }
  const field = fieldOf(key, fields);
  if (field === undefined) {
    return choiceLabel(key);
  }
  return field.tab === 'speech'
    ? `Speech: ${field.label}`
    : field.tab === 'sounds'
      ? `Sound: ${field.label}`
      : field.label;
}

/** Whether a setting is free text (lyrics, styles, a prompt), shown whole in a box that scrolls. */
export function isLongSetting(key: string, fields: readonly CreateField[]): boolean {
  return key === 'lyrics' || key === 'styles' || fieldOf(key, fields)?.type === 'text';
}

/** A setting's value as the page writes it. */
export function settingText(
  key: string,
  value: SettingValue,
  fields: readonly CreateField[],
): string {
  if (value === null) {
    return 'None';
  }
  if (typeof value === 'object') {
    return value.name === '' ? value.personaId : value.name;
  }
  if (typeof value === 'boolean') {
    return value ? 'On' : 'Off';
  }
  if (typeof value === 'number') {
    const unit = fieldOf(key, fields)?.unit;
    return unit === undefined ? String(value) : `${String(value)} ${unit}`;
  }
  if (value.trim() === '') {
    return 'Empty';
  }
  return isLongSetting(key, fields) ? value : choiceLabel(value);
}

/** The prompt's rows: each differing setting with the Version's value and the source's. */
export function choicesOf(
  plan: SourceSettingsPlan,
  fields: readonly CreateField[],
): SettingChoice[] {
  return plan.conflicts.map((conflict) => ({
    key: conflict.key,
    label: settingLabel(conflict.key, fields),
    current: settingText(conflict.key, conflict.versionValue, fields),
    source: settingText(conflict.key, conflict.sourceValue, fields),
    long: isLongSetting(conflict.key, fields),
  }));
}

/** The labels of the empty settings the source fills, in order. */
export function filledLabels(plan: SourceSettingsPlan, fields: readonly CreateField[]): string[] {
  return plan.fills.map((fill) => settingLabel(fill.key, fields));
}

const WHOLE_SOURCE: Partial<Record<UnknownReason, string>> = {
  not_imported: 'n8Tracks does not know the settings of a Suno clip it has not imported',
  song_only: 'n8Tracks does not know which of the Song’s Generations is the source',
  deleted: 'the source is no longer in n8Tracks',
};

/**
 * What the page says about the settings the source's are not known for, or undefined when all are
 * known: the reason once when nothing about the source is known, otherwise each setting by reason.
 */
export function unknownNote(
  plan: SourceSettingsPlan,
  fields: readonly CreateField[],
): string | undefined {
  if (plan.unknown.length === 0) {
    return undefined;
  }
  const reasons = new Set(plan.unknown.map((item) => item.reason));
  const [only] = [...reasons];
  if (reasons.size === 1 && only !== undefined) {
    const whole = WHOLE_SOURCE[only];
    if (whole !== undefined) {
      return `No settings were taken from the source: ${whole}.`;
    }
  }
  const named = (reason: UnknownReason) =>
    plan.unknown
      .filter((item) => item.reason === reason)
      .map((item) => settingLabel(item.key, fields))
      .join(', ');
  const parts: string[] = [];
  if (reasons.has('not_returned')) {
    parts.push(`Suno did not return the source’s ${named('not_returned')}`);
  }
  if (reasons.has('not_valid')) {
    parts.push(`the source’s ${named('not_valid')} cannot be used in n8Tracks`);
  }
  for (const reason of ['not_imported', 'song_only', 'deleted'] as const) {
    if (reasons.has(reason)) {
      parts.push(`${WHOLE_SOURCE[reason] ?? ''} (${named(reason)})`);
    }
  }
  return `Not taken from the source, so left as they were: ${parts.join('; ')}.`;
}

/** What the page says once the source's settings were taken without a question. */
export function appliedNote(
  plan: SourceSettingsPlan,
  fields: readonly CreateField[],
): string | undefined {
  const filled = filledLabels(plan, fields);
  const parts = [
    filled.length > 0 ? `Filled from the source: ${filled.join(', ')}.` : undefined,
    unknownNote(plan, fields),
  ].filter((part): part is string => part !== undefined);
  return parts.length === 0 ? undefined : parts.join(' ');
}
