import { isSunoAddress } from '../adapter/addresses.ts';
import type { ConnectionState, Request, ResponseFor } from '../messages.ts';
import { element } from '../ui/element.ts';
import type { ManifestIdentity } from '../version-label.ts';
import { renderConnection, renderPopup } from './render.ts';

export interface PopupOptions {
  manifest: ManifestIdentity;
  /** Sends a request to the service worker. */
  send: <T extends Request>(request: T) => Promise<ResponseFor[T['type']]>;
  /** `chrome.permissions.request`: must run inside the click that asks for it. */
  requestPermissions: (origins: string[]) => Promise<boolean>;
  openOptions: () => void;
  /** The active tab of the current window, with its address. */
  activeTab: () => Promise<{ id: number; url: string } | null>;
  /** Opens or closes the panel in a Suno tab (a `toggle-panel` message to its content script). */
  togglePanel: (tabId: number) => Promise<void>;
  /** Closes the popup, once the panel has been asked to open. */
  closePopup: () => void;
}

/** The tab's ID when the panel can be shown there: a suno.com tab. */
function sunoTab(tab: { id: number; url: string } | null): number | null {
  if (tab === null) {
    return null;
  }
  try {
    return isSunoAddress(new URL(tab.url)) ? tab.id : null;
  } catch {
    return null;
  }
}

/**
 * Runs the popup: shows the state, and wires Disconnect, Reconnect, the settings button, and, on a
 * Suno tab while connected, the button that opens the panel there.
 */
export async function startPopup(popup: Document, options: PopupOptions): Promise<void> {
  renderPopup(popup, options.manifest);
  const tabId = sunoTab(await options.activeTab().catch(() => null));
  let current: ConnectionState = await options.send({ type: 'state' });
  const show = (state: ConnectionState) => {
    current = state;
    renderConnection(popup, state);
    element(popup, 'show-panel').hidden = tabId === null || state.status !== 'connected';
  };
  show(current);

  element(popup, 'show-panel').addEventListener('click', () => {
    if (tabId === null) {
      return;
    }
    void options.togglePanel(tabId).then(() => {
      options.closePopup();
    });
  });
  element(popup, 'disconnect').addEventListener('click', () => {
    void options.send({ type: 'disconnect' }).then(show);
  });
  element(popup, 'reconnect').addEventListener('click', () => {
    if (current.status !== 'permission-removed') {
      return;
    }
    // Asked first, inside the click: the browser shows its prompt only for a user's action.
    void options.requestPermissions(current.origins).then(async () => {
      show(await options.send({ type: 'state', fresh: true }));
    });
  });
  element(popup, 'open-options').addEventListener('click', () => {
    options.openOptions();
  });
}
