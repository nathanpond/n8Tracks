import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { ManagedTag } from '../api/tags';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';

const RUNNING: ManagedTag = {
  id: '01a10a6e-dc80-7000-8000-0000000000a1',
  name: 'running',
  colour: 'gray',
  songCount: 3,
  revision: 1,
};
const RUNING: ManagedTag = {
  id: '01a10a6e-dc80-7000-8000-0000000000a2',
  name: 'runing',
  colour: 'red',
  songCount: 1,
  revision: 1,
};
const SUMMER: ManagedTag = {
  id: '01a10a6e-dc80-7000-8000-0000000000a3',
  name: 'summer',
  colour: 'pink',
  songCount: 2,
  revision: 2,
};
const NIGHT: ManagedTag = {
  id: '01a10a6e-dc80-7000-8000-0000000000a4',
  name: 'Night',
  colour: 'grape',
  songCount: 0,
  revision: 1,
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
 * A fake n8Tracks holding Tags, answering edit, merge, and delete as the API does: each write is
 * checked against the Tag's own revision (409 `revision_conflict` when stale), a merge keeps the
 * target's colour, and a Tag in use is deleted only with `removeFromSongs=true`. The Songs list
 * answers `total` with `server.songsWithAny` for any Tag filter. `server.next` answers the next
 * write some other way.
 */
function tagServer(tags: ManagedTag[] = [RUNNING, RUNING, SUMMER, NIGHT]) {
  const server = {
    tags: tags.map((tag) => ({ ...tag })),
    writes: [] as Write[],
    countQueries: [] as string[],
    songsWithAny: 0,
    next: undefined as (() => Response) | undefined,
  };

  stubFetch().mockImplementation((input, init) => {
    const path = requestPath(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    const url = new URL(input instanceof Request ? input.url : input.toString(), document.baseURI);
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (url.pathname.endsWith('/api/v1/songs')) {
      server.countQueries.push(url.search);
      return Promise.resolve(
        jsonResponse(200, { items: [], page: 1, pageSize: 1, total: server.songsWithAny }),
      );
    }
    if (!url.pathname.includes('/api/v1/tags')) {
      return Promise.resolve(problem(404, 'not_found'));
    }
    if (method === 'GET') {
      return Promise.resolve(jsonResponse(200, { items: server.tags }));
    }

    const headers = new Headers(init?.headers);
    const body: unknown = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
    server.writes.push({
      method,
      path: url.pathname.replace(/^.*\/api\/v1\//, '') + url.search,
      ifMatch: headers.get('If-Match'),
      body,
    });
    const next = server.next;
    if (next) {
      server.next = undefined;
      return Promise.resolve(next());
    }
    const id = /\/tags\/([^/?]+)/.exec(url.pathname)?.[1];
    const tag = server.tags.find((each) => each.id === id);
    if (tag === undefined) {
      return Promise.resolve(problem(404, 'not_found'));
    }
    if (headers.get('If-Match') !== `"${String(tag.revision)}"`) {
      return Promise.resolve(problem(409, 'revision_conflict', { current: tag }));
    }
    const fields = (body ?? {}) as Record<string, unknown>;
    if (method === 'PATCH') {
      if (typeof fields.name === 'string') {
        const name = fields.name.trim().replace(/\s+/g, ' ');
        const holder = server.tags.find(
          (each) => each.id !== id && each.name.toUpperCase() === name.toUpperCase(),
        );
        if (holder) {
          return Promise.resolve(problem(409, 'tag_name_taken', { tagId: holder.id, tag: holder }));
        }
        tag.name = name;
      }
      if (typeof fields.colour === 'string') {
        tag.colour = fields.colour;
      }
      tag.revision += 1;
      return Promise.resolve(jsonResponse(200, tag));
    }
    if (method === 'POST') {
      const sources = fields.sourceIds as string[];
      tag.songCount = server.songsWithAny || tag.songCount;
      tag.revision += 1;
      server.tags = server.tags.filter((each) => !sources.includes(each.id));
      return Promise.resolve(jsonResponse(200, tag));
    }
    if (tag.songCount > 0 && url.searchParams.get('removeFromSongs') !== 'true') {
      return Promise.resolve(problem(409, 'tag_in_use', { songCount: tag.songCount }));
    }
    server.tags = server.tags.filter((each) => each.id !== id);
    return Promise.resolve(new Response(null, { status: 204 }));
  });
  return server;
}

async function openPage() {
  renderApp('/settings/tags');
  return screen.findByRole('table', { name: 'Tags' });
}

/** The Tag names in the table's order. */
function names(): string[] {
  return [...document.querySelectorAll('tr[data-tag-name]')].map(
    (row) => row.getAttribute('data-tag-name') ?? '',
  );
}

function rowOf(name: string): HTMLElement {
  const row = document.querySelector<HTMLElement>(`tr[data-tag-name="${name}"]`);
  if (!row) {
    throw new Error(`No row for ${name}`);
  }
  return row;
}

function status(): HTMLElement {
  const region = document.querySelector<HTMLElement>('[role="status"][aria-live="polite"]');
  if (!region) {
    throw new Error('No status region');
  }
  return region;
}

async function dialogNamed(name: string): Promise<HTMLElement> {
  const dialog = await screen.findByRole('dialog', { name });
  await waitFor(() => {
    expect(dialog).toBeVisible();
  });
  return dialog;
}

describe('Settings → Tags', () => {
  it('is in the Settings group of the sidebar', async () => {
    tagServer();
    const user = userEvent.setup();
    renderApp('/settings/account');

    await user.click(await screen.findByRole('link', { name: 'Tags' }));

    expect(await screen.findByRole('heading', { level: 2, name: 'Tags' })).toBeVisible();
  });

  it('lists every Tag in its colour with its Song count, by name and sortable by count', async () => {
    tagServer();
    const user = userEvent.setup();

    const table = await openPage();

    expect(names()).toEqual(['Night', 'runing', 'running', 'summer']);
    const summer = rowOf('summer');
    expect(within(summer).getByRole('rowheader')).toHaveTextContent('summer');
    expect(summer.querySelector('[data-tag-colour]')).toHaveAttribute('data-tag-colour', 'pink');
    expect(within(summer).getByTestId('tag-colour')).toHaveTextContent('Pink');
    expect(within(summer).getAllByRole('cell')[2]).toHaveTextContent('2');
    expect(within(table).getByRole('columnheader', { name: /Name/ })).toHaveAttribute(
      'aria-sort',
      'ascending',
    );

    await user.click(within(table).getByRole('button', { name: /Songs/ }));
    expect(names()).toEqual(['running', 'summer', 'runing', 'Night']);
    await user.click(within(table).getByRole('button', { name: /Songs/ }));
    expect(names()).toEqual(['Night', 'runing', 'summer', 'running']);
  });

  it('says so when there are no Tags yet', async () => {
    tagServer([]);
    renderApp('/settings/tags');

    expect(await screen.findByText(/No Tags yet/)).toBeVisible();
  });

  it('recolours a Tag under its revision, sending only the colour', async () => {
    const server = tagServer();
    const user = userEvent.setup();
    await openPage();

    await user.click(screen.getByRole('button', { name: 'Edit summer' }));
    const dialog = await dialogNamed('Edit summer');
    const colours = within(dialog).getByRole('radiogroup', { name: 'Colour' });
    expect(within(colours).getAllByRole('radio')).toHaveLength(12);
    expect(within(colours).getByRole('radio', { name: 'Pink' })).toBeChecked();
    await user.click(within(colours).getByRole('radio', { name: 'Orange' }));
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(within(rowOf('summer')).getByTestId('tag-colour')).toHaveTextContent('Orange');
    });
    expect(rowOf('summer').querySelector('[data-tag-colour]')).toHaveAttribute(
      'data-tag-colour',
      'orange',
    );
    expect(server.writes).toEqual([
      {
        method: 'PATCH',
        path: `tags/${SUMMER.id}`,
        ifMatch: '"2"',
        body: { colour: 'orange' },
      },
    ]);
    expect(status()).toHaveTextContent('summer is now orange.');
  });

  it('renames a Tag, sending only the name, and closes without a write when nothing changed', async () => {
    const server = tagServer();
    const user = userEvent.setup();
    await openPage();

    await user.click(screen.getByRole('button', { name: 'Edit Night' }));
    let dialog = await dialogNamed('Edit Night');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    expect(server.writes).toEqual([]);

    await user.click(screen.getByRole('button', { name: 'Edit Night' }));
    dialog = await dialogNamed('Edit Night');
    const input = within(dialog).getByRole('textbox', { name: 'Name' });
    await user.clear(input);
    await user.type(input, '  late   night ');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(names()).toContain('late night');
    });
    expect(server.writes).toEqual([
      {
        method: 'PATCH',
        path: `tags/${NIGHT.id}`,
        ifMatch: '"1"',
        body: { name: '  late   night ' },
      },
    ]);
    expect(status()).toHaveTextContent('Night is renamed to late night.');
  });

  it('offers to merge instead when the new name is another Tag’s, and the survivor keeps its colour', async () => {
    const server = tagServer();
    const user = userEvent.setup();
    await openPage();

    await user.click(screen.getByRole('button', { name: 'Edit runing' }));
    const edit = await dialogNamed('Edit runing');
    const input = within(edit).getByRole('textbox', { name: 'Name' });
    await user.clear(input);
    await user.type(input, 'RUNNING');
    await user.click(within(edit).getByRole('button', { name: 'Save' }));

    expect(await within(edit).findByText(/Another Tag is already called/)).toBeVisible();
    expect(server.writes).toEqual([]);
    await user.click(within(edit).getByRole('button', { name: 'Merge into running' }));

    const merge = await dialogNamed('Merge runing');
    expect(within(merge).getByRole('combobox', { name: 'Merge into' })).toHaveValue(RUNNING.id);
    expect(within(merge).getByTestId('merge-summary')).toHaveTextContent(/^1 Song changes\./);
    expect(within(merge).getByTestId('merge-summary')).toHaveTextContent(
      'running keeps its colour, gray.',
    );
    await user.click(within(merge).getByRole('button', { name: 'Merge' }));

    await waitFor(() => {
      expect(names()).not.toContain('runing');
    });
    expect(server.writes).toEqual([
      {
        method: 'POST',
        path: `tags/${RUNNING.id}/merge`,
        ifMatch: '"1"',
        body: { sourceIds: [RUNING.id] },
      },
    ]);
    expect(rowOf('running').querySelector('[data-tag-colour]')).toHaveAttribute(
      'data-tag-colour',
      'gray',
    );
    expect(status()).toHaveTextContent('runing merged into running.');
  });

  it('offers the merge when the API says the name is taken although the list did not show it', async () => {
    const server = tagServer();
    const user = userEvent.setup();
    await openPage();
    // Another tab renamed summer to "Night time" after this page loaded.
    server.tags = server.tags.map((tag) =>
      tag.id === SUMMER.id ? { ...tag, name: 'Night time', revision: 3 } : tag,
    );

    await user.click(screen.getByRole('button', { name: 'Edit Night' }));
    const edit = await dialogNamed('Edit Night');
    const input = within(edit).getByRole('textbox', { name: 'Name' });
    await user.clear(input);
    await user.type(input, 'night TIME');
    await user.click(within(edit).getByRole('button', { name: 'Save' }));

    // The list did not predict it, so the rename was sent and refused.
    await waitFor(() => {
      expect(server.writes).toEqual([
        { method: 'PATCH', path: `tags/${NIGHT.id}`, ifMatch: '"1"', body: { name: 'night TIME' } },
      ]);
    });
    expect(
      await within(edit).findByRole('button', { name: 'Merge into Night time' }),
    ).toBeVisible();
    expect(names()).toContain('Night');

    await user.click(within(edit).getByRole('button', { name: 'Merge into Night time' }));
    const merge = await dialogNamed('Merge Night');
    expect(within(merge).getByRole('combobox', { name: 'Merge into' })).toHaveValue(SUMMER.id);
  });

  it('merges selected Tags into another after stating how many Songs change', async () => {
    const server = tagServer();
    server.songsWithAny = 3;
    const user = userEvent.setup();
    await openPage();
    const merge = screen.getByRole('button', { name: 'Merge into…' });
    expect(merge).toBeDisabled();

    await user.click(screen.getByRole('checkbox', { name: 'Select runing' }));
    await user.click(screen.getByRole('checkbox', { name: 'Select summer' }));
    await user.click(merge);

    const dialog = await dialogNamed('Merge runing and summer');
    expect(await within(dialog).findByText(/^3 Songs change\./)).toBeVisible();
    expect(server.countQueries).toEqual([`?pageSize=1&tag=${RUNING.id}&tag=${SUMMER.id}`]);
    await user.selectOptions(
      within(dialog).getByRole('combobox', { name: 'Merge into' }),
      'running',
    );
    expect(within(dialog).getByTestId('merge-summary')).toHaveTextContent('gets running instead');
    await user.click(within(dialog).getByRole('button', { name: 'Merge' }));

    await waitFor(() => {
      expect(names()).toEqual(['Night', 'running']);
    });
    expect(server.writes[0]).toEqual({
      method: 'POST',
      path: `tags/${RUNNING.id}/merge`,
      ifMatch: '"1"',
      body: { sourceIds: [RUNING.id, SUMMER.id] },
    });
  });

  it('deletes an unused Tag directly', async () => {
    const server = tagServer();
    const user = userEvent.setup();
    await openPage();

    await user.click(screen.getByRole('button', { name: 'Delete Night' }));

    await waitFor(() => {
      expect(names()).not.toContain('Night');
    });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(server.writes).toEqual([
      { method: 'DELETE', path: `tags/${NIGHT.id}`, ifMatch: '"1"', body: undefined },
    ]);
    expect(status()).toHaveTextContent('Night is deleted.');
  });

  it('states how many Songs lose a Tag in use before removing it from them', async () => {
    const server = tagServer();
    const user = userEvent.setup();
    await openPage();

    await user.click(screen.getByRole('button', { name: 'Delete running' }));
    const dialog = await dialogNamed('Delete running');
    expect(within(dialog).getByTestId('delete-summary')).toHaveTextContent(
      /^3 Songs have running\. Deleting it removes it from those 3 Songs\./,
    );
    expect(within(dialog).queryByRole('radio')).not.toBeInTheDocument();
    expect(server.writes).toEqual([]);
    await user.click(within(dialog).getByRole('button', { name: 'Delete running' }));

    await waitFor(() => {
      expect(names()).not.toContain('running');
    });
    expect(server.writes).toEqual([
      {
        method: 'DELETE',
        path: `tags/${RUNNING.id}?removeFromSongs=true`,
        ifMatch: '"1"',
        body: undefined,
      },
    ]);
    expect(status()).toHaveTextContent('running is deleted and removed from its Songs.');
  });

  it('asks first when a Tag thought unused turns out to be in use', async () => {
    const server = tagServer();
    const user = userEvent.setup();
    await openPage();
    server.tags = server.tags.map((tag) => (tag.id === NIGHT.id ? { ...tag, songCount: 1 } : tag));

    await user.click(screen.getByRole('button', { name: 'Delete Night' }));

    const dialog = await dialogNamed('Delete Night');
    expect(within(dialog).getByTestId('delete-summary')).toHaveTextContent(
      /^1 Song has Night\. Deleting it removes it from that Song\./,
    );
  });

  it('reloads the list and changes nothing when the Tag was changed elsewhere', async () => {
    const server = tagServer();
    const user = userEvent.setup();
    await openPage();
    server.tags = server.tags.map((tag) =>
      tag.id === SUMMER.id ? { ...tag, name: 'summertime', revision: 3 } : tag,
    );

    await user.click(screen.getByRole('button', { name: 'Edit summer' }));
    const dialog = await dialogNamed('Edit summer');
    await user.click(within(dialog).getByRole('radio', { name: 'Teal' }));
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(status()).toHaveTextContent(/changed somewhere else/);
    });
    expect(names()).toContain('summertime');
    expect(within(rowOf('summertime')).getByTestId('tag-colour')).toHaveTextContent('Pink');
  });
});
