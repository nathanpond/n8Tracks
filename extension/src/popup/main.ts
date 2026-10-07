import { SUNO_FILE } from '../background/connection.ts';
import { sendRequest, type TabMessage } from '../messages.ts';
import { startPopup } from './popup.ts';

void startPopup(document, {
  manifest: chrome.runtime.getManifest(),
  send: (request) => sendRequest(request),
  requestPermissions: (origins) => chrome.permissions.request({ origins }),
  openOptions: () => {
    void chrome.runtime.openOptionsPage();
  },
  activeTab: async () => {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    return tab?.id === undefined || tab.url === undefined ? null : { id: tab.id, url: tab.url };
  },
  togglePanel: async (tabId) => {
    const message: TabMessage = { type: 'toggle-panel' };
    try {
      await chrome.tabs.sendMessage(tabId, message);
    } catch {
      // A tab opened before pairing has no content script yet: add it, then ask again.
      await chrome.scripting.executeScript({ target: { tabId }, files: [SUNO_FILE] });
      await chrome.tabs.sendMessage(tabId, message);
    }
  },
  closePopup: () => {
    window.close();
  },
});
