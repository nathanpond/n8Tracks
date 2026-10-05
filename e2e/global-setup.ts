import { mkdir, mkdtemp, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, dirname, join } from 'node:path';
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
  NO_MEDIA_NAME,
  NO_MEDIA_PORT,
  NO_MEDIA_URL,
  OUTPUT_DIR,
  ROOT_NAME,
  ROOT_PORT,
  ROOT_STORAGE_STATE,
  ROOT_URL,
  SUB_PATH_NAME,
  SUB_PATH_PORT,
  SUB_PATH_STORAGE_STATE,
  SUB_PATH_URL,
  STORAGE_STATE_DIR,
} from './support/targets.ts';

/** When set, each container's log is written to this directory before the container is removed. */
const LOG_DIR = process.env.N8TRACKS_E2E_LOG_DIR;

const targets: Target[] = [
  {
    name: ROOT_NAME,
    port: ROOT_PORT,
    url: ROOT_URL,
    media: true,
    expectedStatus: 'healthy',
  },
  {
    name: SUB_PATH_NAME,
    port: SUB_PATH_PORT,
    url: SUB_PATH_URL,
    media: true,
    baseUrl: SUB_PATH_URL,
    expectedStatus: 'healthy',
  },
  {
    name: NO_MEDIA_NAME,
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

/** How long a local run's output folder (its traces) is kept: well past the end of any run. */
const OLD_RUN_MS = 24 * 60 * 60 * 1000;

/**
 * Removes the output folders of earlier local runs (`test-results/run-<run ID>`) not written to for
 * a day: each run has its own, so no run clears another's, and they would otherwise pile up. In CI
 * the output folder is `test-results` itself and there is nothing to prune.
 */
async function pruneOldRuns(): Promise<void> {
  const parent = dirname(OUTPUT_DIR);
  if (!basename(OUTPUT_DIR).startsWith('run-')) {
    return;
  }
  const entries = await readdir(parent, { withFileTypes: true }).catch(() => []);
  for (const entry of entries) {
    const folder = join(parent, entry.name);
    if (!entry.isDirectory() || !entry.name.startsWith('run-') || folder === OUTPUT_DIR) {
      continue;
    }
    // Another run pruning at the same moment may have removed it already.
    const age = await stat(folder).then(
      (info) => Date.now() - info.mtimeMs,
      () => 0,
    );
    if (age > OLD_RUN_MS) {
      await rm(folder, { recursive: true, force: true });
    }
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
  // The names and ports are this run's own (support/targets.ts), so another run on the machine is
  // never touched; leftovers of this run's ID (a rerun with N8TRACKS_E2E_RUN_ID set) would hold them.
  await removeContainers();
  await pruneOldRuns();
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
    await rm(STORAGE_STATE_DIR, { recursive: true, force: true });
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
