import {
  Group,
  NativeSelect,
  NumberInput,
  SegmentedControl,
  Slider,
  Stack,
  Switch,
  Text,
  TextInput,
  Textarea,
} from '@mantine/core';
import { useId } from 'react';
import type { CreateField } from '../../api/createFields';
import { formatCount } from '../../editor/counts';
import { choiceLabel, formatDuration, spokenDuration } from './optionFormat';

// The controls of a Version's Suno options, each driven by its field of the inventory as the API
// serves it (`GET /api/v1/suno/create-fields`): the label, Suno's explanation (`help`), and every
// limit, range, and list come from there, so nothing here restates Suno's rules. `readOnly` (a
// frozen Version) shows the value without letting it change.

/** What every option control takes: the field, the value, where a change goes, and whether it may change. */
interface ControlProps<T> {
  field: CreateField;
  value: T;
  onChange: (value: T) => void;
  readOnly: boolean;
}

/** A value of a range in its unit, as the page writes it: `80%`, `2:00`, or the number. */
function formatRange(field: CreateField, value: number): string {
  switch (field.unit) {
    case 'percent':
      return `${String(value)}%`;
    case 'seconds':
      return formatDuration(value);
    default:
      return String(value);
  }
}

/** A value of a range as a screen reader announces it: "80 percent", "2 minutes". */
function spokenRange(field: CreateField, value: number): string {
  switch (field.unit) {
    case 'percent':
      return `${String(value)} percent`;
    case 'seconds':
      return spokenDuration(value);
    default:
      return String(value);
  }
}

/** A control's heading: its label, the value shown beside it, and Suno's explanation under it. */
function ControlLabel({
  id,
  helpId,
  field,
  label,
  shown,
}: {
  id: string;
  helpId: string;
  field: CreateField;
  label?: string;
  shown?: string;
}) {
  return (
    <Stack gap={2}>
      <Group gap="xs" justify="space-between">
        <Text id={id} size="sm" fw={500}>
          {label ?? field.label}
        </Text>
        {shown !== undefined && (
          <Text size="sm" aria-hidden="true">
            {shown}
          </Text>
        )}
      </Group>
      {field.help !== null && (
        <Text id={helpId} size="xs" c="var(--n8-color-secondary-text)">
          {field.help}
        </Text>
      )}
    </Stack>
  );
}

/**
 * A range (Weirdness, Style Influence, a custom duration) as a slider between the field's `min` and
 * `max`, operable with the arrow keys, Page Up and Down, Home, and End; the value is shown beside
 * the label and announced in the field's unit.
 */
export function RangeControl({
  field,
  value,
  onChange,
  readOnly,
  label,
}: ControlProps<number> & { label?: string }) {
  const id = useId();
  const helpId = `${id}-help`;
  const min = field.min ?? 0;
  const max = field.max ?? 100;
  const name = label ?? field.label;
  return (
    <Stack gap={6}>
      <ControlLabel
        id={id}
        helpId={helpId}
        field={field}
        label={label}
        shown={formatRange(field, value)}
      />
      <Slider
        min={min}
        max={max}
        step={1}
        value={value}
        onChange={onChange}
        disabled={readOnly}
        label={(current) => formatRange(field, current)}
        thumbLabel={name}
        thumbValueText={(current) => spokenRange(field, current)}
        thumbProps={{ 'aria-describedby': field.help === null ? undefined : helpId }}
        marks={[
          { value: min, label: formatRange(field, min) },
          { value: max, label: formatRange(field, max) },
        ]}
        mb="lg"
        data-option={field.option ?? undefined}
      />
    </Stack>
  );
}

/**
 * A choice Suno shows as a slider of steps (Variety): one step per value of the field's list, in
 * its order, announced by name.
 */
export function StepsControl({ field, value, onChange, readOnly }: ControlProps<string>) {
  const id = useId();
  const helpId = `${id}-help`;
  const values = field.values ?? [];
  const index = Math.max(values.indexOf(value), 0);
  const nameAt = (step: number) => choiceLabel(values[step] ?? '');
  return (
    <Stack gap={6}>
      <ControlLabel id={id} helpId={helpId} field={field} shown={nameAt(index)} />
      <Slider
        min={0}
        max={Math.max(values.length - 1, 0)}
        step={1}
        value={index}
        onChange={(step) => {
          const chosen = values[step];
          if (chosen !== undefined) {
            onChange(chosen);
          }
        }}
        disabled={readOnly}
        label={nameAt}
        thumbLabel={field.label}
        thumbValueText={nameAt}
        thumbProps={{ 'aria-describedby': field.help === null ? undefined : helpId }}
        marks={values.map((choice, step) => ({ value: step, label: choiceLabel(choice) }))}
        restrictToMarks
        mb="lg"
        data-option={field.option ?? undefined}
      />
    </Stack>
  );
}

/**
 * An on-or-off field (Max Mode, Personalize) as a switch. Suno's explanation is outside the
 * switch's label (Mantine would put it inside, into the switch's name) and describes it.
 */
export function ToggleControl({ field, value, onChange, readOnly }: ControlProps<boolean>) {
  const helpId = `${useId()}-help`;
  return (
    <Stack gap={2}>
      <Switch
        label={field.label}
        aria-describedby={field.help === null ? undefined : helpId}
        checked={value}
        disabled={readOnly}
        onChange={(event) => {
          onChange(event.currentTarget.checked);
        }}
        data-option={field.option ?? undefined}
      />
      {field.help !== null && (
        <Text id={helpId} size="xs" c="var(--n8-color-secondary-text)">
          {field.help}
        </Text>
      )}
    </Stack>
  );
}

/** The value a choice with nothing chosen is held as in a segmented control (it becomes part of an element ID, so plain characters only). */
const NONE = '__none__';

/**
 * A choice (Vocal Gender, Duration's Auto or Custom, the kind, the mode) as a group of radio
 * buttons, one per value of the field's list. `noneLabel` adds a first one for null (nothing
 * chosen), for a field whose default is null. `disabledValues` are shown but cannot be chosen.
 */
export function ChoiceControl({
  field,
  value,
  onChange,
  readOnly,
  noneLabel,
  labels = {},
  disabledValues = [],
  label,
}: ControlProps<string | null> & {
  noneLabel?: string;
  labels?: Readonly<Record<string, string>>;
  disabledValues?: readonly string[];
  label?: string;
}) {
  const id = useId();
  const helpId = `${id}-help`;
  const values = field.values ?? [];
  const data = [
    ...(noneLabel === undefined ? [] : [{ value: NONE, label: noneLabel }]),
    ...values.map((choice) => ({
      value: choice,
      label: labels[choice] ?? choiceLabel(choice),
      disabled: disabledValues.includes(choice),
    })),
  ];
  return (
    <Stack gap={6}>
      <ControlLabel id={id} helpId={helpId} field={field} label={label} />
      <SegmentedControl
        aria-labelledby={id}
        aria-describedby={field.help === null ? undefined : helpId}
        data={data}
        value={value ?? NONE}
        readOnly={readOnly}
        onChange={(chosen) => {
          onChange(chosen === NONE ? null : chosen);
        }}
        data-option={field.option ?? undefined}
      />
    </Stack>
  );
}

/**
 * Text (the Simple prompt, Exclude styles, Suno's title) up to the field's `maxLength`, counted as
 * it is typed. Text over the limit can be typed; the message says so, and the page does not send it.
 */
export function TextControl({
  field,
  value,
  onChange,
  readOnly,
  multiline = false,
  description,
}: ControlProps<string> & { multiline?: boolean; description?: string }) {
  const limit = field.maxLength;
  const excess = limit === undefined ? 0 : value.length - limit;
  const counted =
    limit === undefined
      ? undefined
      : `${formatCount(value.length)} / ${formatCount(limit)} characters`;
  const props = {
    label: field.label,
    description: [field.help ?? description, counted].filter(Boolean).join(' · ') || undefined,
    value,
    readOnly,
    error:
      excess > 0
        ? `Over the limit by ${formatCount(excess)} ${excess === 1 ? 'character' : 'characters'}. Shorten the text to save.`
        : undefined,
    'data-option': field.option ?? undefined,
  };
  return multiline ? (
    <Textarea
      {...props}
      rows={3}
      resize="vertical"
      onChange={(event) => {
        onChange(event.currentTarget.value.replace(/\r\n|\r/g, '\n'));
      }}
    />
  ) : (
    <TextInput
      {...props}
      onChange={(event) => {
        onChange(event.currentTarget.value.replace(/\r\n|\r/g, '\n'));
      }}
    />
  );
}

/**
 * The model picker: the models offered for a new choice, in the list's order, and "Not chosen" for
 * a Version with none (the field's null). A Version whose model is no longer offered (retired in
 * Settings → Suno) keeps it: it is shown first, marked retired.
 */
export function ModelControl({
  field,
  models,
  value,
  onChange,
  readOnly,
}: ControlProps<string | null> & { models: readonly string[] }) {
  const retired = value !== null && !models.includes(value);
  return (
    <NativeSelect
      label={field.label}
      description={field.help ?? undefined}
      data={[
        { value: '', label: 'Not chosen' },
        ...(retired ? [{ value, label: `${value} (retired)` }] : []),
        ...models.map((model) => ({ value: model, label: model })),
      ]}
      value={value ?? ''}
      disabled={readOnly}
      onChange={(event) => {
        const chosen = event.currentTarget.value;
        onChange(chosen === '' ? null : chosen);
      }}
      data-option={field.option ?? undefined}
    />
  );
}

/**
 * A whole number that may be left empty (a Sound's BPM, empty for Auto), typed between the field's
 * `min` and `max`; a number outside them is not taken. Empty is null.
 */
export function NumberControl({
  field,
  value,
  onChange,
  readOnly,
  emptyLabel,
}: ControlProps<number | null> & { emptyLabel: string }) {
  const min = field.min ?? 0;
  const max = field.max ?? Number.MAX_SAFE_INTEGER;
  return (
    <NumberInput
      label={field.label}
      description={
        field.help ??
        `${formatCount(min)} to ${formatCount(max)}; leave it empty for ${emptyLabel}.`
      }
      placeholder={emptyLabel}
      min={min}
      max={max}
      allowDecimal={false}
      allowNegative={false}
      clampBehavior="strict"
      value={value ?? ''}
      readOnly={readOnly}
      onChange={(next) => {
        if (next === '') {
          onChange(null);
        } else if (
          typeof next === 'number' &&
          Number.isInteger(next) &&
          next >= min &&
          next <= max
        ) {
          onChange(next);
        }
      }}
      data-option={field.option ?? undefined}
    />
  );
}
