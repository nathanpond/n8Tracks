import { isSunoAddress, sunoCreateAddress } from '../adapter/addresses.ts';
import type { FormJob, VerificationReport } from '../adapter/fill.ts';
import {
  pageRequestOf,
  type ChosenWorkspace,
  type ConnectionState,
  type GenerateFailure,
  type GenerateJob,
  type GenerateReply,
  type GenerateRequest,
  type GenerateState,
  type GenerationHandOff,
  type PageMessage,
  type RelayReply,
  type RequestWorkspace,
  type ResponseFor,
} from '../messages.ts';
import { DisconnectedError, type Connection } from './connection.ts';

/**
 * The service worker's half of Generate on Suno (#144, #145). The n8Tracks page makes a request and
 * hands its ID over through the relay; this checks the connection afresh, claims the request with
 * the extension's own token (binding it to this credential), and keeps the claim in session storage.
 * It then gets a Suno tab onto the Create page: the most recently active suno.com tab in the n8Tracks
 * tab's window, or a new one beside it. The Suno content script in that tab does the steps; this is
 * the only part that calls n8Tracks for them: it reads the request before each step (stopping when
 * it is no longer active) and reports the step, reports Suno's workspace list, and records the
 * workspace the user chose for the Song. It reads nothing from the n8Tracks page but the ID.
 */

/** A browser tab as Generate on Suno reads it. */
export interface GenerateTab {
  id?: number;
  windowId?: number;
  index?: number;
  url?: string;
  /** When the tab was last active, in milliseconds since the epoch. */
  lastAccessed?: number;
}

/** The parts of `chrome.*` Generate on Suno uses, so tests can stand in for the browser. */
export interface GenerateBrowser {
  /** `chrome.storage.session`: the request this extension claimed last, and its Suno tab. */
  session: {
    get(keys: string[]): Promise<Record<string, unknown>>;
    set(items: Record<string, unknown>): Promise<void>;
    remove?(keys: string[]): Promise<void>;
  };
  /** `chrome.runtime.openOptionsPage`. */
  openOptions(): Promise<void>;
  /** `chrome.tabs`, to find or open the Suno tab (#145). */
  tabs?: {
    get(tabId: number): Promise<GenerateTab>;
    query(query: { windowId?: number }): Promise<GenerateTab[]>;
    update(tabId: number, properties: { url: string; active: boolean }): Promise<unknown>;
    create(properties: {
      url: string;
      windowId?: number;
      index?: number;
      active: boolean;
    }): Promise<GenerateTab>;
  };
}

/** The session-storage key of the request this extension claimed last. */
export const GENERATION_KEY = 'generation';

/** The session-storage key of the Suno tab working on that request (#145). */
export const GENERATION_TAB_KEY = 'generationTab';

export const GENERATION_REQUESTS_PATH = 'api/v1/suno/generation-requests';
export const DISCOVERED_WORKSPACES_PATH = 'api/v1/suno/workspaces/discovered';

/** The page the Suno steps start on. */
export const SUNO_CREATE_ADDRESS = sunoCreateAddress().href;

/** How many times a report of the chosen workspace is sent again before the panel says it failed. */
export const RESOLVE_RETRIES = 3;

/** The Suno tab working on a request, as kept between its page loads. */
interface GenerationTab {
  requestId: string;
  tabId: number;
  loads: number;
  /** The workspace the user chose, once n8Tracks recorded it: later loads use it. */
  chosen: RequestWorkspace | null;
}

const DISCONNECTED = 'The extension is not connected to n8Tracks; reconnect it in the options.';
const UNREACHABLE = 'Cannot reach n8Tracks. Check that it is running.';
const ENDED = 'The request has ended in n8Tracks.';

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
   * Answers a page message the relay passed on from the page at `senderUrl` (in tab `senderTab`);
   * null when it is not one Generate on Suno handles.
   */
  async handle(
    message: PageMessage,
    senderUrl: string | undefined,
    senderTab?: number,
  ): Promise<RelayReply | null> {
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
    return this.claim(request.requestId, senderUrl, senderTab);
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

  private async claim(
    requestId: string,
    senderUrl: string | undefined,
    senderTab: number | undefined,
  ): Promise<RelayReply> {
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
    if (this.browser.tabs !== undefined) {
      await this.openSuno(requestId, senderTab);
    }
    return { type: 'generate-accepted', requestId };
  }

  /**
   * Gets a Suno tab onto the Create page for the request: the most recently active suno.com tab in
   * the n8Tracks tab's window, or else a new tab beside the n8Tracks tab. Reports `opening` first,
   * and a stop if no tab could be had. Never fails.
   */
  private async openSuno(requestId: string, senderTab: number | undefined): Promise<void> {
    const tabs = this.browser.tabs;
    if (tabs === undefined) {
      return;
    }
    await this.report(requestId, 'opening', 'open Suno');
    try {
      const from = senderTab === undefined ? null : await tabs.get(senderTab);
      const windowId = from?.windowId;
      const open = await tabs.query(windowId === undefined ? {} : { windowId });
      const suno = open
        .filter((tab) => tab.id !== undefined && onSuno(tab.url))
        .toSorted((a, b) => (b.lastAccessed ?? 0) - (a.lastAccessed ?? 0))[0];
      let tabId = suno?.id;
      if (tabId !== undefined) {
        await tabs.update(tabId, { url: SUNO_CREATE_ADDRESS, active: true });
      } else {
        tabId = (
          await tabs.create({
            url: SUNO_CREATE_ADDRESS,
            active: true,
            ...(windowId === undefined ? {} : { windowId }),
            ...(from?.index === undefined ? {} : { index: from.index + 1 }),
          })
        ).id;
      }
      if (tabId === undefined) {
        throw new Error('The browser gave the new tab no ID.');
      }
      const tab: GenerationTab = { requestId, tabId, loads: 0, chosen: null };
      await this.browser.session.set({ [GENERATION_TAB_KEY]: tab });
    } catch {
      await this.report(
        requestId,
        'stopped',
        'open Suno',
        'The extension could not open a Suno tab. Try again.',
      );
    }
  }

  /** Answers a Generate on Suno message from the Suno content script in tab `tabId` (#145). */
  async handleTab(
    request: GenerateRequest,
    tabId: number,
  ): Promise<ResponseFor[GenerateRequest['type']]> {
    const tab = await this.tab();
    if (request.type === 'generate-resume') {
      return { job: tab?.tabId === tabId ? await this.resume(tab) : null };
    }
    if (tab?.tabId !== tabId) {
      return { ok: false, ended: true, message: 'This tab is not generating anything.' };
    }
    switch (request.type) {
      case 'generate-progress':
        return this.progress(
          tab,
          request.state,
          request.step,
          request.message,
          request.verification,
        );
      case 'generate-workspaces':
        return this.workspaces(request.workspaces);
      case 'generate-resolve':
        return this.resolve(tab, request.workspace);
    }
  }

  /** The Suno tab of a generation was closed: the request stops, saying so. */
  async tabRemoved(tabId: number): Promise<void> {
    const tab = await this.tab();
    if (tab?.tabId === tabId) {
      await this.forget();
      await this.report(tab.requestId, 'stopped', null, 'The Suno tab was closed.');
    }
  }

  private async tab(): Promise<GenerationTab | null> {
    const stored = (await this.browser.session.get([GENERATION_TAB_KEY]))[GENERATION_TAB_KEY];
    return isRecord(stored) &&
      typeof stored.requestId === 'string' &&
      typeof stored.tabId === 'number' &&
      typeof stored.loads === 'number'
      ? {
          requestId: stored.requestId,
          tabId: stored.tabId,
          loads: stored.loads,
          chosen: workspaceOf(stored.chosen),
        }
      : null;
  }

  private async forget(): Promise<void> {
    if (this.browser.session.remove !== undefined) {
      await this.browser.session.remove([GENERATION_TAB_KEY]);
    } else {
      await this.browser.session.set({ [GENERATION_TAB_KEY]: null });
    }
  }

  /** The request as n8Tracks has it now, with its snapshot; null when it cannot be read. */
  private async read(requestId: string): Promise<Record<string, unknown> | null> {
    try {
      const response = await this.connection.call(
        `${GENERATION_REQUESTS_PATH}/${encodeURIComponent(requestId)}`,
      );
      const body = await bodyOf(response);
      return response.ok && isRecord(body) ? body : null;
    } catch {
      return null;
    }
  }

  /** A page load of the tab: the job, if the request is still active. */
  private async resume(tab: GenerationTab): Promise<GenerateJob | null> {
    const request = await this.read(tab.requestId);
    if (request?.active !== true) {
      await this.forget();
      return null;
    }
    const snapshot = isRecord(request.snapshot) ? request.snapshot : {};
    const song = isRecord(snapshot.song) ? snapshot.song : {};
    const loads = tab.loads + 1;
    await this.browser.session.set({ [GENERATION_TAB_KEY]: { ...tab, loads } });
    return {
      requestId: tab.requestId,
      songTitle: typeof song.title === 'string' ? song.title : '',
      workspace: tab.chosen ?? workspaceOf(snapshot.workspace),
      loads,
      form: formOf(snapshot),
    };
  }

  /**
   * A step began: the request is read first, and when it is no longer active the tab stops and is
   * forgotten; otherwise the step is reported. A stop or the end forgets the tab.
   */
  private async progress(
    tab: GenerationTab,
    state: GenerateState,
    step: string,
    message: string | undefined,
    verification?: VerificationReport,
  ): Promise<GenerateReply> {
    const request = await this.read(tab.requestId);
    if (request === null) {
      return { ok: false, ended: false, message: UNREACHABLE };
    }
    if (request.active !== true) {
      await this.forget();
      return {
        ok: false,
        ended: true,
        message: typeof request.message === 'string' ? request.message : ENDED,
      };
    }
    const answer = await this.report(
      tab.requestId,
      state,
      step,
      message ?? null,
      undefined,
      verification,
    );
    if (answer.ok && (state === 'stopped' || state === 'done')) {
      await this.forget();
    }
    return answer;
  }

  /** Suno's complete workspace list, reported so n8Tracks' record follows it. */
  private async workspaces(
    workspaces: unknown[],
  ): Promise<GenerateReply<{ songCounts: Record<string, number> }>> {
    try {
      const response = await this.connection.call(DISCOVERED_WORKSPACES_PATH, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ complete: true, workspaces }),
      });
      const body = await bodyOf(response);
      if (!response.ok || !isRecord(body) || !Array.isArray(body.items)) {
        return {
          ok: false,
          ended: false,
          message: await refusalOf(response, 'the workspace list', body),
        };
      }
      const songCounts: Record<string, number> = {};
      for (const item of body.items) {
        if (isRecord(item) && typeof item.id === 'string' && typeof item.songCount === 'number') {
          songCounts[item.id] = item.songCount;
        }
      }
      return { ok: true, songCounts };
    } catch (error) {
      return { ok: false, ended: false, message: callFailure(error) };
    }
  }

  /**
   * The workspace the user chose, recorded as the Song's with the request's report. A report that
   * fails on the way is sent again (never the creation, which is the tab's and is done once); a
   * refusal is not.
   */
  private async resolve(tab: GenerationTab, chosen: ChosenWorkspace): Promise<GenerateReply> {
    const request = await this.read(tab.requestId);
    if (request !== null && request.active !== true) {
      await this.forget();
      return {
        ok: false,
        ended: true,
        message: typeof request.message === 'string' ? request.message : ENDED,
      };
    }
    let answer: GenerateReply = { ok: false, ended: false, message: UNREACHABLE };
    for (let attempt = 0; attempt <= RESOLVE_RETRIES; attempt += 1) {
      answer = await this.report(tab.requestId, 'workspace', 'workspace chosen', null, chosen);
      if (answer.ok || answer.message !== UNREACHABLE) {
        break;
      }
    }
    if (answer.ok) {
      await this.browser.session.set({
        [GENERATION_TAB_KEY]: {
          ...tab,
          chosen: { sunoId: chosen.sunoId, name: chosen.name, state: 'available' },
        },
      });
    }
    return answer;
  }

  /** Sends a progress report; never fails. */
  private async report(
    requestId: string,
    state: GenerateState,
    step: string | null,
    message: string | null = null,
    resolvedWorkspace?: ChosenWorkspace,
    verification?: VerificationReport,
  ): Promise<GenerateReply> {
    try {
      const response = await this.connection.call(
        `${GENERATION_REQUESTS_PATH}/${encodeURIComponent(requestId)}`,
        {
          method: 'PATCH',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            state,
            step,
            message,
            ...(resolvedWorkspace === undefined ? {} : { resolvedWorkspace }),
            ...(verification === undefined ? {} : { verification }),
          }),
        },
      );
      if (response.ok) {
        return { ok: true };
      }
      const body = await bodyOf(response);
      const code = isRecord(body) && typeof body.code === 'string' ? body.code : '';
      if (response.status >= 500) {
        return { ok: false, ended: false, message: UNREACHABLE };
      }
      return {
        ok: false,
        ended: code !== 'workspace_already_set' && code !== 'validation_failed',
        message: await refusalOf(response, 'the report', body),
      };
    } catch (error) {
      return { ok: false, ended: false, message: callFailure(error) };
    }
  }
}

function onSuno(address: string | undefined): boolean {
  try {
    return address !== undefined && isSunoAddress(new URL(address));
  } catch {
    return false;
  }
}

function textOrNull(value: unknown): string | null {
  return typeof value === 'string' ? value : null;
}

/**
 * What the Suno tab fills the Create form from (#146): the snapshot's kind, mode, entries by key,
 * sources and file inputs by entry, and the keys of unsupported values; null when the snapshot has
 * no kind or mode.
 */
export function formOf(snapshot: Record<string, unknown>): FormJob | null {
  if (typeof snapshot.kind !== 'string' || typeof snapshot.mode !== 'string') {
    return null;
  }
  const records = (value: unknown) =>
    Array.isArray(value)
      ? value.filter((item): item is Record<string, unknown> => isRecord(item))
      : [];
  const entries: Record<string, unknown> = {};
  for (const entry of records(snapshot.entries)) {
    if (typeof entry.key === 'string') {
      entries[entry.key] = entry.value ?? null;
    }
  }
  return {
    kind: snapshot.kind,
    mode: snapshot.mode,
    entries,
    sources: records(snapshot.sources)
      .filter((source) => typeof source.key === 'string')
      .map((source) => ({
        key: source.key as string,
        title: textOrNull(source.title) ?? textOrNull(source.shortcode),
        sunoAction: textOrNull(source.sunoAction),
      })),
    fileInputs: records(snapshot.fileInputs)
      .filter((file) => typeof file.key === 'string')
      .map((file) => ({ key: file.key as string, description: textOrNull(file.description) })),
    unsupported: records(snapshot.unsupported)
      .map((item) => item.key)
      .filter((key): key is string => typeof key === 'string'),
  };
}

/** The workspace a snapshot (or the kept choice) names, or null. */
function workspaceOf(value: unknown): RequestWorkspace | null {
  return isRecord(value) && typeof value.sunoId === 'string' && value.sunoId !== ''
    ? {
        sunoId: value.sunoId,
        name: typeof value.name === 'string' ? value.name : '',
        state: value.state === 'unavailable' ? 'unavailable' : 'available',
      }
    : null;
}

async function bodyOf(response: Response): Promise<unknown> {
  try {
    return (await response.json()) as unknown;
  } catch {
    return undefined;
  }
}

/** Plain words for an n8Tracks refusal: its status and code, never anything it carried. */
async function refusalOf(response: Response, what: string, read?: unknown): Promise<string> {
  const body = read ?? (await bodyOf(response));
  const code = isRecord(body) && typeof body.code === 'string' ? ` (${body.code})` : '';
  return `n8Tracks refused ${what}: ${String(response.status)}${code}.`;
}

function callFailure(error: unknown): string {
  return error instanceof DisconnectedError ? DISCONNECTED : UNREACHABLE;
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
