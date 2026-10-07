import type { Compatibility } from './compatibility.ts';
import type { DiagnosticReport } from './diagnostics/report.ts';

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

/** What a sync reads (#134): the whole library, chosen workspaces, or chosen playlists. */
export type SyncScope =
  | { kind: 'library' }
  | { kind: 'workspaces'; ids: string[] }
  | { kind: 'playlists'; playlists: { id: string; name: string }[] };

/** One list a sync reads, each on its own Suno page. */
export type SyncLeg =
  | { list: 'workspaces' }
  | { list: 'library' }
  | { list: 'playlist'; id: string; name: string }
  | { list: 'trash' };

/** What a sync has read so far, as the panel shows it. */
export interface SyncCounts {
  clips: number;
  trashed: number;
  workspaces: number;
  playlists: number;
}

/**
 * What a sync carries from one Suno page to the next. The reader reads one list per page load;
 * the service worker holds this in between, in session storage, so a restarted service worker
 * still has it. Holds Suno records only as the raw workspace list, until the export is created.
 */
export interface SyncProgress {
  /** The leg to read next, an index into `legs`. */
  leg: number;
  /** How many times this leg's page has been opened again after no first page came. */
  attempt: number;
  /** Parts uploaded before this leg; a leg read again reuses its numbers, replacing its parts. */
  partNumber: number;
  /** Counts as of the start of this leg. */
  counts: SyncCounts;
  /** The workspace list as Suno returned it, until the export is created. */
  workspaces: unknown[];
}

export interface SyncSession extends SyncProgress {
  tabId: number;
  scope: SyncScope;
  legs: SyncLeg[];
  /** The export in n8Tracks, once created. */
  exportId: string | null;
  /** Milliseconds since the epoch of the last change. */
  updatedAt: number;
}

/** A sync step's answer: done, or why not, in plain words for the panel. */
export type SyncReply<T extends object = object> =
  ({ ok: true } & T) | { ok: false; message: string };

/** One part of an export (`docs/suno-integration.md`, Export format). */
export interface ExportPart {
  partNumber: number;
  clips: unknown[];
  trashedClips: unknown[];
  playlists: { id: string; name: string; clipIds: string[] }[];
}

/** The messages the service worker answers, by type, with the answer each gets. */
export type Request =
  | { type: 'state'; fresh?: boolean }
  | { type: 'connect'; address: string; token: string }
  | { type: 'disconnect' }
  | { type: 'relay'; message: PageMessage }
  | { type: 'sync-preview' }
  | { type: 'sync-begin'; scope: SyncScope }
  | { type: 'sync-resume' }
  | { type: 'sync-save'; progress: SyncProgress }
  | { type: 'sync-create'; header: Record<string, unknown> }
  | { type: 'sync-part'; part: ExportPart }
  | { type: 'sync-complete' }
  | { type: 'sync-discard' }
  /** The Suno content script: a run that ended and the self-check states (#150). */
  | { type: 'diagnostics-record'; run?: unknown; statuses?: unknown }
  /** The options page and the panel: the diagnostic report, assembled now (#150). */
  | { type: 'diagnostic-report' };

export interface ResponseFor {
  state: ConnectionState;
  connect: ConnectResult;
  disconnect: ConnectionState;
  relay: RelayReply;
  /** Whether an export of this extension's is waiting for review, which the new one replaces. */
  'sync-preview': { replacesReady: boolean };
  'sync-begin': SyncReply<{ session: SyncSession }>;
  /** The sync this tab is running, if any. */
  'sync-resume': { session: SyncSession | null };
  'sync-save': SyncReply;
  'sync-create': SyncReply<{ exportId: string }>;
  'sync-part': SyncReply;
  'sync-complete': SyncReply<{ reviewUrl: string }>;
  'sync-discard': SyncReply;
  'diagnostics-record': { recorded: true };
  'diagnostic-report': DiagnosticReport;
}

export type Response<T extends Request> = ResponseFor[T['type']];

/**
 * A message the extension's own pages send to the Suno content script in a tab
 * (`chrome.tabs.sendMessage`): the toolbar popup opens or closes the panel with it.
 */
export interface TabMessage {
  type: 'toggle-panel';
}

/** Whether `value` is a message for the Suno content script. */
export function isTabMessage(value: unknown): value is TabMessage {
  return isRecord(value) && value.type === 'toggle-panel';
}

/** The sync messages, which only the Suno content script sends, each for its own tab. */
export const SYNC_TYPES = [
  'sync-preview',
  'sync-begin',
  'sync-resume',
  'sync-save',
  'sync-create',
  'sync-part',
  'sync-complete',
  'sync-discard',
] as const satisfies readonly Request['type'][];

export type SyncRequest = Extract<Request, { type: (typeof SYNC_TYPES)[number] }>;

/** Whether `request` is one of the sync messages. */
export function isSyncRequest(request: Request): request is SyncRequest {
  return (SYNC_TYPES as readonly string[]).includes(request.type);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isTextList(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === 'string' && item !== '');
}

function isCount(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0;
}

function isSyncScope(value: unknown): value is SyncScope {
  if (!isRecord(value)) {
    return false;
  }
  switch (value.kind) {
    case 'library':
      return true;
    case 'workspaces':
      return isTextList(value.ids) && value.ids.length > 0;
    case 'playlists':
      return (
        Array.isArray(value.playlists) &&
        value.playlists.length > 0 &&
        value.playlists.every(
          (playlist) =>
            isRecord(playlist) &&
            typeof playlist.id === 'string' &&
            playlist.id !== '' &&
            typeof playlist.name === 'string',
        )
      );
    default:
      return false;
  }
}

function isSyncProgress(value: unknown): value is SyncProgress {
  return (
    isRecord(value) &&
    isCount(value.leg) &&
    isCount(value.attempt) &&
    isCount(value.partNumber) &&
    isRecord(value.counts) &&
    ['clips', 'trashed', 'workspaces', 'playlists'].every((key) =>
      isCount((value.counts as Record<string, unknown>)[key]),
    ) &&
    Array.isArray(value.workspaces)
  );
}

function isExportPart(value: unknown): value is ExportPart {
  return (
    isRecord(value) &&
    isCount(value.partNumber) &&
    value.partNumber > 0 &&
    Array.isArray(value.clips) &&
    Array.isArray(value.trashedClips) &&
    Array.isArray(value.playlists)
  );
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
    case 'sync-preview':
    case 'sync-resume':
    case 'sync-complete':
    case 'sync-discard':
      return true;
    case 'sync-begin':
      return isSyncScope(value.scope);
    case 'sync-save':
      return isSyncProgress(value.progress);
    case 'sync-create':
      return isRecord(value.header);
    case 'sync-part':
      return isExportPart(value.part);
    // The service worker checks the parts itself: only declared names and fixed values are kept.
    case 'diagnostics-record':
      return (
        (value.run === undefined || isRecord(value.run)) &&
        (value.statuses === undefined || Array.isArray(value.statuses))
      );
    case 'diagnostic-report':
      return true;
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
