import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { RelationshipType } from '../api/relationships';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';
import { relationshipType, SEQUEL, SIBLING, SYSTEM_TYPES } from '../test/songServer';

/** One write the fake server received. */
interface Write {
  method: string;
  path: string;
  ifMatch: string | null;
  body: unknown;
}

/**
 * A fake n8Tracks holding relationship types, answering as the API does: system types refuse
 * changes (409 `system_type`), a write is checked against the type's revision, a new type's name is
 * refused when another type has it in either direction, and a type in use is deleted only with
 * `removeRelationships=true`.
 */
function typeServer(types: RelationshipType[] = [...SYSTEM_TYPES, SEQUEL, SIBLING]) {
  const server = {
    types: types.map((type) => ({ ...type })),
    writes: [] as Write[],
  };
  const key = (name: string) => name.trim().replace(/\s+/g, ' ').toUpperCase();
  const problem = (status: number, code: string, extra: Record<string, unknown> = {}) =>
    jsonResponse(status, { status, code, ...extra });

  stubFetch().mockImplementation((input, init) => {
    const path = requestPath(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    const url = new URL(input instanceof Request ? input.url : input.toString(), document.baseURI);
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (!path.includes('/api/v1/relationship-types')) {
      return Promise.resolve(problem(404, 'not_found'));
    }
    if (method === 'GET') {
      return Promise.resolve(jsonResponse(200, { items: server.types }));
    }
    const ifMatch = new Headers(init?.headers).get('If-Match');
    const body: unknown = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
    server.writes.push({ method, path: `${path}${url.search}`, ifMatch, body });
    const sent = body as { name: string; reverseName: string } | undefined;
    if (method === 'POST' && sent !== undefined) {
      const created = relationshipType(200 + server.types.length, sent.name, sent.reverseName, {
        symmetric: key(sent.name) === key(sent.reverseName),
      });
      server.types.push(created);
      return Promise.resolve(jsonResponse(201, created));
    }
    const id = path.split('/').at(-1) ?? '';
    const type = server.types.find((candidate) => candidate.id === id);
    if (type === undefined) {
      return Promise.resolve(problem(404, 'not_found'));
    }
    if (type.system) {
      return Promise.resolve(problem(409, 'system_type', { current: type }));
    }
    if (ifMatch !== `"${String(type.revision)}"`) {
      return Promise.resolve(problem(409, 'revision_conflict', { current: type }));
    }
    if (method === 'DELETE') {
      if (type.relationshipCount > 0 && url.searchParams.get('removeRelationships') !== 'true') {
        return Promise.resolve(
          problem(409, 'relationship_type_in_use', { relationshipCount: type.relationshipCount }),
        );
      }
      server.types = server.types.filter((candidate) => candidate.id !== id);
      return Promise.resolve(new Response(null, { status: 204 }));
    }
    if (sent !== undefined) {
      Object.assign(type, {
        name: sent.name,
        reverseName: sent.reverseName,
        symmetric: key(sent.name) === key(sent.reverseName),
        revision: type.revision + 1,
      });
    }
    return Promise.resolve(jsonResponse(200, type));
  });

  return server;
}

async function openPage() {
  renderApp('/settings/relationships');
  await screen.findByRole('heading', { level: 2, name: 'Relationships' });
  return screen.findByRole('table', { name: 'Relationship types' });
}

function row(name: string): HTMLElement {
  const found = document.querySelector<HTMLElement>(`tr[data-type-name="${name}"]`);
  if (found === null) {
    throw new Error(`No row for ${name}`);
  }
  return found;
}

describe('Settings → Relationships', () => {
  it('lists the nine system types first, which cannot be renamed or deleted, then the user’s', async () => {
    typeServer();
    const table = await openPage();

    const names = [...table.querySelectorAll('tbody tr')].map((tr) =>
      tr.getAttribute('data-type-name'),
    );
    expect(names).toEqual([...SYSTEM_TYPES.map((type) => type.name), 'Sequel to', 'Sibling of']);
    for (const system of SYSTEM_TYPES) {
      const systemRow = row(system.name);
      expect(within(systemRow).getByText('System type')).toBeVisible();
      expect(within(systemRow).getByText(system.reverseName)).toBeVisible();
      expect(within(systemRow).queryByRole('button')).toBeNull();
    }
    expect(within(row('Sequel to')).getByText('Has sequel')).toBeVisible();
    expect(within(row('Sibling of')).getByText('Same both ways')).toBeVisible();
    expect(
      within(row('Sequel to')).getByRole('button', { name: 'Rename Sequel to' }),
    ).toBeVisible();
    expect(
      within(row('Sequel to')).getByRole('button', { name: 'Delete Sequel to' }),
    ).toBeVisible();
  });

  it('adds a type with both names, and refuses a name another type has in either direction', async () => {
    const user = userEvent.setup();
    const server = typeServer();
    await openPage();
    const form = screen.getByRole('form', { name: 'Add a type' });

    await user.type(within(form).getByRole('textbox', { name: 'Name' }), 'Answer to');
    await user.type(within(form).getByRole('textbox', { name: 'Reverse name' }), 'covered BY');
    await user.click(within(form).getByRole('button', { name: 'Add type' }));
    expect(
      await within(form).findByText(
        'The system type Cover / Covered by already uses this name. Choose another.',
      ),
    ).toBeVisible();
    expect(server.writes).toEqual([]);

    await user.clear(within(form).getByRole('textbox', { name: 'Reverse name' }));
    await user.type(within(form).getByRole('textbox', { name: 'Reverse name' }), 'Answered by');
    await user.click(within(form).getByRole('button', { name: 'Add type' }));

    expect(await screen.findByText('Answer to / Answered by is added.')).toBeVisible();
    expect(server.writes).toEqual([
      {
        method: 'POST',
        path: '/api/v1/relationship-types',
        ifMatch: null,
        body: { name: 'Answer to', reverseName: 'Answered by' },
      },
    ]);
    expect(row('Answer to')).toBeInTheDocument();
    expect(within(form).getByRole('textbox', { name: 'Name' })).toHaveValue('');
  });

  it('renames one of the user’s types on its revision', async () => {
    const user = userEvent.setup();
    const server = typeServer();
    await openPage();

    await user.click(screen.getByRole('button', { name: 'Rename Sequel to' }));
    const dialog = await screen.findByRole('dialog', { name: 'Rename Sequel to / Has sequel' });
    await user.clear(within(dialog).getByRole('textbox', { name: 'Reverse name' }));
    await user.type(within(dialog).getByRole('textbox', { name: 'Reverse name' }), 'Followed by');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(
      await screen.findByText('Sequel to / Has sequel is renamed to Sequel to / Followed by.'),
    ).toBeVisible();
    expect(server.writes).toEqual([
      {
        method: 'PATCH',
        path: `/api/v1/relationship-types/${SEQUEL.id}`,
        ifMatch: '"1"',
        body: { name: 'Sequel to', reverseName: 'Followed by' },
      },
    ]);
  });

  it('deletes an unused type directly, and one in use only after confirming how many relationships go', async () => {
    const user = userEvent.setup();
    const server = typeServer([...SYSTEM_TYPES, { ...SEQUEL, relationshipCount: 3 }, SIBLING]);
    await openPage();

    await user.click(screen.getByRole('button', { name: 'Delete Sibling of' }));
    expect(await screen.findByText('Sibling of is deleted.')).toBeVisible();
    expect(server.writes.at(-1)).toMatchObject({
      method: 'DELETE',
      path: `/api/v1/relationship-types/${SIBLING.id}`,
    });

    await user.click(screen.getByRole('button', { name: 'Delete Sequel to' }));
    const dialog = await screen.findByRole('dialog', { name: 'Delete Sequel to / Has sequel' });
    expect(within(dialog).getByTestId('delete-summary')).toHaveTextContent(
      '3 relationships use Sequel to / Has sequel. Deleting the type removes those 3 relationships from their Songs.',
    );
    expect(server.writes).toHaveLength(1);

    await user.click(within(dialog).getByRole('button', { name: 'Delete Sequel to' }));
    expect(
      await screen.findByText('Sequel to / Has sequel is deleted, with its relationships.'),
    ).toBeVisible();
    expect(server.writes.at(-1)).toEqual({
      method: 'DELETE',
      path: `/api/v1/relationship-types/${SEQUEL.id}?removeRelationships=true`,
      ifMatch: '"1"',
      body: undefined,
    });
    await waitFor(() => {
      expect(document.querySelector('tr[data-type-name="Sequel to"]')).toBeNull();
    });
  });
});
