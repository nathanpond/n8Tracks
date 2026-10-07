import {
  pageRequestOf,
  type ConnectionState,
  type GenerateFailure,
  type GenerationHandOff,
  type PageMessage,
  type RelayReply,
} from '../messages.ts';
import { DisconnectedError, type Connection } from './connection.ts';

/**
 * The service worker's half of Generate on Suno (#144). The n8Tracks page makes a request and hands
 * its ID over through the relay; this checks the connection afresh, claims the request with the
 * extension's own token (binding it to this credential), and keeps the claim in session storage for
 * the Suno steps that follow (#145 onwards). It never makes a request, and it reads nothing from the
 * page but the ID.
 */

/** The parts of `chrome.*` Generate on Suno uses, so tests can stand in for the browser. */
export interface GenerateBrowser {
  /** `chrome.storage.session`: the request this extension claimed last. */
  session: {
    get(keys: string[]): Promise<Record<string, unknown>>;
    set(items: Record<string, unknown>): Promise<void>;
  };
  /** `chrome.runtime.openOptionsPage`. */
  openOptions(): Promise<void>;
}

/** The session-storage key of the request this extension claimed last. */
export const GENERATION_KEY = 'generation';

export const GENERATION_REQUESTS_PATH = 'api/v1/suno/generation-requests';

export interface GenerateCoordinatorOptions {
  connection: Connection;
  browser: GenerateBrowser;
  now?: () => number;
}

function failure(error: GenerateFailure, message: string): RelayReply {
  return { type: 'error', error, message };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Why a connection that is not connected cannot take a request, in the words the page shows. */
function notConnected(state: Exclude<ConnectionState, { status: 'connected' }>): RelayReply {
  switch (state.status) {
    case 'revoked':
      return failure(
        'not_connected',
        'n8Tracks refused the extension’s credential, so it was forgotten. Connect it again in its options.',
      );
    case 'unreachable':
      return failure('unreachable', 'The extension cannot reach n8Tracks right now.');
    case 'permission-removed':
      return failure(
        'not_connected',
        'The extension lost a permission it needs. Connect it again in its options.',
      );
    case 'not-paired':
      return failure('not_connected', 'The extension is not connected to n8Tracks.');
  }
}

/** The origin of `address`, or null when it is not an address. */
function originOf(address: string | undefined): string | null {
  try {
    return address === undefined ? null : new URL(address).origin;
  } catch {
    return null;
  }
}

export class GenerateCoordinator {
  private readonly connection: Connection;
  private readonly browser: GenerateBrowser;
  private readonly now: () => number;

  constructor(options: GenerateCoordinatorOptions) {
    this.connection = options.connection;
    this.browser = options.browser;
    this.now = options.now ?? (() => Date.now());
  }

  /**
   * Answers a page message the relay passed on from the page at `senderUrl`; null when it is not one
   * Generate on Suno handles.
   */
  async handle(message: PageMessage, senderUrl: string | undefined): Promise<RelayReply | null> {
    if (message.type !== 'generate' && message.type !== 'open-options') {
      return null;
    }
    const request = pageRequestOf(message);
    if (request === null) {
      return failure('invalid_request', 'The page did not send a request ID.');
    }
    if (request.type === 'open-options') {
      await this.browser.openOptions();
      return { type: 'options-opened' };
    }
    return this.claim(request.requestId, senderUrl);
  }

  /** The request this extension claimed last, if any. */
  async current(): Promise<GenerationHandOff | null> {
    const stored = (await this.browser.session.get([GENERATION_KEY]))[GENERATION_KEY];
    return isRecord(stored) &&
      typeof stored.requestId === 'string' &&
      typeof stored.claimedAt === 'number'
      ? { requestId: stored.requestId, claimedAt: stored.claimedAt }
      : null;
  }

  private async claim(requestId: string, senderUrl: string | undefined): Promise<RelayReply> {
    // The check before a generation bypasses the cached handshake.
    const state = await this.connection.state(true);
    if (state.status !== 'connected') {
      return notConnected(state);
    }
    // Only the paired n8Tracks may hand over a request: the relay runs nowhere else, and this checks it.
    if (originOf(senderUrl) !== originOf(state.address)) {
      return failure(
        'wrong_origin',
        'This page is not the n8Tracks the extension is connected to.',
      );
    }
    if (state.compatibility.kind !== 'compatible') {
      return failure(
        'incompatible',
        `The extension (${state.compatibility.extensionVersion}) does not work with this n8Tracks (${state.compatibility.applicationVersion}).`,
      );
    }
    const generate = state.features.find((feature) => feature.feature === 'generate');
    if (generate?.available !== true) {
      return failure('no_scope', generate?.reason ?? 'This credential lacks suno.generate.');
    }

    let response: Response;
    try {
      response = await this.connection.call(
        `${GENERATION_REQUESTS_PATH}/${encodeURIComponent(requestId)}/claim`,
        { method: 'POST' },
      );
    } catch (error) {
      if (error instanceof DisconnectedError) {
        return failure('not_connected', 'n8Tracks refused the extension’s credential.');
      }
      return failure('unreachable', 'The extension cannot reach n8Tracks right now.');
    }
    if (!response.ok) {
      return failure('refused', await refusal(response));
    }
    const handOff: GenerationHandOff = { requestId, claimedAt: this.now() };
    await this.browser.session.set({ [GENERATION_KEY]: handOff });
    return { type: 'generate-accepted', requestId };
  }
}

/** Plain words for n8Tracks refusing the claim: its status and code, never anything it carried. */
async function refusal(response: Response): Promise<string> {
  let code = '';
  try {
    const body: unknown = await response.json();
    code = isRecord(body) && typeof body.code === 'string' ? ` (${body.code})` : '';
  } catch {
    // No body to read.
  }
  return `n8Tracks refused the claim: ${String(response.status)}${code}.`;
}
