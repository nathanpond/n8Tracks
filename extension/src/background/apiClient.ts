/**
 * The only code in the extension that calls n8Tracks. Every call carries the credential's token as
 * `Authorization: Bearer` and the extension's own versions, and never carries cookies
 * (`credentials: 'omit'`): the extension holds no n8Tracks session and never forwards anything of
 * Suno's. Nothing here logs: the token must never reach the console.
 */

/** The versions every call reports. */
export interface ClientVersions {
  /** The extension's product version, pre-release suffix included. */
  extension: string;
  /** The Suno adapter's version. */
  adapter: string;
}

/** Where to call and with which token. */
export interface ApiTarget {
  /** The n8Tracks address: origin plus base path, without a trailing slash. */
  address: string;
  token: string;
}

export const EXTENSION_VERSION_HEADER = 'X-N8Tracks-Extension-Version';
export const ADAPTER_VERSION_HEADER = 'X-N8Tracks-Adapter-Version';
export const HANDSHAKE_PATH = 'api/v1/extension/handshake';

/** How long one call may take before it counts as unreachable. */
export const CALL_TIMEOUT_MS = 10_000;

/** What the handshake answers. */
export interface Handshake {
  applicationVersion: string;
  credentialName: string;
  scopes: string[];
  compatible: boolean;
}

export type HandshakeResult =
  | { kind: 'ok'; handshake: Handshake }
  /** No answer at all: a wrong address, n8Tracks down, the network, or a timeout. */
  | { kind: 'unreachable' }
  /** Something answered, but not as n8Tracks would: not JSON with an `applicationVersion`. */
  | { kind: 'not-n8tracks' }
  /** 401 `invalid_token`: the token is revoked, or was never valid. */
  | { kind: 'rejected' }
  /** n8Tracks answered with an error of its own. */
  | { kind: 'failed'; status: number };

export type Fetch = (input: string, init: RequestInit) => Promise<Response>;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

async function json(response: Response): Promise<unknown> {
  try {
    return (await response.json()) as unknown;
  } catch {
    return undefined;
  }
}

/** Whether an answer is n8Tracks refusing the token, which ends the pairing. */
export async function isTokenRefusal(response: Response): Promise<boolean> {
  if (response.status !== 401) {
    return false;
  }
  const body = await json(response.clone());
  return isRecord(body) && body.code === 'invalid_token';
}

/**
 * Calls `path` (relative to the address) on n8Tracks with the token and the versions. Rejects only
 * when no answer came (network failure or timeout); any HTTP answer resolves.
 */
export async function callApi(
  target: ApiTarget,
  path: string,
  versions: ClientVersions,
  init: RequestInit = {},
  fetchImpl: Fetch = (input, request) => fetch(input, request),
): Promise<Response> {
  const headers = new Headers(init.headers);
  headers.set('Authorization', `Bearer ${target.token}`);
  headers.set('Accept', 'application/json');
  headers.set(EXTENSION_VERSION_HEADER, versions.extension);
  headers.set(ADAPTER_VERSION_HEADER, versions.adapter);
  return fetchImpl(`${target.address}/${path}`, {
    ...init,
    headers,
    credentials: 'omit',
    cache: 'no-store',
    redirect: 'error',
    signal: init.signal ?? AbortSignal.timeout(CALL_TIMEOUT_MS),
  });
}

function isHandshake(value: unknown): value is Handshake {
  return (
    isRecord(value) &&
    typeof value.applicationVersion === 'string' &&
    typeof value.credentialName === 'string' &&
    Array.isArray(value.scopes) &&
    value.scopes.every((scope) => typeof scope === 'string') &&
    typeof value.compatible === 'boolean'
  );
}

/** `GET /api/v1/extension/handshake`: checks the token and learns which n8Tracks answered. */
export async function handshake(
  target: ApiTarget,
  versions: ClientVersions,
  fetchImpl?: Fetch,
): Promise<HandshakeResult> {
  let response: Response;
  try {
    response = await callApi(target, HANDSHAKE_PATH, versions, {}, fetchImpl);
  } catch {
    return { kind: 'unreachable' };
  }
  if (await isTokenRefusal(response)) {
    return { kind: 'rejected' };
  }
  const body = await json(response);
  if (response.ok) {
    return isHandshake(body) ? { kind: 'ok', handshake: body } : { kind: 'not-n8tracks' };
  }
  // n8Tracks answers every error as a problem with a code; anything else is another server.
  return isRecord(body) && typeof body.code === 'string'
    ? { kind: 'failed', status: response.status }
    : { kind: 'not-n8tracks' };
}
