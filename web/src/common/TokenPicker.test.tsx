import { MantineProvider } from '@mantine/core';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { TokenPicker } from './TokenPicker';
import type { Token } from './tokenMatching';

const OPTIONS: Token[] = [
  { id: 'folk', name: 'Folk' },
  { id: 'folk-rock', name: 'Folk Rock' },
  { id: 'indie-pop', name: 'Indie Pop' },
  { id: 'rock', name: 'Rock' },
];

function nameError(name: string): string | undefined {
  return name.trim().length > 10 ? 'Use at most 10 characters.' : undefined;
}

/** A picker holding its own chosen tokens, as a page would after each save. */
function Harness({
  initial = [],
  onCreate = () => undefined,
  busy = false,
}: {
  initial?: Token[];
  onCreate?: (name: string) => void;
  busy?: boolean;
}) {
  const [chosen, setChosen] = useState(initial);
  return (
    <MantineProvider>
      <TokenPicker
        label="Genres"
        noun="Genre"
        chosen={chosen}
        options={OPTIONS}
        busy={busy}
        nameError={nameError}
        onAdd={(token) => {
          setChosen((previous) => [...previous, token]);
        }}
        onCreate={onCreate}
        onRemove={(token) => {
          setChosen((previous) => previous.filter((other) => other.id !== token.id));
        }}
      />
    </MantineProvider>
  );
}

function field() {
  return screen.getByRole('combobox', { name: 'Genres' });
}

/** The suggestions shown, once the dropdown has opened. */
async function optionNames() {
  return within(await screen.findByRole('listbox'))
    .queryAllByRole('option')
    .map((option) => option.textContent);
}

describe('the token picker', () => {
  it('suggests by the start of any word, leaving out the chosen, and offers to create a new name', async () => {
    const user = userEvent.setup();
    render(<Harness initial={[{ id: 'rock', name: 'Rock' }]} />);

    await user.type(field(), 'ro');

    expect(await optionNames()).toEqual(['Folk Rock', 'Create Genre “ro”']);

    await user.clear(field());
    await user.type(field(), 'FOLK');
    // "Folk" exists, so there is no create option.
    expect(await optionNames()).toEqual(['Folk', 'Folk Rock']);
  });

  it('chooses a suggestion with the keyboard and clears the field', async () => {
    const user = userEvent.setup();
    render(<Harness />);

    await user.type(field(), 'pop');
    expect(await optionNames()).toEqual(['Indie Pop', 'Create Genre “pop”']);
    await user.keyboard('{Enter}');

    expect(screen.getByText('Indie Pop')).toBeVisible();
    expect(field()).toHaveValue('');

    // Arrow keys move through the suggestions.
    await user.type(field(), 'folk');
    await user.keyboard('{ArrowDown}{Enter}');
    expect(screen.getByText('Folk Rock')).toBeVisible();
  });

  it('creates a name that matches none, and refuses one that is too long', async () => {
    const user = userEvent.setup();
    const onCreate = vi.fn();
    render(<Harness onCreate={onCreate} />);

    await user.type(field(), 'Indie Rock');
    await user.click(await screen.findByRole('option', { name: 'Create Genre “Indie Rock”' }));

    expect(onCreate).toHaveBeenCalledWith('Indie Rock');
    expect(field()).toHaveValue('');

    await user.type(field(), 'Progressive House');
    expect(await optionNames()).toEqual([]);
    expect(screen.getByText('Use at most 10 characters.')).toBeVisible();
    await user.keyboard('{Enter}');
    expect(onCreate).toHaveBeenCalledTimes(1);
  });

  it('removes a token with its button or with Backspace in the empty field', async () => {
    const user = userEvent.setup();
    render(
      <Harness
        initial={[
          { id: 'folk', name: 'Folk' },
          { id: 'rock', name: 'Rock' },
        ]}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Remove Genre Folk' }));
    expect(screen.queryByRole('button', { name: 'Remove Genre Folk' })).not.toBeInTheDocument();

    await user.click(field());
    await user.keyboard('{Backspace}');
    expect(screen.queryByRole('button', { name: 'Remove Genre Rock' })).not.toBeInTheDocument();

    // A removed token is suggested again.
    await user.type(field(), 'ro');
    expect(await optionNames()).toContain('Rock');
  });

  it('reaches each remove button with Tab', async () => {
    const user = userEvent.setup();
    render(<Harness initial={[{ id: 'folk', name: 'Folk' }]} />);

    await user.tab();
    expect(screen.getByRole('button', { name: 'Remove Genre Folk' })).toHaveFocus();
    await user.keyboard('{Enter}');
    expect(screen.queryByRole('button', { name: 'Remove Genre Folk' })).not.toBeInTheDocument();
  });

  it('holds choices while a change is being saved', async () => {
    const user = userEvent.setup();
    const onCreate = vi.fn();
    render(<Harness initial={[{ id: 'folk', name: 'Folk' }]} onCreate={onCreate} busy />);

    expect(field()).toHaveAttribute('readonly');
    await user.click(screen.getByRole('button', { name: 'Remove Genre Folk' }));
    expect(screen.getByText('Folk')).toBeVisible();
  });
});
