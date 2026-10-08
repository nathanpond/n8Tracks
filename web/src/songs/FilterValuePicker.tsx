import { Group, MultiSelect, Select } from '@mantine/core';
import { useEffect, useState } from 'react';
import { readFilterValues, type FilterValue, type FilterValueKind } from '../api/songs';
import { paletteColour } from '../theme/palette';

/** A fixed choice a picker offers before the catalog's values, such as "No Genre". */
export interface ExtraOption {
  value: string;
  label: string;
}

/** The values offered for what is typed, from the API (it matches on word beginnings). */
function useSuggestions(kind: FilterValueKind, search: string): FilterValue[] {
  const [suggested, setSuggested] = useState<FilterValue[]>([]);
  useEffect(() => {
    const controller = new AbortController();
    void readFilterValues(kind, { query: search }, controller.signal).then((found) => {
      if (!controller.signal.aborted && found !== undefined) {
        setSuggested(found);
      }
    });
    return () => {
      controller.abort();
    };
  }, [kind, search]);
  return suggested;
}

interface PickerProps {
  kind: FilterValueKind;
  label: string;
  /** What the empty picker says, such as "Any Genre". */
  placeholder: string;
  /** What it says when nothing matches what is typed. */
  nothingFound: string;
  /** The names of the chosen values (from {@link useFilterValueNames}). */
  names: ReadonlyMap<string, FilterValue> | undefined;
}

/** How one option is drawn: a Tag's colour beside its name. */
function optionContent(option: { label: string }, colour: string | undefined) {
  return (
    <Group gap={6} wrap="nowrap">
      {colour !== undefined && (
        <span
          aria-hidden="true"
          style={{
            display: 'inline-block',
            width: 8,
            height: 8,
            borderRadius: '50%',
            background: paletteColour(colour),
            flex: 'none',
          }}
        />
      )}
      {option.label}
    </Group>
  );
}

/** The options: the extras, then the chosen values (named), then the suggestions not chosen. */
function optionsOf(
  extras: ExtraOption[],
  selected: readonly string[],
  names: ReadonlyMap<string, FilterValue> | undefined,
  suggested: FilterValue[],
) {
  const extraValues = new Set(extras.map((extra) => extra.value));
  return [
    ...extras,
    ...selected
      .filter((id) => !extraValues.has(id))
      .map((id) => ({
        value: id,
        label: names?.get(id)?.name ?? suggested.find((value) => value.id === id)?.name ?? id,
      })),
    ...suggested
      .filter((value) => !selected.includes(value.id))
      .map((value) => ({ value: value.id, label: value.name })),
  ];
}

/**
 * A filter picker of any number of values of `kind` (#225), searched on the server as the user
 * types, so a large catalog is never loaded whole. `extras` come first (such as "No Genre").
 */
export function MultiFilterValuePicker({
  kind,
  label,
  placeholder,
  nothingFound,
  names,
  extras = [],
  selected,
  onChange,
}: PickerProps & {
  extras?: ExtraOption[];
  selected: string[];
  onChange: (values: string[]) => void;
}) {
  const [search, setSearch] = useState('');
  const suggested = useSuggestions(kind, search);
  const colours = new Map<string, string>();
  for (const value of [...suggested, ...(names?.values() ?? [])]) {
    if (value.colour !== undefined) {
      colours.set(value.id, value.colour);
    }
  }

  return (
    <MultiSelect
      label={label}
      placeholder={selected.length === 0 ? placeholder : undefined}
      data={optionsOf(extras, selected, names, suggested)}
      value={selected}
      onChange={onChange}
      renderOption={({ option }) => optionContent(option, colours.get(option.value))}
      searchable
      searchValue={search}
      onSearchChange={setSearch}
      // The API has already matched what is typed; the list is shown as it answered.
      filter={({ options }) => options}
      clearable
      clearButtonProps={{ 'aria-label': `Clear the ${label} filter` }}
      nothingFoundMessage={nothingFound}
      w={{ base: '100%', sm: 280 }}
      comboboxProps={{ withinPortal: false, hideDetached: false }}
    />
  );
}

/** A filter picker of one value of `kind` (#225), searched on the server as the user types. */
export function SingleFilterValuePicker({
  kind,
  label,
  placeholder,
  nothingFound,
  names,
  selected,
  onChange,
}: PickerProps & {
  selected: string | undefined;
  onChange: (value: string | undefined) => void;
}) {
  const [search, setSearch] = useState('');
  const suggested = useSuggestions(kind, search);
  const chosen = selected === undefined ? [] : [selected];

  return (
    <Select
      label={label}
      placeholder={placeholder}
      data={optionsOf([], chosen, names, suggested)}
      value={selected ?? null}
      onChange={(value) => {
        onChange(value ?? undefined);
      }}
      searchable
      onSearchChange={(value) => {
        // Showing the chosen value's name is not a search for it.
        setSearch(value === (names?.get(selected ?? '')?.name ?? '') ? '' : value);
      }}
      filter={({ options }) => options}
      clearable
      clearButtonProps={{ 'aria-label': `Clear the ${label} filter` }}
      nothingFoundMessage={nothingFound}
      w={{ base: '100%', sm: 280 }}
      comboboxProps={{ withinPortal: false, hideDetached: false }}
    />
  );
}
