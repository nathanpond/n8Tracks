import { tmpdir } from 'node:os';
import { join } from 'node:path';

/** The containers the suite runs against. Global setup starts them; the tests only read this. */

const HOST = 'http://localhost';

/** The sub-path the second container is served under. */
export const SUB_PATH = '/n8tracks';

export const ROOT_PORT = 18787;
export const SUB_PATH_PORT = 18788;
export const NO_MEDIA_PORT = 18789;

/** Healthy, at the root of the hostname. */
export const ROOT_URL = `${HOST}:${String(ROOT_PORT)}/`;

/** Healthy, under the sub-path. */
export const SUB_PATH_URL = `${HOST}:${String(SUB_PATH_PORT)}${SUB_PATH}/`;

/** The origin of the sub-path container, for requests that must fall outside the sub-path. */
export const SUB_PATH_ORIGIN = `${HOST}:${String(SUB_PATH_PORT)}`;

/** No media mounted, at the root of the hostname: reports degraded. */
export const NO_MEDIA_URL = `${HOST}:${String(NO_MEDIA_PORT)}/`;

export const FRESH_PORT = 18790;

/** The container the setup test starts for itself, never set up before the test: at the root. */
export const FRESH_NAME = 'n8tracks-e2e-fresh';

export const FRESH_URL = `${HOST}:${String(FRESH_PORT)}/`;

/**
 * Where global setup writes the signed-in browser state (the session cookie) of each shared
 * container, and where the project of the same name reads it from. Outside the repository: it
 * holds a live session of a test instance.
 */
const STORAGE_STATE_DIR = join(tmpdir(), 'n8tracks-e2e-auth');

export const ROOT_STORAGE_STATE = join(STORAGE_STATE_DIR, 'root.json');

export const SUB_PATH_STORAGE_STATE = join(STORAGE_STATE_DIR, 'subpath.json');
