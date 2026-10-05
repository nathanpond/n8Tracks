import type { ReactNode } from 'react';
import {
  fieldOf,
  type CreateField,
  type CreateFields,
  type OptionValue,
} from '../../api/createFields';
import type { VersionOptions } from '../../api/versions';
import {
  ChoiceControl,
  ModelControl,
  RangeControl,
  StepsControl,
  TextControl,
  ToggleControl,
} from './OptionControls';

/** What every kind's options take: the inventory, the options, where a change goes, and whether they may change. */
export interface KindOptionsProps {
  fields: CreateFields;
  options: VersionOptions;
  onOption: (key: string, value: OptionValue) => void;
  /** A frozen Version: every value shown, none can change. */
  readOnly: boolean;
  /** The kind selector, drawn first in the kind's own header row. */
  kindControl: ReactNode;
}

/** An option's value read as text, a number, or on/off; the field's default when it holds another type. */
export function reader(options: VersionOptions) {
  return {
    text: (key: string): string => {
      const value = options[key];
      return typeof value === 'string' ? value : '';
    },
    choice: (key: string): string | null => {
      const value = options[key];
      return typeof value === 'string' ? value : null;
    },
    number: (key: string, field: CreateField): number => {
      const value = options[key];
      return typeof value === 'number'
        ? value
        : typeof field.default === 'number'
          ? field.default
          : 0;
    },
    optionalNumber: (key: string): number | null => {
      const value = options[key];
      return typeof value === 'number' ? value : null;
    },
    flag: (key: string): boolean => options[key] === true,
  };
}

/**
 * The option controls of one kind's panel, each found by the Version option it stores and drawn
 * from its field of the inventory; an option the inventory has no field for draws nothing.
 */
export function optionParts({ fields, options, onOption, readOnly }: KindOptionsProps) {
  const read = reader(options);
  const field = (option: string) => fieldOf(fields, option);
  const set = (option: string) => (value: OptionValue) => {
    onOption(option, value);
  };

  return {
    read,
    field,
    text: (option: string, multiline = false) => {
      const found = field(option);
      return (
        found && (
          <TextControl
            field={found}
            value={read.text(option)}
            onChange={set(option)}
            readOnly={readOnly}
            multiline={multiline}
          />
        )
      );
    },
    range: (option: string) => {
      const found = field(option);
      return (
        found && (
          <RangeControl
            field={found}
            value={read.number(option, found)}
            onChange={set(option)}
            readOnly={readOnly}
          />
        )
      );
    },
    toggle: (option: string) => {
      const found = field(option);
      return (
        found && (
          <ToggleControl
            field={found}
            value={read.flag(option)}
            onChange={set(option)}
            readOnly={readOnly}
          />
        )
      );
    },
    steps: (option: string) => {
      const found = field(option);
      return (
        found && (
          <StepsControl
            field={found}
            value={read.text(option)}
            onChange={set(option)}
            readOnly={readOnly}
          />
        )
      );
    },
    /** A choice; `noneLabel` offers null (for a field whose default is null), otherwise null is never sent. */
    choice: (
      option: string,
      extra: { noneLabel?: string; labels?: Readonly<Record<string, string>> } = {},
    ) => {
      const found = field(option);
      return (
        found && (
          <ChoiceControl
            field={found}
            value={read.choice(option)}
            onChange={(value) => {
              if (value !== null || extra.noneLabel !== undefined) {
                onOption(option, value);
              }
            }}
            readOnly={readOnly}
            noneLabel={extra.noneLabel}
            labels={extra.labels}
          />
        )
      );
    },
    model: (option: string) => {
      const found = field(option);
      return (
        found && (
          <ModelControl
            field={found}
            models={fields.models}
            value={read.choice(option)}
            onChange={set(option)}
            readOnly={readOnly}
          />
        )
      );
    },
  };
}
