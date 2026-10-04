import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import { createServer } from 'node:net';
import { tmpdir, userInfo } from 'node:os';
import { join } from 'node:path';
import { promisify } from 'node:util';
import {
  NO_MEDIA_PORT,
  NO_MEDIA_URL,
  ROOT_PORT,
  ROOT_URL,
  SUB_PATH_PORT,
  SUB_PATH_URL,
} from './support/targets.ts';

const run = promisify(execFile);

const IMAGE = process.env.N8TRACKS_E2E_IMAGE ?? 'n8tracks:dev';
const HEALTH_WAIT_MS = 60_000;
const HEALTH_POLL_MS = 500;
const CONTAINER_PORT = 8787;

interface Target {
  name: string;
  port: number;
  /** The page URL, ending in a slash; health is `health` under it. */
  url: string;
  /** Whether the empty media directory is mounted (read-only). */
  media: boolean;
  /** The public base URL to configure, when the app is not at the root. */
  baseUrl?: string;
  /** The health status the container must reach before the tests start. */
  expectedStatus: string;
}

const targets: Target[] = [
  {
    name: 'n8tracks-e2e-root',
    port: ROOT_PORT,
    url: ROOT_URL,
    media: true,
    expectedStatus: 'healthy',
  },
  {
    name: 'n8tracks-e2e-subpath',
    port: SUB_PATH_PORT,
    url: SUB_PATH_URL,
    media: true,
    baseUrl: SUB_PATH_URL,
    expectedStatus: 'healthy',
  },
  {
    name: 'n8tracks-e2e-nomedia',
    port: NO_MEDIA_PORT,
    url: NO_MEDIA_URL,
    media: false,
    expectedStatus: 'degraded',
  },
];

function errorText(error: unknown): string {
  if (typeof error === 'object' && error !== null && 'stderr' in error) {
    const stderr = String(error.stderr).trim();
    if (stderr !== '') {
      return stderr;
    }
  }
  return error instanceof Error ? error.message : String(error);
}

async function docker(...args: string[]): Promise<string> {
  const { stdout } = await run('docker', args);
  return stdout.trim();
}

async function requireDocker(): Promise<void> {
  try {
    await docker('version', '--format', '{{.Server.Version}}');
  } catch (error) {
    throw new Error(
      `The end-to-end suite needs a running Docker daemon, and "docker version" failed: ${errorText(error)}`,
      { cause: error },
    );
  }
}

async function requireImage(): Promise<void> {
  try {
    await docker('image', 'inspect', '--format', '{{.Id}}', IMAGE);
  } catch (error) {
    throw new Error(
      `The image under test, "${IMAGE}", does not exist. Build it from the repository root with ` +
        '"docker build -t n8tracks:dev ." or set N8TRACKS_E2E_IMAGE to an image that exists.',
      { cause: error },
    );
  }
}

async function removeContainers(): Promise<void> {
  // "docker rm --force" succeeds for a name that does not exist, so this is safe to repeat.
  await docker('rm', '--force', '--volumes', ...targets.map((target) => target.name));
}

function requireFreePort(port: number): Promise<void> {
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

async function start(target: Target, work: string): Promise<void> {
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

async function waitForHealth(target: Target): Promise<void> {
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

/**
 * Starts the three containers under test from the application image and returns the teardown that
 * removes them and their temporary directories. Runs once per test run.
 */
export default async function globalSetup(): Promise<() => Promise<void>> {
  await requireDocker();
  await requireImage();
  // Leftovers from an aborted run would hold the names and the ports.
  await removeContainers();
  for (const target of targets) {
    await requireFreePort(target.port);
  }

  const work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-'));
  const teardown = async () => {
    await removeContainers();
    await rm(work, { recursive: true, force: true });
  };

  try {
    await mkdir(join(work, 'media'));
    for (const target of targets) {
      await start(target, work);
    }
    await Promise.all(targets.map(waitForHealth));
  } catch (error) {
    await teardown();
    throw error;
  }

  return teardown;
}
