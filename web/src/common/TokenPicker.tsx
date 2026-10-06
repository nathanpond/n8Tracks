import { Combobox, Loader, Pill, PillsInput, Text, useCombobox } from '@mantine/core';
import { useId } from '@mantine/hooks';
import { useEffect, useState, type KeyboardEvent } from 'react';
import { comparable, hasExactMatch, suggestTokens, type Token } from './tokenMatching';

/** The option value that stands for "create a new one with the typed name". */
const CREATE = 'create-new';

/** A token's colour as a small dot before its name; decorative, since the name is always written. */
function Swatch({ colour }: { colour: string | undefined }) {
  if (colour === undefined) {
    return null;
  }
  return (
    <span
      aria-hidden="true"
      data-swatch={colour}
      style={{
        display: 'inline-block',
        width: 8,
        height: 8,
        borderRadius: '50%',
        background: colour,
        marginInlineEnd: 6,
        verticalAlign: 'middle',
        flex: 'none',
      }}
    />
  );
}

/**
 * A picker for a list the user owns (Genres, Tags; Artists and collections later): the
 * chosen tokens as pills, and a text field that suggests the others as the user types, by the
 * start of any word in their name, ignoring case (up to ten, leaving out the chosen). When no
 * name matches the typed text exactly, a last option creates one with that name.
 *
 * Keyboard: type to filter, ↓/↑ to move through the suggestions, Enter to choose, Escape to close
 * them; Backspace in the empty field removes the last token, and each token's remove button can be
 * reached with Tab. While `busy` (a change being saved), the field is read only and choices wait.
 *
 * `colourOf`, when given, draws each token's colour (a CSS colour) as a swatch before its name, in
 * the chosen tokens and in the suggestions.
 */
export function TokenPicker<T extends Token>({
  label,
  noun,
  chosen,
  options,
  onAdd,
  onCreate,
  onRemove,
  nameError,
  busy = false,
  error,
  description,
  colourOf,
}: {
  label: string;
  /** What one token is, as a sentence names it: "Genre". */
  noun: string;
  /** The tokens held, in the order shown. */
  chosen: readonly Token[];
  /** Every token there is to choose from (chosen ones included); undefined while it loads. */
  options: readonly T[] | undefined;
  onAdd: (token: T) => void;
  /** Called with the typed name, as typed, when the user asks to create a new token. */
  onCreate: (name: string) => void;
  onRemove: (token: Token) => void;
  /** Why the typed name cannot be created, or undefined when it can. */
  nameError: (name: string) => string | undefined;
  busy?: boolean;
  error?: string;
  description?: string;
  colourOf?: (token: Token) => string | undefined;
}) {
  const id = useId();
  const [search, setSearch] = useState('');
  const combobox = useCombobox({
    onDropdownClose: () => {
      combobox.resetSelectedOption();
    },
  });

  // As the user types, the best suggestion (or the create option) is the one Enter takes.
  const { selectFirstOption, resetSelectedOption } = combobox;
  useEffect(() => {
    if (search.trim() === '') {
      resetSelectedOption();
    } else {
      selectFirstOption();
    }
  }, [search, options, selectFirstOption, resetSelectedOption]);

  const all = options ?? [];
  const suggestions = suggestTokens(all, chosen, search);
  const typed = comparable(search) !== '';
  const offerCreate = typed && !hasExactMatch([...all, ...chosen], search);
  const createProblem = offerCreate ? nameError(search) : undefined;

  const submit = (value: string) => {
    if (busy) {
      return;
    }
    if (value === CREATE) {
      if (createProblem === undefined) {
        onCreate(search);
        setSearch('');
        combobox.closeDropdown();
      }
      return;
    }
    const token = all.find((candidate) => candidate.id === value);
    if (token !== undefined) {
      onAdd(token);
      setSearch('');
      combobox.closeDropdown();
    }
  };

  const keys = (event: KeyboardEvent<HTMLInputElement>) => {
    const last = chosen.at(-1);
    if (event.key === 'Backspace' && search === '' && last !== undefined && !busy) {
      event.preventDefault();
      onRemove(last);
    }
  };

  return (
    // Not hidden when the field scrolls out of view: the suggestions are short-lived, and the check
    // hides them at once where nothing is laid out (jsdom).
    <Combobox store={combobox} onOptionSubmit={submit} withinPortal={false} hideDetached={false}>
      <Combobox.DropdownTarget>
        <PillsInput
          id={id}
          label={label}
          description={description}
          error={error}
          onClick={() => {
            combobox.openDropdown();
          }}
          rightSection={busy || options === undefined ? <Loader size="xs" /> : undefined}
        >
          <Pill.Group>
            {chosen.map((token) => (
              <Pill
                key={token.id}
                withRemoveButton
                onRemove={() => {
                  if (!busy) {
                    onRemove(token);
                  }
                }}
                removeButtonProps={{
                  'aria-label': `Remove ${noun} ${token.name}`,
                  'aria-hidden': false,
                  tabIndex: 0,
                  // Removing a token does not also open the suggestions.
                  onClick: (event) => {
                    event.stopPropagation();
                  },
                }}
              >
                <Swatch colour={colourOf?.(token)} />
                {token.name}
              </Pill>
            ))}
            <Combobox.EventsTarget withExpandedAttribute>
              <PillsInput.Field
                id={id}
                value={search}
                placeholder={chosen.length === 0 ? `Add a ${noun}` : undefined}
                readOnly={busy}
                onFocus={() => {
                  combobox.openDropdown();
                }}
                onBlur={() => {
                  combobox.closeDropdown();
                }}
                onChange={(event) => {
                  setSearch(event.currentTarget.value);
                  combobox.openDropdown();
                }}
                onKeyDown={keys}
              />
            </Combobox.EventsTarget>
          </Pill.Group>
        </PillsInput>
      </Combobox.DropdownTarget>

      <Combobox.Dropdown hidden={options === undefined || (!typed && suggestions.length === 0)}>
        <Combobox.Options aria-label={`${label} suggestions`}>
          {suggestions.map((token) => (
            <Combobox.Option value={token.id} key={token.id}>
              <Swatch colour={colourOf?.(token)} />
              {token.name}
            </Combobox.Option>
          ))}
          {offerCreate && createProblem === undefined && (
            <Combobox.Option value={CREATE}>
              Create {noun} “{search.trim()}”
            </Combobox.Option>
          )}
          {offerCreate && createProblem !== undefined && (
            <Combobox.Empty>
              <Text size="sm" c="var(--mantine-color-error)">
                {createProblem}
              </Text>
            </Combobox.Empty>
          )}
          {suggestions.length === 0 && typed && !offerCreate && (
            <Combobox.Empty>{search.trim()} is already chosen.</Combobox.Empty>
          )}
        </Combobox.Options>
      </Combobox.Dropdown>
    </Combobox>
  );
}
