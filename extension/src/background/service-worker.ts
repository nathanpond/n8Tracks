import { ADAPTER_VERSION } from '../adapter/version.ts';
import { ADAPTER_WORKFLOWS } from '../adapter/workflows/index.ts';
import { browserVersion, Diagnostics, type UserAgentData } from '../diagnostics/report.ts';
import { browserDownloads, Downloader } from '../download/downloader.ts';
import type { DownloadPrepareReply, DownloadTabMessage } from '../messages.ts';
import { displayVersion } from '../version-label.ts';
import { CompletionWatch } from './completion.ts';
import { Connection } from './connection.ts';
import { DownloadCoordinator } from './download.ts';
import { GenerateCoordinator } from './generate.ts';
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

// The completion watch (#154): a recorded Create's Generations, filled in when Suno finishes them;
// in session storage, with an alarm for the end of their ten minutes.
const completion = new CompletionWatch({
  connection,
  browser: { session: chrome.storage.session, alarms: chrome.alarms },
});

// Generate on Suno (#144, #145): the request this extension claimed and its Suno tab, in session
// storage.
const generate = new GenerateCoordinator({
  connection,
  browser: {
    session: chrome.storage.session,
    openOptions: () => chrome.runtime.openOptionsPage(),
    tabs: chrome.tabs,
  },
  completion,
});

// The download queue (#216): files prepared in the run's Suno tab (or the stream's address from the
// clip data), handed to the browser's downloads interface, and kept in local storage.
const browserDownloadsApi = browserDownloads();
const QUEUE_ALIVE_KEY = 'downloadQueueAlive';
const tell = (tabId: number, message: DownloadTabMessage) =>
  chrome.tabs.sendMessage(tabId, message);
const downloader = new Downloader({
  downloads: browserDownloadsApi,
  prepare: async (tabId, job) =>
    (await tell(tabId, { type: 'download-prepare', job })) as DownloadPrepareReply,
  storage: chrome.storage.local,
  // Session storage is empty after a browser restart: the queue then waits for Resume.
  browserStarted: async () => {
    const alive = (await chrome.storage.session.get([QUEUE_ALIVE_KEY]))[QUEUE_ALIVE_KEY] === true;
    await chrome.storage.session.set({ [QUEUE_ALIVE_KEY]: true });
    return !alive;
  },
  onChange: (run) => {
    if (run.tabId !== null) {
      void tell(run.tabId, { type: 'download-progress', run }).catch(() => undefined);
    }
  },
});
browserDownloadsApi.onChanged((id) => {
  downloader.downloadChanged(id);
});
void downloader.restore().catch(() => undefined);

// The Download view (#215): a Load library kept across its page load, the formats last chosen,
// and n8Tracks' clip lookup. It waits while a sync or Generate on Suno runs.
const download = new DownloadCoordinator({
  connection,
  downloader,
  browser: { session: chrome.storage.session, local: chrome.storage.local },
  busy: async () => {
    if (await sync.running()) {
      return 'A sync to n8Tracks is running. Load the library when it has finished or been cancelled.';
    }
    if (await generate.running()) {
      return 'Generate on Suno is running. Load the library when it has finished.';
    }
    return null;
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
  void route(
    connection,
    message,
    sender,
    chrome.runtime.id,
    diagnostics,
    sync,
    generate,
    download,
  ).then(sendResponse);
  return true;
});

// A sync's Suno tab that is closed, or taken off suno.com, ends the sync and discards its export.
chrome.tabs.onRemoved.addListener((tabId) => {
  void sync.tabRemoved(tabId).catch(() => undefined);
  // A generation's Suno tab that is closed stops its request, saying so, and ends its watch.
  void generate.tabRemoved(tabId).catch(() => undefined);
  void completion.tabRemoved(tabId).catch(() => undefined);
  // The download run's Suno tab: files that need the page wait for Resume; the stream goes on.
  void downloader.tabClosed(tabId).catch(() => undefined);
});
chrome.alarms.onAlarm.addListener((alarm) => {
  void completion.alarm(alarm.name).catch(() => undefined);
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
