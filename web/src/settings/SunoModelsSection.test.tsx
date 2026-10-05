import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { SunoModel, SunoModelList } from '../api/sunoModels';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';

const V6: SunoModel = {
  id: '01a10a6e-dd00-7000-8000-000000000001',
  name: 'v6',
  note: null,
  order: 1,
  retired: false,
  discovered: false,
  versionCount: 2,
};
const V6_WILD: SunoModel = {
  id: '01a10a6e-dd01-7001-8000-000000000002',
  name: 'v6-wild',
  note: 'Pro plan',
  order: 2,
  retired: false,
  discovered: false,
  versionCount: 0,
};
const V6_MINI: SunoModel = {
  id: '01a10a6e-dd02-7002-8000-000000000003',
  name: 'v6-mini',
  note: null,
  order: 3,
  retired: false,
  discovered: false,
  versionCount: 1,
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
 * A fake n8Tracks holding a model list, answering as the API does: every write is checked against
 * the list revision (409 `revision_conflict` with `current` when stale), applied, and answered with
 * the whole list at the next revision. Renaming or deleting a model with Versions is 409
 * `model_in_use`. `server.changeElsewhere` plays another tab; `server.next` answers the next write
 * some other way.
 */
function modelServer(models: SunoModel[] = [V6, V6_WILD, V6_MINI]) {
  const server = {
    list: { revision: 1, items: models.map((model) => ({ ...model })) } satisfies SunoModelList,
    writes: [] as Write[],
    next: undefined as (() => Response) | undefined,
    changeElsewhere(change: (items: SunoModel[]) => SunoModel[]) {
      server.list = { revision: server.list.revision + 1, items: change(server.list.items) };
    },
  };
  const renumber = (items: SunoModel[]) =>
    items.map((item, index) => ({ ...item, order: index + 1 }));

  const mock = stubFetch();
  mock.mockImplementation((input, init) => {
    const path = requestPath(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (!path.includes('/api/v1/suno/models')) {
      return Promise.resolve(problem(404, 'not_found'));
    }
    if (method === 'GET') {
      return Promise.resolve(jsonResponse(200, server.list));
    }

    const url = new URL(input instanceof Request ? input.url : input.toString(), document.baseURI);
    const headers = new Headers(init?.headers);
    const body: unknown = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
    server.writes.push({ method, path: url.pathname, ifMatch: headers.get('If-Match'), body });
    const next = server.next;
    if (next) {
      server.next = undefined;
      return Promise.resolve(next());
    }
    if (headers.get('If-Match') !== `"${String(server.list.revision)}"`) {
      return Promise.resolve(problem(409, 'revision_conflict', { current: server.list }));
    }

    const items = server.list.items;
    const id = /\/suno\/models\/([^/?]+)/.exec(url.pathname)?.[1];
    const target = items.find((item) => item.id === id);
    const fields = (body ?? {}) as Record<string, unknown>;
    let changed: SunoModel[];
    if (method === 'POST') {
      const note = typeof fields.note === 'string' ? fields.note.trim() : '';
      changed = [
        ...items,
        {
          id: `01a10a6e-dd90-7000-8000-${String(items.length + 1).padStart(12, '0')}`,
          name: String(fields.name).trim(),
          note: note === '' ? null : note,
          order: items.length + 1,
          retired: false,
          discovered: false,
          versionCount: 0,
        },
      ];
    } else if (method === 'PUT') {
      const ids = fields.ids as string[];
      changed = ids.flatMap((each) => items.filter((item) => item.id === each));
    } else if (method === 'PATCH') {
      if (target && target.versionCount > 0 && typeof fields.name === 'string') {
        return Promise.resolve(problem(409, 'model_in_use', { versionCount: target.versionCount }));
      }
      changed = items.map((item) =>
        item.id === id
          ? {
              ...item,
              ...(typeof fields.name === 'string' ? { name: fields.name.trim() } : {}),
              ...(typeof fields.note === 'string'
                ? { note: fields.note.trim() === '' ? null : fields.note.trim() }
                : {}),
              ...(typeof fields.retired === 'boolean' ? { retired: fields.retired } : {}),
            }
          : item,
      );
    } else {
      if (target && target.versionCount > 0) {
        return Promise.resolve(problem(409, 'model_in_use', { versionCount: target.versionCount }));
      }
      changed = items.filter((item) => item.id !== id);
    }
    server.list = { revision: server.list.revision + 1, items: renumber(changed) };
    return Promise.resolve(jsonResponse(method === 'POST' ? 201 : 200, server.list));
  });
  return server;
}

async function openPage() {
  renderApp('/settings/suno');
  return screen.findByRole('list', { name: 'Suno models' });
}

function rowOf(name: string): HTMLElement {
  const row = document.querySelector<HTMLElement>(`li[data-model-name="${name}"]`);
  if (!row) {
    throw new Error(`No row for ${name}`);
  }
  return row;
}

function names(): string[] {
  return [...document.querySelectorAll('li[data-model-name]')].map(
    (row) => row.getAttribute('data-model-name') ?? '',
  );
}

function status(): HTMLElement {
  const region = document.querySelector<HTMLElement>('[role="status"][aria-live="polite"]');
  if (!region) {
    throw new Error('No status region');
  }
  return region;
}

describe('Settings → Suno', () => {
  it('is in the Settings group of the sidebar', async () => {
    modelServer();
    const user = userEvent.setup();
    renderApp('/settings/account');

    await user.click(await screen.findByRole('link', { name: 'Suno' }));

    expect(await screen.findByRole('heading', { level: 2, name: 'Suno' })).toBeVisible();
    expect(screen.getByRole('heading', { level: 3, name: 'Models' })).toBeVisible();
  });

  it('lists the models in order with their note, Version count, and whether they are retired', async () => {
    modelServer([V6, V6_WILD, { ...V6_MINI, retired: true, discovered: true }]);

    await openPage();

    expect(names()).toEqual(['v6', 'v6-wild', 'v6-mini']);
    expect(within(rowOf('v6')).getByText('2 Versions')).toBeVisible();
    expect(within(rowOf('v6-wild')).getByText('No Versions')).toBeVisible();
    expect(within(rowOf('v6-wild')).getByText('Pro plan')).toBeVisible();
    expect(within(rowOf('v6-mini')).getByText('1 Version')).toBeVisible();
    expect(within(rowOf('v6-mini')).getByText('Retired')).toBeVisible();
    expect(within(rowOf('v6-mini')).getByText('Discovered')).toBeVisible();
    expect(within(rowOf('v6')).queryByText('Retired')).not.toBeInTheDocument();

    // A model Versions name cannot be deleted; an unused one can.
    expect(within(rowOf('v6')).getByRole('button', { name: 'Delete v6' })).toBeDisabled();
    expect(within(rowOf('v6-wild')).getByRole('button', { name: 'Delete v6-wild' })).toBeEnabled();
    expect(within(rowOf('v6-mini')).getByRole('button', { name: 'Restore v6-mini' })).toBeEnabled();
  });

  it('adds a model with a note at the end, checking the name before sending it', async () => {
    const server = modelServer();
    const user = userEvent.setup();
    await openPage();
    const input = screen.getByRole('textbox', { name: /New model/ });

    for (const [typed, message] of [
      ['  V6-MINI ', 'Another model already has this name.'],
      ['a'.repeat(51), 'Use at most 50 characters.'],
    ] as const) {
      await user.clear(input);
      await user.type(input, typed);
      await user.click(screen.getByRole('button', { name: 'Add model' }));
      expect(await screen.findByText(message)).toBeVisible();
    }
    await user.clear(input);
    await user.click(screen.getByRole('button', { name: 'Add model' }));
    expect(await screen.findByText('Enter a name.')).toBeVisible();
    expect(server.writes).toEqual([]);

    await user.type(input, ' v7 ');
    await user.type(screen.getByRole('textbox', { name: /^Note/ }), 'Beta');
    await user.click(screen.getByRole('button', { name: 'Add model' }));

    await waitFor(() => {
      expect(names()).toEqual(['v6', 'v6-wild', 'v6-mini', 'v7']);
    });
    expect(server.writes).toEqual([
      {
        method: 'POST',
        path: '/api/v1/suno/models',
        ifMatch: '"1"',
        body: { name: ' v7 ', note: 'Beta' },
      },
    ]);
    expect(input).toHaveValue('');
    expect(within(status()).getByText('v7 is added at the end.')).toBeVisible();
    expect(within(rowOf('v7')).getByText('Beta')).toBeVisible();
  });

  it('walks the Demo: adds v7, moves it to the top, and retires v6-mini', async () => {
    const server = modelServer();
    const user = userEvent.setup();
    await openPage();

    await user.type(screen.getByRole('textbox', { name: /New model/ }), 'v7');
    await user.click(screen.getByRole('button', { name: 'Add model' }));
    await waitFor(() => {
      expect(names()).toEqual(['v6', 'v6-wild', 'v6-mini', 'v7']);
    });

    for (const expected of [
      ['v6', 'v6-wild', 'v7', 'v6-mini'],
      ['v6', 'v7', 'v6-wild', 'v6-mini'],
      ['v7', 'v6', 'v6-wild', 'v6-mini'],
    ]) {
      await user.click(within(rowOf('v7')).getByRole('button', { name: 'Move v7 up' }));
      await waitFor(() => {
        expect(names()).toEqual(expected);
      });
    }
    expect(within(rowOf('v7')).getByRole('button', { name: 'Move v7 up' })).toBeDisabled();
    await waitFor(() => {
      expect(within(rowOf('v7')).getByRole('button', { name: 'Move v7 down' })).toHaveFocus();
    });
    expect(within(status()).getByText('Moved v7 to position 1 of 4.')).toBeVisible();

    await user.click(within(rowOf('v6-mini')).getByRole('button', { name: 'Retire v6-mini' }));

    expect(await within(rowOf('v6-mini')).findByText('Retired')).toBeVisible();
    expect(
      within(status()).getByText(
        'v6-mini is retired: it is no longer offered, and Versions that name it keep it.',
      ),
    ).toBeVisible();
    expect(server.writes.map((write) => `${write.method} ${write.ifMatch ?? ''}`)).toEqual([
      'POST "1"',
      'PUT "2"',
      'PUT "3"',
      'PUT "4"',
      'PATCH "5"',
    ]);
    expect(server.writes.at(-1)?.body).toEqual({ retired: true });
    expect(server.writes.at(-2)?.body).toEqual({
      ids: [server.list.items[0]?.id, V6.id, V6_WILD.id, V6_MINI.id],
    });

    // Restoring offers it again.
    await user.click(within(rowOf('v6-mini')).getByRole('button', { name: 'Restore v6-mini' }));
    await waitFor(() => {
      expect(within(rowOf('v6-mini')).queryByText('Retired')).not.toBeInTheDocument();
    });
  });

  it('keeps the last model offered: it cannot be retired or deleted', async () => {
    modelServer([V6, { ...V6_WILD, retired: true }]);
    await openPage();

    expect(within(rowOf('v6')).getByRole('button', { name: 'Retire v6' })).toBeDisabled();
    expect(within(rowOf('v6')).getByRole('button', { name: 'Delete v6' })).toBeDisabled();
    expect(
      within(rowOf('v6')).getByText(
        'The only model offered: restore or add another before retiring or deleting this one.',
      ),
    ).toBeVisible();
  });

  it('renames an unused model and annotates a used one, whose name stays', async () => {
    const server = modelServer();
    const user = userEvent.setup();
    await openPage();

    await user.click(within(rowOf('v6-wild')).getByRole('button', { name: 'Edit v6-wild' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit v6-wild' });
    const name = within(dialog).getByRole('textbox', { name: 'Name' });
    await user.clear(name);
    await user.type(name, 'v6-wilder');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => {
      expect(names()).toEqual(['v6', 'v6-wilder', 'v6-mini']);
    });
    expect(server.writes.at(-1)?.body).toEqual({ name: 'v6-wilder' });

    await user.click(within(rowOf('v6')).getByRole('button', { name: 'Edit v6' }));
    const used = await screen.findByRole('dialog', { name: 'Edit v6' });
    expect(within(used).getByRole('textbox', { name: 'Name' })).toBeDisabled();
    expect(within(used).getByText('2 Versions name this model, so its name stays.')).toBeVisible();
    await user.type(within(used).getByRole('textbox', { name: /^Note/ }), 'The default');
    await user.click(within(used).getByRole('button', { name: 'Save' }));

    expect(await within(rowOf('v6')).findByText('The default')).toBeVisible();
    expect(server.writes.at(-1)?.body).toEqual({ note: 'The default' });
  });

  it('says why a model a Version now names was not deleted', async () => {
    const server = modelServer();
    server.next = () => problem(409, 'model_in_use', { versionCount: 1 });
    const user = userEvent.setup();
    await openPage();

    await user.click(within(rowOf('v6-wild')).getByRole('button', { name: 'Delete v6-wild' }));

    expect(
      await within(status()).findByText(
        '1 Version names v6-wild, so it cannot be renamed or deleted. Retire it instead to stop offering it.',
      ),
    ).toBeVisible();
    expect(names()).toEqual(['v6', 'v6-wild', 'v6-mini']);
  });

  it('reloads the list when it was changed elsewhere and applies nothing', async () => {
    const server = modelServer();
    const user = userEvent.setup();
    await openPage();
    server.changeElsewhere((items) => [...items, { ...V6_WILD, id: 'elsewhere', name: 'v8' }]);

    await user.click(within(rowOf('v6-wild')).getByRole('button', { name: 'Delete v6-wild' }));

    await waitFor(() => {
      expect(names()).toEqual(['v6', 'v6-wild', 'v6-mini', 'v8']);
    });
    expect(within(status()).getByText(/changed somewhere else/)).toBeVisible();
  });
});
