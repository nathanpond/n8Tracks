import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../test/helpers';
import { testVersion, versionServer } from '../test/versionServer';
import { deletionSummary } from './versionDeletion';

/** The Demo's tree: 1, 1.1, 1.1.1, and 2, the current one. */
const DEMO = [
  testVersion('1'),
  testVersion('1.1'),
  testVersion('1.1.1'),
  testVersion('2', { current: true }),
];

function tree() {
  return screen.getByRole('tree', { name: 'Versions' });
}

/** The tree's shape: each node's number (a placeholder's in parentheses), its children in brackets. */
function shape(container: Element | null = tree()): string {
  if (container === null) {
    return '';
  }
  return [...container.children]
    .filter((child) => child.getAttribute('role') === 'none')
    .map((wrapper) => {
      const item = wrapper.querySelector(':scope > [role="treeitem"]');
      const deleted = item?.getAttribute('data-deleted-number');
      const number =
        deleted !== null && deleted !== undefined
          ? `(${deleted})`
          : (item?.getAttribute('data-version-number') ?? '?');
      const children = shape(wrapper.querySelector(':scope > [role="group"]'));
      return children === '' ? number : `${number}[${children}]`;
    })
    .join(' ');
}

async function openSong(path = '/songs/n8-7') {
  renderApp(path);
  await screen.findByRole('tree', { name: 'Versions' });
  await waitFor(() => {
    expect(screen.queryByText('Loading the lyrics and styles…')).toBeNull();
  });
}

/** The pane's heading: the selected Version's, or what the page says about the URL's number. */
function heading() {
  return screen.getByRole('heading', { level: 3, name: /^Version [\d.]+( was deleted)?$/ });
}

describe('deleting a Version', () => {
  it('confirms with the counts, keeps the descendants under a placeholder, and selects the current Version', async () => {
    const user = userEvent.setup();
    const { server } = versionServer(DEMO);

    await openSong('/songs/n8-7/v/1.1');
    await user.click(screen.getByRole('button', { name: 'Delete' }));

    const dialog = await screen.findByRole('dialog', { name: 'Delete Version 1.1?' });
    const summary = await within(dialog).findByTestId('delete-version-summary');
    expect(summary).toHaveTextContent(
      'Version 1.1 and its editing history are deleted permanently.',
    );
    expect(summary).toHaveTextContent('1 descendant Version will remain, keeping its number');
    expect(summary).toHaveTextContent('No Generations are deleted with it.');
    expect(summary).not.toHaveTextContent('blank');

    await user.click(within(dialog).getByRole('button', { name: 'Delete Version 1.1' }));

    await waitFor(() => {
      expect(shape()).toBe('1[(1.1)[1.1.1]] 2');
    });
    expect(server.writes).toContainEqual({
      method: 'DELETE',
      path: `/api/v1/versions/${testVersion('1.1').id}`,
      body: {},
    });
    expect(await screen.findByTestId('version-deleted')).toHaveTextContent(
      'Version 1.1 deleted. Version 2 is current.',
    );
    await waitFor(() => {
      expect(heading()).toHaveTextContent('Version 2');
    });

    // The placeholder carries the number, cannot be selected, and has no actions.
    const placeholder = within(tree()).getByRole('treeitem', { name: 'Deleted Version 1.1' });
    expect(placeholder).toHaveAttribute('aria-disabled', 'true');
    expect(placeholder).toHaveAttribute('aria-selected', 'false');
    expect(within(placeholder).queryByRole('button')).toBeNull();
    await user.click(placeholder);
    placeholder.focus();
    await user.keyboard('{Enter}');
    expect(heading()).toHaveTextContent('Version 2');
  });

  it('deletes from the tree menu, and a Song’s last Version is replaced by a new blank one', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([testVersion('1', { current: true, lyrics: 'Old words' })]);

    await openSong();
    await user.click(screen.getByRole('button', { name: 'Actions for Version 1' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Delete' }));

    const dialog = await screen.findByRole('dialog', { name: 'Delete Version 1?' });
    expect(await within(dialog).findByTestId('delete-version-summary')).toHaveTextContent(
      'This is the Song’s only Version, so a new blank Version is created and becomes the current one.',
    );
    await user.click(within(dialog).getByRole('button', { name: 'Delete Version 1' }));

    expect(await screen.findByTestId('version-deleted')).toHaveTextContent(
      'Version 1 deleted. It was the only Version, so a new blank Version 2 was created and is current.',
    );
    await waitFor(() => {
      expect(shape()).toBe('2');
    });
    expect(heading()).toHaveTextContent('Version 2');
    expect(server.song.currentVersion.number).toBe('2');
  });

  it('says a frozen Version’s Generations go with it', async () => {
    const user = userEvent.setup();
    versionServer([testVersion('1'), testVersion('2', { current: true, isFrozen: true })]);

    await openSong();
    await user.click(screen.getByRole('button', { name: 'Delete' }));

    const dialog = await screen.findByRole('dialog', { name: 'Delete Version 2?' });
    expect(await within(dialog).findByTestId('delete-version-summary')).toHaveTextContent(
      'Its 1 Generation is deleted with it.',
    );
  });

  it('deletes nothing when the Version changed since the dialog opened, and reads the counts again', async () => {
    const user = userEvent.setup();
    const { server } = versionServer(DEMO);

    await openSong('/songs/n8-7/v/1.1');
    await user.click(screen.getByRole('button', { name: 'Delete' }));
    const dialog = await screen.findByRole('dialog', { name: 'Delete Version 1.1?' });
    await within(dialog).findByTestId('delete-version-summary');
    server.changeElsewhere('1.1', { name: 'Renamed elsewhere' });

    await user.click(within(dialog).getByRole('button', { name: 'Delete Version 1.1' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'Version 1.1 was changed elsewhere since this opened, so it was not deleted.',
    );
    expect(server.versions.some((version) => version.number === '1.1')).toBe(true);

    // The counts were read again, on the new revision, so Delete now goes through.
    await within(dialog).findByTestId('delete-version-summary');
    await user.click(within(dialog).getByRole('button', { name: 'Delete Version 1.1' }));
    await waitFor(() => {
      expect(server.versions.some((version) => version.number === '1.1')).toBe(false);
    });
  });

  it('closes on Cancel and deletes nothing', async () => {
    const user = userEvent.setup();
    const { server } = versionServer(DEMO);

    await openSong('/songs/n8-7/v/1.1');
    await user.click(screen.getByRole('button', { name: 'Delete' }));
    const dialog = await screen.findByRole('dialog', { name: 'Delete Version 1.1?' });
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(server.writes).toEqual([]);
    expect(shape()).toBe('1[1.1[1.1.1]] 2');
  });
});

describe('a deleted Version', () => {
  it('says its page URL was deleted', async () => {
    const { server } = versionServer(DEMO);
    server.deleteElsewhere('1.1');

    await openSong('/songs/n8-7/v/1.1');

    expect(
      await screen.findByRole('heading', { level: 3, name: 'Version 1.1 was deleted' }),
    ).toBeVisible();
    expect(screen.getByText('n8-7-v1.1 was deleted. Its number is not used again.')).toBeVisible();
  });

  it('offers unsaved text as a new Version when its next save finds it deleted', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([testVersion('1', { current: true }), testVersion('1.1')]);

    await openSong('/songs/n8-7/v/1.1');
    server.deleteElsewhere('1.1');
    await user.type(screen.getByRole('textbox', { name: 'Styles' }), 'dream pop');

    expect(
      await screen.findByRole(
        'heading',
        { level: 3, name: 'Version 1.1 was deleted' },
        { timeout: 4_000 },
      ),
    ).toBeVisible();
    expect(screen.getByTestId('carried-text')).toHaveTextContent(
      'Your unsaved changes to its lyrics and styles could not be saved.',
    );

    await user.click(screen.getByRole('button', { name: 'Create a new Version with my text' }));
    const dialog = await screen.findByRole('dialog', { name: 'Create New Version From 1' });
    await waitFor(() => {
      expect(
        within(dialog).getByText(/The new Version starts with the lyrics and styles carried over/),
      ).toBeVisible();
    });
    await user.click(await within(dialog).findByRole('button', { name: 'Create Version' }));

    await waitFor(() => {
      expect(server.versions.some((version) => version.styles === 'dream pop')).toBe(true);
    });
  }, 10_000);
});

describe('what the confirmation says', () => {
  it('counts Generations and descendants, and says when a blank Version is created', () => {
    expect(
      deletionSummary('3', {
        generationCount: 2,
        remainingDescendantCount: 3,
        isLastVersion: false,
        revision: 1,
      }),
    ).toEqual([
      'Version 3 and its editing history are deleted permanently.',
      'Its 2 Generations are deleted with it.',
      '3 descendant Versions will remain, keeping their numbers, under a “Deleted Version 3” placeholder.',
      'Its number is never used again.',
    ]);
    expect(
      deletionSummary('1', {
        generationCount: 0,
        remainingDescendantCount: 0,
        isLastVersion: true,
        revision: 1,
      }),
    ).toEqual([
      'Version 1 and its editing history are deleted permanently.',
      'No Generations are deleted with it.',
      'No descendant Versions will remain.',
      'This is the Song’s only Version, so a new blank Version is created and becomes the current one.',
      'Its number is never used again.',
    ]);
  });
});
