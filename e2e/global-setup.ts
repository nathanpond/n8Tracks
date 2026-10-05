import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  docker,
  errorText,
  IMAGE,
  removeContainers as removeNamedContainers,
  requireFreePort,
  run,
  startContainer,
  waitForHealth,
  type Target,
} from './support/containers.ts';
import { writeSignedInState } from './support/session.ts';
import { completeSetup } from './support/setup.ts';
import {
  FRESH_NAME,
  FRESH_PORT,
  NO_MEDIA_PORT,
  NO_MEDIA_URL,
  ROOT_PORT,
  ROOT_STORAGE_STATE,
  ROOT_URL,
  SUB_PATH_PORT,
  SUB_PATH_STORAGE_STATE,
  SUB_PATH_URL,
} from './support/targets.ts';

/** When set, each container's log is written to this directory before the container is removed. */
const LOG_DIR = process.env.N8TRACKS_E2E_LOG_DIR;

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
  await removeNamedContainers(...targets.map((target) => target.name), FRESH_NAME);
}

/** Keeps what the containers logged: once they are removed, there is nothing left to read. */
async function saveContainerLogs(directory: string): Promise<void> {
  await mkdir(directory, { recursive: true });
  for (const target of targets) {
    let log: string;
    try {
      const { stdout, stderr } = await run('docker', ['logs', target.name], {
        maxBuffer: 64 * 1024 * 1024,
      });
      log = `${stdout}${stderr}`;
    } catch (error) {
      log = `(no log: ${errorText(error)})\n`;
    }
    await writeFile(join(directory, `${target.name}.log`), log);
  }
}

/**
 * Starts the three containers under test from the application image, completes first-run setup on
 * each through the API with the test administrator (so every test meets a set-up instance), signs
 * in to the root and sub-path containers and saves each session as the storage state its project
 * starts every test from, and returns the teardown that removes them, their temporary
 * directories, and the saved sessions. Runs once per test run.
 * The port for the fresh container the setup test starts must be free too.
 */
export default async function globalSetup(): Promise<() => Promise<void>> {
  await requireDocker();
  await requireImage();
  // Leftovers from an aborted run would hold the names and the ports.
  await removeContainers();
  for (const port of [...targets.map((target) => target.port), FRESH_PORT]) {
    await requireFreePort(port);
  }

  const work = await mkdtemp(join(tmpdir(), 'n8tracks-e2e-'));
  const teardown = async () => {
    if (LOG_DIR !== undefined && LOG_DIR !== '') {
      await saveContainerLogs(LOG_DIR);
    }
    await removeContainers();
    await rm(work, { recursive: true, force: true });
    await rm(ROOT_STORAGE_STATE, { force: true });
    await rm(SUB_PATH_STORAGE_STATE, { force: true });
  };

  try {
    await mkdir(join(work, 'media'));
    for (const target of targets) {
      await startContainer(target, work);
    }
    await Promise.all(targets.map(waitForHealth));
    await Promise.all(targets.map((target) => completeSetup(target.url)));
    // The signed-in fixture: each project starts every test with a session on its container.
    await writeSignedInState(ROOT_URL, ROOT_STORAGE_STATE);
    await writeSignedInState(SUB_PATH_URL, SUB_PATH_STORAGE_STATE);
  } catch (error) {
    await teardown();
    throw error;
  }

  return teardown;
}
