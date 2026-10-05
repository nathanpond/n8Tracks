import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { HEALTH_REFRESH_MS, HEALTH_TIMEOUT_MS } from '../api/health';
import { formatDateTime } from '../api/timeZone';
import {
  degradedReport,
  healthyReport,
  jsonResponse,
  neverAnswers,
  renderApp,
  setVisibility,
  stubFetch,
  unhealthyReport,
} from '../test/helpers';
import { COLOR_SCHEME_STORAGE_KEY } from '../theme/theme';

/** The System page: the health data the M0 shell page showed, now under Settings. */
function renderSystem() {
  return renderApp('/settings/system');
}

const LOADING = 'Loading health information…';
const UNAVAILABLE = 'Health information is unavailable.';
const STALE = /may be out of date/;

function componentRow(key: string): HTMLElement {
  const row = document.querySelector<HTMLElement>(`[data-component="${key}"]`);
  if (!row) {
    throw new Error(`No row for component ${key}.`);
  }

  return row;
}

async function advance(milliseconds: number): Promise<void> {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(milliseconds);
  });
}

describe('Settings → System', () => {
  it('shows the product name and a loading state on first load', async () => {
    stubFetch().mockImplementation(neverAnswers);

    renderSystem();

    // The setup gate answers first; then the shell starts loading health.
    expect(await screen.findByText(LOADING)).toBeVisible();
    expect(screen.getByRole('heading', { level: 1, name: 'n8Tracks' })).toBeVisible();
    expect(screen.queryByTestId('version')).not.toBeInTheDocument();
  });

  it('shows the version, the overall status, and each component when healthy', async () => {
    stubFetch().mockResolvedValue(jsonResponse(200, healthyReport));

    renderSystem();

    expect(await screen.findByTestId('version')).toHaveTextContent('0.1.0');
    expect(screen.getByTestId('overall-status')).toHaveTextContent('healthy');
    expect(screen.queryByText(LOADING)).not.toBeInTheDocument();

    const rows = within(screen.getByRole('table', { name: 'Components' })).getAllByRole('row');
    expect(rows.slice(1).map((row) => row.textContent)).toEqual([
      'Applicationhealthyrunning',
      'Databasehealthyreachable',
      'Database schemahealthyup to date',
      'Media libraryhealthyavailable',
    ]);
  });

  it('shows the last upgrade outcome and when the last safety backup was taken, in the configured zone', async () => {
    stubFetch().mockResolvedValue(
      jsonResponse(200, {
        ...healthyReport,
        timeZone: 'Pacific/Auckland',
        components: {
          ...healthyReport.components,
          migrations: {
            status: 'healthy',
            detail: 'up to date',
            lastApplied: '20261005110608_AddGenerations',
            lastOutcome: 'succeeded',
            lastSafetyBackupAt: '2026-10-05T09:30:00.000Z',
          },
        },
      }),
    );

    renderSystem();

    const row = await waitFor(() => componentRow('migrations'));
    expect(within(row).getByTestId('last-migration-outcome')).toHaveTextContent(
      'Upgraded at this start',
    );
    expect(within(row).getByTestId('last-safety-backup')).toHaveTextContent(
      `Last safety backup ${formatDateTime('2026-10-05T09:30:00.000Z', 'Pacific/Auckland')}`,
    );
  });

  it('says when nothing was upgraded and no safety backup has been taken', async () => {
    stubFetch().mockResolvedValue(
      jsonResponse(200, {
        ...healthyReport,
        components: {
          ...healthyReport.components,
          migrations: {
            status: 'healthy',
            detail: 'up to date',
            lastOutcome: 'none',
            lastSafetyBackupAt: null,
          },
        },
      }),
    );

    renderSystem();

    const row = await waitFor(() => componentRow('migrations'));
    expect(row).toHaveTextContent(
      'Database schemahealthyup to dateNothing to upgrade at this startNo safety backup yet',
    );
  });

  it('shows a degraded report', async () => {
    stubFetch().mockResolvedValue(jsonResponse(200, degradedReport));

    renderSystem();

    await waitFor(() => {
      expect(screen.getByTestId('overall-status')).toHaveTextContent('degraded');
    });
    expect(componentRow('media')).toHaveTextContent('Media librarydegradedunavailable');
    expect(componentRow('database')).toHaveTextContent('healthy');
  });

  it('shows a 503 with a valid body as health data', async () => {
    stubFetch().mockResolvedValue(jsonResponse(503, unhealthyReport));

    renderSystem();

    await waitFor(() => {
      expect(screen.getByTestId('overall-status')).toHaveTextContent('unhealthy');
    });
    expect(componentRow('database')).toHaveTextContent('Databaseunhealthyunreachable');
    expect(screen.queryByText(UNAVAILABLE)).not.toBeInTheDocument();
  });

  it('shows a 500 with a valid-looking body as the error state, not as data', async () => {
    stubFetch().mockResolvedValue(jsonResponse(500, healthyReport));

    renderSystem();

    expect(await screen.findByText(UNAVAILABLE)).toBeVisible();
    expect(screen.queryByTestId('version')).not.toBeInTheDocument();
  });

  it.each([
    ['a body that is not a health report', () => jsonResponse(200, { status: 'healthy' })],
    ['a body that is not JSON', () => new Response('<html>', { status: 200 })],
  ])('shows %s as the error state', async (_name, response) => {
    stubFetch().mockResolvedValue(response());

    renderSystem();

    expect(await screen.findByText(UNAVAILABLE)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeVisible();
  });

  it('shows an error with Retry when the first load fails, and loads again on Retry', async () => {
    const fetchMock = stubFetch();
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'));
    fetchMock.mockResolvedValue(jsonResponse(200, healthyReport));
    const user = userEvent.setup();

    renderSystem();

    expect(await screen.findByText(UNAVAILABLE)).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Retry' }));

    expect(await screen.findByTestId('version')).toHaveTextContent('0.1.0');
    expect(screen.queryByText(UNAVAILABLE)).not.toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('gives up on a first load that does not answer within the timeout', async () => {
    vi.useFakeTimers();
    stubFetch().mockImplementation(neverAnswers);

    renderSystem();
    // Let the setup gate answer, so the health request is the only one the clock is running for.
    await advance(0);
    expect(screen.getByText(LOADING)).toBeVisible();
    await advance(HEALTH_TIMEOUT_MS - 1);
    expect(screen.getByText(LOADING)).toBeVisible();

    await advance(1);
    expect(screen.getByText(UNAVAILABLE)).toBeVisible();
  });

  it('shows a component it does not know under its raw key and an unknown status as plain text', async () => {
    stubFetch().mockResolvedValue(
      jsonResponse(200, {
        ...healthyReport,
        components: { search_index: { status: 'warming' }, ...healthyReport.components },
      }),
    );

    renderSystem();

    await screen.findByTestId('version');
    const rows = within(screen.getByRole('table', { name: 'Components' })).getAllByRole('row');
    expect(rows).toHaveLength(6);
    expect(rows[5]).toHaveTextContent('search_indexwarming');
    expect(componentRow('search_index').querySelector('[data-status]')).toBeNull();
    expect(componentRow('media').querySelector('[data-status="healthy"]')).not.toBeNull();
  });
});

describe('refreshing', () => {
  it('refreshes every 30 seconds without showing the loading state', async () => {
    vi.useFakeTimers();
    const fetchMock = stubFetch();
    fetchMock.mockResolvedValueOnce(jsonResponse(200, healthyReport));
    fetchMock.mockImplementationOnce(neverAnswers);

    renderSystem();
    await advance(0);
    expect(screen.getByTestId('overall-status')).toHaveTextContent('healthy');

    await advance(HEALTH_REFRESH_MS - 1);
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await advance(1);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    // The refresh is in flight: the data stays and nothing says "loading".
    expect(screen.getByTestId('overall-status')).toHaveTextContent('healthy');
    expect(screen.queryByText(LOADING)).not.toBeInTheDocument();
  });

  it('shows the new data after a refresh', async () => {
    vi.useFakeTimers();
    const fetchMock = stubFetch();
    fetchMock.mockResolvedValueOnce(jsonResponse(200, healthyReport));
    fetchMock.mockResolvedValueOnce(jsonResponse(200, degradedReport));

    renderSystem();
    await advance(0);
    await advance(HEALTH_REFRESH_MS);

    expect(screen.getByTestId('overall-status')).toHaveTextContent('degraded');
  });

  it('keeps the last data with a notice when a refresh fails, and clears it on the next success', async () => {
    vi.useFakeTimers();
    const fetchMock = stubFetch();
    fetchMock.mockResolvedValueOnce(jsonResponse(200, healthyReport));
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'));
    fetchMock.mockResolvedValueOnce(jsonResponse(500, {}));
    fetchMock.mockResolvedValueOnce(jsonResponse(200, healthyReport));

    renderSystem();
    await advance(0);
    expect(screen.queryByText(STALE)).not.toBeInTheDocument();

    await advance(HEALTH_REFRESH_MS);
    expect(screen.getByText(STALE)).toBeVisible();
    expect(screen.getByTestId('version')).toHaveTextContent('0.1.0');
    expect(screen.getByTestId('overall-status')).toHaveTextContent('healthy');
    expect(screen.queryByText(UNAVAILABLE)).not.toBeInTheDocument();

    await advance(HEALTH_REFRESH_MS);
    expect(screen.getByText(STALE)).toBeVisible();

    await advance(HEALTH_REFRESH_MS);
    expect(fetchMock).toHaveBeenCalledTimes(4);
    expect(screen.queryByText(STALE)).not.toBeInTheDocument();
    expect(screen.getByTestId('version')).toBeVisible();
  });

  it('makes no request while the tab is hidden, and refreshes when it is shown again', async () => {
    vi.useFakeTimers();
    const fetchMock = stubFetch();
    fetchMock.mockImplementation(() => Promise.resolve(jsonResponse(200, healthyReport)));

    try {
      renderSystem();
      await advance(0);
      expect(fetchMock).toHaveBeenCalledTimes(1);

      act(() => {
        setVisibility('hidden');
      });
      await advance(HEALTH_REFRESH_MS * 5);
      expect(fetchMock).toHaveBeenCalledTimes(1);

      act(() => {
        setVisibility('visible');
      });
      await advance(0);
      expect(fetchMock).toHaveBeenCalledTimes(2);

      await advance(HEALTH_REFRESH_MS);
      expect(fetchMock).toHaveBeenCalledTimes(3);
    } finally {
      setVisibility('visible');
    }
  });
});

describe('the colour scheme', () => {
  function option(name: string): HTMLElement {
    return within(screen.getByRole('radiogroup', { name: 'Colour scheme' })).getByRole('radio', {
      name,
    });
  }

  it('is auto by default', () => {
    stubFetch().mockImplementation(neverAnswers);

    renderSystem();

    expect(option('Auto')).toBeChecked();
    expect(option('Light')).not.toBeChecked();
    expect(option('Dark')).not.toBeChecked();
    expect(window.localStorage.getItem(COLOR_SCHEME_STORAGE_KEY)).toBeNull();
  });

  it('remembers dark and restores it on the next visit', async () => {
    stubFetch().mockImplementation(neverAnswers);
    const user = userEvent.setup();

    const firstVisit = renderSystem();
    await screen.findByText(LOADING);
    await user.click(option('Dark'));

    expect(option('Dark')).toBeChecked();
    expect(document.documentElement).toHaveAttribute('data-mantine-color-scheme', 'dark');
    expect(window.localStorage.getItem(COLOR_SCHEME_STORAGE_KEY)).toBe('dark');

    firstVisit.unmount();
    document.documentElement.removeAttribute('data-mantine-color-scheme');
    renderSystem();

    expect(option('Dark')).toBeChecked();
    expect(option('Auto')).not.toBeChecked();
    expect(document.documentElement).toHaveAttribute('data-mantine-color-scheme', 'dark');
  });
});
