import { ADAPTER_VERSION } from '../adapter/version.ts';
import { displayVersion } from '../version-label.ts';
import { Connection } from './connection.ts';
import { route } from './router.ts';

/**
 * The service worker holds the pairing and is the only part of the extension that calls n8Tracks
 * (through `apiClient.ts`). It answers the popup, the options page, and the content scripts.
 */
const connection = new Connection({
  browser: {
    storage: chrome.storage.local,
    permissions: chrome.permissions,
    scripting: chrome.scripting,
  },
  versions: {
    extension: displayVersion(chrome.runtime.getManifest()),
    adapter: String(ADAPTER_VERSION),
  },
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  void route(connection, message, sender, chrome.runtime.id).then(sendResponse);
  return true;
});

// A permission removed in the browser's settings shows at the next state check, not a minute later.
chrome.permissions.onRemoved.addListener(() => {
  connection.forgetHandshake();
});

// Re-run the handshake whenever the service worker starts.
void connection.start().catch(() => undefined);
