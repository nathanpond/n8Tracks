import { execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

/**
 * The containers the suite runs against. Global setup starts them; the tests only read this.
 *
 * Each run has its own: two runs on one machine (say, two checkouts verified at once) must not
 * remove each other's containers or share a port. The run's ID and its four host ports are chosen
 * once, by the first process that loads this module (the Playwright runner, as it reads the
 * config), and kept in the environment, which global setup and the workers it starts inherit; so
 * every process of a run agrees on them. `N8TRACKS_E2E_RUN_ID` set beforehand names the run.
 */

const HOST = 'http://localhost';

const RUN_ID_VARIABLE = 'N8TRACKS_E2E_RUN_ID';
const PORTS_VARIABLE = 'N8TRACKS_E2E_PORTS';

/** This run's ID: part of every container name and of the run's own files. */
export const RUN_ID = (process.env[RUN_ID_VARIABLE] ??= randomBytes(4).toString('hex'));

if (!/^[a-z0-9][a-z0-9_.-]*$/.test(RUN_ID)) {
  throw new Error(
    `${RUN_ID_VARIABLE} must be lowercase letters, digits, ".", "_", or "-" (it is part of container names); it is "${RUN_ID}".`,
  );
}

/**
 * Asks the operating system for four distinct free ports. A separate process, because the config
 * that reads them cannot wait: each listens on port 0 at once, so the four differ, and closes again.
 * Global setup checks that each is still free before it starts a container on it.
 */
function freePorts(): string {
  const script = `
    const { createServer } = require('node:net');
    const servers = [0, 1, 2, 3].map(() => createServer());
    Promise.all(servers.map((server) => new Promise((resolve) => server.listen(0, '127.0.0.1', resolve))))
      .then(() => {
        process.stdout.write(servers.map((server) => server.address().port).join(','));
        servers.forEach((server) => server.close());
      });
  `;
  return execFileSync(process.execPath, ['--eval', script], { encoding: 'utf8' });
}

function runPorts(): [number, number, number, number] {
  const text = (process.env[PORTS_VARIABLE] ??= freePorts());
  const ports = text.split(',').map(Number);
  const [root, subPath, noMedia, fresh] = ports;
  if (
    ports.length !== 4 ||
    root === undefined ||
    subPath === undefined ||
    noMedia === undefined ||
    fresh === undefined ||
    ports.some((port) => !Number.isInteger(port) || port < 1 || port > 65_535)
  ) {
    throw new Error(
      `${PORTS_VARIABLE} must be four port numbers separated by commas; it is "${text}".`,
    );
  }
  return [root, subPath, noMedia, fresh];
}

const [ROOT_PORT_OF_RUN, SUB_PATH_PORT_OF_RUN, NO_MEDIA_PORT_OF_RUN, FRESH_PORT_OF_RUN] =
  runPorts();

/** A container's name in this run: `n8tracks-e2e-<role>-<run ID>`. */
function containerName(role: string): string {
  return `n8tracks-e2e-${role}-${RUN_ID}`;
}

/** The containers of the two projects, by project name: `docker exec` runs commands in them. */
export const ROOT_NAME = containerName('root');
export const SUB_PATH_NAME = containerName('subpath');
export const CONTAINER_BY_PROJECT: Readonly<Record<string, string>> = {
  root: ROOT_NAME,
  subpath: SUB_PATH_NAME,
};

/** The sub-path the second container is served under. */
export const SUB_PATH = '/n8tracks';

export const ROOT_PORT = ROOT_PORT_OF_RUN;
export const SUB_PATH_PORT = SUB_PATH_PORT_OF_RUN;
export const NO_MEDIA_PORT = NO_MEDIA_PORT_OF_RUN;

/** The container without media. */
export const NO_MEDIA_NAME = containerName('nomedia');

/** Healthy, at the root of the hostname. */
export const ROOT_URL = `${HOST}:${String(ROOT_PORT)}/`;

/** Healthy, under the sub-path. */
export const SUB_PATH_URL = `${HOST}:${String(SUB_PATH_PORT)}${SUB_PATH}/`;

/** The origin of the sub-path container, for requests that must fall outside the sub-path. */
export const SUB_PATH_ORIGIN = `${HOST}:${String(SUB_PATH_PORT)}`;

/** No media mounted, at the root of the hostname: reports degraded. */
export const NO_MEDIA_URL = `${HOST}:${String(NO_MEDIA_PORT)}/`;

export const FRESH_PORT = FRESH_PORT_OF_RUN;

/** The container the setup test starts for itself, never set up before the test: at the root. */
export const FRESH_NAME = containerName('fresh');

export const FRESH_URL = `${HOST}:${String(FRESH_PORT)}/`;

/**
 * Where global setup writes the signed-in browser state (the session cookie) of each shared
 * container, and where the project of the same name reads it from: this run's own folder. Outside
 * the repository: it holds a live session of a test instance.
 */
export const STORAGE_STATE_DIR = join(tmpdir(), `n8tracks-e2e-auth-${RUN_ID}`);

export const ROOT_STORAGE_STATE = join(STORAGE_STATE_DIR, 'root.json');

export const SUB_PATH_STORAGE_STATE = join(STORAGE_STATE_DIR, 'subpath.json');

/**
 * Where this run's traces and other test output go. In CI (one run per machine) it stays
 * `test-results`, which the workflow uploads; locally each run has a folder of its own under it,
 * so one run starting does not clear the folder another is writing to.
 */
const RESULTS_DIR = join(import.meta.dirname, '..', 'test-results');

export const OUTPUT_DIR = process.env.CI ? RESULTS_DIR : join(RESULTS_DIR, `run-${RUN_ID}`);
