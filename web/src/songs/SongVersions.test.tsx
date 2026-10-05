import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { jsonResponse, renderApp } from '../test/helpers';
import { testVersion, versionServer } from '../test/versionServer';

const ONE = testVersion('1', { current: true });

function tree() {
  return screen.getByRole('navigation', { name: 'Versions' });
}

/** The link that selects a Version in the tree. */
function node(number: string) {
  const link = tree().querySelector(`a[data-version-number="${number}"]`);
  if (!(link instanceof HTMLElement)) {
    throw new Error(`Version ${number} is not drawn.`);
  }
  return link;
}

/** The tree's shape: each drawn Version's number, its children in brackets. */
function shape(list: Element | null = tree().querySelector('ul')): string {
  if (list === null) {
    return '';
  }
  return [...list.children]
    .map((item) => {
      const number = item.querySelector('a')?.getAttribute('data-version-number') ?? '?';
      const children = shape(item.querySelector(':scope > ul'));
      return children === '' ? number : `${number}[${children}]`;
    })
    .join(' ');
}

async function openSong(path = '/songs/n8-7') {
  renderApp(path);
  await screen.findByRole('navigation', { name: 'Versions' });
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
    expect(node('1.1')).toHaveAttribute('aria-current', 'true');
    expect(within(node('1.1')).getByText('Current')).toBeVisible();
    expect(within(node('1')).queryByText('Current')).toBeNull();
    expect(screen.getByRole('heading', { level: 3, name: 'Version 1.1' })).toBeVisible();
    expect(screen.getByText('Current working Version')).toBeVisible();
    expect(screen.getByText('n8-7-v1.1')).toBeVisible();
    // A Version that is already current offers no "Make current".
    expect(screen.queryByRole('button', { name: 'Make current' })).toBeNull();
    expect(node('2')).toHaveAttribute('href', '/songs/n8-7/v/2');
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
    expect(node('2')).toHaveAttribute('aria-current', 'true');
    expect(screen.getByText('Too slow', { selector: 'p' })).toBeVisible();
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
    expect(node('2')).toHaveAttribute('aria-current', 'true');

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
    expect(node('1.1')).toHaveAttribute('aria-current', 'true');
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
