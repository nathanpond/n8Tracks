import { pairingOrigins, parseAddress, type N8TracksAddress } from '../address.ts';
import { SUNO_ORIGIN_PATTERN } from '../adapter/addresses.ts';
import { compatibilityOf } from '../compatibility.ts';
import type {
  ConnectFailure,
  ConnectResult,
  ConnectedState,
  ConnectionState,
  FeatureState,
} from '../messages.ts';
import {
  callApi,
  handshake,
  isTokenRefusal,
  type ClientVersions,
  type Fetch,
  type Handshake,
} from './apiClient.ts';

/** The parts of `chrome.*` the connection uses, so tests can stand in for the browser. */
export interface BrowserApis {
  storage: {
    get(keys: string[]): Promise<Record<string, unknown>>;
    set(items: Record<string, unknown>): Promise<void>;
    remove(keys: string[]): Promise<void>;
  };
  permissions: {
    contains(permissions: { origins: string[] }): Promise<boolean>;
    remove(permissions: { origins: string[] }): Promise<boolean>;
  };
  scripting: {
    registerContentScripts(scripts: chrome.scripting.RegisteredContentScript[]): Promise<void>;
    unregisterContentScripts(filter: { ids: string[] }): Promise<void>;
    getRegisteredContentScripts(filter: {
      ids: string[];
    }): Promise<chrome.scripting.RegisteredContentScript[]>;
  };
}

export interface ConnectionOptions {
  browser: BrowserApis;
  versions: ClientVersions;
  fetch?: Fetch;
  /** Milliseconds since the epoch; a test clock in tests. */
  now?: () => number;
}

/** The storage key of the pairing: `{ address, token }`, or `{ address }` after a revoked token. */
export const PAIRING_KEY = 'pairing';
/** The storage key of the reason the last pairing ended, when n8Tracks ended it. */
export const NOTICE_KEY = 'notice';

/** The relay's registration ID and its built file. */
export const RELAY_ID = 'n8tracks-relay';
export const RELAY_FILE = 'relay.js';

/** The Suno content script's (adapter and panel) registration ID and its built file. */
export const SUNO_SCRIPT_ID = 'n8tracks-suno';
export const SUNO_FILE = 'suno.js';

const SCRIPT_IDS = [RELAY_ID, SUNO_SCRIPT_ID];

/** A handshake is reused for this long, except by the check before a sync or a generation. */
export const HANDSHAKE_CACHE_MS = 60_000;

/** What `chrome.storage.local` holds: the address and the token, and nothing else of n8Tracks. */
interface StoredPairing {
  address: string;
  token?: string;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function storedPairing(value: unknown): StoredPairing | null {
  if (!isRecord(value) || typeof value.address !== 'string') {
    return null;
  }
  return typeof value.token === 'string' && value.token !== ''
    ? { address: value.address, token: value.token }
    : { address: value.address };
}

const FEATURES: readonly Pick<FeatureState, 'feature' | 'label' | 'scope'>[] = [
  { feature: 'sync', label: 'Library sync', scope: 'suno.sync' },
  { feature: 'generate', label: 'Generate on Suno', scope: 'suno.generate' },
];

/** Each feature with whether the credential's scopes allow it, and why not. */
export function featuresFor(scopes: readonly string[]): FeatureState[] {
  return FEATURES.map((feature) => {
    const available = scopes.includes(feature.scope);
    return {
      ...feature,
      available,
      reason: available ? null : `This credential lacks ${feature.scope}`,
    };
  });
}

/** Thrown by {@link Connection.call} when there is no working pairing; the workflow stops. */
export class DisconnectedError extends Error {
  readonly state: ConnectionState;

  constructor(state: ConnectionState) {
    super(`The extension is not connected to n8Tracks (${state.status}).`);
    this.name = 'DisconnectedError';
    this.state = state;
  }
}

const FAILURE_MESSAGES: Record<Exclude<ConnectFailure, 'invalid-address'>, string> = {
  'missing-token': 'Enter the token you created in n8Tracks under Settings → Credentials.',
  'permission-declined':
    'The browser did not give the extension access to suno.com and your n8Tracks, so it stays disconnected. Choose Connect again and allow access.',
  unreachable:
    'Cannot reach n8Tracks at this address. Check the address and that n8Tracks is running.',
  'not-n8tracks': 'Something answered at this address, but it is not an n8Tracks server.',
  rejected:
    'n8Tracks rejected the token. Check that you copied all of it and that the credential has not been revoked.',
  'no-suno-scope':
    'This credential has neither suno.sync nor suno.generate. In n8Tracks, create an extension credential with at least one of them.',
  failed: 'n8Tracks did not answer as expected. Try again in a moment.',
};

/**
 * The pairing with n8Tracks, held by the service worker. It stores only the address and the token
 * (in `chrome.storage.local`, never synced), checks the token with a handshake before saving it,
 * caches the handshake for a minute, registers the relay on the paired origin and the Suno
 * content script on suno.com, and forgets the token the first time n8Tracks refuses it.
 */
export class Connection {
  private readonly browser: BrowserApis;
  private readonly versions: ClientVersions;
  private readonly fetchImpl: Fetch | undefined;
  private readonly now: () => number;
  private cached: { address: string; at: number; state: ConnectedState } | null = null;

  constructor(options: ConnectionOptions) {
    this.browser = options.browser;
    this.versions = options.versions;
    this.fetchImpl = options.fetch;
    this.now = options.now ?? (() => Date.now());
  }

  /** Forgets the cached handshake, so the next state check asks n8Tracks again. */
  forgetHandshake(): void {
    this.cached = null;
  }

  /**
   * Where the extension stands. Uses the cached handshake when it is under a minute old, unless
   * `fresh` (the check before a sync or a generation).
   */
  async state(fresh = false): Promise<ConnectionState> {
    const pairing = await this.pairing();
    if (pairing === null) {
      return { status: 'not-paired' };
    }
    const address = parseAddress(pairing.address);
    if (!address.ok) {
      return { status: 'not-paired' };
    }
    if (pairing.token === undefined) {
      const notice = (await this.browser.storage.get([NOTICE_KEY]))[NOTICE_KEY];
      return notice === 'revoked'
        ? { status: 'revoked', address: pairing.address }
        : { status: 'not-paired' };
    }
    const origins = pairingOrigins(address.value);
    if (!(await this.browser.permissions.contains({ origins }))) {
      this.cached = null;
      return { status: 'permission-removed', address: pairing.address, origins };
    }
    const cached = this.cached;
    if (
      !fresh &&
      cached !== null &&
      cached.address === pairing.address &&
      this.now() - cached.at < HANDSHAKE_CACHE_MS
    ) {
      return cached.state;
    }

    const result = await handshake(
      { address: pairing.address, token: pairing.token },
      this.versions,
      this.fetchImpl,
    );
    switch (result.kind) {
      case 'ok': {
        const state = this.connected(pairing.address, result.handshake);
        this.cached = { address: pairing.address, at: this.now(), state };
        await this.ensureScripts(address.value);
        return state;
      }
      case 'rejected':
        return this.tokenRefused(pairing.address);
      default:
        // An unknown application version is Disconnected, not a mismatch; the token is kept.
        this.cached = null;
        return { status: 'unreachable', address: pairing.address };
    }
  }

  /**
   * Pairs with the n8Tracks at `addressText` using `token`. The options page has already asked
   * the browser for the two host permissions; this checks they were given, checks the token with
   * a handshake, and only then saves the pairing and registers the content scripts. Replacing a
   * pairing with another origin gives back the old origin's permission. Nothing is saved on any
   * failure, and a permission given for a new origin that failed is given back.
   */
  async connect(addressText: string, token: string): Promise<ConnectResult> {
    const parsed = parseAddress(addressText);
    if (!parsed.ok) {
      return { ok: false, failure: 'invalid-address', message: parsed.error };
    }
    const address = parsed.value;
    const trimmedToken = token.trim();
    if (trimmedToken === '') {
      return this.failure('missing-token');
    }
    const previous = await this.pairing();
    const previousAddress = previous === null ? null : parseAddress(previous.address);
    const previousPattern =
      previousAddress?.ok === true ? previousAddress.value.pattern : undefined;

    if (!(await this.browser.permissions.contains({ origins: pairingOrigins(address) }))) {
      return this.failure('permission-declined');
    }

    const result = await handshake(
      { address: address.address, token: trimmedToken },
      this.versions,
      this.fetchImpl,
    );
    let failure: ConnectFailure | null = null;
    if (result.kind !== 'ok') {
      failure = result.kind;
    } else if (
      !result.handshake.scopes.some((scope) => scope === 'suno.sync' || scope === 'suno.generate')
    ) {
      failure = 'no-suno-scope';
    }
    if (failure !== null || result.kind !== 'ok') {
      if (address.pattern !== previousPattern) {
        await this.browser.permissions.remove({ origins: [address.pattern] });
      }
      return this.failure(failure ?? 'failed');
    }

    if (previousPattern !== undefined && previousPattern !== address.pattern) {
      await this.unregisterScripts();
      await this.browser.permissions.remove({ origins: [previousPattern] });
    }
    const pairing: StoredPairing = { address: address.address, token: trimmedToken };
    await this.browser.storage.set({ [PAIRING_KEY]: pairing });
    await this.browser.storage.remove([NOTICE_KEY]);
    await this.registerScripts(address);

    const state = this.connected(address.address, result.handshake);
    this.cached = { address: address.address, at: this.now(), state };
    return { ok: true, state };
  }

  /**
   * Forgets the pairing and the token, unregisters the relay and the Suno content script, and
   * gives back the n8Tracks permission.
   */
  async disconnect(): Promise<ConnectionState> {
    const pairing = await this.pairing();
    await this.browser.storage.remove([PAIRING_KEY, NOTICE_KEY]);
    this.cached = null;
    await this.unregisterScripts();
    const address = pairing === null ? null : parseAddress(pairing.address);
    if (address?.ok === true) {
      await this.browser.permissions.remove({ origins: [address.value.pattern] });
    }
    return { status: 'not-paired' };
  }

  /** When the service worker starts: re-runs the handshake, which makes sure the content scripts are registered. */
  async start(): Promise<ConnectionState> {
    return this.state(true);
  }

  /**
   * Calls n8Tracks for a workflow (sync, generation). Throws {@link DisconnectedError} when there
   * is no token, and when n8Tracks refuses the token (401 `invalid_token`), which also forgets it:
   * the running workflow stops at that call.
   */
  async call(path: string, init: RequestInit = {}): Promise<Response> {
    const pairing = await this.pairing();
    if (pairing?.token === undefined) {
      throw new DisconnectedError(await this.state());
    }
    const response = await callApi(
      { address: pairing.address, token: pairing.token },
      path,
      this.versions,
      init,
      this.fetchImpl,
    );
    if (await isTokenRefusal(response)) {
      throw new DisconnectedError(await this.tokenRefused(pairing.address));
    }
    return response;
  }

  private connected(address: string, answer: Handshake): ConnectedState {
    return {
      status: 'connected',
      address,
      credentialName: answer.credentialName,
      scopes: answer.scopes,
      applicationVersion: answer.applicationVersion,
      compatibility: compatibilityOf(
        this.versions.extension,
        answer.applicationVersion,
        answer.compatible,
      ),
      features: featuresFor(answer.scopes),
    };
  }

  /** n8Tracks refused the token: forget it, keep the address, and remember why. */
  private async tokenRefused(address: string): Promise<ConnectionState> {
    this.cached = null;
    await this.browser.storage.set({ [PAIRING_KEY]: { address }, [NOTICE_KEY]: 'revoked' });
    return { status: 'revoked', address };
  }

  private failure(failure: Exclude<ConnectFailure, 'invalid-address'>): ConnectResult {
    return { ok: false, failure, message: FAILURE_MESSAGES[failure] };
  }

  private async pairing(): Promise<StoredPairing | null> {
    return storedPairing((await this.browser.storage.get([PAIRING_KEY]))[PAIRING_KEY]);
  }

  /** The relay on the paired n8Tracks origin, and the adapter and panel on suno.com. */
  private contentScripts(address: N8TracksAddress): chrome.scripting.RegisteredContentScript[] {
    return [
      {
        id: RELAY_ID,
        matches: [address.pattern],
        js: [RELAY_FILE],
        runAt: 'document_start',
        allFrames: false,
        persistAcrossSessions: true,
      },
      {
        id: SUNO_SCRIPT_ID,
        matches: [SUNO_ORIGIN_PATTERN],
        js: [SUNO_FILE],
        runAt: 'document_idle',
        allFrames: false,
        persistAcrossSessions: true,
      },
    ];
  }

  private async registerScripts(address: N8TracksAddress): Promise<void> {
    await this.unregisterScripts();
    await this.browser.scripting.registerContentScripts(this.contentScripts(address));
  }

  /** Registers the scripts again when one is missing or registered for another origin. */
  private async ensureScripts(address: N8TracksAddress): Promise<void> {
    const registered = await this.browser.scripting.getRegisteredContentScripts({
      ids: SCRIPT_IDS,
    });
    const intact = this.contentScripts(address).every((script) => {
      const found = registered.find((item) => item.id === script.id);
      const matches = found?.matches ?? [];
      return matches.length === 1 && matches[0] === script.matches?.[0];
    });
    if (registered.length !== SCRIPT_IDS.length || !intact) {
      await this.registerScripts(address);
    }
  }

  private async unregisterScripts(): Promise<void> {
    const registered = await this.browser.scripting.getRegisteredContentScripts({
      ids: SCRIPT_IDS,
    });
    if (registered.length > 0) {
      await this.browser.scripting.unregisterContentScripts({
        ids: registered.map((script) => script.id),
      });
    }
  }
}
