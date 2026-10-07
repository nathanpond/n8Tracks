import type { Compatibility } from './compatibility.ts';
import type { FormJob, VerificationReport } from './adapter/fill.ts';
import type { DiagnosticReport } from './diagnostics/report.ts';
import type { PrepareJob, PrepareOutcome } from './adapter/downloadSteps.ts';
import type { DownloadRun } from './download/downloader.ts';
import { isDownloadFormat, type PlanEntry } from './download/selection.ts';

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

/**
 * The page messages the extension handles (#144), as the relay passes them on: Generate on Suno hands
 * over the ID of a request n8Tracks made, which the extension claims with its own token; the page
 * may also ask the extension to open its options.
 */
export type PageRequest = { type: 'generate'; requestId: string } | { type: 'open-options' };

/** Why the extension did not take a generation request, for the page to say. */
export type GenerateFailure =
  | 'invalid_request'
  | 'wrong_origin'
  | 'not_connected'
  | 'incompatible'
  | 'no_scope'
  | 'unreachable'
  | 'refused';

/** What the service worker answers a message the relay passes on. */
export type RelayReply =
  | { type: 'error'; error: 'unknown_type' | GenerateFailure; message: string }
  /** The request is claimed: the extension has it, bound to its credential. */
  | { type: 'generate-accepted'; requestId: string }
  | { type: 'options-opened' };

/**
 * A generation request the extension has claimed (#144), kept in session storage for the steps that
 * follow (#145): only its ID and when it was claimed; the snapshot is read again before each step.
 */
export interface GenerationHandOff {
  requestId: string;
  claimedAt: number;
}

/** The Song's Suno workspace as a request's snapshot gives it (#145). */
export interface RequestWorkspace {
  sunoId: string;
  name: string;
  state: 'available' | 'unavailable';
}

/**
 * What the Suno tab of a claimed request is to do (#145), as the service worker reads it from
 * n8Tracks before the tab starts: the Song's title and its workspace (the one the user chose in
 * the panel, once chosen), how many times the tab has loaded for the request, and what to fill
 * the Create form with (#146; null when the snapshot cannot be read).
 */
export interface GenerateJob {
  requestId: string;
  songTitle: string;
  workspace: RequestWorkspace | null;
  loads: number;
  form: FormJob | null;
  /**
   * How many of the user's Creates n8Tracks has recorded for the request (#149); absent or 0 before
   * the first. After one, a load of the tab does not fill the form again: on the Create page the tab
   * watches for further Creates, and anywhere else the request is done.
   */
  created?: number;
  /** Where loading the Version's source has got to across page loads (#148); null before it. */
  source?: SourcePhase | null;
}

/**
 * Loading a source spans page loads (#148): the tab goes to the source clip's page (`opening`),
 * chooses the action there (`chosen`), and Suno opens the Create form with the source. While the
 * clip's page is not captured (#341), the user loads it by hand (`byHand`): the tab waits, and the
 * next Create form it sees is checked for the source.
 */
export interface SourcePhase {
  phase: 'opening' | 'chosen' | 'byHand';
  /** The source clip's Suno ID. */
  sunoId: string;
}

/** What an observed Create came to in n8Tracks (#149), in plain words for the panel. */
export interface ObservedSummary {
  /** `attached`, `branched`, or `none` (every clip skipped). */
  outcome: string;
  message: string;
}

/** The states the extension reports a request in (`PATCH`, `docs/suno-integration.md`). */
export type GenerateState = 'opening' | 'workspace' | 'filling' | 'waiting' | 'done' | 'stopped';

/** The workspace the user chose for the Song in the panel (#145). */
export interface ChosenWorkspace {
  sunoId: string;
  name: string;
  how: 'created' | 'picked';
}

/**
 * A generate step's answer: done, or why not in plain words for the panel. `ended` says the
 * request is over in n8Tracks (cancelled, expired, or refused), so the tab stops.
 */
export type GenerateReply<T extends object = object> =
  ({ ok: true } & T) | { ok: false; ended: boolean; message: string };

/** A request ID as n8Tracks writes it: a UUID. */
const REQUEST_ID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** The page message as one the extension handles, or null for any other type or a malformed one. */
export function pageRequestOf(message: PageMessage): PageRequest | null {
  switch (message.type) {
    case 'generate':
      return typeof message.requestId === 'string' && REQUEST_ID_PATTERN.test(message.requestId)
        ? { type: 'generate', requestId: message.requestId }
        : null;
    case 'open-options':
      return { type: 'open-options' };
    default:
      return null;
  }
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

/**
 * The cover images of a sync (#152), as the panel shows them: sent once the export is ready.
 * `failed` counts images that could not be read or were refused (those Generations are imported
 * without artwork); `ignored` counts those not needed (a Generation that already had an image, or
 * a record that was not imported).
 */
export interface ImageProgress {
  exportId: string;
  tabId: number;
  /**
   * `collecting` while the sync reads; `waiting` for n8Tracks to get the export ready; `sending`;
   * then `finished`, or `stopped` (the export went, or the extension was disconnected), or
   * `skipped` when Suno's images cannot be read without credentials.
   */
  state: 'collecting' | 'waiting' | 'sending' | 'finished' | 'stopped' | 'skipped';
  total: number;
  sent: number;
  failed: number;
  ignored: number;
}

/** What a Load library or Refresh carries over its page load (#215): the clips selected before. */
export interface DownloadLoad {
  selected: string[];
}

/** One row of n8Tracks' clip lookup (#215), as `POST /api/v1/suno/clips/lookup` answers it. */
export interface ClipLookupRow {
  sunoId: string;
  generation: { id: string; shortcode: string } | null;
  artist: string | null;
  deleted: boolean;
  downloadedFormats: string[];
}

/**
 * The lookup's answer: the rows, or why not. `unavailable` when the extension is not connected or
 * its credential lacks `suno.sync` (the view says so and offers nothing); otherwise the lookup
 * failed and may be tried again.
 */
export type ClipLookupReply =
  { ok: true; rows: ClipLookupRow[] } | { ok: false; unavailable: boolean; message: string };

/** A Download step's answer: done, or why not, in plain words for the panel. */
export type DownloadReply = { ok: true } | { ok: false; message: string };

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
  /** The cover images of this tab's last sync (#152). */
  | { type: 'sync-images' }
  /** The Suno content script: a run that ended and the self-check states (#150). */
  | { type: 'diagnostics-record'; run?: unknown; statuses?: unknown }
  /** The options page and the panel: the diagnostic report, assembled now (#150). */
  | { type: 'diagnostic-report' }
  /** The Suno content script, on each page load: whether its tab is generating (#145). */
  | { type: 'generate-resume' }
  /** A step of Generate on Suno began: read the request, then report it (#145). */
  | {
      type: 'generate-progress';
      state: GenerateState;
      step: string;
      message?: string;
      /** The verification summary of the filled form (#146), which replaces the last one. */
      verification?: VerificationReport;
    }
  /** Suno's complete workspace list, for n8Tracks' record (#145). */
  | { type: 'generate-workspaces'; workspaces: unknown[] }
  /** The workspace the user chose in the panel, for the Song (#145). */
  | { type: 'generate-resolve'; workspace: ChosenWorkspace }
  /**
   * The user's own Create click, as the page observer saw it (#149): Suno's response, and the
   * request values at the import field map's `createRequest` paths (null when not read).
   */
  | {
      type: 'generate-observed';
      response: Record<string, unknown>;
      submitted: Record<string, unknown> | null;
    }
  /** Where loading the source has got to, kept for the next page load; null once loaded (#148). */
  | { type: 'generate-source'; source: SourcePhase | null }
  /**
   * The completion watch (#154): the clips of the user's Creates that a feed answer the page got
   * shows finished (none, to ask only what is still watched), as Suno's feed returned them.
   */
  | { type: 'generate-completion'; clips: Record<string, unknown>[] }
  /**
   * Load library or Refresh in the Download view (#215): the library page is opened again and read
   * on its next load, carrying the selection. Refused while a sync or Generate on Suno runs.
   */
  | { type: 'download-begin'; selected: string[] }
  /** The Suno content script, on each page load: whether its tab is to read the library (#215). */
  | { type: 'download-resume' }
  /** Which of these clips n8Tracks has as Generations (#215). */
  | { type: 'download-lookup'; sunoIds: string[] }
  /** The formats last chosen (#215); with `formats`, remembers those first. */
  | { type: 'download-formats'; formats?: string[] }
  /**
   * Start in the Download view (#216): the plan's files, and the Suno unlocks the user confirmed
   * for it. Files already queued are not added twice.
   */
  | { type: 'download-start'; files: PlanEntry[]; unlocks: number }
  /** Cancel the downloads, Retry failed downloads, or Resume a queue waiting for its tab (#216). */
  | { type: 'download-control'; action: DownloadAction }
  /** The download queue as it stands, for the panel on a page load (#216). */
  | { type: 'download-run' };

/**
 * How recording downloads in n8Tracks stands (#222): whether the extension holds a token, how many
 * reports wait to be sent, and how many of the run's files n8Tracks refused or were saved while not
 * connected.
 */
export interface RecordingStatus {
  connected: boolean;
  pending: number;
  refused: number;
  unrecorded: number;
}

/** The recording status in `value`, checked member by member, or null when it is not one. */
export function recordingOf(value: unknown): RecordingStatus | null {
  if (
    !isRecord(value) ||
    typeof value.connected !== 'boolean' ||
    !isCount(value.pending) ||
    !isCount(value.refused) ||
    !isCount(value.unrecorded)
  ) {
    return null;
  }
  return {
    connected: value.connected,
    pending: value.pending,
    refused: value.refused,
    unrecorded: value.unrecorded,
  };
}

/** What the Download view's run controls ask of the queue (#216). */
export type DownloadAction = 'cancel' | 'retry' | 'resume';

const DOWNLOAD_ACTIONS: readonly DownloadAction[] = ['cancel', 'retry', 'resume'];

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
  'sync-images': { images: ImageProgress | null };
  'diagnostics-record': { recorded: true };
  'diagnostic-report': DiagnosticReport;
  /** `watching`: the Suno IDs of this tab's clips still watched for completion (#154). */
  'generate-resume': { job: GenerateJob | null; watching?: string[] };
  'generate-progress': GenerateReply;
  /** n8Tracks' Song count of each workspace, by Suno ID, after the report. */
  'generate-workspaces': GenerateReply<{ songCounts: Record<string, number> }>;
  'generate-resolve': GenerateReply;
  'generate-observed': GenerateReply<{ recorded: ObservedSummary }>;
  'generate-source': GenerateReply;
  'generate-completion': GenerateReply<{ watching: string[] }>;
  'download-begin': DownloadReply;
  'download-resume': { load: DownloadLoad | null };
  'download-lookup': ClipLookupReply;
  'download-formats': { formats: string[] };
  'download-start': DownloadReply;
  'download-control': DownloadReply;
  'download-run': { run: DownloadRun | null; records?: RecordingStatus };
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

/**
 * What the service worker's download queue (#216) sends the run's Suno tab
 * (`chrome.tabs.sendMessage`): prepare one file on the page, answered with a `PrepareOutcome`, or
 * the queue as it now stands, for the panel.
 */
export type DownloadTabMessage =
  | { type: 'download-prepare'; job: PrepareJob }
  | { type: 'download-progress'; run: DownloadRun; records?: RecordingStatus };

/** The answer to `download-prepare`. */
export type DownloadPrepareReply = PrepareOutcome;

/** Whether `value` is a message from the download queue for the Suno content script. */
export function isDownloadTabMessage(value: unknown): value is DownloadTabMessage {
  if (!isRecord(value)) {
    return false;
  }
  if (value.type === 'download-prepare') {
    return (
      isRecord(value.job) &&
      typeof value.job.sunoId === 'string' &&
      ['wav', 'mp3', 'm4a'].includes(value.job.format as string)
    );
  }
  return (
    value.type === 'download-progress' && isRecord(value.run) && Array.isArray(value.run.files)
  );
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
  'sync-images',
] as const satisfies readonly Request['type'][];

export type SyncRequest = Extract<Request, { type: (typeof SYNC_TYPES)[number] }>;

/** The Generate on Suno messages, which only the Suno content script sends, each for its own tab. */
export const GENERATE_TYPES = [
  'generate-resume',
  'generate-progress',
  'generate-workspaces',
  'generate-resolve',
  'generate-observed',
  'generate-source',
  'generate-completion',
] as const satisfies readonly Request['type'][];

export type GenerateRequest = Extract<Request, { type: (typeof GENERATE_TYPES)[number] }>;

/** The Download view's messages (#215), which only the Suno content script sends, each for its own tab. */
export const DOWNLOAD_TYPES = [
  'download-begin',
  'download-resume',
  'download-lookup',
  'download-formats',
  'download-start',
  'download-control',
  'download-run',
] as const satisfies readonly Request['type'][];

export type DownloadRequest = Extract<Request, { type: (typeof DOWNLOAD_TYPES)[number] }>;

/** Whether `request` is one of the Download view's messages. */
export function isDownloadRequest(request: Request): request is DownloadRequest {
  return (DOWNLOAD_TYPES as readonly string[]).includes(request.type);
}

/** Whether `request` is one of the Generate on Suno messages. */
export function isGenerateRequest(request: Request): request is GenerateRequest {
  return (GENERATE_TYPES as readonly string[]).includes(request.type);
}

const GENERATE_STATES: readonly string[] = [
  'opening',
  'workspace',
  'filling',
  'waiting',
  'done',
  'stopped',
] satisfies GenerateState[];

/** Whether `value` is a source phase as the Suno tab sends it. */
export function isSourcePhase(value: unknown): value is SourcePhase {
  return (
    isRecord(value) &&
    (value.phase === 'opening' || value.phase === 'chosen' || value.phase === 'byHand') &&
    typeof value.sunoId === 'string' &&
    value.sunoId !== ''
  );
}

function isChosenWorkspace(value: unknown): value is ChosenWorkspace {
  return (
    isRecord(value) &&
    typeof value.sunoId === 'string' &&
    value.sunoId !== '' &&
    typeof value.name === 'string' &&
    (value.how === 'created' || value.how === 'picked')
  );
}

function isVerification(value: unknown): value is VerificationReport {
  return (
    isRecord(value) &&
    typeof value.adapterVersion === 'number' &&
    typeof value.mode === 'string' &&
    typeof value.checkedAt === 'string' &&
    Array.isArray(value.entries) &&
    value.entries.every(
      (entry) =>
        isRecord(entry) && typeof entry.key === 'string' && typeof entry.outcome === 'string',
    )
  );
}

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

/** A file of the Download view's plan (#216), member by member. */
function isPlanEntry(value: unknown): value is PlanEntry {
  return (
    isRecord(value) &&
    typeof value.sunoId === 'string' &&
    value.sunoId !== '' &&
    typeof value.title === 'string' &&
    typeof value.displayName === 'string' &&
    (value.artist === null || typeof value.artist === 'string') &&
    isDownloadFormat(value.format) &&
    typeof value.unlocked === 'boolean' &&
    (value.streamAddress === null || typeof value.streamAddress === 'string')
  );
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
    case 'sync-images':
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
    case 'generate-resume':
      return true;
    case 'generate-progress':
      return (
        typeof value.state === 'string' &&
        GENERATE_STATES.includes(value.state) &&
        typeof value.step === 'string' &&
        (value.message === undefined || typeof value.message === 'string') &&
        (value.verification === undefined || isVerification(value.verification))
      );
    case 'generate-workspaces':
      return Array.isArray(value.workspaces);
    case 'generate-resolve':
      return isChosenWorkspace(value.workspace);
    case 'generate-observed':
      return isRecord(value.response) && (value.submitted === null || isRecord(value.submitted));
    case 'generate-source':
      return value.source === null || isSourcePhase(value.source);
    case 'generate-completion':
      return Array.isArray(value.clips) && value.clips.every(isRecord);
    case 'download-begin':
      return Array.isArray(value.selected) && isTextList(value.selected);
    case 'download-resume':
      return true;
    case 'download-lookup':
      return isTextList(value.sunoIds) && value.sunoIds.length > 0;
    case 'download-formats':
      return (
        value.formats === undefined ||
        (Array.isArray(value.formats) && value.formats.every((item) => typeof item === 'string'))
      );
    case 'download-start':
      return (
        Array.isArray(value.files) &&
        value.files.length > 0 &&
        value.files.every(isPlanEntry) &&
        isCount(value.unlocks)
      );
    case 'download-control':
      return DOWNLOAD_ACTIONS.includes(value.action as DownloadAction);
    case 'download-run':
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
