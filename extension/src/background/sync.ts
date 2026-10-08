import { isSunoAddress } from '../adapter/addresses.ts';
import { legsFor } from '../adapter/libraryReader.ts';
import type {
  DiscardReason,
  ExportPart,
  ResponseFor,
  SyncProgress,
  SyncReply,
  SyncRequest,
  SyncScope,
  SyncSession,
} from '../messages.ts';
import { DisconnectedError, type Connection } from './connection.ts';
import { CoverImages } from './images.ts';

/**
 * The service worker's half of a sync (#134). The Suno tab reads; this holds the sync between its
 * page loads (in session storage, so a restarted service worker still has it) and is the only part
 * that calls n8Tracks: it creates the export, uploads its parts, completes it and opens the review,
 * or discards it. A closed Suno tab, or one taken off Suno, discards the export.
 */

/** The parts of `chrome.*` a sync uses, so tests can stand in for the browser. */
export interface SyncBrowser {
  /** `chrome.storage.session`: the sync in progress. */
  session: {
    get(keys: string[]): Promise<Record<string, unknown>>;
    set(items: Record<string, unknown>): Promise<void>;
    remove(keys: string[]): Promise<void>;
  };
  /** `chrome.storage.local`: the last export this extension created. */
  local: {
    get(keys: string[]): Promise<Record<string, unknown>>;
    set(items: Record<string, unknown>): Promise<void>;
    remove(keys: string[]): Promise<void>;
  };
  tabs: {
    get(tabId: number): Promise<{ id?: number; windowId: number }>;
    query(query: { windowId?: number }): Promise<{ id?: number; url?: string }[]>;
    update(tabId: number, properties: { url: string; active: boolean }): Promise<unknown>;
    create(properties: { url: string; windowId?: number; active: boolean }): Promise<unknown>;
  };
}

/** The session-storage key of the sync in progress. */
export const SYNC_KEY = 'sync';
/** The local-storage key of the last export this extension created, to warn before replacing it. */
export const LAST_EXPORT_KEY = 'lastSunoExport';

/** How many times a failed part upload is sent again before the sync stops. */
export const PART_RETRIES = 3;

export const EXPORTS_PATH = 'api/v1/suno/exports';

/** The address of the review of an export in n8Tracks (the review story, #139). */
export function reviewAddress(address: string, exportId: string): string {
  return `${address}/suno/imports/${encodeURIComponent(exportId)}`;
}

function onSuno(address: string): boolean {
  try {
    return isSunoAddress(new URL(address));
  } catch {
    return false;
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

async function bodyOf(response: Response): Promise<unknown> {
  try {
    return (await response.json()) as unknown;
  } catch {
    return undefined;
  }
}

/** Plain words for an n8Tracks refusal: its code, never anything the request carried. */
async function refusal(response: Response, what: string, read?: unknown): Promise<string> {
  const body = read ?? (await bodyOf(response));
  const code = isRecord(body) && typeof body.code === 'string' ? ` (${body.code})` : '';
  return `n8Tracks refused ${what}: ${String(response.status)}${code}.`;
}

const DISCONNECTED = 'The extension is not connected to n8Tracks; reconnect it in the options.';

export interface SyncCoordinatorOptions {
  connection: Connection;
  browser: SyncBrowser;
  /** The cover images (#152); by default over `browser.local`. */
  images?: CoverImages;
  now?: () => number;
}

export class SyncCoordinator {
  private readonly connection: Connection;
  private readonly browser: SyncBrowser;
  private readonly now: () => number;
  /** The cover images of the sync (#152), sent after the export is complete and ready. */
  readonly images: CoverImages;

  constructor(options: SyncCoordinatorOptions) {
    this.connection = options.connection;
    this.browser = options.browser;
    this.now = options.now ?? (() => Date.now());
    this.images =
      options.images ??
      new CoverImages({ connection: options.connection, storage: options.browser.local });
  }

  /** Answers a sync message from the Suno content script in tab `tabId`. */
  async handle(request: SyncRequest, tabId: number): Promise<ResponseFor[SyncRequest['type']]> {
    switch (request.type) {
      case 'sync-preview':
        return { replacesReady: await this.replacesReady() };
      case 'sync-begin':
        return this.begin(request.scope, tabId);
      case 'sync-resume': {
        const session = await this.session();
        return { session: session?.tabId === tabId ? session : null };
      }
      case 'sync-save':
        return this.save(request.progress, tabId);
      case 'sync-create':
        return this.create(request.header, tabId);
      case 'sync-part':
        return this.part(request.part, tabId);
      case 'sync-complete':
        return this.complete(tabId);
      case 'sync-discard':
        return this.discard(tabId, request.reason);
      case 'sync-images':
        return { images: await this.images.progress(tabId) };
    }
  }

  /** Whether a sync is under way, in any tab (the Download view waits for it, #215). */
  async running(): Promise<boolean> {
    return (await this.session()) !== null;
  }

  /** The Suno tab of a sync was closed: the export is discarded. */
  async tabRemoved(tabId: number): Promise<void> {
    const session = await this.session();
    if (session?.tabId === tabId) {
      await this.end(session);
    }
  }

  /** A tab's address changed: a sync's tab that left suno.com is lost, and its export discarded. */
  async tabUpdated(tabId: number, address: string | undefined): Promise<void> {
    if (address === undefined) {
      return;
    }
    const session = await this.session();
    if (session?.tabId === tabId && !onSuno(address)) {
      await this.end(session);
    }
  }

  private async session(): Promise<SyncSession | null> {
    const stored = (await this.browser.session.get([SYNC_KEY]))[SYNC_KEY];
    return isRecord(stored) && typeof stored.tabId === 'number'
      ? (stored as unknown as SyncSession)
      : null;
  }

  private async store(session: SyncSession): Promise<void> {
    await this.browser.session.set({ [SYNC_KEY]: { ...session, updatedAt: this.now() } });
  }

  /**
   * Forgets the sync, discarding its export in n8Tracks if there is one, with the reason when one is
   * known (#229): a tab closed or taken off Suno gives none. Never fails.
   */
  private async end(session: SyncSession, reason?: DiscardReason): Promise<void> {
    await this.browser.session.remove([SYNC_KEY]);
    if (session.exportId !== null) {
      await this.images.discard(session.exportId).catch(() => undefined);
      try {
        await this.connection.call(
          `${EXPORTS_PATH}/${encodeURIComponent(session.exportId)}/discard`,
          reason === undefined
            ? { method: 'POST' }
            : {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(reason),
              },
        );
      } catch {
        // Not connected or not reachable: n8Tracks expires a receiving export after 24 hours.
      }
    }
  }

  private async ownSession(tabId: number): Promise<SyncSession | null> {
    const session = await this.session();
    return session?.tabId === tabId ? session : null;
  }

  private async replacesReady(): Promise<boolean> {
    const last = (await this.browser.local.get([LAST_EXPORT_KEY]))[LAST_EXPORT_KEY];
    if (typeof last !== 'string') {
      return false;
    }
    try {
      const response = await this.connection.call(`${EXPORTS_PATH}/${encodeURIComponent(last)}`);
      const body = response.ok ? await bodyOf(response) : undefined;
      return isRecord(body) && (body.state === 'ready' || body.state === 'classifying');
    } catch {
      return false;
    }
  }

  private async begin(
    scope: SyncScope,
    tabId: number,
  ): Promise<SyncReply<{ session: SyncSession }>> {
    const state = await this.connection.state(true);
    if (state.status !== 'connected') {
      return { ok: false, message: DISCONNECTED };
    }
    if (!state.scopes.includes('suno.sync')) {
      return { ok: false, message: 'This credential lacks suno.sync.' };
    }
    // A sync still receiving, in this tab or another, is discarded before the new one starts.
    const earlier = await this.session();
    if (earlier !== null) {
      await this.end(earlier, { reason: 'cancelled' });
    }
    const session: SyncSession = {
      tabId,
      scope,
      legs: legsFor(scope),
      leg: 0,
      attempt: 0,
      partNumber: 0,
      counts: { clips: 0, trashed: 0, workspaces: 0, playlists: 0 },
      workspaces: [],
      exportId: null,
      updatedAt: this.now(),
    };
    await this.store(session);
    return { ok: true, session };
  }

  private async save(progress: SyncProgress, tabId: number): Promise<SyncReply> {
    const session = await this.ownSession(tabId);
    if (session === null) {
      return { ok: false, message: 'This tab is not running a sync.' };
    }
    await this.store({ ...session, ...progress });
    return { ok: true };
  }

  private async create(
    header: Record<string, unknown>,
    tabId: number,
  ): Promise<SyncReply<{ exportId: string }>> {
    const session = await this.ownSession(tabId);
    if (session === null) {
      return { ok: false, message: 'This tab is not running a sync.' };
    }
    if (session.exportId !== null) {
      return { ok: true, exportId: session.exportId };
    }
    try {
      const response = await this.connection.call(EXPORTS_PATH, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(header),
      });
      const body = await bodyOf(response);
      if (!response.ok || !isRecord(body) || typeof body.id !== 'string') {
        return { ok: false, message: await refusal(response, 'the export', body) };
      }
      await this.store({ ...session, exportId: body.id, workspaces: [] });
      await this.browser.local.set({ [LAST_EXPORT_KEY]: body.id });
      return { ok: true, exportId: body.id };
    } catch (error) {
      return { ok: false, message: this.callFailure(error) };
    }
  }

  /**
   * Uploads a part. A part that fails is sent again up to three times; a 401 stops at once (the
   * token is forgotten), and so does any other refusal, which sending again would not change.
   */
  private async part(part: ExportPart, tabId: number): Promise<SyncReply> {
    const exportId = (await this.ownSession(tabId))?.exportId ?? null;
    if (exportId === null) {
      return { ok: false, message: 'This tab has no export to send to.' };
    }
    const path = `${EXPORTS_PATH}/${encodeURIComponent(exportId)}/parts`;
    const body = JSON.stringify(part);
    let last = 'n8Tracks did not answer.';
    for (let attempt = 0; attempt <= PART_RETRIES; attempt += 1) {
      try {
        const response = await this.connection.call(path, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body,
        });
        if (response.ok) {
          // The part's covers are read and sent once the export is ready (#152).
          await this.images.collect(exportId, tabId, part).catch(() => undefined);
          return { ok: true };
        }
        last = await refusal(response, `part ${String(part.partNumber)}`);
        if (response.status < 500) {
          return { ok: false, message: last };
        }
      } catch (error) {
        if (error instanceof DisconnectedError) {
          return { ok: false, message: DISCONNECTED };
        }
        last = this.callFailure(error);
      }
    }
    return { ok: false, message: last };
  }

  private async complete(tabId: number): Promise<SyncReply<{ reviewUrl: string }>> {
    const session = await this.ownSession(tabId);
    const exportId = session?.exportId ?? null;
    if (session === null || exportId === null) {
      return { ok: false, message: 'This tab has no export to finish.' };
    }
    try {
      const response = await this.connection.call(
        `${EXPORTS_PATH}/${encodeURIComponent(exportId)}/complete`,
        { method: 'POST' },
      );
      if (!response.ok) {
        const message = await refusal(response, 'the finished export');
        await this.end(session);
        return { ok: false, message };
      }
    } catch (error) {
      await this.end(session);
      return { ok: false, message: this.callFailure(error) };
    }
    await this.browser.session.remove([SYNC_KEY]);
    const state = await this.connection.state();
    const address = state.status === 'not-paired' ? null : state.address;
    if (address === null) {
      return { ok: false, message: DISCONNECTED };
    }
    const reviewUrl = reviewAddress(address, exportId);
    // Images go on in the background; the user may start the review meanwhile (#152).
    void this.images.start(exportId, tabId).catch(() => undefined);
    await this.openReview(reviewUrl, address, tabId);
    return { ok: true, reviewUrl };
  }

  private async discard(tabId: number, reason?: DiscardReason): Promise<SyncReply> {
    const session = await this.ownSession(tabId);
    if (session !== null) {
      await this.end(session, reason);
    }
    return { ok: true };
  }

  /** Opens the review in an n8Tracks tab of the Suno tab's window, or in a new tab there. */
  private async openReview(reviewUrl: string, address: string, tabId: number): Promise<void> {
    let windowId: number | undefined;
    try {
      windowId = (await this.browser.tabs.get(tabId)).windowId;
    } catch {
      windowId = undefined;
    }
    const tabs = await this.browser.tabs.query(windowId === undefined ? {} : { windowId });
    const n8Tracks = tabs.find(
      (tab) =>
        tab.id !== undefined &&
        tab.url !== undefined &&
        (tab.url === address || tab.url.startsWith(`${address}/`)),
    );
    if (n8Tracks?.id !== undefined) {
      await this.browser.tabs.update(n8Tracks.id, { url: reviewUrl, active: true });
      return;
    }
    await this.browser.tabs.create({
      url: reviewUrl,
      active: true,
      ...(windowId === undefined ? {} : { windowId }),
    });
  }

  private callFailure(error: unknown): string {
    return error instanceof DisconnectedError
      ? DISCONNECTED
      : 'Cannot reach n8Tracks. Check that it is running.';
  }
}
