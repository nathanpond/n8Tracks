import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { IgnoredItem } from '../api/sunoIgnored';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';
import { statusText } from './ignoredItemsRules';

function testItem(sunoId: string, change: Partial<IgnoredItem> = {}): IgnoredItem {
  return {
    sunoId,
    title: `Clip ${sunoId}`,
    workspaceId: 'studio',
    workspaceName: 'Studio',
    ignoredAt: '2026-10-02T09:00:00Z',
    status: 'present',
    lastSeenAt: '2026-10-02T08:00:00Z',
    ...change,
  };
}

/**
 * A fake ignore list: it filters and pages as the server does (50 to a page, the title or the start of
 * the Suno ID), and records each removal it is sent.
 */
function ignoredServer(items: IgnoredItem[]) {
  const server = {
    items: [...items],
    queries: [] as string[],
    removals: [] as string[][],
    failRemoval: false,
  };
  stubFetch().mockImplementation((input, init) => {
    const path = requestPath(input);
    const url = new URL(input instanceof Request ? input.url : input.toString(), document.baseURI);
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (path.endsWith('/api/v1/suno/ignored/remove') && init?.method === 'POST') {
      if (server.failRemoval) {
        return Promise.resolve(jsonResponse(500, {}));
      }
      const sunoIds = (
        JSON.parse(typeof init.body === 'string' ? init.body : '{}') as { sunoIds: string[] }
      ).sunoIds;
      server.removals.push(sunoIds);
      const before = server.items.length;
      server.items = server.items.filter((item) => !sunoIds.includes(item.sunoId));
      const removed = before - server.items.length;
      return Promise.resolve(jsonResponse(200, { removed, unknown: sunoIds.length - removed }));
    }
    if (path.endsWith('/api/v1/suno/ignored')) {
      server.queries.push(url.search);
      const q = (url.searchParams.get('q') ?? '').toLowerCase();
      const workspace = url.searchParams.get('workspace');
      const status = url.searchParams.get('status');
      const page = Number(url.searchParams.get('page') ?? '1');
      const matching = server.items.filter(
        (item) =>
          (q === '' ||
            (item.title ?? '').toLowerCase().includes(q) ||
            item.sunoId.toLowerCase().startsWith(q)) &&
          (workspace === null || item.workspaceId === workspace) &&
          (status === null || item.status === status),
      );
      const workspaces = [
        ...new Set(
          server.items.flatMap((item) => (item.workspaceId === null ? [] : [item.workspaceId])),
        ),
      ].map((id) => ({
        id,
        name: server.items.find((item) => item.workspaceId === id)?.workspaceName ?? null,
        count: server.items.filter((item) => item.workspaceId === id).length,
      }));
      return Promise.resolve(
        jsonResponse(200, {
          items: matching.slice((page - 1) * 50, page * 50),
          page,
          pageSize: 50,
          total: matching.length,
          workspaces,
        }),
      );
    }
    return Promise.resolve(jsonResponse(404, { code: 'not_found' }));
  });
  return server;
}

function row(sunoId: string): HTMLElement {
  const found = document.querySelector<HTMLElement>(`tr[data-item="${sunoId}"]`);
  if (found === null) {
    throw new Error(`No row for ${sunoId}`);
  }
  return found;
}

async function openList(path = '/suno/ignored') {
  const rendered = renderApp(path);
  expect(
    await screen.findByRole('heading', { level: 2, name: 'Ignored Suno items' }),
  ).toBeVisible();
  return rendered;
}

describe('the Ignored Suno Items screen', () => {
  it('lists each item’s Suno title, Suno ID, workspace, ignored date, and Suno status', async () => {
    ignoredServer([
      testItem('a1', { title: 'Morning Light' }),
      testItem('b2', { title: null, workspaceId: null, workspaceName: null, status: 'trashed' }),
      testItem('c3', { workspaceId: 'ws-x', workspaceName: null, status: 'missing' }),
      testItem('d4', { status: 'not_seen', lastSeenAt: '2026-09-15T08:00:00Z' }),
    ]);
    await openList();

    const table = await screen.findByRole('table', { name: 'Ignored Suno items' });
    expect(
      within(table)
        .getAllByRole('columnheader')
        .map((header) => header.textContent),
    ).toEqual(['', 'Suno title', 'Suno ID', 'Workspace', 'Ignored', 'Suno status']);
    expect(within(row('a1')).getByRole('rowheader')).toHaveTextContent('Morning Light');
    expect(row('a1')).toHaveTextContent('a1');
    expect(row('a1')).toHaveTextContent('Studio');
    expect(row('a1')).toHaveTextContent('Oct 2, 2026');
    expect(within(row('a1')).getByTestId('item-status')).toHaveTextContent('Present');
    expect(within(row('b2')).getByRole('rowheader')).toHaveTextContent('Untitled');
    expect(row('b2')).toHaveTextContent('None');
    expect(within(row('b2')).getByTestId('item-status')).toHaveTextContent('In Trash');
    expect(row('c3')).toHaveTextContent('ws-x');
    expect(within(row('c3')).getByTestId('item-status')).toHaveTextContent('Missing');
    expect(within(row('d4')).getByTestId('item-status')).toHaveTextContent(
      'Not seen since Sep 15, 2026',
    );
    expect(screen.getByTestId('selected-count')).toHaveTextContent('None selected of 4 items');
  });

  it('searches and filters through the address, so a reload shows the same list', async () => {
    const server = ignoredServer([
      testItem('a1', { title: 'Morning Light' }),
      testItem('b2', { title: 'Evening', workspaceId: 'demos', workspaceName: 'Demos' }),
      testItem('c3', { title: 'Night', status: 'trashed' }),
    ]);
    const user = userEvent.setup();
    const { router } = await openList();
    await screen.findByRole('table', { name: 'Ignored Suno items' });

    await user.type(screen.getByRole('textbox', { name: /Search/ }), 'morning');
    await user.click(screen.getByRole('button', { name: 'Search' }));
    await waitFor(() => {
      expect(router.state.location.search).toBe('?q=morning');
    });
    await waitFor(() => {
      expect(document.querySelectorAll('tr[data-item]')).toHaveLength(1);
    });
    expect(row('a1')).toBeVisible();

    await user.clear(screen.getByRole('textbox', { name: /Search/ }));
    await user.click(screen.getByRole('button', { name: 'Search' }));
    await user.selectOptions(screen.getByRole('combobox', { name: 'Workspace' }), 'Demos');
    await waitFor(() => {
      expect(router.state.location.search).toBe('?workspace=demos');
    });
    await waitFor(() => {
      expect(document.querySelectorAll('tr[data-item]')).toHaveLength(1);
    });
    expect(row('b2')).toBeVisible();

    await user.selectOptions(screen.getByRole('combobox', { name: 'Workspace' }), 'All workspaces');
    await user.selectOptions(screen.getByRole('combobox', { name: 'Suno status' }), 'In Trash');
    await waitFor(() => {
      expect(router.state.location.search).toBe('?status=trashed');
    });
    await waitFor(() => {
      expect(document.querySelectorAll('tr[data-item]')).toHaveLength(1);
    });
    expect(row('c3')).toBeVisible();
    expect(server.queries).toContain('?status=trashed');

    await user.selectOptions(screen.getByRole('combobox', { name: 'Workspace' }), 'Demos');
    expect(await screen.findByTestId('no-ignored-items')).toHaveTextContent(
      'No ignored item matches.',
    );
  });

  it('opens at the search and filters in its address', async () => {
    const server = ignoredServer([testItem('a1', { title: 'Morning Light' }), testItem('b2')]);
    await openList('/suno/ignored?q=morning&status=present');

    await waitFor(() => {
      expect(document.querySelectorAll('tr[data-item]')).toHaveLength(1);
    });
    expect(screen.getByRole('textbox', { name: /Search/ })).toHaveValue('morning');
    expect(screen.getByRole('combobox', { name: 'Suno status' })).toHaveValue('present');
    expect(server.queries[0]).toBe('?q=morning&status=present');
  });

  it('pages at 50, the page in the address', async () => {
    ignoredServer(
      Array.from({ length: 51 }, (_, index) => testItem(`id-${String(index).padStart(2, '0')}`)),
    );
    const user = userEvent.setup();
    const { router } = await openList();
    await screen.findByRole('table', { name: 'Ignored Suno items' });
    expect(document.querySelectorAll('tr[data-item]')).toHaveLength(50);

    await user.click(screen.getByRole('button', { name: 'Page 2' }));
    await waitFor(() => {
      expect(router.state.location.search).toBe('?page=2');
    });
    await waitFor(() => {
      expect(document.querySelectorAll('tr[data-item]')).toHaveLength(1);
    });
    expect(row('id-50')).toBeVisible();
  });

  it('removes the selected items after a confirmation, importing nothing', async () => {
    const server = ignoredServer([testItem('a1'), testItem('b2'), testItem('c3')]);
    const user = userEvent.setup();
    await openList();
    await screen.findByRole('table', { name: 'Ignored Suno items' });
    expect(screen.getByRole('button', { name: 'Remove selected items' })).toBeDisabled();

    await user.click(within(row('a1')).getByRole('checkbox'));
    await user.click(within(row('c3')).getByRole('checkbox'));
    expect(screen.getByTestId('selected-count')).toHaveTextContent('2 items selected of 3 items');
    await user.click(screen.getByRole('button', { name: 'Remove 2 items' }));

    const dialog = await screen.findByRole('dialog', {
      name: 'Remove 2 items from the ignore list?',
    });
    expect(within(dialog).getByTestId('remove-count')).toHaveTextContent(
      'Nothing is imported now. The next sync lists each of them as New',
    );
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(server.removals).toEqual([]);

    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    await user.click(screen.getByRole('button', { name: 'Remove 2 items' }));
    await user.click(
      within(
        await screen.findByRole('dialog', { name: 'Remove 2 items from the ignore list?' }),
      ).getByRole('button', { name: 'Remove 2 items' }),
    );

    expect(await screen.findByTestId('items-removed')).toHaveTextContent(
      'Removed 2 items from the ignore list. Nothing was imported.',
    );
    expect(server.removals).toEqual([['a1', 'c3']]);
    await waitFor(() => {
      expect(document.querySelectorAll('tr[data-item]')).toHaveLength(1);
    });
    expect(row('b2')).toBeVisible();
    expect(screen.getByTestId('selected-count')).toHaveTextContent('None selected of 1 item');
  });

  it('selects every item on the page at once', async () => {
    const server = ignoredServer([testItem('a1'), testItem('b2')]);
    const user = userEvent.setup();
    await openList();
    await screen.findByRole('table', { name: 'Ignored Suno items' });

    await user.click(screen.getByRole('checkbox', { name: 'Select every item on this page' }));
    await user.click(screen.getByRole('button', { name: 'Remove 2 items' }));
    await user.click(
      within(
        await screen.findByRole('dialog', { name: 'Remove 2 items from the ignore list?' }),
      ).getByRole('button', { name: 'Remove 2 items' }),
    );

    expect(await screen.findByTestId('no-ignored-items')).toHaveTextContent(
      'No Suno items are ignored.',
    );
    expect(server.removals).toEqual([['a1', 'b2']]);
  });

  it('keeps the dialog open and says nothing was removed when the removal fails', async () => {
    const server = ignoredServer([testItem('a1')]);
    server.failRemoval = true;
    const user = userEvent.setup();
    await openList();
    await screen.findByRole('table', { name: 'Ignored Suno items' });

    await user.click(within(row('a1')).getByRole('checkbox'));
    await user.click(screen.getByRole('button', { name: 'Remove 1 item' }));
    const dialog = await screen.findByRole('dialog', {
      name: 'Remove 1 item from the ignore list?',
    });
    await user.click(within(dialog).getByRole('button', { name: 'Remove 1 item' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Nothing was removed');
    expect(row('a1')).toBeInTheDocument();
  });

  it('says when nothing is ignored, and is reached from the sidebar', async () => {
    ignoredServer([]);
    const user = userEvent.setup();
    renderApp('/songs');
    await user.click(await screen.findByRole('link', { name: 'Ignored Suno items' }));

    expect(await screen.findByTestId('no-ignored-items')).toHaveTextContent(
      'No Suno items are ignored.',
    );
  });

  it('names a status never seen by a sync, and a not-seen one without a date', () => {
    expect(statusText(testItem('x', { status: null }), 'UTC')).toBe('Not seen by a sync yet');
    expect(statusText(testItem('x', { status: 'not_seen', lastSeenAt: null }), 'UTC')).toBe(
      'Not seen since it was ignored',
    );
  });
});
