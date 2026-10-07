import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { jsonResponse, renderApp } from '../test/helpers';
import { testVersion, versionServer } from '../test/versionServer';

const ONE = testVersion('1', { current: true });

function tree() {
  return screen.getByRole('tree', { name: 'Versions' });
}

/** The tree node of a Version. */
function node(number: string) {
  const item = tree().querySelector(`[role="treeitem"][data-version-number="${number}"]`);
  if (!(item instanceof HTMLElement)) {
    throw new Error(`Version ${number} is not drawn.`);
  }
  return item;
}

/** The tree's shape: each drawn Version's number, its children in brackets. */
function shape(container: Element | null = tree()): string {
  if (container === null) {
    return '';
  }
  return [...container.children]
    .filter((child) => child.getAttribute('role') === 'none')
    .map((wrapper) => {
      const number =
        wrapper.querySelector(':scope > [role="treeitem"]')?.getAttribute('data-version-number') ??
        '?';
      const children = shape(wrapper.querySelector(':scope > [role="group"]'));
      return children === '' ? number : `${number}[${children}]`;
    })
    .join(' ');
}

/** The notice an archive leaves, with its Undo. */
function archiveNotice() {
  return screen.findByText(/archived\./, { selector: 'p' });
}

async function openSong(path = '/songs/n8-7') {
  renderApp(path);
  await screen.findByRole('tree', { name: 'Versions' });
  // The selected Version's pane is drawn again once its lyrics are in: clicking its buttons
  // before then could land on the loading pane's copies as they are replaced.
  await waitFor(() => {
    expect(screen.queryByText('Loading the lyrics and styles…')).toBeNull();
  });
}

describe('the Version tree', () => {
  it('opens a Song at its current Version, nested by number with the current one marked', async () => {
    versionServer([
      testVersion('1'),
      testVersion('1.1', { name: 'Guitar experimentation', current: true }),
      testVersion('1.2'),
      testVersion('2'),
      testVersion('1.10'),
    ]);

    await openSong();

    expect(shape()).toBe('1[1.1 1.2 1.10] 2');
    expect(node('1.1')).toHaveAttribute('aria-selected', 'true');
    expect(node('1.1')).toHaveAttribute('aria-current', 'true');
    expect(node('1')).toHaveAttribute('aria-selected', 'false');
    expect(node('1')).not.toHaveAttribute('aria-current');
    expect(within(node('1.1')).getByText('Current')).toBeVisible();
    expect(within(node('1')).queryByText('Current')).toBeNull();
    expect(screen.getByRole('heading', { level: 3, name: 'Version 1.1' })).toBeVisible();
    expect(screen.getByText('Current working Version')).toBeVisible();
    expect(screen.getByText('n8-7-v1.1')).toBeVisible();
    // A Version that is already current offers no "Make current".
    expect(screen.queryByRole('button', { name: 'Make current' })).toBeNull();
  });

  it('draws a Version whose parent is missing under the nearest existing ancestor', async () => {
    versionServer([ONE, testVersion('1.3.1'), testVersion('2.1')]);

    await openSong();

    expect(shape()).toBe('1[1.3.1] 2.1');
  });

  it('hides archived Versions until "Show archived" is on, then draws them dimmed', async () => {
    const user = userEvent.setup();
    versionServer([
      ONE,
      testVersion('1.1', { archived: true }),
      testVersion('1.1.1'),
      testVersion('2', { archived: true }),
    ]);

    await openSong();
    const toggle = screen.getByRole('switch', { name: 'Show archived' });
    expect(toggle).not.toBeChecked();
    expect(shape()).toBe('1[1.1.1]');

    await user.click(toggle);

    expect(shape()).toBe('1[1.1[1.1.1]] 2');
    expect(node('2')).toHaveAttribute('data-archived', 'true');
    expect(within(node('2')).getByText('(archived)')).toBeVisible();
    expect(node('1')).not.toHaveAttribute('data-archived');
  });

  it('opens a linked Version, and a link to an archived one turns "Show archived" on', async () => {
    versionServer([ONE, testVersion('2', { archived: true, name: 'Too slow' })]);

    await openSong('/songs/n8-7/v/2');

    expect(screen.getByRole('heading', { level: 3, name: 'Version 2' })).toBeVisible();
    expect(screen.getByRole('switch', { name: 'Show archived' })).toBeChecked();
    expect(node('2')).toHaveAttribute('aria-selected', 'true');
    expect(await screen.findByRole('textbox', { name: 'Name' })).toHaveValue('Too slow');
    expect(screen.getByRole('button', { name: 'Make current' })).toBeEnabled();
  });

  it('says so when the Song has no Version with the linked number', async () => {
    const user = userEvent.setup();
    versionServer([ONE]);

    await openSong('/songs/n8-7/v/9');

    expect(screen.getByRole('heading', { level: 3, name: 'Version not found' })).toBeVisible();
    expect(screen.getByText('n8-7 has no Version 9.')).toBeVisible();
    await user.click(screen.getByRole('link', { name: 'Open n8-7 at its current Version' }));
    expect(await screen.findByRole('heading', { level: 3, name: 'Version 1' })).toBeVisible();
  });

  it('selects a Version from the tree and makes it current', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE, testVersion('2')]);

    await openSong();
    await user.click(node('2'));
    expect(await screen.findByRole('heading', { level: 3, name: 'Version 2' })).toBeVisible();
    expect(node('2')).toHaveAttribute('aria-selected', 'true');

    await user.click(screen.getByRole('button', { name: 'Make current' }));

    await waitFor(() => {
      expect(within(node('2')).getByText('Current')).toBeVisible();
    });
    expect(within(node('1')).queryByText('Current')).toBeNull();
    expect(server.writes).toEqual([
      {
        method: 'PUT',
        path: '/api/v1/songs/0199b1a0-0000-7000-8000-000000000007/current-version',
        body: { versionId: testVersion('2').id },
      },
    ]);
  });

  it('keeps the marker where it was when making a Version current fails', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE, testVersion('2')]);
    server.next = () => jsonResponse(500, {});

    await openSong('/songs/n8-7/v/2');
    await user.click(screen.getByRole('button', { name: 'Make current' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Not changed');
    expect(within(node('1')).getByText('Current')).toBeVisible();
  });
});

describe('the Create New Version dialog', () => {
  it('offers the valid numbers with the proposal chosen, and creates the new current Version', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);

    await openSong();
    await user.click(screen.getByRole('button', { name: 'Create New Version From 1' }));
    const dialog = await screen.findByRole('dialog', { name: 'Create New Version From 1' });

    const proposed = await within(dialog).findByRole('radio', { name: /^2 \(proposed\)/ });
    expect(proposed).toBeChecked();
    const child = within(dialog).getByRole('radio', { name: /^1\.1/ });
    expect(child).not.toBeChecked();
    expect(within(dialog).getByText('Branch under 1')).toBeInTheDocument();

    await user.click(child);
    await user.type(
      within(dialog).getByRole('textbox', { name: 'Name' }),
      'Guitar experimentation',
    );
    await user.click(within(dialog).getByRole('button', { name: 'Create Version' }));

    expect(await screen.findByRole('heading', { level: 3, name: 'Version 1.1' })).toBeVisible();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(shape()).toBe('1[1.1]');
    expect(node('1.1')).toHaveAttribute('aria-selected', 'true');
    expect(node('1.1')).toHaveAttribute('aria-current', 'true');
    expect(node('1')).toHaveAttribute('aria-selected', 'false');
    expect(node('1')).not.toHaveAttribute('aria-current');
    expect(within(node('1.1')).getByText('Current')).toBeVisible();
    expect(within(node('1')).queryByText('Current')).toBeNull();
    expect(server.writes).toEqual([
      {
        method: 'POST',
        path: '/api/v1/songs/0199b1a0-0000-7000-8000-000000000007/versions',
        body: { sourceVersionId: ONE.id, number: '1.1', name: 'Guitar experimentation' },
      },
    ]);
  });

  it('offers fresh numbers and keeps the name when the chosen one was taken meanwhile', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);

    await openSong();
    await user.click(screen.getByRole('button', { name: 'Create New Version From 1' }));
    const dialog = await screen.findByRole('dialog', { name: 'Create New Version From 1' });
    expect(await within(dialog).findByRole('radio', { name: /^2 \(proposed\)/ })).toBeChecked();
    const name = within(dialog).getByRole('textbox', { name: 'Name' });
    await user.type(name, 'Faster');

    server.addElsewhere('2');
    await user.click(within(dialog).getByRole('button', { name: 'Create Version' }));

    expect(
      await within(dialog).findByText(/Version 2 was created elsewhere meanwhile/),
    ).toBeVisible();
    expect(within(dialog).getByRole('radio', { name: /^1\.1 \(proposed\)/ })).toBeChecked();
    expect(within(dialog).getByRole('radio', { name: /^3/ })).not.toBeChecked();
    expect(name).toHaveValue('Faster');

    await user.click(within(dialog).getByRole('button', { name: 'Create Version' }));
    expect(await screen.findByRole('heading', { level: 3, name: 'Version 1.1' })).toBeVisible();
    expect(server.writes.map((write) => write.body.number)).toEqual(['2', '1.1']);
  });

  it('checks the name before sending it', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);

    await openSong();
    await user.click(screen.getByRole('button', { name: 'Create New Version From 1' }));
    const dialog = await screen.findByRole('dialog', { name: 'Create New Version From 1' });
    await within(dialog).findByRole('radio', { name: /^2/ });
    const name = within(dialog).getByRole('textbox', { name: 'Name' });
    await user.click(name);
    await user.paste('a'.repeat(201));
    await user.click(within(dialog).getByRole('button', { name: 'Create Version' }));

    expect(within(dialog).getByText('Use at most 200 characters.')).toBeVisible();
    expect(name).toHaveAttribute('aria-invalid', 'true');
    expect(server.writes).toEqual([]);
  });
});

describe('archiving Versions', () => {
  it('remembers "Show archived" in this browser', async () => {
    const user = userEvent.setup();
    versionServer([ONE, testVersion('2', { archived: true })]);

    await openSong();
    expect(shape()).toBe('1');
    await user.click(screen.getByRole('switch', { name: 'Show archived' }));
    expect(shape()).toBe('1 2');
    expect(window.localStorage.getItem('n8tracks-show-archived-versions')).toBe('true');
  });

  it('opens with "Show archived" as this browser left it', async () => {
    window.localStorage.setItem('n8tracks-show-archived-versions', 'true');
    versionServer([ONE, testVersion('2', { archived: true })]);

    await openSong();

    expect(screen.getByRole('switch', { name: 'Show archived' })).toBeChecked();
    expect(shape()).toBe('1 2');
  });

  it('always draws the current Version, dimmed and marked, even archived with the toggle off', async () => {
    versionServer([testVersion('1', { archived: true, current: true }), testVersion('2')]);

    await openSong();

    expect(screen.getByRole('switch', { name: 'Show archived' })).not.toBeChecked();
    expect(shape()).toBe('1 2');
    expect(node('1')).toHaveAttribute('data-archived', 'true');
    expect(node('1')).toHaveAttribute('aria-current', 'true');
    expect(within(node('1')).getByText('Current')).toBeVisible();
    expect(within(node('1')).getByText('(archived)')).toBeVisible();
  });

  it('draws the visible children of a hidden archived Version under its nearest visible ancestor', async () => {
    versionServer([
      ONE,
      testVersion('1.1', { archived: true }),
      testVersion('1.1.1', { archived: true }),
      testVersion('1.1.1.1'),
      testVersion('2', { archived: true }),
      testVersion('2.1'),
    ]);

    await openSong();

    expect(shape()).toBe('1[1.1.1.1] 2.1');
  });

  it('archives from the tree menu at once, hides the Version, and offers Undo', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE, testVersion('2'), testVersion('2.1')]);

    await openSong();
    await user.click(screen.getByRole('button', { name: 'Actions for Version 2' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Archive' }));

    expect(await archiveNotice()).toHaveTextContent('Version 2 archived.');
    // 2 is hidden; its child stays, drawn under the nearest visible ancestor (none: the top).
    expect(shape()).toBe('1 2.1');
    expect(server.writes).toEqual([
      {
        method: 'PATCH',
        path: `/api/v1/versions/${testVersion('2').id}`,
        body: { archived: true },
      },
    ]);

    await user.click(screen.getByRole('button', { name: 'Undo' }));

    await waitFor(() => {
      expect(shape()).toBe('1 2[2.1]');
    });
    expect(server.writes.map((write) => write.body)).toEqual([
      { archived: true },
      { archived: false },
    ]);
    expect(screen.queryByText(/archived\./)).toBeNull();
  });

  it('moves the selection to the current Version when the selected one is archived and hidden', async () => {
    const user = userEvent.setup();
    versionServer([ONE, testVersion('2')]);

    await openSong('/songs/n8-7/v/2');
    expect(screen.getByRole('heading', { level: 3, name: 'Version 2' })).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Archive' }));

    expect(await screen.findByRole('heading', { level: 3, name: 'Version 1' })).toBeVisible();
    expect(shape()).toBe('1');
    expect(node('1')).toHaveAttribute('aria-selected', 'true');
  });

  it('keeps the archived current Version selected, visible, and marked', async () => {
    const user = userEvent.setup();
    versionServer([ONE, testVersion('2')]);

    await openSong();
    await user.click(screen.getByRole('button', { name: 'Archive' }));

    expect(await archiveNotice()).toHaveTextContent('Version 1 archived.');
    expect(shape()).toBe('1 2');
    expect(node('1')).toHaveAttribute('data-archived', 'true');
    expect(node('1')).toHaveAttribute('aria-current', 'true');
    expect(screen.getByRole('heading', { level: 3, name: 'Version 1' })).toBeVisible();
    expect(
      within(screen.getByRole('region', { name: 'Version 1' })).getByText('Archived'),
    ).toBeVisible();
    expect(screen.getByRole('button', { name: 'Unarchive' })).toBeEnabled();
  });

  it('retries an archive on a newer revision without asking', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE, testVersion('2')]);

    await openSong('/songs/n8-7/v/2');
    server.changeElsewhere('2', { notes: 'Their note' });
    await user.click(screen.getByRole('button', { name: 'Archive' }));

    await waitFor(() => {
      expect(server.versions.find((version) => version.number === '2')?.archived).toBe(true);
    });
    expect(server.writes).toHaveLength(2);
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('says so when archiving fails, and changes nothing', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE, testVersion('2')]);
    server.next = () => jsonResponse(500, {});

    await openSong('/songs/n8-7/v/2');
    await user.click(screen.getByRole('button', { name: 'Archive' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Not changed');
    expect(shape()).toBe('1 2');
  });
});

describe('the tree from the keyboard', () => {
  it('names each node by its number, name, and whether it is current or archived', async () => {
    window.localStorage.setItem('n8tracks-show-archived-versions', 'true');
    versionServer([
      ONE,
      testVersion('1.1', { name: 'Guitar experimentation' }),
      testVersion('2', { archived: true, name: 'Too slow' }),
    ]);

    await openSong();

    expect(screen.getByRole('treeitem', { name: 'Version 1, current working Version' })).toBe(
      node('1'),
    );
    expect(screen.getByRole('treeitem', { name: 'Version 1.1, Guitar experimentation' })).toBe(
      node('1.1'),
    );
    expect(screen.getByRole('treeitem', { name: 'Version 2, Too slow, archived' })).toBe(node('2'));
    expect(node('1')).toHaveAttribute('aria-expanded', 'true');
    expect(node('1')).toHaveAttribute('aria-level', '1');
    expect(node('1.1')).toHaveAttribute('aria-level', '2');
    expect(node('1.1')).not.toHaveAttribute('aria-expanded');
  });

  it('moves with the arrow keys, collapses and expands, and selects with Enter', async () => {
    const user = userEvent.setup();
    versionServer([ONE, testVersion('1.1'), testVersion('1.2'), testVersion('2')]);

    await openSong();
    // One tab stop: the selected node.
    expect(node('1')).toHaveAttribute('tabindex', '0');
    expect(node('2')).toHaveAttribute('tabindex', '-1');
    node('1').focus();

    await user.keyboard('{ArrowDown}');
    expect(node('1.1')).toHaveFocus();
    await user.keyboard('{ArrowDown}{ArrowDown}');
    expect(node('2')).toHaveFocus();
    await user.keyboard('{ArrowDown}');
    expect(node('2')).toHaveFocus();
    await user.keyboard('{Home}');
    expect(node('1')).toHaveFocus();
    await user.keyboard('{End}');
    expect(node('2')).toHaveFocus();
    expect(node('2')).toHaveAttribute('tabindex', '0');

    // Left on 1.2 goes to its parent; Left on the parent collapses it, Right expands it again.
    await user.keyboard('{ArrowUp}{ArrowLeft}');
    expect(node('1')).toHaveFocus();
    await user.keyboard('{ArrowLeft}');
    expect(node('1')).toHaveAttribute('aria-expanded', 'false');
    expect(shape()).toBe('1 2');
    await user.keyboard('{ArrowDown}');
    expect(node('2')).toHaveFocus();
    await user.keyboard('{ArrowUp}{ArrowRight}');
    expect(node('1')).toHaveAttribute('aria-expanded', 'true');
    await user.keyboard('{ArrowRight}');
    expect(node('1.1')).toHaveFocus();

    await user.keyboard('{ArrowDown}{Enter}');
    expect(await screen.findByRole('heading', { level: 3, name: 'Version 1.2' })).toBeVisible();
    expect(node('1.2')).toHaveAttribute('aria-selected', 'true');
    expect(node('1.2')).toHaveFocus();
  });

  it('opens a node’s actions with Shift+F10, and reaches its actions button with Tab', async () => {
    const user = userEvent.setup();
    versionServer([ONE, testVersion('2')]);

    await openSong();
    node('1').focus();
    await user.keyboard('{ArrowDown}{Shift>}{F10}{/Shift}');

    const menu = await screen.findByRole('menu');
    expect(
      within(menu).getByRole('menuitem', { name: 'Create New Version From 2' }),
    ).toBeInTheDocument();
    expect(within(menu).getByRole('menuitem', { name: 'Make current' })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitem', { name: 'Archive' })).toBeInTheDocument();

    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('menu')).toBeNull();
    });
    expect(node('2')).toHaveFocus();

    await user.tab();
    expect(screen.getByRole('button', { name: 'Actions for Version 2' })).toHaveFocus();
  });
});

/** Waits for the autosave after the user's pause (1.5 s) to have sent `count` writes. */
async function writesReach(writes: unknown[], count: number) {
  await waitFor(
    () => {
      expect(writes).toHaveLength(count);
    },
    { timeout: 4_000 },
  );
}

describe('a Version’s name and notes', () => {
  it('saves a new name and notes automatically, each on its revision', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE, testVersion('1.1')]);

    await openSong('/songs/n8-7/v/1.1');
    const name = await screen.findByRole('textbox', { name: 'Name' });
    await user.type(name, '  Guitar experimentation ');
    await writesReach(server.writes, 1);

    await waitFor(() => {
      expect(within(node('1.1')).getByText('Guitar experimentation')).toBeVisible();
    });
    expect(node('1.1')).toHaveAccessibleName('Version 1.1, Guitar experimentation');
    expect(name).toHaveValue('  Guitar experimentation ');

    await user.type(screen.getByRole('textbox', { name: 'Notes' }), 'Try a capo.');
    await writesReach(server.writes, 2);
    expect(await screen.findByText('Saved')).toBeVisible();
    expect(server.writes).toEqual([
      {
        method: 'PATCH',
        path: `/api/v1/versions/${testVersion('1.1').id}`,
        body: { name: 'Guitar experimentation' },
      },
      {
        method: 'PATCH',
        path: `/api/v1/versions/${testVersion('1.1').id}`,
        body: { notes: 'Try a capo.' },
      },
    ]);
    expect(server.versions.find((version) => version.number === '1.1')?.revision).toBe(3);
  }, 10_000);

  it('edits the name of an archived Version too, and clears it to none', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE, testVersion('2', { archived: true, name: 'Old' })]);

    await openSong('/songs/n8-7/v/2');
    await user.clear(await screen.findByRole('textbox', { name: 'Name' }));

    await writesReach(server.writes, 1);
    expect(server.writes.map((write) => write.body)).toEqual([{ name: null }]);
    await waitFor(() => {
      expect(node('2')).toHaveAccessibleName('Version 2, archived');
    });
  });

  it('flags a name over the limit in the field without sending it, and one the API refuses', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);

    await openSong();
    const name = await screen.findByRole('textbox', { name: 'Name' });
    await user.click(name);
    await user.paste('a'.repeat(201));

    expect(screen.getByText('Use at most 200 characters.')).toBeVisible();
    expect(name).toHaveAttribute('aria-invalid', 'true');
    expect(
      screen.getByText('Not saved: The name is over its limit. Shorten the text to save.'),
    ).toBeVisible();
    await new Promise((resolve) => setTimeout(resolve, 2_000));
    expect(server.writes).toEqual([]);

    server.next = () =>
      jsonResponse(422, {
        code: 'validation_failed',
        errors: { name: ['A name is one line, with no control characters.'] },
      });
    await user.clear(name);
    await user.type(name, 'Fine here');

    expect(
      await screen.findByText(
        'Not saved: A name is one line, with no control characters. Change the text to save it.',
        undefined,
        { timeout: 4_000 },
      ),
    ).toBeVisible();
    expect(name).toHaveValue('Fine here');
  }, 10_000);

  it('offers the conflict dialog when the notes changed elsewhere', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);

    await openSong();
    server.changeElsewhere('1', { notes: 'Their note' });
    await user.type(await screen.findByRole('textbox', { name: 'Notes' }), 'My note');

    const dialog = await screen.findByRole('dialog', undefined, { timeout: 4_000 });
    expect(within(dialog).getByText(/This Version/)).toBeInTheDocument();
    expect(within(dialog).getByText('Their note')).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Notes', hidden: true })).toHaveValue('My note');
  });
});
