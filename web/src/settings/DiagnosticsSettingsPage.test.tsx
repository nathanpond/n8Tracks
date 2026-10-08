import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { describeBytes, type LoggingSettings } from '../api/logging';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';

interface Write {
  ifMatch: string | null;
  body: unknown;
}

const DEFAULTS: LoggingSettings = {
  revision: 0,
  level: 'information',
  levelSource: 'environment',
  retentionDays: 14,
  maxMegabytes: 200,
  debugUntil: null,
  folderProblem: null,
};

/**
 * A fake n8Tracks with the log settings; `server.writes` holds each PUT. A write that lowers the
 * retention below `server.deletesBelowDays` needs `confirmDelete`, as the API asks for it.
 */
function loggingServer(initial: LoggingSettings = DEFAULTS) {
  const server = {
    settings: initial,
    writes: [] as Write[],
    failGet: false,
    deletesBelowDays: 0,
    nextWrite: undefined as Response | undefined,
  };
  stubFetch().mockImplementation((input, init) => {
    const path = requestPath(input);
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (path.endsWith('/api/v1/settings/logging')) {
      if ((init?.method ?? 'GET') === 'GET') {
        return Promise.resolve(
          server.failGet
            ? jsonResponse(500, { code: 'internal' })
            : jsonResponse(200, server.settings),
        );
      }
      const ifMatch = new Headers(init?.headers).get('If-Match');
      const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as {
        level: string;
        retentionDays: number;
        maxMegabytes: number;
        confirmDelete: boolean;
      };
      server.writes.push({ ifMatch, body });
      if (server.nextWrite !== undefined) {
        const answer = server.nextWrite;
        server.nextWrite = undefined;
        return Promise.resolve(answer);
      }
      if (ifMatch !== `"${String(server.settings.revision)}"`) {
        return Promise.resolve(
          jsonResponse(409, { code: 'revision_conflict', current: server.settings }),
        );
      }
      if (body.retentionDays < server.deletesBelowDays && !body.confirmDelete) {
        return Promise.resolve(
          jsonResponse(409, { code: 'confirmation_required', files: 3, bytes: 3 * 1024 * 1024 }),
        );
      }
      server.settings = {
        revision: server.settings.revision + 1,
        level: body.level,
        levelSource: 'setting',
        retentionDays: body.retentionDays,
        maxMegabytes: body.maxMegabytes,
        debugUntil: body.level === 'debug' ? '2026-10-02T09:00:00Z' : null,
        folderProblem: null,
      };
      return Promise.resolve(jsonResponse(200, server.settings));
    }
    return Promise.resolve(jsonResponse(404, { code: 'not_found' }));
  });
  return server;
}

async function openDiagnostics() {
  renderApp('/settings/diagnostics');
  await screen.findByRole('heading', { level: 2, name: 'Diagnostics' });
  return screen.findByRole('form', { name: 'Log settings' });
}

const days = () => screen.getByRole('textbox', { name: /^Days to keep log files/ });
const size = () => screen.getByRole('textbox', { name: /^Most space for log files/ });

async function chooseLevel(user: ReturnType<typeof userEvent.setup>, label: string) {
  await user.selectOptions(levelField(), label);
}

const levelField = () => screen.getByRole('combobox', { name: /^Log level/ });

describe('Settings → Diagnostics', () => {
  it('shows the environment level and the default limits, and saves Debug with its end', async () => {
    const server = loggingServer();
    const user = userEvent.setup();
    const form = await openDiagnostics();

    expect(screen.getByTestId('logging-summary')).toHaveTextContent(
      'Logging at Information, set by N8TRACKS_LOG_LEVEL until you save a level here.',
    );
    expect(screen.getByTestId('logging-summary')).toHaveTextContent(
      'Log files are kept for 14 days and take at most 200 MB together.',
    );
    expect(days()).toHaveValue('14');
    expect(size()).toHaveValue('200');

    await chooseLevel(user, 'Debug');
    await user.click(within(form).getByRole('button', { name: 'Save log settings' }));

    await waitFor(() => {
      expect(screen.getByTestId('logging-status')).toHaveTextContent('Log settings saved.');
    });
    expect(server.writes).toEqual([
      {
        ifMatch: '"0"',
        body: { level: 'debug', retentionDays: 14, maxMegabytes: 200, confirmDelete: false },
      },
    ]);
    expect(screen.getByTestId('logging-summary')).toHaveTextContent('Logging at Debug.');
    expect(screen.getByTestId('debug-until')).toHaveTextContent(
      'Debug switches back to Information on',
    );
    expect(screen.getByTestId('debug-until')).toHaveTextContent('2026');
  });

  it('shows a level only the environment can set, and offers the four', async () => {
    loggingServer({ ...DEFAULTS, level: 'trace' });
    await openDiagnostics();

    expect(screen.getByTestId('logging-summary')).toHaveTextContent('Logging at Trace, set by');
    expect(
      within(levelField())
        .getAllByRole('option')
        .map((option) => option.textContent),
    ).toEqual(['Error', 'Warning', 'Information', 'Debug']);
  });

  it.each([
    ['days', '0', 'Enter a whole number of days from 1 to 90.'],
    ['days', '91', 'Enter a whole number of days from 1 to 90.'],
    ['size', '9', 'Enter a whole number of megabytes from 10 to 5,120 (5 GB).'],
    ['size', '5121', 'Enter a whole number of megabytes from 10 to 5,120 (5 GB).'],
  ])('refuses %s of %s before sending anything', async (field, typed, message) => {
    const server = loggingServer();
    const user = userEvent.setup();
    const form = await openDiagnostics();
    const input = field === 'days' ? days() : size();

    await user.clear(input);
    await user.type(input, typed);
    await user.click(within(form).getByRole('button', { name: 'Save log settings' }));

    expect(await within(form).findByText(message)).toBeVisible();
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(server.writes).toEqual([]);
  });

  it.each([
    ['days', '1'],
    ['days', '90'],
    ['size', '10'],
    ['size', '5120'],
  ])('accepts %s of %s', async (field, typed) => {
    const server = loggingServer();
    const user = userEvent.setup();
    const form = await openDiagnostics();
    const input = field === 'days' ? days() : size();

    await user.clear(input);
    await user.type(input, typed);
    await user.click(within(form).getByRole('button', { name: 'Save log settings' }));

    await waitFor(() => {
      expect(server.writes).toHaveLength(1);
    });
  });

  it('asks before limits delete log files, and deletes only once confirmed', async () => {
    const server = loggingServer();
    server.deletesBelowDays = 14;
    const user = userEvent.setup();
    const form = await openDiagnostics();

    await user.clear(days());
    await user.type(days(), '7');
    await user.click(within(form).getByRole('button', { name: 'Save log settings' }));

    const dialog = await screen.findByRole('dialog', { name: 'Delete log files?' });
    expect(within(dialog).getByTestId('deletion-summary')).toHaveTextContent(
      'These limits delete 3 log files (3 MB) now. This cannot be undone.',
    );

    // Cancel saves nothing.
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    expect(server.settings.revision).toBe(0);

    await user.click(within(form).getByRole('button', { name: 'Save log settings' }));
    await user.click(
      within(await screen.findByRole('dialog', { name: 'Delete log files?' })).getByRole('button', {
        name: 'Delete and save',
      }),
    );

    await waitFor(() => {
      expect(screen.getByTestId('logging-status')).toHaveTextContent('Log settings saved.');
    });
    expect(
      server.writes.map((write) => (write.body as { confirmDelete: boolean }).confirmDelete),
    ).toEqual([false, false, true]);
    expect(screen.getByTestId('logging-summary')).toHaveTextContent('kept for 7 days');
  });

  it('shows why log files are not being written', async () => {
    loggingServer({
      ...DEFAULTS,
      folderProblem:
        'n8Tracks is not allowed to write to the log folder. Logs go to standard output only.',
    });
    await openDiagnostics();

    expect(screen.getByText('Log files are not being written')).toBeVisible();
    expect(screen.getByTestId('log-folder-problem')).toHaveTextContent(
      'n8Tracks is not allowed to write to the log folder.',
    );
  });

  it('reloads the settings when they were changed elsewhere', async () => {
    const server = loggingServer();
    const user = userEvent.setup();
    const form = await openDiagnostics();
    server.settings = { ...DEFAULTS, revision: 1, level: 'error', levelSource: 'setting' };

    await user.click(within(form).getByRole('button', { name: 'Save log settings' }));

    expect(await screen.findByText('The log settings were changed elsewhere')).toBeVisible();
    expect(screen.getByTestId('logging-summary')).toHaveTextContent('Logging at Error.');
  });

  it('shows the field error the API answers with, and says so when the save fails', async () => {
    const server = loggingServer();
    server.nextWrite = jsonResponse(422, {
      code: 'validation_failed',
      errors: { level: ['Choose error, warning, information, or debug.'] },
    });
    const user = userEvent.setup();
    const form = await openDiagnostics();

    await user.click(within(form).getByRole('button', { name: 'Save log settings' }));
    expect(
      await within(form).findByText('Choose error, warning, information, or debug.'),
    ).toBeVisible();

    server.nextWrite = jsonResponse(500, { code: 'internal' });
    await user.click(within(form).getByRole('button', { name: 'Save log settings' }));
    expect(await screen.findByText('The log settings were not saved')).toBeVisible();
  });

  it('offers to try again when the settings cannot be loaded', async () => {
    const server = loggingServer();
    server.failGet = true;
    const user = userEvent.setup();
    renderApp('/settings/diagnostics');

    expect(await screen.findByText('The log settings could not be loaded')).toBeVisible();
    server.failGet = false;
    await user.click(screen.getByRole('button', { name: 'Try again' }));

    expect(await screen.findByRole('form', { name: 'Log settings' })).toBeVisible();
  });
});

describe('describeBytes', () => {
  it.each([
    [1, '1 byte'],
    [512, '512 bytes'],
    [2048, '2 KB'],
    [3 * 1024 * 1024 + 200 * 1024, '3.2 MB'],
    [5 * 1024 * 1024 * 1024, '5 GB'],
  ])('describes %d bytes as %s', (bytes, words) => {
    expect(describeBytes(bytes)).toBe(words);
  });
});
