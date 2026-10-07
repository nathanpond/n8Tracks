import { ADAPTER_VERSION } from '../adapter/version.ts';
import { ADAPTER_WORKFLOWS } from '../adapter/workflows/index.ts';
import { browserVersion, Diagnostics, type UserAgentData } from '../diagnostics/report.ts';
import { displayVersion } from '../version-label.ts';
import { Connection } from './connection.ts';
import { route } from './router.ts';
import { SyncCoordinator } from './sync.ts';

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

const sync = new SyncCoordinator({
  connection,
  browser: {
    session: chrome.storage.session,
    local: chrome.storage.local,
    tabs: chrome.tabs,
  },
});

// The diagnostic report's step log: in session storage only, cleared on Disconnect, never sent.
const diagnostics = new Diagnostics({
  storage: chrome.storage.session,
  workflows: ADAPTER_WORKFLOWS,
  versions: {
    extension: displayVersion(chrome.runtime.getManifest()),
    adapter: String(ADAPTER_VERSION),
  },
  connectionState: () => connection.state(),
  browser: () =>
    browserVersion((navigator as Navigator & { userAgentData?: UserAgentData }).userAgentData),
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  void route(connection, message, sender, chrome.runtime.id, diagnostics, sync).then(sendResponse);
  return true;
});

// A sync's Suno tab that is closed, or taken off suno.com, ends the sync and discards its export.
chrome.tabs.onRemoved.addListener((tabId) => {
  void sync.tabRemoved(tabId).catch(() => undefined);
});
chrome.tabs.onUpdated.addListener((tabId, change) => {
  void sync.tabUpdated(tabId, change.url).catch(() => undefined);
});

// A permission removed in the browser's settings shows at the next state check, not a minute later.
chrome.permissions.onRemoved.addListener(() => {
  connection.forgetHandshake();
});

// Re-run the handshake whenever the service worker starts.
void connection.start().catch(() => undefined);

// Cover images a stopped service worker left unsent are sent now (#152).
void sync.images.resume().catch(() => undefined);
