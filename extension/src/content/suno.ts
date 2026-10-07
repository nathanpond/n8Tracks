import { ADAPTER_VERSION } from '../adapter/version.ts';
import { Page } from '../adapter/primitives.ts';
import { AdapterSession, WorkflowRegistry } from '../adapter/registry.ts';
import { ADAPTER_WORKFLOWS } from '../adapter/workflows/index.ts';
import type { ConnectionState, Request } from '../messages.ts';
import { Panel } from '../panel/panel.ts';

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
}

export interface SunoContent {
  session: AdapterSession;
  panel: Panel;
  /** Opens the panel if closed, closes it if open; opening runs the self-check. */
  toggle(): Promise<void>;
  /** Reads the connection and runs the self-check again, if the panel is open. */
  refresh(): Promise<void>;
  stop(): void;
}

function isConnectionState(value: unknown): value is ConnectionState {
  return typeof value === 'object' && value !== null && 'status' in value;
}

/**
 * The Suno content script: the adapter session for this tab and the panel. The panel opens from
 * the toolbar (a `toggle-panel` message); opening it runs the self-check, which only reads the
 * page, and so does every navigation while it is open. A failure is kept in memory only.
 */
export function startSunoContent(options: SunoContentOptions): SunoContent {
  const { document: page, send } = options;
  const registry = options.registry ?? new WorkflowRegistry(ADAPTER_WORKFLOWS);
  const location = options.address ?? (() => page.location.href);
  const session = new AdapterSession(registry, new Page(page, { address: location }));
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
    panel.render({ connection, workflows: session.statuses() });
  };

  const panel = new Panel(page, {
    versions: { extension: options.extensionVersion, adapter: ADAPTER_VERSION },
    onCheckAgain: () => {
      void refresh();
    },
    onTryAgain: (workflowId) => {
      session.forget(workflowId);
      void refresh();
    },
  });

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

  return {
    session,
    panel,
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
      panel.remove();
    },
  };
}
