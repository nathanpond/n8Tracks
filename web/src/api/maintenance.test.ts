import { describe, expect, it } from 'vitest';
import { jsonResponse } from '../test/helpers';
import {
  clearMaintenance,
  isMaintenanceStatus,
  noticeMaintenance,
  reportMaintenance,
  stageWhile,
} from './maintenance';

describe('maintenance', () => {
  it('reads a status with a known stage and outcome', () => {
    expect(
      isMaintenanceStatus({ active: true, stage: 'safety-backup', percent: 3, outcome: null }),
    ).toBe(true);
    expect(
      isMaintenanceStatus({ active: false, stage: null, percent: 0, outcome: 'rolled-back' }),
    ).toBe(true);
    expect(
      isMaintenanceStatus({
        active: true,
        stage: 'migrating',
        percent: 40,
        outcome: 'rollback-failed',
      }),
    ).toBe(true);
    expect(
      isMaintenanceStatus({ active: false, stage: null, percent: 0, outcome: 'gave-up' }),
    ).toBe(false);
    expect(isMaintenanceStatus({ active: true, stage: 'dancing', percent: 3, outcome: null })).toBe(
      false,
    );
    expect(isMaintenanceStatus({ active: 'yes', stage: null, percent: 0, outcome: null })).toBe(
      false,
    );
  });

  it('notices only the API’s 503 maintenance, and leaves the body readable', async () => {
    const maintenance = jsonResponse(503, { code: 'maintenance', title: 'x' });
    expect(await noticeMaintenance(maintenance)).toBe(true);
    expect(((await maintenance.json()) as { code: string }).code).toBe('maintenance');

    expect(await noticeMaintenance(jsonResponse(503, { code: 'setup_required' }))).toBe(false);
    expect(await noticeMaintenance(new Response('down', { status: 503 }))).toBe(false);
    expect(await noticeMaintenance(jsonResponse(409, { code: 'maintenance' }))).toBe(false);
  });

  it('report and clear are idempotent', () => {
    reportMaintenance();
    reportMaintenance();
    clearMaintenance();
    clearMaintenance();
  });

  it('names what the restore was doing at each stage', () => {
    expect(stageWhile('migrating')).toBe('updating the database');
    expect(stageWhile('safety-backup')).toBe('taking the safety backup');
    expect(stageWhile(null)).toBe('replacing the data');
  });
});
