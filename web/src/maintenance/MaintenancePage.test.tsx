import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { DEFAULT_SCHEDULE } from '../api/backups';
import type { MaintenanceStatus } from '../api/maintenance';
import {
  completeSetup,
  healthyReport,
  isSessionRequest,
  isSetupStatusRequest,
  jsonResponse,
  renderApp,
  requestPath,
  signedInSession,
  stubAllFetch,
  stubFetch,
} from '../test/helpers';

const maintenanceProblem = () =>
  jsonResponse(503, { status: 503, code: 'maintenance', title: 'In maintenance.', requestId: 'r' });

/**
 * The whole API in maintenance until `statuses` runs out of active ones: every call is 503
 * `maintenance`, and the status answers each of `statuses` in turn (the last repeating).
 */
function inMaintenance(statuses: MaintenanceStatus[]) {
  const queue = [...statuses];
  const mock = stubAllFetch();
  mock.mockImplementation((input, init) => {
    const path = requestPath(input);
    const current = queue[0];
    const active = current?.active ?? false;
    if (path.endsWith('/api/v1/maintenance')) {
      const next = queue.length > 1 ? queue.shift() : queue[0];
      return Promise.resolve(jsonResponse(200, next));
    }
    if (active) {
      return Promise.resolve(maintenanceProblem());
    }
    if (isSetupStatusRequest(input)) {
      return Promise.resolve(jsonResponse(200, completeSetup));
    }
    if (isSessionRequest(input, init)) {
      return Promise.resolve(jsonResponse(200, signedInSession));
    }
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (path.endsWith('/api/v1/backups')) {
      return Promise.resolve(
        jsonResponse(200, {
          destination: 'mount',
          sharesDiskWithData: false,
          activeJobId: null,
          items: [],
          lastSuccessAt: null,
          schedule: { ...DEFAULT_SCHEDULE, nextAt: null, lastAttempt: null },
        }),
      );
    }
    if (path.endsWith('/api/v1/settings/backup-schedule')) {
      return Promise.resolve(jsonResponse(200, { ...DEFAULT_SCHEDULE, revision: 1 }));
    }
    return Promise.resolve(jsonResponse(200, { items: [], total: 0 }));
  });
  return mock;
}

describe('The maintenance page', () => {
  it('shows instead of the app when the API is in maintenance at load, with the stage and percent', async () => {
    inMaintenance([{ active: true, stage: 'validating', percent: 0, outcome: null }]);

    renderApp('/settings/backups');

    expect(
      await screen.findByRole('heading', { level: 2, name: 'Restoring a backup' }),
    ).toBeVisible();
    expect(await screen.findByText('Checking the backup… 0%')).toBeVisible();
    expect(screen.getByRole('progressbar', { name: 'Restore progress' })).toHaveAttribute(
      'aria-valuenow',
      '0',
    );
    expect(screen.queryByRole('navigation')).toBeNull();
  });

  it('appears when a request in the signed-in app meets maintenance', async () => {
    const mock = stubFetch();
    mock.mockImplementation((input) => {
      const path = requestPath(input);
      if (path.endsWith('/api/v1/maintenance')) {
        return Promise.resolve(
          jsonResponse(200, { active: true, stage: 'replacing', percent: 10, outcome: null }),
        );
      }
      if (path.endsWith('/health')) {
        return Promise.resolve(jsonResponse(200, healthyReport));
      }
      return Promise.resolve(maintenanceProblem());
    });

    renderApp('/settings/backups');

    expect(await screen.findByText('Replacing the data… 10%')).toBeVisible();
  });

  it('after a failed restore says nothing was changed and continues to the app on request', async () => {
    inMaintenance([
      { active: true, stage: 'safety-backup', percent: 50, outcome: null },
      { active: false, stage: 'safety-backup', percent: 100, outcome: 'failed' },
    ]);
    const user = userEvent.setup();

    renderApp('/settings/backups');

    expect(
      await screen.findByText('The restore did not complete', {}, { timeout: 4000 }),
    ).toBeVisible();
    expect(screen.getByText(/Nothing was changed\./)).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Continue' }));

    expect(await screen.findByRole('heading', { level: 2, name: 'Backups' })).toBeVisible();
  });

  it('goes back to the app by itself when maintenance ends without a failure', async () => {
    inMaintenance([
      { active: true, stage: 'finishing', percent: 90, outcome: null },
      { active: false, stage: 'finishing', percent: 100, outcome: 'succeeded' },
    ]);

    renderApp('/settings/backups');

    expect(await screen.findByText('Finishing… 90%')).toBeVisible();
    expect(
      await screen.findByRole('heading', { level: 2, name: 'Backups' }, { timeout: 4000 }),
    ).toBeVisible();
  });
});
