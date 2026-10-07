import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { MediaScanSchedule } from '../api/mediaScan';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';

interface Write {
  ifMatch: string | null;
  body: unknown;
}

/** A fake n8Tracks with the media scan schedule; `server.writes` holds each PUT. */
function scheduleServer(
  initial: MediaScanSchedule = { enabled: true, intervalMinutes: 15, revision: 0 },
) {
  const server = {
    schedule: initial,
    writes: [] as Write[],
    failGet: false,
    nextWrite: undefined as Response | undefined,
  };
  stubFetch().mockImplementation((input, init) => {
    const path = requestPath(input);
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (path.endsWith('/api/v1/settings/media-scan')) {
      if ((init?.method ?? 'GET') === 'GET') {
        return Promise.resolve(
          server.failGet
            ? jsonResponse(500, { code: 'internal' })
            : jsonResponse(200, server.schedule),
        );
      }
      const ifMatch = new Headers(init?.headers).get('If-Match');
      const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as {
        enabled: boolean;
        intervalMinutes: number;
      };
      server.writes.push({ ifMatch, body });
      if (server.nextWrite !== undefined) {
        const answer = server.nextWrite;
        server.nextWrite = undefined;
        return Promise.resolve(answer);
      }
      if (ifMatch !== `"${String(server.schedule.revision)}"`) {
        return Promise.resolve(
          jsonResponse(409, { code: 'revision_conflict', current: server.schedule }),
        );
      }
      server.schedule = { ...body, revision: server.schedule.revision + 1 };
      return Promise.resolve(jsonResponse(200, server.schedule));
    }
    return Promise.resolve(jsonResponse(404, { code: 'not_found' }));
  });
  return server;
}

async function openLibrary() {
  renderApp('/settings/library');
  await screen.findByRole('heading', { level: 2, name: 'Library' });
  return screen.findByRole('form', { name: 'Scan schedule' });
}

const interval = () => screen.getByRole('textbox', { name: /^Minutes between scans/ });

describe('Settings → Library', () => {
  it('shows the default schedule and saves a new interval on revision 0', async () => {
    const server = scheduleServer();
    const user = userEvent.setup();
    const form = await openLibrary();

    expect(screen.getByTestId('scan-schedule-summary')).toHaveTextContent(
      'Scheduled scans run every 15 minutes, counted from the end of the previous scan.',
    );
    expect(within(form).getByRole('switch', { name: 'Scan on a schedule' })).toBeChecked();
    expect(interval()).toHaveValue('15');

    await user.clear(interval());
    await user.type(interval(), '1');
    await user.click(within(form).getByRole('button', { name: 'Save schedule' }));

    await waitFor(() => {
      expect(screen.getByTestId('scan-schedule-status')).toHaveTextContent('Schedule saved.');
    });
    expect(server.writes).toEqual([
      { ifMatch: '"0"', body: { enabled: true, intervalMinutes: 1 } },
    ]);
    expect(screen.getByTestId('scan-schedule-summary')).toHaveTextContent(
      'Scheduled scans run every minute',
    );

    // The next save is based on the revision the first one answered.
    await user.clear(interval());
    await user.type(interval(), '1440');
    await user.click(within(form).getByRole('button', { name: 'Save schedule' }));
    await waitFor(() => {
      expect(server.writes).toHaveLength(2);
    });
    expect(server.writes[1]).toEqual({
      ifMatch: '"1"',
      body: { enabled: true, intervalMinutes: 1440 },
    });
    expect(await screen.findByText(/every 1,440 minutes/)).toBeVisible();
  });

  it('turns scheduled scans off and keeps the interval, shown disabled', async () => {
    const server = scheduleServer({ enabled: true, intervalMinutes: 30, revision: 3 });
    const user = userEvent.setup();
    const form = await openLibrary();

    await user.click(within(form).getByRole('switch', { name: 'Scan on a schedule' }));
    expect(interval()).toBeDisabled();
    expect(interval()).toHaveValue('30');
    await user.click(within(form).getByRole('button', { name: 'Save schedule' }));

    await waitFor(() => {
      expect(screen.getByTestId('scan-schedule-summary')).toHaveTextContent(
        'Scheduled scans are off. The media folder is still scanned when n8Tracks starts and whenever you ask.',
      );
    });
    expect(server.writes).toEqual([
      { ifMatch: '"3"', body: { enabled: false, intervalMinutes: 30 } },
    ]);
    expect(interval()).toBeDisabled();
    expect(interval()).toHaveValue('30');
  });

  it.each([
    ['0', '0'],
    ['1441', '1441'],
    ['empty', ''],
  ])('refuses an interval of %s before sending anything', async (_name, typed) => {
    const server = scheduleServer();
    const user = userEvent.setup();
    const form = await openLibrary();

    await user.clear(interval());
    if (typed !== '') {
      await user.type(interval(), typed);
    }
    await user.click(within(form).getByRole('button', { name: 'Save schedule' }));

    expect(
      await within(form).findByText('Enter a whole number of minutes from 1 to 1,440.'),
    ).toBeVisible();
    expect(interval()).toHaveAttribute('aria-invalid', 'true');
    expect(server.writes).toEqual([]);
  });

  it('shows the field error the API answers with', async () => {
    const server = scheduleServer();
    server.nextWrite = jsonResponse(422, {
      code: 'validation_failed',
      errors: { intervalMinutes: ['Enter a whole number of minutes from 1 to 1,440.'] },
    });
    const user = userEvent.setup();
    const form = await openLibrary();

    await user.click(within(form).getByRole('button', { name: 'Save schedule' }));

    expect(
      await within(form).findByText('Enter a whole number of minutes from 1 to 1,440.'),
    ).toBeVisible();
    expect(screen.getByTestId('scan-schedule-status')).toBeEmptyDOMElement();
  });

  it('reloads the schedule when it was changed elsewhere', async () => {
    const server = scheduleServer();
    const user = userEvent.setup();
    const form = await openLibrary();
    server.schedule = { enabled: false, intervalMinutes: 60, revision: 1 };

    await user.click(within(form).getByRole('button', { name: 'Save schedule' }));

    expect(await screen.findByText('The schedule was changed elsewhere')).toBeVisible();
    expect(screen.getByTestId('scan-schedule-summary')).toHaveTextContent(
      'Scheduled scans are off.',
    );
    expect(interval()).toHaveValue('60');
    expect(server.writes).toEqual([
      { ifMatch: '"0"', body: { enabled: true, intervalMinutes: 15 } },
    ]);
  });

  it('says so when the save fails', async () => {
    const server = scheduleServer();
    server.nextWrite = jsonResponse(500, { code: 'internal' });
    const user = userEvent.setup();
    const form = await openLibrary();

    await user.click(within(form).getByRole('button', { name: 'Save schedule' }));

    expect(await screen.findByText('The schedule was not saved')).toBeVisible();
    expect(screen.getByTestId('scan-schedule-summary')).toHaveTextContent('every 15 minutes');
  });

  it('offers to try again when the schedule cannot be loaded', async () => {
    const server = scheduleServer();
    server.failGet = true;
    const user = userEvent.setup();
    renderApp('/settings/library');

    expect(await screen.findByText('The scan schedule could not be loaded')).toBeVisible();
    server.failGet = false;
    await user.click(screen.getByRole('button', { name: 'Try again' }));

    expect(await screen.findByRole('form', { name: 'Scan schedule' })).toBeVisible();
  });
});
