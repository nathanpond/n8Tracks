import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { Credential } from '../api/credentials';
import { formatDateTime } from '../api/timeZone';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';
import { TOKEN_SHOWN_ONCE_MESSAGE } from './CredentialsPage';

const TOKEN = 'n8t_0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefg';

const active: Credential = {
  id: '0199b1a0-0000-7000-8000-000000000001',
  name: 'test script',
  kind: 'api',
  scopes: ['catalog.read'],
  createdUtc: '2026-10-01T09:00:00Z',
  lastUsedUtc: null,
  revokedUtc: null,
  revision: 1,
};

const revoked: Credential = {
  id: '0199b1a0-0000-7000-8000-000000000002',
  name: 'old browser',
  kind: 'extension',
  scopes: ['catalog.read', 'songs.write'],
  createdUtc: '2026-09-01T09:00:00Z',
  lastUsedUtc: '2026-09-20T18:30:00Z',
  revokedUtc: '2026-09-21T08:15:00Z',
  revision: 2,
};

function problem(status: number, code: string, extra: Record<string, unknown> = {}): Response {
  return new Response(JSON.stringify({ status, code, title: 'refused', ...extra }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

type Handler = (init: RequestInit | undefined) => Response;

/**
 * A backend holding `list`: GET credentials answers it, the health report names UTC, and each
 * write is answered by the handler given for it (by method and path suffix).
 */
function backend(list: Credential[], writes: Record<string, Handler> = {}) {
  const state = { list };
  const mock = stubFetch();
  mock.mockImplementation((input, init) => {
    const path = requestPath(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (method === 'GET' && path.endsWith('/api/v1/credentials')) {
      return Promise.resolve(jsonResponse(200, state.list));
    }
    const key = Object.keys(writes).find((candidate) => {
      const [m, suffix] = candidate.split(' ');
      return m === method && suffix !== undefined && path.endsWith(suffix);
    });
    const handler = key === undefined ? undefined : writes[key];
    return Promise.resolve(handler ? handler(init) : problem(404, 'not_found'));
  });
  return { mock, state };
}

function writesTo(mock: ReturnType<typeof stubFetch>, method: string, suffix: string) {
  return mock.mock.calls.filter(
    ([input, init]) => init?.method === method && requestPath(input).endsWith(suffix),
  );
}

async function table() {
  return screen.findByRole('table', { name: 'Credentials' });
}

function row(name: string): HTMLElement {
  const header = screen.getByRole('rowheader', { name });
  const found = header.closest('tr');
  if (!found) {
    throw new Error(`No row for ${name}`);
  }
  return found;
}

describe('Settings → Credentials', () => {
  it('lists each credential with its kind, scopes, and dates, never used as Never, revoked ones marked', async () => {
    backend([active, revoked]);

    renderApp('/settings/credentials');

    expect(await screen.findByRole('heading', { level: 2, name: 'Credentials' })).toBeVisible();
    await table();
    const first = row('test script');
    expect(within(first).getByText('API')).toBeVisible();
    expect(within(first).getByText('catalog.read')).toBeVisible();
    expect(within(first).getByText(formatDateTime(active.createdUtc, 'UTC'))).toBeVisible();
    expect(within(first).getByText('Never')).toBeVisible();
    expect(within(first).getByText('Active')).toBeVisible();
    expect(within(first).getByRole('button', { name: 'Revoke test script' })).toBeVisible();

    const second = row('old browser');
    expect(within(second).getByText('Extension')).toBeVisible();
    expect(within(second).getByText('songs.write')).toBeVisible();
    expect(within(second).getByText(formatDateTime('2026-09-20T18:30:00Z', 'UTC'))).toBeVisible();
    expect(
      within(second).getByText(`Revoked ${formatDateTime('2026-09-21T08:15:00Z', 'UTC')}`),
    ).toBeVisible();
    // A revoked credential can be neither renamed nor revoked again.
    expect(within(second).queryByRole('button')).not.toBeInTheDocument();

    // No token is shown anywhere in the list.
    expect(document.body).not.toHaveTextContent('n8t_');
  });

  it('filters the list by kind', async () => {
    backend([active, revoked]);
    const user = userEvent.setup();

    renderApp('/settings/credentials');
    await table();
    await user.click(screen.getByRole('radio', { name: 'Extension' }));

    expect(screen.queryByRole('rowheader', { name: 'test script' })).not.toBeInTheDocument();
    expect(screen.getByRole('rowheader', { name: 'old browser' })).toBeVisible();

    await user.click(screen.getByRole('radio', { name: 'MCP gateway' }));
    expect(screen.getByText('There are no MCP gateway credentials.')).toBeVisible();
  });

  it('creates a credential and shows its token once, with a copy control, until the dialog is closed', async () => {
    const created = {
      ...active,
      id: '0199b1a0-0000-7000-8000-000000000003',
      name: 'gateway',
      kind: 'mcp-gateway',
      scopes: ['catalog.read', 'songs.write'],
    };
    const { mock, state } = backend([], {
      'POST /api/v1/credentials': () => {
        state.list = [created];
        return jsonResponse(201, { ...created, token: TOKEN });
      },
    });
    const user = userEvent.setup();
    // After userEvent.setup(), which puts its own clipboard in place.
    const writeText = vi.fn(() => Promise.resolve());
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    renderApp('/settings/credentials');
    expect(await screen.findByText('There are no credentials yet.')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Create credential' }));

    const form = await screen.findByRole('form', { name: 'Create credential' });
    await user.type(within(form).getByLabelText(/^Name/), 'gateway');
    await user.click(within(form).getByRole('radio', { name: 'MCP gateway' }));
    await user.click(within(form).getByRole('checkbox', { name: /songs\.write/ }));
    await user.click(within(form).getByRole('checkbox', { name: /catalog\.read/ }));
    await user.click(within(form).getByRole('button', { name: 'Create credential' }));

    const dialog = await screen.findByRole('dialog', { name: 'Copy the new token' });
    expect(within(dialog).getByText(TOKEN_SHOWN_ONCE_MESSAGE)).toBeVisible();
    expect(within(dialog).getByLabelText('Token')).toHaveValue(TOKEN);
    await user.click(within(dialog).getByRole('button', { name: 'Copy token' }));
    expect(writeText).toHaveBeenCalledWith(TOKEN);
    expect(await within(dialog).findByText('The token is on the clipboard.')).toBeVisible();

    const [call] = writesTo(mock, 'POST', '/api/v1/credentials');
    expect(new Headers(call?.[1]?.headers).get('X-N8Tracks-Request')).toBe('1');
    expect(JSON.parse(call?.[1]?.body as string)).toEqual({
      name: 'gateway',
      kind: 'mcp-gateway',
      scopes: ['songs.write', 'catalog.read'],
    });

    // The list behind the dialog was reloaded; closing the dialog forgets the token for good.
    await user.click(within(dialog).getByRole('button', { name: 'Done' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    expect(screen.getByRole('rowheader', { name: 'gateway' })).toBeVisible();
    expect(document.body).not.toHaveTextContent(TOKEN);

    // Opening the dialog again starts a new, empty form.
    await user.click(screen.getByRole('button', { name: 'Create credential' }));
    const again = await screen.findByRole('form', { name: 'Create credential' });
    expect(within(again).getByLabelText(/^Name/)).toHaveValue('');
    expect(document.body).not.toHaveTextContent(TOKEN);
  });

  it("shows the API's field errors in the create dialog and keeps what was typed", async () => {
    backend([], {
      'POST /api/v1/credentials': () =>
        problem(422, 'validation_failed', {
          errors: {
            name: ['Another credential already has this name.'],
            scopes: ['Choose at least one scope.'],
          },
        }),
    });
    const user = userEvent.setup();

    renderApp('/settings/credentials');
    await screen.findByText('There are no credentials yet.');
    await user.click(screen.getByRole('button', { name: 'Create credential' }));
    const form = await screen.findByRole('form', { name: 'Create credential' });
    await user.type(within(form).getByLabelText(/^Name/), 'test script');
    await user.click(within(form).getByRole('button', { name: 'Create credential' }));

    expect(
      await within(form).findByText('Another credential already has this name.'),
    ).toBeVisible();
    expect(within(form).getByText('Choose at least one scope.')).toBeVisible();
    expect(within(form).getByLabelText(/^Name/)).toHaveValue('test script');
    expect(within(form).getByLabelText(/^Name/)).toHaveAttribute('aria-invalid', 'true');
    expect(screen.queryByLabelText('Token')).not.toBeInTheDocument();
  });

  it('asks before revoking, and revokes only on confirmation', async () => {
    const { mock, state } = backend([active], {
      'POST /revoke': () => {
        state.list = [{ ...active, revokedUtc: '2026-10-05T10:00:00Z', revision: 2 }];
        return jsonResponse(200, state.list[0]);
      },
    });
    const user = userEvent.setup();

    renderApp('/settings/credentials');
    await table();

    await user.click(screen.getByRole('button', { name: 'Revoke test script' }));
    const confirm = await screen.findByRole('dialog', { name: 'Revoke credential?' });
    expect(within(confirm).getByText(/cannot\s+be undone/)).toBeVisible();
    await user.click(within(confirm).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    expect(writesTo(mock, 'POST', '/revoke')).toHaveLength(0);

    await user.click(screen.getByRole('button', { name: 'Revoke test script' }));
    const again = await screen.findByRole('dialog', { name: 'Revoke credential?' });
    await user.click(within(again).getByRole('button', { name: 'Revoke credential' }));

    expect(
      await screen.findByText(`Revoked ${formatDateTime('2026-10-05T10:00:00Z', 'UTC')}`),
    ).toBeVisible();
    const [call] = writesTo(mock, 'POST', '/revoke');
    expect(requestPath(call?.[0] ?? '')).toBe(`/api/v1/credentials/${active.id}/revoke`);
    expect(new Headers(call?.[1]?.headers).get('X-N8Tracks-Request')).toBe('1');
  });

  it('renames with the revision it loaded, and says so when the credential changed meanwhile', async () => {
    const { mock, state } = backend([active], {
      [`PATCH /api/v1/credentials/${active.id}`]: (init) => {
        const ifMatch = new Headers(init?.headers).get('If-Match');
        const current = state.list[0] ?? active;
        if (ifMatch !== `"${String(current.revision)}"`) {
          return problem(409, 'revision_conflict', { current });
        }
        const { name } = JSON.parse(init?.body as string) as { name: string };
        state.list = [{ ...current, name, revision: current.revision + 1 }];
        return jsonResponse(200, state.list[0]);
      },
    });
    const user = userEvent.setup();

    renderApp('/settings/credentials');
    await table();
    await user.click(screen.getByRole('button', { name: 'Rename test script' }));
    const form = await screen.findByRole('form', { name: 'Rename credential' });
    const input = within(form).getByLabelText(/^Name/);
    expect(input).toHaveValue('test script');
    await user.clear(input);
    await user.type(input, 'nightly export');
    await user.click(within(form).getByRole('button', { name: 'Save name' }));

    expect(await screen.findByRole('rowheader', { name: 'nightly export' })).toBeVisible();
    const [call] = writesTo(mock, 'PATCH', active.id);
    expect(new Headers(call?.[1]?.headers).get('If-Match')).toBe('"1"');

    // Someone renamed it elsewhere after this page loaded it at revision 2.
    state.list = [{ ...active, name: 'renamed elsewhere', revision: 3 }];
    await user.click(screen.getByRole('button', { name: 'Rename nightly export' }));
    const stale = await screen.findByRole('form', { name: 'Rename credential' });
    await user.click(within(stale).getByRole('button', { name: 'Save name' }));

    expect(
      await within(stale).findByText(/changed elsewhere: it is now called “renamed elsewhere”/),
    ).toBeVisible();
    expect(
      await screen.findByRole('rowheader', { name: 'renamed elsewhere', hidden: true }),
    ).toBeInTheDocument();
  });

  it('says so when the list cannot be loaded, and tries again', async () => {
    const { mock } = backend([active]);
    mock.mockImplementationOnce((input) =>
      Promise.resolve(
        requestPath(input).endsWith('/health')
          ? jsonResponse(200, healthyReport)
          : problem(500, 'internal_error'),
      ),
    );
    mock.mockImplementationOnce((input) =>
      Promise.resolve(
        requestPath(input).endsWith('/health')
          ? jsonResponse(200, healthyReport)
          : problem(500, 'internal_error'),
      ),
    );
    const user = userEvent.setup();

    renderApp('/settings/credentials');

    expect(await screen.findByText('Credentials could not be loaded')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('rowheader', { name: 'test script' })).toBeVisible();
  });
});
