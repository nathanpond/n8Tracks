import type { Compatibility } from './compatibility.ts';

/** A feature of the extension and the scope it needs. */
export interface FeatureState {
  feature: 'sync' | 'generate';
  label: string;
  scope: 'suno.sync' | 'suno.generate';
  /** Whether the credential holds the scope. */
  available: boolean;
  /** Why it is not available, shown beside its disabled control: "This credential lacks suno.sync". */
  reason: string | null;
}

export interface ConnectedState {
  status: 'connected';
  address: string;
  credentialName: string;
  scopes: string[];
  applicationVersion: string;
  compatibility: Compatibility;
  features: FeatureState[];
}

/**
 * Where the extension stands with n8Tracks. Never carries the token: the popup, the options page,
 * the panel on Suno, and the relay on the n8Tracks page all read it.
 */
export type ConnectionState =
  | { status: 'not-paired' }
  /** A call was refused with 401 `invalid_token`: the token is forgotten. */
  | { status: 'revoked'; address: string }
  /** The handshake could not reach n8Tracks, or it did not answer as expected. The token is kept. */
  | { status: 'unreachable'; address: string }
  /** A host permission the pairing needs was removed outside the extension. */
  | { status: 'permission-removed'; address: string; origins: string[] }
  | ConnectedState;

/** Why connecting did not work. Nothing is saved in any of these cases. */
export type ConnectFailure =
  | 'invalid-address'
  | 'missing-token'
  | 'permission-declined'
  | 'unreachable'
  | 'not-n8tracks'
  | 'rejected'
  | 'no-suno-scope'
  | 'failed';

export type ConnectResult =
  { ok: true; state: ConnectedState } | { ok: false; failure: ConnectFailure; message: string };

/** The source field of a message the n8Tracks web app posts to the relay. */
export const PAGE_SOURCE = 'n8tracks';

/** The source field of a message the relay posts back to the page. */
export const EXTENSION_SOURCE = 'n8tracks-extension';

/** A message from the n8Tracks web app: `window.postMessage({ source: "n8tracks", type, ... })`. */
export interface PageMessage {
  source: typeof PAGE_SOURCE;
  type: string;
  id?: string;
  [field: string]: unknown;
}

/** What the service worker answers a message the relay passes on. */
export interface RelayReply {
  type: 'error';
  error: 'unknown_type';
  message: string;
}

/** The messages the service worker answers, by type, with the answer each gets. */
export type Request =
  | { type: 'state'; fresh?: boolean }
  | { type: 'connect'; address: string; token: string }
  | { type: 'disconnect' }
  | { type: 'relay'; message: PageMessage };

export interface ResponseFor {
  state: ConnectionState;
  connect: ConnectResult;
  disconnect: ConnectionState;
  relay: RelayReply;
}

export type Response<T extends Request> = ResponseFor[T['type']];

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Whether `value` is a message from the n8Tracks web app. */
export function isPageMessage(value: unknown): value is PageMessage {
  return (
    isRecord(value) &&
    value.source === PAGE_SOURCE &&
    typeof value.type === 'string' &&
    (value.id === undefined || typeof value.id === 'string')
  );
}

/** Whether `value` is a request the service worker answers. */
export function isRequest(value: unknown): value is Request {
  if (!isRecord(value)) {
    return false;
  }
  switch (value.type) {
    case 'state':
      return value.fresh === undefined || typeof value.fresh === 'boolean';
    case 'connect':
      return typeof value.address === 'string' && typeof value.token === 'string';
    case 'disconnect':
      return true;
    case 'relay':
      return isPageMessage(value.message);
    default:
      return false;
  }
}

/** Sends `request` to the service worker and resolves with its typed answer. */
export async function sendRequest<T extends Request>(
  request: T,
  send: (message: unknown) => Promise<unknown> = (message) => chrome.runtime.sendMessage(message),
): Promise<Response<T>> {
  return (await send(request)) as Response<T>;
}
