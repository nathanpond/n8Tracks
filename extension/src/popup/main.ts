import { sendRequest } from '../messages.ts';
import { startPopup } from './popup.ts';

void startPopup(document, {
  manifest: chrome.runtime.getManifest(),
  send: (request) => sendRequest(request),
  requestPermissions: (origins) => chrome.permissions.request({ origins }),
  openOptions: () => {
    void chrome.runtime.openOptionsPage();
  },
});
