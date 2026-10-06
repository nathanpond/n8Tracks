import { sendRequest } from '../messages.ts';
import { startOptions } from './options.ts';

void startOptions(document, {
  manifest: chrome.runtime.getManifest(),
  send: (request) => sendRequest(request),
  requestPermissions: (origins) => chrome.permissions.request({ origins }),
});
