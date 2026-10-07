import {
  EXTENSION_SOURCE,
  isPageMessage,
  type ConnectionState,
  type PageMessage,
  type Request,
} from '../messages.ts';

/** What the relay tells the page about the connection: never the token, which it never has. */
export interface ConnectionSummary {
  /** `checking` when the service worker did not answer in time for the page's 500 ms ping. */
  status: ConnectionState['status'] | 'checking';
  credentialName?: string;
  scopes?: string[];
  applicationVersion?: string;
  compatible?: boolean;
}

export interface RelayOptions {
  /** The page's window: the relay listens to it and posts back to it. */
  window: Window;
  /** Sends a request to the service worker. */
  send: (request: Request) => Promise<unknown>;
  versions: { extension: string; adapter: string };
  /** How long `ping` waits for the connection state; the page gives up on a ping at 500 ms. */
  pingStateTimeoutMs?: number;
}

const CHECKING = Symbol('checking');

function summary(state: unknown): ConnectionSummary {
  if (state === CHECKING) {
    return { status: 'checking' };
  }
  const known = state as ConnectionState | undefined;
  if (known?.status !== 'connected') {
    return { status: known?.status ?? 'not-paired' };
  }
  return {
    status: 'connected',
    credentialName: known.credentialName,
    scopes: known.scopes,
    applicationVersion: known.applicationVersion,
    compatible: known.compatibility.kind === 'compatible',
  };
}

/**
 * The relay on the paired n8Tracks origin. The web app posts `{ source: "n8tracks", type, ... }`
 * to its own window; the relay answers `ping` itself (with the extension's versions and the
 * connection state) and passes any other message to the service worker, posting the answer back
 * as `{ source: "n8tracks-extension", ... }` with the same `id`. A message from another window or
 * another origin is ignored, and answers go only to the page's own origin. Returns a function that
 * stops the relay.
 */
export function startRelay(options: RelayOptions): () => void {
  const { window: page, send, versions } = options;
  const pingStateTimeoutMs = options.pingStateTimeoutMs ?? 300;
  const origin = page.location.origin;

  const reply = (message: PageMessage, answer: Record<string, unknown>) => {
    page.postMessage(
      {
        ...answer,
        source: EXTENSION_SOURCE,
        replyTo: message.type,
        ...(message.id === undefined ? {} : { id: message.id }),
      },
      origin,
    );
  };

  const handle = async (message: PageMessage) => {
    if (message.type === 'ping') {
      let state: unknown;
      let timer: ReturnType<typeof setTimeout> | undefined;
      try {
        state = await Promise.race([
          send({ type: 'state' }),
          new Promise((resolve) => {
            timer = setTimeout(() => {
              resolve(CHECKING);
            }, pingStateTimeoutMs);
          }),
        ]);
      } catch {
        state = undefined;
      } finally {
        clearTimeout(timer);
      }
      reply(message, {
        type: 'pong',
        extensionVersion: versions.extension,
        adapterVersion: versions.adapter,
        connection: summary(state),
      });
      return;
    }
    try {
      const answer = await send({ type: 'relay', message });
      reply(message, typeof answer === 'object' && answer !== null ? { ...answer } : {});
    } catch {
      reply(message, {
        type: 'error',
        error: 'extension_unavailable',
        message: 'The extension did not answer.',
      });
    }
  };

  const listener = (event: MessageEvent<unknown>) => {
    if (event.source !== page || event.origin !== origin || !isPageMessage(event.data)) {
      return;
    }
    void handle(event.data);
  };

  page.addEventListener('message', listener);
  return () => {
    page.removeEventListener('message', listener);
  };
}
