import { execFile } from 'node:child_process';
import { mkdir } from 'node:fs/promises';
import { createServer } from 'node:net';
import { userInfo } from 'node:os';
import { join } from 'node:path';
import { promisify } from 'node:util';

/** Runs the application image as containers, for global setup and for tests that need a fresh one. */

export const run = promisify(execFile);

export const IMAGE = process.env.N8TRACKS_E2E_IMAGE ?? 'n8tracks:dev';
const HEALTH_WAIT_MS = 60_000;
const HEALTH_POLL_MS = 500;
const CONTAINER_PORT = 8787;

export interface Target {
  name: string;
  port: number;
  /** The page URL, ending in a slash; health is `health` under it. */
  url: string;
  /** Whether the empty media directory under the work directory is mounted (read-only). */
  media: boolean;
  /** The public base URL to configure, when the app is not at the root. */
  baseUrl?: string;
  /** The health status the container must reach before the tests start. */
  expectedStatus: string;
}

export function errorText(error: unknown): string {
  if (typeof error === 'object' && error !== null && 'stderr' in error) {
    const stderr = String(error.stderr).trim();
    if (stderr !== '') {
      return stderr;
    }
  }
  return error instanceof Error ? error.message : String(error);
}

export async function docker(...args: string[]): Promise<string> {
  const { stdout } = await run('docker', args);
  return stdout.trim();
}

/** Removes the named containers and their anonymous volumes; a name that does not exist is fine. */
export async function removeContainers(...names: string[]): Promise<void> {
  await docker('rm', '--force', '--volumes', ...names);
}

export function requireFreePort(port: number): Promise<void> {
  return new Promise((resolve, reject) => {
    const server = createServer();
    server.once('error', (error) => {
      reject(
        new Error(
          `Host port ${String(port)} is already in use, and the end-to-end suite needs it. ` +
            `Stop whatever is listening on it and run the suite again. (${error.message})`,
          { cause: error },
        ),
      );
    });
    server.listen(port, () => {
      server.close(() => {
        resolve();
      });
    });
  });
}

/**
 * Starts a container for the target with an empty data folder of its own under `work`. The media
 * folder mounted, when the target has one, is `work/media`, which must exist.
 */
export async function startContainer(target: Target, work: string): Promise<void> {
  const data = join(work, `data-${target.name}`);
  await mkdir(data);
  const { uid, gid } = userInfo();
  const args = [
    'run',
    '--detach',
    '--name',
    target.name,
    '--publish',
    `${String(target.port)}:${String(CONTAINER_PORT)}`,
    '--env',
    `PUID=${String(uid)}`,
    '--env',
    `PGID=${String(gid)}`,
    '--volume',
    `${data}:/data`,
  ];
  if (target.media) {
    args.push('--volume', `${join(work, 'media')}:/media:ro`);
  }
  if (target.baseUrl !== undefined) {
    args.push('--env', `N8TRACKS_BASE_URL=${target.baseUrl}`);
  }
  await docker(...args, IMAGE);
}

async function healthStatus(target: Target): Promise<string | undefined> {
  try {
    const response = await fetch(new URL('health', target.url), {
      signal: AbortSignal.timeout(2_000),
    });
    const body: unknown = await response.json();
    if (typeof body === 'object' && body !== null && 'status' in body) {
      return String(body.status);
    }
    return undefined;
  } catch {
    // Not listening yet.
    return undefined;
  }
}

/** Waits until the container reports its expected health status, or fails with its last log lines. */
export async function waitForHealth(target: Target): Promise<void> {
  const deadline = Date.now() + HEALTH_WAIT_MS;
  let last: string | undefined;
  while (Date.now() < deadline) {
    last = await healthStatus(target);
    if (last === target.expectedStatus) {
      return;
    }
    await new Promise((resolve) => setTimeout(resolve, HEALTH_POLL_MS));
  }
  let log: string;
  try {
    const { stdout, stderr } = await run('docker', ['logs', '--tail', '20', target.name]);
    log = `${stdout}${stderr}`.trim();
  } catch (error) {
    log = `(no log: ${errorText(error)})`;
  }
  throw new Error(
    `Container ${target.name} did not report "${target.expectedStatus}" at ${target.url}health within ` +
      `${String(HEALTH_WAIT_MS / 1000)} seconds (last answer: ${last ?? 'none'}). Its last log lines:\n${log}`,
  );
}
