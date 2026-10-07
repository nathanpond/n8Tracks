/**
 * Talking to the n8Tracks browser extension (#144). The extension's relay content script runs on this
 * page when the extension is paired with this n8Tracks. The page posts
 * `{ source: "n8tracks", type, id, ... }` to its own window and the relay answers
 * `{ source: "n8tracks-extension", replyTo, id, ... }`. Nothing is shared but these messages: the
 * page never sees the extension's token.
 */

/** The source field of a message the page posts to the relay. */
export const PAGE_SOURCE = 'n8tracks';
/** The source field of a message the relay posts back. */
export const EXTENSION_SOURCE = 'n8tracks-extension';
/** How long the page waits for the relay to answer a `ping`. */
export const PING_TIMEOUT_MS = 500;
/** How long the page waits for any other answer: the extension calls n8Tracks before it answers. */
export const REPLY_TIMEOUT_MS = 15_000;
/** The scope the extension's credential needs for Generate on Suno. */
export const GENERATE_SCOPE = 'suno.generate';

/** Where the extension stands, as Generate on Suno needs to know it. */
export type ExtensionState =
  /** No relay answered: the extension is not installed, or not paired with this n8Tracks. */
  | { kind: 'absent' }
  /** The relay answered, but the extension is not connected (`status` says how). */
  | { kind: 'disconnected'; status: string }
  /** Connected, but the extension and this n8Tracks are not compatible versions. */
  | { kind: 'incompatible'; extensionVersion: string; applicationVersion: string | null }
  /** Connected, but its credential lacks `suno.generate`. */
  | { kind: 'no-scope'; credentialName: string | null }
  | { kind: 'ready'; extensionVersion: string };

/** A message for the extension: its type and fields. */
export interface BridgeMessage {
  type: string;
  [field: string]: unknown;
}

/** The extension's answer: a record, or null when it did not answer in time. */
export type BridgeReply = Record<string, unknown> | null;

export interface Bridge {
  /** Asks the extension where it stands. */
  detect: () => Promise<ExtensionState>;
  /** Sends `message` and resolves with the answer, or null after `timeoutMs`. */
  send: (message: BridgeMessage, timeoutMs?: number) => Promise<BridgeReply>;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function text(value: unknown): string | null {
  return typeof value === 'string' && value !== '' ? value : null;
}

/** The state a relay's `pong` (or its absence) says. */
export function stateOfPong(pong: BridgeReply): ExtensionState {
  if (pong?.type !== 'pong') {
    return { kind: 'absent' };
  }
  const connection = isRecord(pong.connection) ? pong.connection : {};
  const status = text(connection.status) ?? 'not-paired';
  if (status !== 'connected') {
    return { kind: 'disconnected', status };
  }
  const extensionVersion = text(pong.extensionVersion) ?? 'unknown';
  if (connection.compatible !== true) {
    return {
      kind: 'incompatible',
      extensionVersion,
      applicationVersion: text(connection.applicationVersion),
    };
  }
  const scopes = Array.isArray(connection.scopes) ? connection.scopes : [];
  if (!scopes.includes(GENERATE_SCOPE)) {
    return { kind: 'no-scope', credentialName: text(connection.credentialName) };
  }
  return { kind: 'ready', extensionVersion };
}

let counter = 0;

function nextId(): string {
  counter += 1;
  return `n8tracks-${String(Date.now())}-${String(counter)}`;
}

/**
 * The bridge over `target`'s own window messages. Answers are taken only from the same window and
 * origin, with the `id` the page sent.
 */
export function windowBridge(target: Window = window): Bridge {
  const send = (message: BridgeMessage, timeoutMs = REPLY_TIMEOUT_MS): Promise<BridgeReply> =>
    new Promise((resolve) => {
      const id = nextId();
      const origin = target.location.origin;
      const listener = (event: MessageEvent<unknown>) => {
        const data = event.data;
        if (
          event.source !== target ||
          event.origin !== origin ||
          !isRecord(data) ||
          data.source !== EXTENSION_SOURCE ||
          data.id !== id
        ) {
          return;
        }
        finish(data);
      };
      const finish = (reply: BridgeReply) => {
        clearTimeout(timer);
        target.removeEventListener('message', listener);
        resolve(reply);
      };
      target.addEventListener('message', listener);
      const timer = setTimeout(() => {
        finish(null);
      }, timeoutMs);
      target.postMessage({ ...message, source: PAGE_SOURCE, id }, origin);
    });
  return {
    detect: async () => stateOfPong(await send({ type: 'ping' }, PING_TIMEOUT_MS)),
    send,
  };
}
