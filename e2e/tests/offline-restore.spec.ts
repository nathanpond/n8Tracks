import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir, userInfo } from 'node:os';
import { join } from 'node:path';
import { expect, test } from '@playwright/test';
import { expectAccessibleInLightAndDark } from '../support/a11y.ts';
import {
  docker,
  IMAGE,
  removeContainers,
  run,
  startContainer,
  waitForHealth,
  type Target,
} from '../support/containers.ts';
import {
  ANTIFORGERY_HEADERS,
  signInHeading,
  signInThroughApi,
  signInWithTheForm,
} from '../support/session.ts';
import { completeSetup, TEST_ADMIN } from '../support/setup.ts';
import { FRESH_NAME, FRESH_PORT, FRESH_URL } from '../support/targets.ts';

const fresh: Target = {
  name: FRESH_NAME,
  port: FRESH_PORT,
  url: FRESH_URL,
  media: true,
  expectedStatus: 'healthy',
};

/**
 * Walks the Demo of disaster recovery from the container: a backup is made, the container is
 * stopped, `n8tracks list-backups` and `n8tracks restore` run in one-off containers from the same
 * image with the instance's data folder, and the restarted instance asks for a new sign-in and
 * holds the backup's catalog. On a container of its own, removed afterwards.
 */
test.describe('Restoring from the container', { tag: '@root-only' }, () => {
  let work: string | undefined;

  test.beforeEach(async ({ page }) => {
    await removeContainers(FRESH_NAME);
    work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-offline-restore-'));
    await mkdir(join(work, 'media'));
    await startContainer(fresh, work);
    await waitForHealth(fresh);
    await completeSetup(FRESH_URL);
    await signInThroughApi(page.request, FRESH_URL);
  });

  test.afterEach(async () => {
    await removeContainers(FRESH_NAME);
    if (work !== undefined) {
      await rm(work, { recursive: true, force: true });
    }
  });

  test('lists the backups and restores one with the app stopped', async ({ page }) => {
    const folder = work ?? '';

    /** `n8tracks <args>` in a one-off container with the instance's data folder, as the README says. */
    const command = (...args: string[]) => {
      const { uid, gid } = userInfo();
      return run('docker', [
        'run',
        '--rm',
        '--env',
        `PUID=${String(uid)}`,
        '--env',
        `PGID=${String(gid)}`,
        '--volume',
        `${join(folder, `data-${FRESH_NAME}`)}:/data`,
        IMAGE,
        'n8tracks',
        ...args,
      ]);
    };

    const create = async (title: string) => {
      const created = await page.request.post(`${FRESH_URL}api/v1/songs`, {
        headers: ANTIFORGERY_HEADERS,
        data: { title },
      });
      expect(created.status()).toBe(201);
    };

    // A Song, a backup through the API, then a second Song.
    await create('Before the backup');
    const started = await page.request.post(`${FRESH_URL}api/v1/backups`, {
      headers: ANTIFORGERY_HEADERS,
    });
    expect(started.status()).toBe(202);
    const { jobId } = (await started.json()) as { jobId: string };
    let name = '';
    await expect
      .poll(
        async () => {
          const job = (await (
            await page.request.get(`${FRESH_URL}api/v1/jobs/${jobId}`)
          ).json()) as {
            status: string;
            result?: { name?: string };
          };
          name = job.result?.name ?? '';
          return job.status;
        },
        { timeout: 30_000 },
      )
      .toBe('succeeded');
    await create('After the backup');

    // 1. With the container stopped, the backups are listed.
    await docker('stop', FRESH_NAME);
    const listed = await command('list-backups');
    expect(listed.stdout).toContain(`/data/backups/${name}`);
    expect(listed.stdout).toMatch(/manual/);

    // 2. The restore reports success, and names where the data from before is kept.
    const restored = await command('restore', `/data/backups/${name}`);
    // Standard output carries at most the entrypoint's JSON line about the folder's owner.
    expect(restored.stdout).not.toContain('Restored');
    expect(restored.stderr).toContain(`Restored ${name}`);
    expect(restored.stderr).toMatch(/kept in \/data\/before-restore-\d{8}T\d{6}Z/);

    // 3. Started again: every session ended, and the catalog is the backup's.
    await docker('start', FRESH_NAME);
    await waitForHealth(fresh);
    await page.goto(`${FRESH_URL}songs`);
    await expect(signInHeading(page)).toBeVisible({ timeout: 15_000 });
    await expectAccessibleInLightAndDark(page);

    await signInWithTheForm(page, TEST_ADMIN.username, TEST_ADMIN.password);
    await expect(signInHeading(page)).toBeHidden({ timeout: 15_000 });
    await page.goto(`${FRESH_URL}songs`);
    await expect(page.getByRole('link', { name: 'Before the backup' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'After the backup' })).toHaveCount(0);
    await expectAccessibleInLightAndDark(page);
  });
});
