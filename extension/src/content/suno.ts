import type { Clock } from '../adapter/clock.ts';
import { ObservationFeed, type FeedWindow } from '../adapter/observations.ts';
import { ADAPTER_VERSION } from '../adapter/version.ts';
import { Page } from '../adapter/primitives.ts';
import {
  AdapterSession,
  WorkflowRegistry,
  type RecordedRun,
  type WorkflowStatus,
} from '../adapter/registry.ts';
import { ADAPTER_WORKFLOWS } from '../adapter/workflows/index.ts';
import type { DiagnosticReport } from '../diagnostics/report.ts';
import type { ConnectionState, Request } from '../messages.ts';
import { Panel, type ObjectUrls } from '../panel/panel.ts';
import { SyncView } from '../panel/SyncView.ts';
import { SunoSync } from './sunoSync.ts';

export interface SunoContentOptions {
  document: Document;
  /** Sends a request to the service worker: the panel asks only for the connection `state`. */
  send: (request: Request) => Promise<unknown>;
  extensionVersion: string;
  registry?: WorkflowRegistry;
  /** How often the address is compared while the panel is open, to re-check after navigation. */
  watchMs?: number;
  /** The page's address; the document's own unless a test stands in for it. */
  address?: () => string;
  /** Loads another Suno address in the tab; the document's own navigation unless a test stands in. */
  navigate?: (address: string) => void;
  /** Where the page observer's messages arrive; the document's window unless a test stands in. */
  window?: FeedWindow;
  clock?: Clock;
  /** The panel's Blob addresses for the diagnostic report; the browser's unless a test stands in. */
  objectUrls?: ObjectUrls;
}

export interface SunoContent {
  session: AdapterSession;
  panel: Panel;
  sync: SunoSync;
  syncView: SyncView;
  observations: ObservationFeed;
  /** The resume check of this page load: whether this tab is in a sync, and if so its leg. */
  resumed: Promise<void>;
  /** Opens the panel if closed, closes it if open; opening runs the self-check. */
  toggle(): Promise<void>;
  /** Reads the connection and runs the self-check again, if the panel is open. */
  refresh(): Promise<void>;
  stop(): void;
}

function isConnectionState(value: unknown): value is ConnectionState {
  return typeof value === 'object' && value !== null && 'status' in value;
}

function isReport(value: unknown): value is DiagnosticReport {
  return typeof value === 'object' && value !== null && 'reportVersion' in value;
}

/** A run for the service worker's step log: the adapter's record, nothing from n8Tracks. */
function runRecord(run: RecordedRun) {
  return {
    workflowId: run.workflowId,
    log: run.log.map((entry) => ({ ...entry })),
    failure: run.failure,
    structure: run.structure,
  };
}

/**
 * The Suno content script: the adapter session for this tab and the panel. The panel opens from
 * the toolbar (a `toggle-panel` message); opening it runs the self-check, which only reads the
 * page, and so does every navigation while it is open. A failure is kept in memory only.
 *
 * It also takes the page observer's messages, and asks once per page load whether this tab is in
 * a sync the user started (#134); if it is, it reads that sync's leg with the panel open.
 */
export function startSunoContent(options: SunoContentOptions): SunoContent {
  const { document: page, send } = options;
  const registry = options.registry ?? new WorkflowRegistry(ADAPTER_WORKFLOWS);
  const location = options.address ?? (() => page.location.href);
  const sunoPage = new Page(page, {
    address: location,
    ...(options.navigate === undefined ? {} : { navigate: options.navigate }),
    ...(options.clock === undefined ? {} : { clock: options.clock }),
  });
  // Every run that ends goes to the service worker's step log (#150), which keeps only declared
  // names, fixed outcomes, numbers, and the redacted expectation.
  const session = new AdapterSession(registry, sunoPage, (run) => {
    void send({ type: 'diagnostics-record', run: runRecord(run) })
      .catch(() => undefined)
      .then(() => refresh());
  });
  const view = options.window ?? page.defaultView;
  const observations = new ObservationFeed(
    view ?? {
      origin: '',
      postMessage: () => undefined,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    },
    options.clock,
  );
  const watchMs = options.watchMs ?? 1000;
  let watcher: ReturnType<typeof setInterval> | undefined;
  let address = location();

  const refresh = async () => {
    if (!panel.isOpen) {
      return;
    }
    let connection: ConnectionState = { status: 'not-paired' };
    try {
      const answer = await send({ type: 'state' });
      if (isConnectionState(answer)) {
        connection = answer;
      }
    } catch {
      // The service worker did not answer: the panel reads as not connected.
    }
    const feature =
      connection.status === 'connected'
        ? connection.features.find((item) => item.feature === 'sync')
        : undefined;
    syncView.setAvailable(
      feature?.available === true,
      connection.status !== 'connected'
        ? 'Connect the extension to n8Tracks to sync.'
        : (feature?.reason ?? 'This credential lacks suno.sync'),
    );
    const workflows = session.statuses();
    panel.render({ connection, workflows });
    await offerReport(workflows);
  };

  /** Hands the states to the step log, then offers the report, assembled now, in the panel. */
  const offerReport = async (workflows: readonly WorkflowStatus[]) => {
    try {
      const statuses = workflows.map(({ id, state, step, stopped }) => ({
        id,
        state,
        step,
        stopped,
      }));
      await send({ type: 'diagnostics-record', statuses });
      const report = await send({ type: 'diagnostic-report' });
      panel.setReport(isReport(report) ? report : null);
    } catch {
      panel.setReport(null);
    }
  };

  // Set below: the view's buttons call the sync, and the sync shows its states in the view.
  let sync: SunoSync | null = null;
  const syncView = new SyncView(page, {
    choices: () => ({
      workspaces: observations.listedWorkspaces(),
      playlists: observations.listedPlaylists(),
    }),
    preview: (scope) => {
      void sync?.preview(scope);
    },
    start: (scope) => {
      void sync?.start(scope);
    },
    cancel: () => {
      void sync?.cancel();
    },
  });

  const panel = new Panel(page, {
    versions: { extension: options.extensionVersion, adapter: ADAPTER_VERSION },
    ...(options.objectUrls === undefined ? {} : { objectUrls: options.objectUrls }),
    onCheckAgain: () => {
      void refresh();
    },
    onTryAgain: (workflowId) => {
      session.forget(workflowId);
      void refresh();
    },
    sync: syncView.element,
  });

  sync = new SunoSync({
    page: sunoPage,
    session,
    observations,
    send,
    show: (state) => {
      syncView.show(state);
      if (!panel.isOpen) {
        panel.open();
        watch();
        void refresh();
      }
    },
    showImages: (images) => {
      syncView.setImages(images);
    },
    versions: { extension: options.extensionVersion, adapter: ADAPTER_VERSION },
    ...(options.clock === undefined ? {} : { clock: options.clock }),
  });
  observations.start();

  const watch = () => {
    clearInterval(watcher);
    address = location();
    watcher = setInterval(() => {
      if (!panel.isOpen) {
        clearInterval(watcher);
        return;
      }
      if (location() !== address) {
        address = location();
        void refresh();
      }
    }, watchMs);
  };

  // Never starts a sync: only reads on if this tab's sync, started by the user, is under way.
  const resumed = sync.resume();

  return {
    session,
    panel,
    sync,
    syncView,
    observations,
    resumed,
    toggle: async () => {
      if (panel.isOpen) {
        panel.close();
        clearInterval(watcher);
        return;
      }
      panel.open();
      watch();
      await refresh();
    },
    refresh,
    stop: () => {
      clearInterval(watcher);
      observations.stop();
      panel.remove();
    },
  };
}
