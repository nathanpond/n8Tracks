import type { ConnectionState, Request, ResponseFor } from '../messages.ts';
import { element } from '../ui/connectionView.ts';
import type { ManifestIdentity } from '../version-label.ts';
import { renderConnection, renderPopup } from './render.ts';

export interface PopupOptions {
  manifest: ManifestIdentity;
  /** Sends a request to the service worker. */
  send: <T extends Request>(request: T) => Promise<ResponseFor[T['type']]>;
  /** `chrome.permissions.request`: must run inside the click that asks for it. */
  requestPermissions: (origins: string[]) => Promise<boolean>;
  openOptions: () => void;
}

/** Runs the popup: shows the state, and wires Disconnect, Reconnect, and the settings button. */
export async function startPopup(popup: Document, options: PopupOptions): Promise<void> {
  renderPopup(popup, options.manifest);
  let current: ConnectionState = await options.send({ type: 'state' });
  const show = (state: ConnectionState) => {
    current = state;
    renderConnection(popup, state);
  };
  show(current);

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
