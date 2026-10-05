import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { ManagedState, WorkflowList } from '../api/workflow';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';

const IDEA: ManagedState = {
  id: '01a10a6e-dc80-7000-8000-000000000001',
  name: 'Idea',
  colour: 'yellow',
  order: 1,
  hidden: false,
  songCount: 2,
};
const WRITING: ManagedState = {
  id: '01a10a6e-dc81-7001-8000-000000000002',
  name: 'Writing',
  colour: 'blue',
  order: 2,
  hidden: false,
  songCount: 0,
};
const FINAL: ManagedState = {
  id: '01a10a6e-dc84-7004-8000-000000000005',
  name: 'Final',
  colour: 'green',
  order: 3,
  hidden: false,
  songCount: 1,
};
const ARCHIVED: ManagedState = {
  id: '01a10a6e-dc86-7006-8000-000000000007',
  name: 'Archived',
  colour: 'gray',
  order: 4,
  hidden: true,
  songCount: 0,
};

function problem(status: number, code: string, extra: Record<string, unknown> = {}): Response {
  return new Response(JSON.stringify({ status, code, title: 'refused', ...extra }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

/** One write the fake server received. */
interface Write {
  method: string;
  path: string;
  ifMatch: string | null;
  body: unknown;
}

/**
 * A fake n8Tracks holding a workflow, answering as the API does: every write is checked against
 * the workflow revision (409 `revision_conflict` with `current` when stale), applied, and answered
 * with the whole list at the next revision. `server.changeElsewhere` plays another tab;
 * `server.next` answers the next write some other way.
 */
function workflowServer(states: ManagedState[] = [IDEA, WRITING, FINAL, ARCHIVED]) {
  const server = {
    list: { revision: 1, items: states.map((state) => ({ ...state })) } satisfies WorkflowList,
    writes: [] as Write[],
    next: undefined as (() => Response) | undefined,
    changeElsewhere(change: (items: ManagedState[]) => ManagedState[]) {
      server.list = { revision: server.list.revision + 1, items: change(server.list.items) };
    },
  };
  const renumber = (items: ManagedState[]) =>
    items.map((item, index) => ({ ...item, order: index + 1 }));

  const mock = stubFetch();
  mock.mockImplementation((input, init) => {
    const path = requestPath(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (!path.includes('/api/v1/workflow-states')) {
      return Promise.resolve(problem(404, 'not_found'));
    }
    if (method === 'GET') {
      return Promise.resolve(jsonResponse(200, server.list));
    }

    const url = new URL(input instanceof Request ? input.url : input.toString(), document.baseURI);
    const headers = new Headers(init?.headers);
    const body: unknown = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
    server.writes.push({
      method,
      path: url.pathname + url.search,
      ifMatch: headers.get('If-Match'),
      body,
    });
    const next = server.next;
    if (next) {
      server.next = undefined;
      return Promise.resolve(next());
    }
    if (headers.get('If-Match') !== `"${String(server.list.revision)}"`) {
      return Promise.resolve(problem(409, 'revision_conflict', { current: server.list }));
    }

    const items = server.list.items;
    const id = /\/workflow-states\/([^/?]+)/.exec(url.pathname)?.[1];
    const fields = (body ?? {}) as Record<string, unknown>;
    let changed: ManagedState[];
    if (method === 'POST') {
      changed = [
        ...items,
        {
          id: `01a10a6e-dc90-7000-8000-${String(items.length + 1).padStart(12, '0')}`,
          name: String(fields.name).trim(),
          colour: 'red',
          order: items.length + 1,
          hidden: false,
          songCount: 0,
        },
      ];
    } else if (method === 'PUT') {
      const ids = fields.ids as string[];
      changed = ids.flatMap((each) => items.filter((item) => item.id === each));
    } else if (method === 'PATCH') {
      changed = items.map((item) =>
        item.id === id
          ? {
              ...item,
              ...(typeof fields.name === 'string' ? { name: fields.name.trim() } : {}),
              ...(typeof fields.colour === 'string' ? { colour: fields.colour } : {}),
              ...(typeof fields.hidden === 'boolean' ? { hidden: fields.hidden } : {}),
            }
          : item,
      );
    } else {
      const target = items.find((item) => item.id === id);
      const replacement = url.searchParams.get('replacement');
      if (target && target.songCount > 0 && replacement === null) {
        return Promise.resolve(problem(409, 'state_in_use', { songCount: target.songCount }));
      }
      changed = items
        .filter((item) => item.id !== id)
        .map((item) =>
          item.id === replacement
            ? { ...item, songCount: item.songCount + (target?.songCount ?? 0) }
            : item,
        );
    }
    server.list = { revision: server.list.revision + 1, items: renumber(changed) };
    return Promise.resolve(jsonResponse(method === 'POST' ? 201 : 200, server.list));
  });
  return server;
}

async function openPage() {
  renderApp('/settings/workflow');
  return screen.findByRole('list', { name: 'Workflow states' });
}

function rowOf(name: string): HTMLElement {
  const row = document.querySelector<HTMLElement>(`li[data-state-name="${name}"]`);
  if (!row) {
    throw new Error(`No row for ${name}`);
  }
  return row;
}

function names(): string[] {
  return [...document.querySelectorAll('li[data-state-name]')].map(
    (row) => row.getAttribute('data-state-name') ?? '',
  );
}

function status(): HTMLElement {
  const region = document.querySelector<HTMLElement>('[role="status"][aria-live="polite"]');
  if (!region) {
    throw new Error('No status region');
  }
  return region;
}

describe('Settings → Workflow', () => {
  it('is in the Settings group of the sidebar', async () => {
    workflowServer();
    const user = userEvent.setup();
    renderApp('/settings/account');

    await user.click(await screen.findByRole('link', { name: 'Workflow' }));

    expect(await screen.findByRole('heading', { level: 2, name: 'Workflow' })).toBeVisible();
  });

  it('lists the states in order with their colour, Song count, and whether they are hidden', async () => {
    workflowServer();

    const list = await openPage();

    expect(names()).toEqual(['Idea', 'Writing', 'Final', 'Archived']);
    expect(within(rowOf('Idea')).getByText('2 Songs')).toBeVisible();
    expect(within(rowOf('Writing')).getByText('No Songs')).toBeVisible();
    expect(within(rowOf('Final')).getByText('1 Song')).toBeVisible();
    expect(
      within(rowOf('Final')).getByText('Final').closest('[data-state-colour]'),
    ).toHaveAttribute('data-state-colour', 'green');
    expect(within(rowOf('Archived')).getByText('Hidden')).toBeVisible();
    expect(within(rowOf('Idea')).queryByText('Hidden')).not.toBeInTheDocument();
    expect(within(list).getByRole('button', { name: 'Show Archived' })).toBeVisible();
  });

  it('adds a state at the end, checking the name before sending it', async () => {
    const server = workflowServer();
    const user = userEvent.setup();
    await openPage();
    const input = screen.getByRole('textbox', { name: /New state/ });

    for (const [typed, message] of [
      ['   ', 'Enter a name.'],
      ['  IDEA ', 'Another state already has this name.'],
      ['archived', 'Another state already has this name.'],
      ['a'.repeat(51), 'Use at most 50 characters.'],
    ] as const) {
      await user.clear(input);
      if (typed.trim() !== '') {
        await user.type(input, typed);
      } else {
        fireEvent.change(input, { target: { value: typed } });
      }
      await user.click(screen.getByRole('button', { name: 'Add state' }));
      expect(await screen.findByText(message)).toBeVisible();
    }
    expect(server.writes).toEqual([]);

    await user.clear(input);
    await user.type(input, ' Mixing ');
    await user.click(screen.getByRole('button', { name: 'Add state' }));

    await waitFor(() => {
      expect(names()).toEqual(['Idea', 'Writing', 'Final', 'Archived', 'Mixing']);
    });
    expect(server.writes).toEqual([
      {
        method: 'POST',
        path: '/api/v1/workflow-states',
        ifMatch: '"1"',
        body: { name: ' Mixing ' },
      },
    ]);
    expect(input).toHaveValue('');
    expect(within(status()).getByText('Mixing is added at the end.')).toBeVisible();
  });

  it('shows the name error the API answers', async () => {
    const server = workflowServer();
    server.next = () =>
      problem(422, 'validation_failed', { errors: { name: ['Taken in another tab.'] } });
    const user = userEvent.setup();
    await openPage();

    await user.type(screen.getByRole('textbox', { name: /New state/ }), 'Mixing');
    await user.click(screen.getByRole('button', { name: 'Add state' }));

    expect(await screen.findByText('Taken in another tab.')).toBeVisible();
    expect(names()).toEqual(['Idea', 'Writing', 'Final', 'Archived']);
  });

  it('reorders by keyboard with Move up and Move down, keeping the focus on the button', async () => {
    const server = workflowServer();
    const user = userEvent.setup();
    await openPage();

    within(rowOf('Final')).getByRole('button', { name: 'Move Final up' }).focus();
    await user.keyboard('{Enter}');

    await waitFor(() => {
      expect(names()).toEqual(['Idea', 'Final', 'Writing', 'Archived']);
    });
    expect(server.writes.at(-1)).toEqual({
      method: 'PUT',
      path: '/api/v1/workflow-states/order',
      ifMatch: '"1"',
      body: { ids: [IDEA.id, FINAL.id, WRITING.id, ARCHIVED.id] },
    });
    expect(within(status()).getByText('Moved Final to position 2 of 4.')).toBeVisible();
    await waitFor(() => {
      expect(within(rowOf('Final')).getByRole('button', { name: 'Move Final up' })).toHaveFocus();
    });

    // Up again reaches the top: Move up is then disabled, so the focus goes to Move down.
    await user.keyboard('{Enter}');
    await waitFor(() => {
      expect(names()).toEqual(['Final', 'Idea', 'Writing', 'Archived']);
    });
    expect(within(rowOf('Final')).getByRole('button', { name: 'Move Final up' })).toBeDisabled();
    await waitFor(() => {
      expect(within(rowOf('Final')).getByRole('button', { name: 'Move Final down' })).toHaveFocus();
    });

    await user.keyboard('{Enter}');
    await waitFor(() => {
      expect(names()).toEqual(['Idea', 'Final', 'Writing', 'Archived']);
    });
    expect(server.writes.map((write) => write.ifMatch)).toEqual(['"1"', '"2"', '"3"']);
    expect(
      within(rowOf('Archived')).getByRole('button', { name: 'Move Archived down' }),
    ).toBeDisabled();
  });

  it('reorders by dragging a state onto another', async () => {
    const server = workflowServer();
    await openPage();

    fireEvent.dragStart(rowOf('Archived'), { dataTransfer: { setData: () => undefined } });
    fireEvent.dragOver(rowOf('Writing'));
    fireEvent.drop(rowOf('Writing'));

    await waitFor(() => {
      expect(names()).toEqual(['Idea', 'Archived', 'Writing', 'Final']);
    });
    expect(server.writes.at(-1)?.body).toEqual({
      ids: [IDEA.id, ARCHIVED.id, WRITING.id, FINAL.id],
    });
  });

  it('renames and recolours a state in one save', async () => {
    const server = workflowServer();
    const user = userEvent.setup();
    await openPage();

    await user.click(within(rowOf('Idea')).getByRole('button', { name: 'Edit Idea' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit Idea' });
    const name = within(dialog).getByRole('textbox', { name: 'Name' });
    await user.clear(name);
    await user.type(name, 'Writing');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(await within(dialog).findByText('Another state already has this name.')).toBeVisible();
    expect(server.writes).toEqual([]);

    await user.clear(name);
    await user.type(name, 'Spark');
    await user.click(within(dialog).getByRole('radio', { name: 'Cyan' }));
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(names()[0]).toBe('Spark');
    });
    expect(server.writes).toEqual([
      {
        method: 'PATCH',
        path: `/api/v1/workflow-states/${IDEA.id}`,
        ifMatch: '"1"',
        body: { name: 'Spark', colour: 'cyan' },
      },
    ]);
    expect(
      within(rowOf('Spark')).getByText('Spark').closest('[data-state-colour]'),
    ).toHaveAttribute('data-state-colour', 'cyan');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('hides a state at once and offers Undo, and shows a hidden one', async () => {
    const server = workflowServer();
    const user = userEvent.setup();
    await openPage();

    await user.click(within(rowOf('Writing')).getByRole('button', { name: 'Hide Writing' }));

    expect(await within(status()).findByText('Writing is hidden.')).toBeVisible();
    expect(within(rowOf('Writing')).getByText('Hidden')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Undo' }));

    expect(await within(status()).findByText('Writing is shown.')).toBeVisible();
    expect(within(rowOf('Writing')).queryByText('Hidden')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Undo' })).not.toBeInTheDocument();

    await user.click(within(rowOf('Archived')).getByRole('button', { name: 'Show Archived' }));
    await waitFor(() => {
      expect(within(rowOf('Archived')).queryByText('Hidden')).not.toBeInTheDocument();
    });
    expect(server.writes.map((write) => [write.ifMatch, write.body])).toEqual([
      ['"1"', { hidden: true }],
      ['"2"', { hidden: false }],
      ['"3"', { hidden: false }],
    ]);
  });

  it('does not let the last visible state be hidden or deleted', async () => {
    workflowServer([IDEA, { ...ARCHIVED, order: 2 }]);
    await openPage();

    expect(within(rowOf('Idea')).getByRole('button', { name: 'Hide Idea' })).toBeDisabled();
    expect(within(rowOf('Idea')).getByRole('button', { name: 'Delete Idea' })).toBeDisabled();
    expect(within(rowOf('Idea')).getByText(/The only visible state/)).toBeVisible();
    expect(
      within(rowOf('Archived')).getByRole('button', { name: 'Delete Archived' }),
    ).toBeEnabled();
  });

  it('says so when the API refuses to hide the last visible state', async () => {
    const server = workflowServer();
    server.next = () => problem(409, 'last_visible_state');
    const user = userEvent.setup();
    await openPage();

    await user.click(within(rowOf('Writing')).getByRole('button', { name: 'Hide Writing' }));

    expect(await within(status()).findByText(/At least one state must stay visible/)).toBeVisible();
    expect(within(rowOf('Writing')).queryByText('Hidden')).not.toBeInTheDocument();
  });

  it('deletes a state without Songs at once', async () => {
    const server = workflowServer();
    const user = userEvent.setup();
    await openPage();

    await user.click(within(rowOf('Writing')).getByRole('button', { name: 'Delete Writing' }));

    await waitFor(() => {
      expect(names()).toEqual(['Idea', 'Final', 'Archived']);
    });
    expect(server.writes).toEqual([
      {
        method: 'DELETE',
        path: `/api/v1/workflow-states/${WRITING.id}`,
        ifMatch: '"1"',
        body: undefined,
      },
    ]);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('asks for a replacement, hidden states included, before deleting a state with Songs', async () => {
    const server = workflowServer();
    const user = userEvent.setup();
    await openPage();

    await user.click(within(rowOf('Idea')).getByRole('button', { name: 'Delete Idea' }));
    const dialog = await screen.findByRole('dialog', { name: 'Delete Idea' });
    expect(within(dialog).getByText(/2 Songs are in/)).toBeVisible();
    const select = within(dialog).getByRole('combobox', { name: 'Move its Songs to' });
    expect(
      within(select)
        .getAllByRole('option')
        .map((option) => option.textContent),
    ).toEqual(['Writing', 'Final', 'Archived (hidden)']);
    expect(server.writes).toEqual([]);

    await user.selectOptions(select, FINAL.id);
    await user.click(within(dialog).getByRole('button', { name: 'Move Songs and delete' }));

    await waitFor(() => {
      expect(names()).toEqual(['Writing', 'Final', 'Archived']);
    });
    expect(server.writes).toEqual([
      {
        method: 'DELETE',
        path: `/api/v1/workflow-states/${IDEA.id}?replacement=${FINAL.id}`,
        ifMatch: '"1"',
        body: undefined,
      },
    ]);
    expect(within(rowOf('Final')).getByText('3 Songs')).toBeVisible();
    expect(
      within(status()).getByText('Idea is deleted; its Songs are now in Final.'),
    ).toBeVisible();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('opens the replacement dialog when Songs were put in an empty-looking state elsewhere', async () => {
    const server = workflowServer();
    server.next = () => problem(409, 'state_in_use', { songCount: 4 });
    const user = userEvent.setup();
    await openPage();

    await user.click(within(rowOf('Writing')).getByRole('button', { name: 'Delete Writing' }));

    const dialog = await screen.findByRole('dialog', { name: 'Delete Writing' });
    expect(within(dialog).getByText(/4 Songs are in/)).toBeVisible();
    expect(names()).toEqual(['Idea', 'Writing', 'Final', 'Archived']);
  });

  it('reloads the list with a notice, without applying the change, when the workflow changed elsewhere', async () => {
    const server = workflowServer();
    const user = userEvent.setup();
    await openPage();
    server.changeElsewhere((items) => [
      ...items,
      { ...WRITING, id: '01a10a6e-dc90-7000-8000-000000000099', name: 'Mastering', order: 5 },
    ]);

    await user.click(within(rowOf('Final')).getByRole('button', { name: 'Move Final up' }));

    expect(await within(status()).findByText(/changed somewhere else/)).toBeVisible();
    expect(names()).toEqual(['Idea', 'Writing', 'Final', 'Archived', 'Mastering']);
    expect(server.writes).toHaveLength(1);

    // The next change is based on the reloaded revision.
    await user.click(within(rowOf('Final')).getByRole('button', { name: 'Move Final up' }));
    await waitFor(() => {
      expect(names()).toEqual(['Idea', 'Final', 'Writing', 'Archived', 'Mastering']);
    });
    expect(server.writes.at(-1)?.ifMatch).toBe('"2"');
  });
});
